// -----------------------------------------------------------------------
// <copyright file="TaskExecutionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.IO;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 16.9 — read-side surfaces for the Live Execution view.
///
/// <list type="bullet">
///   <item><c>GET /api/work-items/{id}/execution</c> — single snapshot:
///         current iteration, generator + evaluator crew/agent ids,
///         deliverables history, cumulative tokens, budget remaining,
///         last-N transcript events, last-known worktree state.</item>
///   <item><c>GET /api/work-items/{id}/transcript?from=...&amp;limit=...</c> —
///         paginated <c>task_transcript_event</c> rows for replay-on-open
///         when the SignalR live tail is attached.</item>
/// </list>
/// </summary>
public static class TaskExecutionEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static WebApplication MapTaskExecutionEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/work-items").WithTags("WorkItems");
        grp.MapGet("/{id}/execution", GetExecution).WithName("GetWorkItemExecution");
        grp.MapGet("/{id}/transcript", GetTranscript).WithName("GetWorkItemTranscript");
        grp.MapDelete("/{id}/transcript", ClearTranscript).WithName("ClearWorkItemTranscript");
        grp.MapPatch("/{id}/execution", PatchExecution).WithName("PatchWorkItemExecution");
        return app;
    }

    private static async Task<IResult> GetExecution(
        string id,
        ForgeDbContext db,
        WorktreeChangeFeed worktreeFeed,
        CancellationToken ct)
    {
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

        // When the work item row doesn't exist (e.g. plan/code task enqueued
        // before the WIT row is materialized), build a minimal snapshot from
        // the queue + transcript data instead of returning 404.
        var queueRows = await db.TaskQueue.AsNoTracking()
            .Where(r => r.WorkItemId == id)
            .ToListAsync(ct);
        var latestQueue = queueRows.OrderByDescending(r => r.EnqueuedAt).FirstOrDefault();

        if (wi is null && latestQueue is null)
            return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Deliverables for this work item, oldest → newest.
        var deliverableRows = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        deliverableRows = deliverableRows.OrderBy(d => d.CreatedAt).ThenBy(d => d.Iter).ToList();
        var deliverables = deliverableRows.Select(MapDeliverable).ToList();

        // Generator + evaluator from the most recent deliverable, falling
        // back to the queue row or work item assignment when no deliverables
        // exist yet (task is queued/claimed but hasn't produced output).
        string? generatorCrewId = null, evaluatorCrewId = null;
        string? generatorAgentId = null, evaluatorAgentId = null;
        var current = deliverableRows.LastOrDefault();
        if (current is not null)
        {
            generatorCrewId = current.CrewId;
            generatorAgentId = current.GeneratorAgentId;
            evaluatorAgentId = current.EvaluatorAgentId;
            var evaluatorAgent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == current.EvaluatorAgentId, ct);
            evaluatorCrewId = evaluatorAgent?.CrewId;
        }
        else
        {
            // Fallback: read from the queue row or work item assignment JSON.
            if (latestQueue is not null)
            {
                generatorCrewId = latestQueue.RoutedToCrewId;
                generatorAgentId = latestQueue.PickedUpByAgent;
            }
            // Also try the work item's AssignedAgentJson (Phase 22) or
            // AssignedToJson (legacy) for any still-missing fields.
            // The queue row may set generatorCrewId but leave generatorAgentId null
            // (e.g. restart before claim), so check each field independently.
            var assignmentJson = wi?.AssignedAgentJson ?? wi?.AssignedToJson;
            if (!string.IsNullOrEmpty(assignmentJson)
                && (string.IsNullOrEmpty(generatorCrewId) || string.IsNullOrEmpty(generatorAgentId)
                    || string.IsNullOrEmpty(evaluatorAgentId) || string.IsNullOrEmpty(evaluatorCrewId)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(assignmentJson);
                    if (doc.RootElement.TryGetProperty("crewId", out var c))
                        generatorCrewId ??= c.GetString();
                    if (doc.RootElement.TryGetProperty("generatorAgentId", out var g))
                        generatorAgentId ??= g.GetString();
                    // Phase 22: agents[] array in AssignedAgentJson.
                    if (string.IsNullOrEmpty(generatorAgentId)
                        && doc.RootElement.TryGetProperty("agents", out var agArr) && agArr.ValueKind == JsonValueKind.Array && agArr.GetArrayLength() > 0)
                        generatorAgentId ??= agArr[0].GetString();
                    if (doc.RootElement.TryGetProperty("evaluatorAgentId", out var e) && e.ValueKind == JsonValueKind.String)
                        evaluatorAgentId ??= e.GetString();
                    // Phase 22: agents[1] is the evaluator in paired runs.
                    if (string.IsNullOrEmpty(evaluatorAgentId)
                        && doc.RootElement.TryGetProperty("agents", out var agArr2) && agArr2.ValueKind == JsonValueKind.Array && agArr2.GetArrayLength() > 1)
                        evaluatorAgentId ??= agArr2[1].GetString();
                    if (doc.RootElement.TryGetProperty("evaluatorCrewId", out var ec) && ec.ValueKind == JsonValueKind.String)
                        evaluatorCrewId ??= ec.GetString();
                }
                catch { /* best effort */ }
            }
            // Last resort: check agent_usage ledger (covers direct invocations).
            if (string.IsNullOrEmpty(generatorCrewId))
            {
                var usageRows = await db.AgentUsage.AsNoTracking()
                    .Where(u => u.TaskId == id && u.CrewId != null && u.CrewId != "")
                    .ToListAsync(ct);
                var latestUsage = usageRows.OrderByDescending(u => u.StartedAt).FirstOrDefault();
                if (latestUsage is not null)
                {
                    generatorCrewId = latestUsage.CrewId;
                    generatorAgentId ??= latestUsage.AgentId;
                }
            }
        }

        // Cost: split by category from the agent_usage ledger.
        var usage = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == id)
            .ToListAsync(ct);
        decimal genCost  = usage.Where(u => u.CostCategory == "generator").Sum(u => u.CostUsd);
        decimal evalCost = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.CostUsd);
        long genIn  = usage.Where(u => u.CostCategory == "generator").Sum(u => u.InputTokens);
        long genOut = usage.Where(u => u.CostCategory == "generator").Sum(u => u.OutputTokens);
        long evalIn  = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.InputTokens);
        long evalOut = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.OutputTokens);

        // Crew budget (today's spend vs. daily cap, if any).
        // Also resolve evaluatorCrewId from the generator crew's pair when not
        // yet known from deliverables (pre-first-iteration state).
        decimal? dailyBudget = null;
        decimal? spendToday = null;
        decimal? maxTaskBudget = null;
        string? generatorCrewName = null;
        string? evaluatorCrewName = null;
        if (!string.IsNullOrEmpty(generatorCrewId))
        {
            var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == generatorCrewId, ct);
            dailyBudget = crew?.DailyBudgetUsd;
            maxTaskBudget = crew?.MaxTaskBudgetUsd;
            generatorCrewName = crew?.Name;
            if (string.IsNullOrEmpty(evaluatorCrewId) && !string.IsNullOrEmpty(crew?.PairedWithCrewId))
            {
                evaluatorCrewId = crew.PairedWithCrewId;
            }
            // Resolve evaluator agent from the evaluator crew's lead when
            // not yet known (pre-first-iteration or AssignedToJson cleared).
            if (string.IsNullOrEmpty(evaluatorAgentId) && !string.IsNullOrEmpty(evaluatorCrewId))
            {
                var evalCrew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evaluatorCrewId, ct);
                evaluatorAgentId = crew?.DefaultEvaluatorAgentId ?? evalCrew?.Lead;
                evaluatorCrewName = evalCrew?.Name;
            }
            if (crew is not null)
            {
                var since = DateTimeOffset.UtcNow.AddHours(-24);
                var crewUsage = (await db.AgentUsage.AsNoTracking()
                    .Where(u => u.CrewId == crew.Id)
                    .ToListAsync(ct))
                    .Where(u => u.StartedAt >= since);
                spendToday = crewUsage.Sum(u => u.CostUsd);
            }
        }

        // Total transcript count for paging UI.
        var transcriptTotalCount = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id)
            .CountAsync(ct);

        // Recent transcript (last 500 events, oldest→newest for natural rendering).
        // Always include prompt + iteration boundary events so the UI can
        // show the prompt section and iteration pager even when the window
        // has scrolled past them.
        var transcriptRows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id)
            .OrderByDescending(e => e.Id)
            .Take(500)
            .ToListAsync(ct);
        var pinnedKinds = new HashSet<string>
        {
            "task.prompt", "task.delegated",
            "task.iteration.started", "task.iteration.scored",
            "task.iteration.passed", "task.iteration.rejected",
            "task.iteration.iterated", "task.iteration.stalled",
        };
        var pinnedIds = new HashSet<long>(transcriptRows.Select(r => r.Id));
        var pinnedRows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id && pinnedKinds.Contains(e.Kind) && !pinnedIds.Contains(e.Id))
            .ToListAsync(ct);
        transcriptRows.AddRange(pinnedRows);
        transcriptRows = transcriptRows.OrderBy(e => e.Id).ToList();
        var transcript = transcriptRows.Select(MapTranscript).ToList();

        // Worktree (best-effort): most recent task.worktree-tick payload.
        // Falls back to a live disk read when no tick events exist (e.g.
        // after a server restart that lost in-memory feed subscriptions).
        WorktreeSnapshot? worktree = null;
        var lastTick = transcriptRows.LastOrDefault(e => e.Kind == "task.worktree-tick");
        if (lastTick is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(lastTick.PayloadJson);
                worktree = ParseWorktreeTick(doc.RootElement, lastTick.At);
            }
            catch { /* malformed payload — fall through to null */ }
        }

        // Fallback: read worktree directly from disk using the path from
        // the started event or derived from the work item's repo + branch.
        if (worktree is null)
        {
            string? wtPath = null;
            var startedEvt = transcriptRows.FirstOrDefault(e => e.Kind == "task.started" || e.Kind == "task.prompts");
            if (startedEvt is not null)
            {
                try
                {
                    using var startDoc = JsonDocument.Parse(startedEvt.PayloadJson);
                    if (startDoc.RootElement.TryGetProperty("worktreePath", out var wp))
                        wtPath = wp.GetString();
                }
                catch { /* ignore */ }
            }

            // Convention fallback: E:\forge\{repoId}\.worktree\ws-{workItemId}
            if (string.IsNullOrEmpty(wtPath) && wi is not null && !string.IsNullOrEmpty(wi.RepoId))
            {
                var repo = await db.Set<Forge.Api.Persistence.RepoRow>().AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
                if (repo is not null && !string.IsNullOrEmpty(repo.CloneRoot))
                    wtPath = Path.Combine(repo.CloneRoot, ".worktree", $"ws-{id}");
            }

            if (!string.IsNullOrEmpty(wtPath) && Directory.Exists(wtPath))
            {
                try
                {
                    var diskSnap = await worktreeFeed.SampleAsync(wtPath, ct);
                    if (diskSnap is not null)
                    {
                        worktree = new WorktreeSnapshot(
                            string.Empty,
                            diskSnap.Added, diskSnap.Modified, diskSnap.Deleted,
                            diskSnap.Ahead, diskSnap.Behind, diskSnap.Dirty,
                            diskSnap.HeadSha, diskSnap.HeadMessage, diskSnap.HeadAuthor, diskSnap.HeadAt,
                            diskSnap.Truncated, DateTimeOffset.UtcNow,
                            wtPath);

                        // Re-subscribe the feed so future ticks are emitted.
                        worktreeFeed.Subscribe(id, string.Empty, wtPath);
                    }
                }
                catch { /* best-effort */ }
            }
        }

        // Iteration counter + score: derived from the chronologically latest deliverable.
        var currentIter = current?.Iter ?? 0;
        int? currentScore = current?.Score;
        string? currentVerdict = current?.Verdict;

        // Resolve agent full names.
        string? generatorAgentName = null;
        string? evaluatorAgentName = null;
        var agentIds = new HashSet<string>();
        if (!string.IsNullOrEmpty(generatorAgentId)) agentIds.Add(generatorAgentId);
        if (!string.IsNullOrEmpty(evaluatorAgentId)) agentIds.Add(evaluatorAgentId);
        if (agentIds.Count > 0)
        {
            var agentRows = await db.Agents.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
            generatorAgentName = agentRows.FirstOrDefault(a => a.Id == generatorAgentId)?.Name;
            evaluatorAgentName = agentRows.FirstOrDefault(a => a.Id == evaluatorAgentId)?.Name;
        }

        var snap = new TaskExecutionSnapshot(
            WorkItemId: id,
            GeneratorCrewId: generatorCrewId,
            GeneratorCrewName: generatorCrewName,
            EvaluatorCrewId: evaluatorCrewId,
            EvaluatorCrewName: evaluatorCrewName,
            GeneratorAgentId: generatorAgentId,
            GeneratorAgentName: generatorAgentName,
            EvaluatorAgentId: evaluatorAgentId,
            EvaluatorAgentName: evaluatorAgentName,
            CurrentIteration: currentIter,
            CurrentScore: currentScore,
            CurrentVerdict: currentVerdict,
            GeneratorCostUsd: genCost,
            EvaluatorCostUsd: evalCost,
            TotalCostUsd: genCost + evalCost,
            GeneratorInputTokens: genIn,
            GeneratorOutputTokens: genOut,
            EvaluatorInputTokens: evalIn,
            EvaluatorOutputTokens: evalOut,
            CrewDailyBudgetUsd: dailyBudget,
            CrewSpendTodayUsd: spendToday,
            MaxTaskBudgetUsd: maxTaskBudget,
            AutoDispatchedAt: wi?.AutoDispatchedAt,
            ExpectedFileChanges: wi?.ExpectedFileChanges,
            Deliverables: deliverables,
            RecentTranscript: transcript,
            Worktree: worktree,
            ExecutionStatus: wi?.ExecutionStatus ?? ExecutionStatuses.Idle,
            TrialCount: wi?.TrialCount ?? 0,
            // Default to the points-derived cap (5 per pt) so the live-exec
            // header reads "iter N of {pts*5}" instead of always "of 5".
            // The runtime PATCH override on the queue row still wins.
            MaxIterations: latestQueue?.MaxIterOverride ?? ScoringConstants.MaxIterationsForPoints(wi?.Points ?? 0),
            TranscriptTotalCount: transcriptTotalCount);

        return Results.Ok(snap);
    }

    private static async Task<IResult> GetTranscript(
        string id,
        ForgeDbContext db,
        long? from,
        long? before,
        int? limit,
        string? order,
        CancellationToken ct)
    {
        var size = Math.Clamp(limit ?? 500, 1, 2000);
        var totalCount = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id).CountAsync(ct);
        var query = db.TaskTranscriptEvents.AsNoTracking().Where(e => e.WorkItemId == id);

        var desc = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);
        if (desc)
        {
            if (before is long cur) query = query.Where(e => e.Id < cur);
            var rows = await query.OrderByDescending(e => e.Id).Take(size).ToListAsync(ct);
            rows.Reverse();
            return Results.Ok(new { events = rows.Select(MapTranscript).ToList(), totalCount });
        }
        else
        {
            if (from is long cursor) query = query.Where(e => e.Id > cursor);
            var rows = await query.OrderBy(e => e.Id).Take(size).ToListAsync(ct);
            return Results.Ok(new { events = rows.Select(MapTranscript).ToList(), totalCount });
        }
    }

    private static async Task<IResult> ClearTranscript(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var rows = await db.TaskTranscriptEvents
            .Where(e => e.WorkItemId == id)
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            db.TaskTranscriptEvents.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }

        // Phase 21 — also delete deliverable rows so the iter counter
        // resets to 0 when the live-exec view reloads after a restart.
        var deliverables = await db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        if (deliverables.Count > 0)
        {
            db.Deliverables.RemoveRange(deliverables);
            await db.SaveChangesAsync(ct);
        }

        // Phase 22 — clear AssignedAgent and record cleared-restart.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            var agentIds = Forge.Api.Endpoints.WorkItemsEndpoints.ParseAgentIds(wi.AssignedAgentJson);
            var userId = ctx.Request.Headers.TryGetValue("X-Forge-User", out var uv) ? uv.FirstOrDefault() : null;
            db.AgentAssignments.Add(new AgentAssignmentRow
            {
                WorkItemId = id,
                Kind = AgentAssignmentKinds.ClearedRestart,
                AgentIdsJson = System.Text.Json.JsonSerializer.Serialize(agentIds),
                FromExecutionStatus = wi.ExecutionStatus,
                ToExecutionStatus = ExecutionStatuses.Queued,
                AssignedBy = userId,
                At = DateTimeOffset.UtcNow,
            });
            wi.AssignedAgentJson = null;
            // Reset execution state so the item can be re-queued and re-claimed.
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.TrialCount = 0;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { deleted = rows.Count, deletedDeliverables = deliverables.Count });
    }

    private static Deliverable MapDeliverable(DeliverableRow r)
    {
        Dictionary<string, int> dims = new();
        try { dims = JsonSerializer.Deserialize<Dictionary<string, int>>(r.DimsJson) ?? new(); }
        catch { /* ignore malformed dims */ }
        return new Deliverable(
            r.Id, r.WorkItemId, r.CrewId, r.GeneratorAgentId, r.EvaluatorAgentId,
            r.Iter, r.Score, dims, r.Verdict, r.Notes, r.GenCostUsd, r.EvalCostUsd,
            r.JudgeAgentId, r.JudgeScore, r.JudgeNotes, r.CreatedAt,
            r.GeneratorSummary ?? string.Empty);
    }

    /// <summary>
    /// Per-event payload cap shipped via <c>/execution</c>. The full payload
    /// stays in <c>task_transcript_events</c> and is fetchable per-event via
    /// <c>/transcript</c>; the snapshot only ships a trimmed view so a long
    /// run with many <c>task.prompt</c> events (each carrying a multi-KB
    /// system prompt) doesn't balloon a single response into hundreds of KB.
    /// </summary>
    private const int SnapshotPayloadByteCap = 4096;

    private static TaskTranscriptEvent MapTranscript(TaskTranscriptEventRow r)
    {
        JsonElement payload;
        var raw = string.IsNullOrEmpty(r.PayloadJson) ? "{}" : r.PayloadJson;
        if (raw.Length > SnapshotPayloadByteCap && r.Kind is "task.prompt" or "task.tool-call" or "task.tool-result" or "task.iteration.scored")
        {
            // Replace bulky text fields with truncation markers so the UI
            // still renders the row but doesn't tax the wire.
            raw = TruncateBulkyFields(raw);
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            payload = doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }
        return new TaskTranscriptEvent(r.Id, r.WorkItemId, r.Kind, r.AgentId, r.CrewId, payload, r.At);
    }

    /// <summary>
    /// Strip / abbreviate the bulky string fields the live-exec view never
    /// renders inline (system prompts, full tool results). The full text is
    /// still available via <c>/api/work-items/{id}/transcript?from=…</c>.
    /// </summary>
    private static string TruncateBulkyFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && (prop.NameEquals("systemPrompt") || prop.NameEquals("userPrompt")
                            || prop.NameEquals("text") || prop.NameEquals("result")
                            || prop.NameEquals("notes") || prop.NameEquals("toolPayload")
                            || prop.NameEquals("content") || prop.NameEquals("body")))
                    {
                        var s = prop.Value.GetString() ?? string.Empty;
                        if (s.Length > 320)
                        {
                            w.WriteString(prop.Name, s[..320] + $"…[truncated {s.Length} → 320, GET /transcript/{{id}} for full]");
                            continue;
                        }
                    }
                    prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }

    private static WorktreeSnapshot? ParseWorktreeTick(JsonElement el, DateTimeOffset at)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        string? wsId = el.TryGetProperty("workstreamId", out var w) ? w.GetString() : null;
        string? wtPath = el.TryGetProperty("worktreePath", out var wp) ? wp.GetString() : null;
        int added    = TryGetInt(el, "added", 0);
        int modified = TryGetInt(el, "modified", 0);
        int deleted  = TryGetInt(el, "deleted", 0);
        int ahead  = TryGetInt(el, "ahead", 0);
        int behind = TryGetInt(el, "behind", 0);
        int dirty  = TryGetInt(el, "dirty", added + modified + deleted);
        bool truncated = el.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;

        string? sha = null, msg = null, author = null;
        DateTimeOffset? headAt = null;
        if (el.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
        {
            sha    = head.TryGetProperty("sha", out var s) ? s.GetString() : null;
            msg    = head.TryGetProperty("message", out var m) ? m.GetString() : null;
            author = head.TryGetProperty("author", out var a) ? a.GetString() : null;
            if (head.TryGetProperty("at", out var aa) && aa.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(aa.GetString(), out var dt))
                headAt = dt;
        }

        return new WorktreeSnapshot(
            wsId ?? string.Empty,
            added, modified, deleted,
            ahead, behind, dirty,
            sha, msg, author, headAt,
            truncated, at,
            wtPath);
    }

    private static int TryGetInt(JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return fallback;
    }

    // ── Phase 20 — PATCH /api/work-items/{id}/execution ─────────────────

    private sealed record PatchExecutionBody(int? MaxIterations);

    private static async Task<IResult> PatchExecution(
        string id,
        [FromBody] PatchExecutionBody body,
        ForgeDbContext db,
        CancellationToken ct)
    {
        if (body.MaxIterations is int val && (val < 1 || val > 50))
            return Results.Problem(title: "maxIterations must be between 1 and 50", statusCode: 400);

        // Prefer the active (non-completed) queue row so overrides land on the running task.
        var queueRow = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed")
            .OrderByDescending(q => q.Id)
            .FirstOrDefaultAsync(ct)
            ?? await db.TaskQueue
                .Where(q => q.WorkItemId == id)
                .OrderByDescending(q => q.Id)
                .FirstOrDefaultAsync(ct);

        if (queueRow is null)
            return Results.Problem(title: $"No queue entry for work item {id}", statusCode: 404);

        queueRow.MaxIterOverride = body.MaxIterations;
        queueRow.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { workItemId = id, maxIterations = body.MaxIterations });
    }
}
