// -----------------------------------------------------------------------
// <copyright file="LeadAutoDispatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;
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
/// The dispatcher itself does not execute the deliverable loop — it just
/// hands the work item off. The Phase 14.4 <c>IDeliverableLoop</c> picks
/// up from there when the plan/code endpoints are wired (Phase 13.4 / 13.5
/// stubs become real). Until then the audit row + delegated event are the
/// observable side-effect.
/// </para>
/// </summary>
public sealed class LeadAutoDispatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly TaskChannel channel;
    private readonly ILogger<LeadAutoDispatcher> logger;

    /// <summary>Per-crew round-robin cursor for member selection.</summary>
    private readonly ConcurrentDictionary<string, int> rrCursors = new();

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
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var dispatched = 0;
        var skipped = 0;
        var capacityBlocked = 0;
        var offShift = 0;

        // 1. Build the team-lead universe. A "team lead" for Phase 16 is an
        //    agent set as a crew's Lead (rather than role='tech-lead', which
        //    is human-side). We look up by crew.Lead so the dispatcher works
        //    even if the operator hasn't backfilled role on every agent.
        var crewsByLead = await db.Crews.AsNoTracking()
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
            if (leadId is null) continue;
            if (!crewsByLead.TryGetValue(leadId, out var leadCrew)) continue;

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
                evaluatorCrew = await db.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == leadCrew.PairedWithCrewId, ct);
            }

            // 4. Schedule gate (Phase 14.1 / Q14.1) — off-shift parks the row.
            var sched = ScheduleGate.Evaluate(generatorCrew, DateTimeOffset.UtcNow);
            if (sched.Verdict == ScheduleGate.Verdict.OffShift)
            {
                LatencyMs.Record(Elapsed(wi), KeyVal("crewId", generatorCrew.Id), KeyVal("pair", evaluatorCrew is not null), KeyVal("outcome", "off-shift"));
                offShift++;
                continue;
            }

            // 5. Capacity check — refuse if the crew has no idle members.
            var member = await PickMemberAsync(db, generatorCrew, ct);
            if (member is null)
            {
                Refused.Add(1, KeyVal("crewId", generatorCrew.Id));
                LatencyMs.Record(Elapsed(wi), KeyVal("crewId", generatorCrew.Id), KeyVal("pair", evaluatorCrew is not null), KeyVal("outcome", "capacity-blocked"));
                capacityBlocked++;
                continue;
            }

            // 6. Stamp + audit + emit. Pull the tracked row to update.
            var tracked = await db.WorkItems.FirstAsync(w => w.Id == wi.Id, ct);
            tracked.AutoDispatchedAt = DateTimeOffset.UtcNow;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            db.Audit.Add(new AuditRow
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
                    memberAgentId = member,
                }),
                Supervising = false,
            });
            await db.SaveChangesAsync(ct);

            await this.channel.EmitAsync(
                wi.Id,
                "task.delegated",
                new
                {
                    leadId,
                    generatorCrewId = generatorCrew.Id,
                    evaluatorCrewId = evaluatorCrew?.Id,
                    memberAgentId = member,
                    paired = evaluatorCrew is not null,
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
    /// Round-robin pick across the crew's members; skip the lead (per Q16.7
    /// the lead routes, doesn't generate). Returns null when the crew is at
    /// or above its <see cref="CrewRow.MaxConcurrent"/> cap, computed as
    /// "members currently working".
    /// </summary>
    private async Task<string?> PickMemberAsync(ForgeDbContext db, CrewRow crew, CancellationToken ct)
    {
        // Members are crew_members rows + (if not in members) the lead.
        // Phase 16.3 excludes the lead from the round-robin pool.
        var members = await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == crew.Id && m.AgentId != crew.Lead)
            .Select(m => m.AgentId)
            .ToListAsync(ct);
        if (members.Count == 0) return null;

        // Capacity = MaxConcurrent. We approximate "currently working" by
        // counting deliverables whose work item is still proposed/active.
        var inFlight = await db.Deliverables.AsNoTracking()
            .Where(d => d.CrewId == crew.Id)
            .Join(db.WorkItems.AsNoTracking(),
                  d => d.WorkItemId,
                  w => w.Id,
                  (d, w) => new { w.State })
            .CountAsync(x => x.State == "proposed" || x.State == "active", ct);
        if (inFlight >= crew.MaxConcurrent) return null;

        // Cursor walks the list; ties broken by lastActiveAt (we don't track
        // last-active per-agent at v0.1, so the round-robin is the only
        // discriminator — close enough for the dispatcher's purposes).
        var seen = this.rrCursors.GetOrAdd(crew.Id, _ => 0);
        var pick = members[((seen % members.Count) + members.Count) % members.Count];
        this.rrCursors[crew.Id] = seen + 1;
        return pick;
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
