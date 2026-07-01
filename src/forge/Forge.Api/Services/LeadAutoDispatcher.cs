// -----------------------------------------------------------------------
// <copyright file="LeadAutoDispatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Phase 16.3 — when a work item lands on a team-lead agent's queue,
/// auto-dispatch it to a member of the lead's crew within ~10 s. Honors the
/// schedule gate (off-shift work parks until next shift open) and the
/// crew's <c>MaxConcurrent</c> capacity.
/// <para>
/// Operation: every 5 s ±1 s, query <c>work_items</c> WHERE the assignee is
/// a known team-lead AND <c>auto_dispatched_at IS NULL</c> AND state ∈
/// (proposed, active). For each match: pick a generator crew (the lead's
/// crew, or its pair-generator side), pick an available member by
/// round-robin, write a <c>forge_actions(kind='auto-dispatched')</c> audit
/// row, set <c>auto_dispatched_at</c>, and emit <c>task.delegated</c> on
/// the per-work-item live channel.
/// </para>
/// <para>
/// After stamping the row, the dispatcher enqueues the work item into the
/// Phase 17 task queue via <see cref="ITaskQueueService"/>. The
/// <see cref="CrewQueueWorker"/> picks it up from there and starts the
/// agent runner. <c>AutoDispatchedAt</c> is only stamped after a
/// successful enqueue so the row is never orphaned.
/// </para>
/// </summary>
public sealed class LeadAutoDispatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly TaskChannel channel;
    private readonly ILogger<LeadAutoDispatcher> logger;

    /// <summary>
    /// Phase 16.15 — dispatch latency histogram (ms). Tagged with
    /// <c>crewId</c>, <c>pair</c> (bool), <c>outcome</c> ∈
    /// dispatched | capacity-blocked | off-shift | no-crew.
    /// </summary>
    public static readonly Meter Meter = new("Forge.Api.Phase16", "1.0");
    private static readonly Histogram<double> LatencyMs =
        Meter.CreateHistogram<double>("forge.dispatch.lead_to_crew.latency_ms", unit: "ms");
    private static readonly Counter<long> Refused =
        Meter.CreateCounter<long>("forge.dispatch.lead_no_capacity");

    private CancellationTokenSource? cts;
    private Task? loop;

    public LeadAutoDispatcher(IServiceScopeFactory scopeFactory, TaskChannel channel, ILogger<LeadAutoDispatcher> logger)
    {
        this.scopeFactory = scopeFactory;
        this.channel = channel;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.loop = Task.Run(() => this.LoopAsync(this.cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.cts?.Cancel();
        if (this.loop is not null)
        {
            try { await this.loop; } catch { /* swallow shutdown */ }
        }
    }

    public void Dispose() => this.cts?.Dispose();

    /// <summary>
    /// Run a single dispatch tick. Public so tests can drive deterministic
    /// scenarios without spawning the timer.
    /// </summary>
    public async Task<DispatchTickResult> RunOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        // Phase G — resolve control DB once at scope level for cross-context reads.
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        // Phase E — fan out per product. The dispatcher's work-item scan
        // is product-scoped; crews are control-side but ProductDbContext
        // also exposes them (Phase D.2 interim compromise).
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
        var totalDispatched = 0;
        var totalSkipped = 0;
        var totalCapacityBlocked = 0;
        var totalOffShift = 0;
        var lockObj = new object();

        await fanout.ForEachProductAsync(async (control, db, slug, fct) =>
        {
            var perProduct = await this.DispatchForProductAsync(control, db, scope, fct);
            lock (lockObj)
            {
                totalDispatched += perProduct.Dispatched;
                totalSkipped += perProduct.Skipped;
                totalCapacityBlocked += perProduct.CapacityBlocked;
                totalOffShift += perProduct.OffShift;
            }
        }, ct);

        return new DispatchTickResult(totalDispatched, totalCapacityBlocked, totalOffShift, totalSkipped);
    }

    private async Task<DispatchTickResult> DispatchForProductAsync(
        ControlDbContext control, ProductDbContext db, IServiceScope scope, CancellationToken ct)
    {
        var dispatched = 0;
        var skipped = 0;
        var capacityBlocked = 0;
        var offShift = 0;

        // Enqueue through a slug-scoped child scope so the task queue's
        // ProductDbContext binds to THIS product's {slug}.db. This method runs
        // inside the ProductFanout callback where the AsyncLocal product slug is
        // active, so a fresh scope resolves correctly; the outer request scope
        // (`scope` param) is NOT slug-bound and would strand rows in the control
        // DB — never swept by ProductFanout (2026-06-30 fix; see the guard in
        // TaskQueueService.EnqueueAsync).
        using var qScope = this.scopeFactory.CreateScope();
        var taskQueue = qScope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        // 1. Build the team-lead universe. A "team lead" for Phase 16 is an
        //    agent set as a crew's Lead (rather than role='tech-lead', which
        //    is human-side). We look up by crew.Lead so the dispatcher works
        //    even if the operator hasn't backfilled role on every agent.
        var crewsByLead = await control.Crews.AsNoTracking()
            .Where(c => c.Lead != null && c.Lead != "")
            .ToDictionaryAsync(c => c.Lead!, ct);

        if (crewsByLead.Count == 0) return new DispatchTickResult(0, 0, 0, 0);

        // 2. Find candidate work items.
        //    SQLite EF can't filter on a deserialized JSON field, so pull the
        //    candidate slice (state in proposed|active, no auto-dispatched-at
        //    yet) and resolve assigned-to client-side. Order client-side too
        //    because SQLite EF can't ORDER BY DateTimeOffset.
        var candidates = await db.WorkItems
            .Where(w => w.AutoDispatchedAt == null
                     && (w.State == "proposed" || w.State == "active")
                     && w.AssignedToJson != null)
            .ToListAsync(ct);
        candidates = candidates.OrderBy(w => w.UpdatedAt).Take(50).ToList();

        foreach (var wi in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string? leadId = TryGetAssigneeId(wi.AssignedToJson);
            CrewRow? leadCrew = null;

            if (leadId is not null)
            {
                crewsByLead.TryGetValue(leadId, out leadCrew);
            }

            // Crew-assigned items (from the Assign endpoint) store
            // {"crewId":"...", "agentId":"..."} — resolve the crew's lead.
            if (leadCrew is null)
            {
                var crewId = TryGetCrewId(wi.AssignedToJson);
                if (crewId is not null)
                {
                    leadCrew = crewsByLead.Values.FirstOrDefault(c => c.Id == crewId);
                }
            }

            if (leadCrew is null) continue;

            // 3. Resolve generator + evaluator crews. If the lead's crew is
            //    a generator-side of a pair, evaluator = paired peer. If the
            //    lead's crew is itself an evaluator-side, the work item was
            //    routed to the wrong crew — log + skip rather than auto-route.
            CrewRow generatorCrew;
            CrewRow? evaluatorCrew = null;
            if (string.Equals(leadCrew.PairRole, "evaluator", StringComparison.OrdinalIgnoreCase))
            {
                this.logger.LogInformation(
                    "Skipping auto-dispatch of {Wid}: assigned to evaluator-side lead {Lead}",
                    wi.Id, leadId);
                skipped++;
                continue;
            }
            generatorCrew = leadCrew;
            if (!string.IsNullOrEmpty(leadCrew.PairedWithCrewId))
            {
                evaluatorCrew = await control.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == leadCrew.PairedWithCrewId, ct);
            }

            // 4. Schedule gate (Phase 14.1 / Q14.1) — off-shift parks the row.
            //    Phase 18.6 (Q18.3) — evaluate the lead specifically, not the
            //    crew as a whole. A crew with a 24×7 lead is always
            //    dispatchable in the routing sense (the lead can pick up); the
            //    gate now refuses only when the lead is also gone (null).
            var sched = ScheduleGate.EvaluateAgent(generatorCrew, generatorCrew.Lead, DateTimeOffset.UtcNow);
            if (sched.Verdict == ScheduleGate.Verdict.OffShift)
            {
                LatencyMs.Record(Elapsed(wi), KeyVal("crewId", generatorCrew.Id), KeyVal("pair", evaluatorCrew is not null), KeyVal("outcome", "off-shift"));
                offShift++;
                continue;
            }

            // 5. Capacity pre-check — skip if the crew is at MaxConcurrent.
            //    The queue worker also checks capacity, but this avoids flooding
            //    the queue with items that can't be picked up yet.
            if (!await HasCapacityAsync(control, db, generatorCrew, ct))
            {
                Refused.Add(1, KeyVal("crewId", generatorCrew.Id));
                LatencyMs.Record(Elapsed(wi), KeyVal("crewId", generatorCrew.Id), KeyVal("pair", evaluatorCrew is not null), KeyVal("outcome", "capacity-blocked"));
                capacityBlocked++;
                continue;
            }

            // 6. Enqueue to Phase 17 task queue → CrewQueueWorker picks it up
            //    and starts the agent runner. AutoDispatchedAt is stamped only
            //    AFTER a successful enqueue so the row is never orphaned.
            //    `taskQueue` is the slug-scoped instance resolved at the top of
            //    this method (binds to this product's {slug}.db).
            TaskQueueRow? queueRow;
            try
            {
                queueRow = await taskQueue.EnqueueAsync(wi.Id, "crew", generatorCrew.Id, ct);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to enqueue {Wid} to crew {Crew}", wi.Id, generatorCrew.Id);
                skipped++;
                continue;
            }

            // 7. Stamp + audit + emit. Pull the tracked row to update.
            var tracked = await db.WorkItems.FirstAsync(w => w.Id == wi.Id, ct);
            tracked.AutoDispatchedAt = DateTimeOffset.UtcNow;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            control.Audit.Add(new AuditRow
            {
                At = DateTimeOffset.UtcNow,
                User = leadId,
                Method = "AUTO",
                Path = $"/api/crews/{generatorCrew.Id}/auto-dispatched",
                Status = 200,
                IdempotencyKey = null,
                RequestBodySnippet = JsonSerializer.Serialize(new
                {
                    workItemId = wi.Id,
                    leadId,
                    generatorCrewId = generatorCrew.Id,
                    evaluatorCrewId = evaluatorCrew?.Id,
                    queueRowId = queueRow?.Id,
                }),
                Supervising = false,
            });
            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);

            await this.channel.EmitAsync(
                wi.Id,
                "task.delegated",
                new
                {
                    leadId,
                    generatorCrewId = generatorCrew.Id,
                    evaluatorCrewId = evaluatorCrew?.Id,
                    paired = evaluatorCrew is not null,
                    viaQueue = true,
                },
                crewId: generatorCrew.Id,
                ct: ct);

            LatencyMs.Record(Elapsed(wi), KeyVal("crewId", generatorCrew.Id), KeyVal("pair", evaluatorCrew is not null), KeyVal("outcome", "dispatched"));
            dispatched++;
        }

        return new DispatchTickResult(dispatched, capacityBlocked, offShift, skipped);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var jitter = new Random();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await this.RunOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "LeadAutoDispatcher tick errored");
            }

            // ±1 s jitter so multiple instances don't synchronise.
            var delay = PollInterval + TimeSpan.FromMilliseconds(jitter.Next(-1000, 1000));
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Returns true when the crew has at least one non-lead member AND the
    /// in-flight count is below <see cref="CrewRow.MaxConcurrent"/>.
    /// </summary>
    private async Task<bool> HasCapacityAsync(ControlDbContext control, ProductDbContext db, CrewRow crew, CancellationToken ct)
    {
        var memberCount = await control.CrewMembers.AsNoTracking()
            .CountAsync(m => m.CrewId == crew.Id && m.AgentId != crew.Lead, ct);
        if (memberCount == 0) return false;

        var inFlight = await db.Deliverables.AsNoTracking()
            .Where(d => d.CrewId == crew.Id)
            .Join(db.WorkItems.AsNoTracking(),
                  d => d.WorkItemId,
                  w => w.Id,
                  (d, w) => new { w.State })
            .CountAsync(x => x.State == "proposed" || x.State == "active", ct);
        return inFlight < crew.MaxConcurrent;
    }

    /// <summary>Best-effort assignee.id extraction from the JSON blob.</summary>
    public static string? TryGetAssigneeId(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            // Honor camelCase + PascalCase serialisation alike.
            if (doc.RootElement.TryGetProperty("id", out var idCamel)) return idCamel.GetString();
            if (doc.RootElement.TryGetProperty("Id", out var idPascal)) return idPascal.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extract crewId from a crew-assigned JSON blob.</summary>
    public static string? TryGetCrewId(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("crewId", out var v)) return v.GetString();
            if (doc.RootElement.TryGetProperty("CrewId", out var v2)) return v2.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static double Elapsed(WorkItemRow wi)
    {
        // The "lead-to-crew" SLO measures the time the row spent assigned to
        // the lead before we picked it up. We approximate it as
        // (now - UpdatedAt) since the assignment write bumped UpdatedAt.
        var d = DateTimeOffset.UtcNow - wi.UpdatedAt;
        return Math.Max(0, d.TotalMilliseconds);
    }

    private static KeyValuePair<string, object?> KeyVal(string k, object? v) => new(k, v);

    /// <summary>Counts emitted by one tick. Used by tests + the meter.</summary>
    public sealed record DispatchTickResult(int Dispatched, int CapacityBlocked, int OffShift, int Skipped);
}
