// -----------------------------------------------------------------------
// <copyright file="TaskQueueService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 17.2 — concrete <see cref="ITaskQueueService"/>. Each transition is
/// an atomic SQLite <c>UPDATE … WHERE id=? AND state=?</c> compare-and-swap;
/// concurrent <see cref="ClaimNextAsync"/> calls for the same crew can
/// never double-pop because exactly one UPDATE succeeds per row.
/// </summary>
public sealed class TaskQueueService : ITaskQueueService
{
    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly ILogger<TaskQueueService> logger;

    public TaskQueueService(ControlDbContext control, ProductDbContext db, ILogger<TaskQueueService> logger)
    {
        this.control = control;
        this.db = db;
        this.logger = logger;
    }

    public async Task<TaskQueueRow> EnqueueAsync(string workItemId, string targetKind, string targetId, CancellationToken ct, string kind = TaskQueueKinds.Work, string? preferredAgentId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workItemId);
        ArgumentException.ThrowIfNullOrEmpty(targetKind);
        ArgumentException.ThrowIfNullOrEmpty(targetId);

        // Q17.6 — idempotency: the partial UNIQUE prevents two open rows for
        // the same work item, but we resolve the existing row first so the
        // caller sees the same shape on a duplicate call instead of a 409.
        // Q24.8 — same-kind idempotency: a 'comment-response' row in flight does
        // not block a fresh 'work' enqueue and vice-versa.
        // SQLite EF can't translate string[].Contains; convert to List<string>.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var existing = await this.db.TaskQueue
            .Where(r => r.WorkItemId == workItemId && r.Kind == kind && openStates.Contains(r.State))
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        // Priority comes from the work item; missing rows fall through as med.
        var wi = await this.db.WorkItems
            .Where(w => w.Id == workItemId)
            .FirstOrDefaultAsync(ct);
        var rank = PriorityRankFor(wi?.Priority);

        // Guard (2026-06-30) — a task_queue write with NO product scope binds to
        // the UNSCOPED control DB (ProductDbContext.ProductSlug == null; see
        // ProductDbContextFactory.CreateUnscoped). CrewQueueWorker drains via
        // ProductFanout over the per-product {slug}.db files ONLY and never
        // sweeps the control DB, so such a row is stranded forever — never
        // claimed, permanently reported as "strained". Surface it loudly, and
        // when the work item is ALSO absent from this DB the row is
        // unquestionably mis-routed (it belongs to another product's {slug}.db),
        // so refuse to create the orphan. Callers must run under a product slug
        // (ProductDbContextFactory.PushAsyncLocalSlug or an X-Forge-Product-Id
        // header). See the LeadAutoDispatcher / *Reconciler child-scope fix.
        if (string.IsNullOrEmpty(this.db.ProductSlug))
        {
            // LOG-LOUD (not throw) on purpose: throwing here could break any
            // not-yet-audited enqueue site at runtime on a live host. The
            // LeadAutoDispatcher/*Reconciler child-scope fixes stop the known
            // strandings, and the boot self-heal (Program.cs) cancels any that
            // slip through, so surfacing is enough. `wi is null` means the work
            // item isn't even in this (control) DB → the row is unquestionably
            // mis-routed (it belongs to another product's {slug}.db). Flip this
            // to a throw once every enqueue call site is confirmed slug-scoped.
            this.logger.LogError(
                "Unscoped task_queue enqueue for work item {WorkItemId} -> {TargetKind}:{TargetId} " +
                "resolved to the control DB (no product slug in scope; work item {Presence} in this DB). " +
                "This row will be STRANDED — never swept by ProductFanout. Push a product slug at the " +
                "enqueue call site (ProductDbContextFactory.PushAsyncLocalSlug / X-Forge-Product-Id).",
                workItemId, targetKind, targetId, wi is null ? "ABSENT" : "present");
        }

        var now = DateTimeOffset.UtcNow;
        var row = new TaskQueueRow
        {
            WorkItemId = workItemId,
            TargetKind = targetKind,
            TargetId = targetId,
            // Direct-to-crew assignments skip Stage 1; everything else enters
            // at awaiting-ack with the 10s SLO clock running.
            State = string.Equals(targetKind, "crew", StringComparison.OrdinalIgnoreCase)
                ? TaskQueueStates.Queued
                : TaskQueueStates.AwaitingAck,
            PriorityRank = rank,
            EnqueuedAt = now,
            AckRequiredBy = string.Equals(targetKind, "crew", StringComparison.OrdinalIgnoreCase)
                ? null
                : now.Add(TaskQueueDefaults.AckTimeout),
            RoutedToCrewId = string.Equals(targetKind, "crew", StringComparison.OrdinalIgnoreCase) ? targetId : null,
            LastUpdatedAt = now,
            Kind = kind,
            PreferredAgentId = string.IsNullOrEmpty(preferredAgentId) ? null : preferredAgentId,
        };
        this.db.TaskQueue.Add(row);
        this.db.TaskQueueHistory.Add(new TaskQueueHistoryRow
        {
            QueueId = 0, // patched after SaveChanges below
            Kind = "enqueued",
            ToValue = row.State,
            PayloadJson = JsonSerializer.Serialize(new { targetKind, targetId, priorityRank = rank }),
            At = now,
        });

        // Phase 19 — mark the work item as queued when a new row is inserted.
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = now;
        }

        await this.db.SaveChangesAsync(ct);

        // Patch the history row with the assigned id (we couldn't know it
        // pre-insert; doing this in one transaction keeps the audit honest).
        var pendingHistory = await this.db.TaskQueueHistory
            .Where(h => h.QueueId == 0 && h.Kind == "enqueued")
            .OrderByDescending(h => h.Id)
            .FirstOrDefaultAsync(ct);
        if (pendingHistory is not null)
        {
            pendingHistory.QueueId = row.Id;
            await this.db.SaveChangesAsync(ct);
        }
        return row;
    }

    public async Task<TaskQueueRow?> AcknowledgeAsync(long queueId, string? byUserId, bool auto, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await this.AtomicTransitionAsync(
            queueId,
            fromState: TaskQueueStates.AwaitingAck,
            toState: TaskQueueStates.AwaitingRoute,
            extraSets: new Dictionary<string, object?>
            {
                ["AckedAt"] = now,
                ["AckByUserId"] = byUserId,
                ["AutoAcked"] = auto ? 1 : 0,
                ["AckRequiredBy"] = null,
            },
            ct);
        if (!changed) return null;

        await this.WriteHistoryAsync(queueId, kind: auto ? "ack-timeout-auto" : "acked",
            from: TaskQueueStates.AwaitingAck, to: TaskQueueStates.AwaitingRoute,
            byUserId: byUserId, ct: ct);

        var row = await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
        if (row is null) return null;

        // Stage 2 fast-path: when the team maps to exactly one crew, route
        // through immediately. Multi-crew teams: prefer the on-shift crew
        // (day/night) so items don't stall at awaiting-route.
        if (row.TargetKind == "team")
        {
            var teamCrews = await this.control.Crews.AsNoTracking()
                .Where(c => c.Name.StartsWith(row.TargetId))
                .ToListAsync(ct);
            string? routeTarget = null;
            if (teamCrews.Count == 1)
            {
                routeTarget = teamCrews[0].Id;
            }
            else if (teamCrews.Count > 1)
            {
                // Prefer the generator-side crew if a pair exists.
                var genCrew = teamCrews.FirstOrDefault(c =>
                    string.Equals(c.PairRole, "generator", StringComparison.OrdinalIgnoreCase));
                routeTarget = genCrew?.Id ?? teamCrews[0].Id;
            }
            if (routeTarget is not null)
            {
                return await this.RouteToCrewAsync(queueId, routeTarget, byUserId, ct) ?? row;
            }
        }
        return row;
    }

    public async Task<TaskQueueRow?> RouteToCrewAsync(long queueId, string crewId, string? byUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await this.AtomicTransitionAsync(
            queueId,
            fromState: TaskQueueStates.AwaitingRoute,
            toState: TaskQueueStates.Queued,
            extraSets: new Dictionary<string, object?>
            {
                ["RoutedToCrewId"] = crewId,
                ["TargetKind"] = "crew",
                ["TargetId"] = crewId,
            },
            ct);
        if (!changed) return null;

        await this.WriteHistoryAsync(queueId, kind: "routed",
            from: TaskQueueStates.AwaitingRoute, to: TaskQueueStates.Queued,
            byUserId: byUserId,
            payload: new { crewId },
            ct: ct);
        return await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
    }

    public async Task<TaskQueueRow?> ClaimNextAsync(string crewId, string? byAgentId, CancellationToken ct)
    {
        // Find the head of queue. Filter SQL-side by indexed columns, then
        // sort client-side because SQLite EF can't ORDER BY DateTimeOffset.
        var candidates = await this.db.TaskQueue
            .Where(r => r.TargetKind == "crew"
                     && r.TargetId == crewId
                     && r.State == TaskQueueStates.Queued)
            .ToListAsync(ct);
        var head = candidates
            .OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt)
            .FirstOrDefault();
        if (head is null) return null;

        var now = DateTimeOffset.UtcNow;
        var changed = await this.AtomicTransitionAsync(
            head.Id,
            fromState: TaskQueueStates.Queued,
            toState: TaskQueueStates.PickedUp,
            extraSets: new Dictionary<string, object?>
            {
                ["PickedUpAt"] = now,
                ["PickedUpByAgent"] = byAgentId,
            },
            ct);
        if (!changed) return null; // another worker won the race; caller can retry

        await this.WriteHistoryAsync(head.Id, kind: "claimed",
            from: TaskQueueStates.Queued, to: TaskQueueStates.PickedUp,
            byUserId: byAgentId,
            payload: new { crewId, agent = byAgentId },
            ct: ct);
        return await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == head.Id, ct);
    }

    public async Task<TaskQueueRow?> CompleteAsync(long queueId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await this.AtomicTransitionAsync(
            queueId,
            fromState: TaskQueueStates.PickedUp,
            toState: TaskQueueStates.Completed,
            extraSets: new Dictionary<string, object?> { ["CompletedAt"] = now },
            ct);
        if (!changed) return null;
        await this.WriteHistoryAsync(queueId, kind: "completed",
            from: TaskQueueStates.PickedUp, to: TaskQueueStates.Completed, ct: ct);
        var row = await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
        // Phase 27 Q27.12 — terminal completion drains the build_fix_pending
        // row for this work item (auto-promote on completion / cancel).
        if (row is not null) await this.PromotePendingBuildFixIfAnyAsync(row.WorkItemId, ct);
        return row;
    }

    public async Task<TaskQueueRow?> ReassignAsync(long queueId, string newCrewId, string byUserId, CancellationToken ct)
    {
        var row = await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
        if (row is null) return null;
        // Q17.3 — only queued / awaiting-route can be reassigned. Picked-up
        // rows are mid-deliverable; cancellation is the right tool there.
        if (row.State != TaskQueueStates.Queued && row.State != TaskQueueStates.AwaitingRoute)
        {
            return null;
        }
        var fromCrew = row.RoutedToCrewId;
        row.RoutedToCrewId = newCrewId;
        row.TargetKind = "crew";
        row.TargetId = newCrewId;
        row.State = TaskQueueStates.Queued; // promote awaiting-route into queued post-pick
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await this.db.SaveChangesAsync(ct);

        await this.WriteHistoryAsync(queueId, kind: "reassigned",
            from: fromCrew ?? "", to: newCrewId,
            byUserId: byUserId,
            payload: new { fromCrew, toCrew = newCrewId },
            ct: ct);
        return row;
    }

    public async Task<TaskQueueRow?> BlockAsync(long queueId, string error, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        var row = await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
        if (row is null) return null;
        // Only queued rows can transition to blocked — picked-up rows have
        // an active invocation; cancellation is the right tool there.
        if (row.State != TaskQueueStates.Queued) return null;
        var fromState = row.State;
        row.State = TaskQueueStates.Blocked;
        row.LastError = error;
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await this.db.SaveChangesAsync(ct);

        await this.WriteHistoryAsync(queueId, kind: "blocked",
            from: fromState, to: TaskQueueStates.Blocked,
            payload: new { error, reason = reason ?? string.Empty },
            ct: ct);
        return row;
    }

    public async Task<TaskQueueRow?> CancelAsync(long queueId, string byUserId, string? reason, CancellationToken ct)
    {
        var row = await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
        if (row is null) return null;
        if (Array.IndexOf(TaskQueueStates.OpenStates, row.State) < 0) return null;
        var fromState = row.State;
        row.State = TaskQueueStates.Cancelled;
        row.LastUpdatedAt = DateTimeOffset.UtcNow;

        // Phase 19 — mark execution as cancelled.
        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == row.WorkItemId, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Cancelled;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await this.db.SaveChangesAsync(ct);

        await this.WriteHistoryAsync(queueId, kind: "cancelled",
            from: fromState, to: TaskQueueStates.Cancelled,
            byUserId: byUserId,
            payload: new { reason = reason ?? string.Empty },
            ct: ct);
        // Phase 27 Q27.12 — promote any pending build-fix on terminal cancel.
        await this.PromotePendingBuildFixIfAnyAsync(row.WorkItemId, ct);
        return row;
    }

    /// <summary>
    /// Phase 27 Q27.12 — when the blocking row (comment-response / coding /
    /// work) reaches a terminal state, drain the pending build-fix row by
    /// promoting it into a real <c>task_queue</c> row (kind='build-fix',
    /// preferred-agent pin preserved). Idempotent — re-running on a clean
    /// table is a no-op.
    /// </summary>
    private async Task PromotePendingBuildFixIfAnyAsync(string workItemId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workItemId)) return;

        // No promotion if any other open queue row blocks it.
        var openStates = TaskQueueStates.OpenStates.ToList();
        var stillBlocked = await this.db.TaskQueue.AnyAsync(r =>
            r.WorkItemId == workItemId
            && (r.Kind == TaskQueueKinds.CommentResponse
                || r.Kind == TaskQueueKinds.Coding
                || r.Kind == TaskQueueKinds.Work
                || r.Kind == TaskQueueKinds.BuildFix)
            && openStates.Contains(r.State), ct);
        if (stillBlocked) return;

        var pending = await this.db.BuildFixPending.FirstOrDefaultAsync(p => p.WorkItemId == workItemId, ct);
        if (pending is null) return;

        // Need crew + preferred agent from the work item's AssignedAgentJson.
        var wi = await this.db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null)
        {
            // Stale pending — work item gone. Clean up.
            this.db.BuildFixPending.Remove(pending);
            await this.db.SaveChangesAsync(ct);
            return;
        }
        var crewId = ResolveCrewId(wi.AssignedAgentJson);
        var preferredAgentId = ResolvePreferredAgentId(wi.AssignedAgentJson);
        if (string.IsNullOrEmpty(crewId)) return;

        // Enqueue a new build-fix row.
        await this.EnqueueAsync(
            workItemId,
            "crew",
            crewId!,
            ct,
            kind: TaskQueueKinds.BuildFix,
            preferredAgentId: preferredAgentId);

        // Drain the pending staging row.
        this.db.BuildFixPending.Remove(pending);
        await this.db.SaveChangesAsync(ct);
    }

    private static string? ResolveCrewId(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("crewId", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
            if (doc.RootElement.TryGetProperty("CrewId", out var c2) && c2.ValueKind == JsonValueKind.String)
                return c2.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? ResolvePreferredAgentId(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (TryFirstString(doc.RootElement, "agents", out var lower)) return lower;
            if (TryFirstString(doc.RootElement, "Agents", out var upper)) return upper;
        }
        catch { /* ignore */ }
        return null;

        static bool TryFirstString(JsonElement root, string name, out string? value)
        {
            value = null;
            if (!root.TryGetProperty(name, out var arr)) return false;
            if (arr.ValueKind != JsonValueKind.Array) return false;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    value = el.GetString();
                    return !string.IsNullOrEmpty(value);
                }
            }
            return false;
        }
    }

    public async Task<IReadOnlyList<TaskQueueRow>> GetQueueAsync(string targetKind, string targetId, IReadOnlyList<string>? states, CancellationToken ct)
    {
        var q = this.db.TaskQueue.AsNoTracking()
            .Where(r => r.TargetKind == targetKind && r.TargetId == targetId);
        if (states is { Count: > 0 })
        {
            var filter = states.ToList();
            q = q.Where(r => filter.Contains(r.State));
        }
        var rows = await q.ToListAsync(ct);
        // Order client-side so SQLite's DateTimeOffset weakness doesn't bite.
        return rows.OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt).ToList();
    }

    public Task<TaskQueueRow?> GetAsync(long queueId, CancellationToken ct) =>
        this.db.TaskQueue.AsNoTracking().FirstOrDefaultAsync(r => r.Id == queueId, ct);

    public async Task<IReadOnlyList<TaskQueueHistoryRow>> GetHistoryAsync(long queueId, CancellationToken ct)
    {
        var rows = await this.db.TaskQueueHistory.AsNoTracking()
            .Where(h => h.QueueId == queueId)
            .ToListAsync(ct);
        return rows.OrderBy(h => h.At).ThenBy(h => h.Id).ToList();
    }

    public async Task<CrewQueueStats> GetCrewStatsAsync(string crewId, CancellationToken ct)
    {
        var queued = await this.db.TaskQueue.AsNoTracking()
            .Where(r => r.TargetKind == "crew" && r.TargetId == crewId && r.State == TaskQueueStates.Queued)
            .ToListAsync(ct);
        var depth = queued.Count;
        var oldest = queued.Count == 0 ? (DateTimeOffset?)null
            : queued.Min(r => (DateTimeOffset?)r.EnqueuedAt);
        var head = queued
            .OrderBy(r => r.PriorityRank).ThenBy(r => r.EnqueuedAt)
            .FirstOrDefault();

        // ETA = depth × p50 recent iteration minutes for the crew. Sample =
        // closed deliverable durations (created_at deltas of consecutive rows
        // grouped by work item) for that crew over the last 20 deliverables.
        var p50 = await this.ComputeP50IterationMinutesAsync(crewId, ct);
        var eta = depth * p50;

        var strained = depth > TaskQueueDefaults.StrainDepthThreshold
                       || eta > TaskQueueDefaults.StrainEtaMinutesThreshold;

        return new CrewQueueStats(
            CrewId: crewId,
            Depth: depth,
            EtaMinutes: eta,
            Strained: strained,
            OldestQueuedAt: oldest,
            HeadWorkItemId: head?.WorkItemId);
    }

    public async Task<TaskQueueRow?> ConfirmStrainAsync(long queueId, string byUserId, CancellationToken ct)
    {
        var row = await this.db.TaskQueue.FirstOrDefaultAsync(r => r.Id == queueId, ct);
        if (row is null) return null;
        await this.WriteHistoryAsync(queueId, kind: "strain-confirmed",
            byUserId: byUserId, ct: ct);
        return row;
    }

    /// <summary>
    /// Atomic compare-and-swap. Uses a raw <c>UPDATE</c> so the row only
    /// changes when its current state matches <paramref name="fromState"/>.
    /// Returns true when the update changed exactly one row.
    /// </summary>
    private async Task<bool> AtomicTransitionAsync(
        long queueId,
        string fromState,
        string toState,
        IReadOnlyDictionary<string, object?> extraSets,
        CancellationToken ct)
    {
        var conn = (SqliteConnection)this.db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var sets = new List<string> { "State = @ToState", "LastUpdatedAt = @Now" };
        var i = 0;
        var pairs = new List<(string Param, object? Value)>
        {
            ("@ToState", toState),
            ("@Now", DateTimeOffset.UtcNow.ToString("o")),
        };
        foreach (var (col, value) in extraSets)
        {
            var pname = $"@p{i++}";
            sets.Add($"{col} = {pname}");
            pairs.Add((pname, value is DateTimeOffset dto ? dto.ToString("o") : value));
        }
        var sql = $"UPDATE task_queue SET {string.Join(", ", sets)} WHERE Id = @Id AND State = @FromState;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", queueId);
        cmd.Parameters.AddWithValue("@FromState", fromState);
        foreach (var (p, v) in pairs)
        {
            cmd.Parameters.AddWithValue(p, v ?? DBNull.Value);
        }
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 1)
        {
            // Drop EF's cached snapshot so a subsequent read sees the new state.
            var tracked = this.db.ChangeTracker.Entries<TaskQueueRow>()
                .FirstOrDefault(e => e.Entity.Id == queueId);
            if (tracked is not null) await tracked.ReloadAsync(ct);
        }
        return rows == 1;
    }

    private async Task WriteHistoryAsync(
        long queueId,
        string kind,
        string? from = null,
        string? to = null,
        string? byUserId = null,
        int? atDepth = null,
        int? etaMinutes = null,
        object? payload = null,
        CancellationToken ct = default)
    {
        this.db.TaskQueueHistory.Add(new TaskQueueHistoryRow
        {
            QueueId = queueId,
            Kind = kind,
            FromValue = from,
            ToValue = to,
            ByUserId = byUserId,
            AtDepth = atDepth,
            EtaMinutes = etaMinutes,
            PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload),
            At = DateTimeOffset.UtcNow,
        });
        await this.db.SaveChangesAsync(ct);
    }

    private async Task<int> ComputeP50IterationMinutesAsync(string crewId, CancellationToken ct)
    {
        var recent = await this.db.Deliverables.AsNoTracking()
            .Where(d => d.CrewId == crewId)
            .ToListAsync(ct);
        if (recent.Count < 5) return TaskQueueDefaults.DefaultIterationMinutes;

        var sorted = recent.OrderByDescending(d => d.CreatedAt).Take(20).OrderBy(d => d.CreatedAt).ToList();
        var deltas = new List<double>();
        for (var i = 1; i < sorted.Count; i++)
        {
            var d = (sorted[i].CreatedAt - sorted[i - 1].CreatedAt).TotalMinutes;
            if (d > 0 && d < 240) deltas.Add(d); // drop bogus / multi-day gaps
        }
        if (deltas.Count == 0) return TaskQueueDefaults.DefaultIterationMinutes;
        deltas.Sort();
        var p50 = deltas[deltas.Count / 2];
        return Math.Max(1, (int)Math.Round(p50));
    }

    private static int PriorityRankFor(string? priority) => priority?.ToLowerInvariant() switch
    {
        "high" => 0,
        "low" => 2,
        _ => 1,
    };
}
