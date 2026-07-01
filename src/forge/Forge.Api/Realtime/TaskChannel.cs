// -----------------------------------------------------------------------
// <copyright file="TaskChannel.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Realtime;

using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Phase 16.5 / 16.7 — per-work-item live channel. Aggregates the
/// generator's, evaluator's, and (when present) judge's events under a
/// single SignalR group so a UI subscriber sees one ordered transcript
/// instead of stitching three per-agent feeds together.
/// <para>
/// Clients join via the existing <c>ForgeHub.Subscribe("task.{wid}")</c>
/// invocation and leave via <c>Unsubscribe("task.{wid}")</c> — this class
/// is the server-side fan-out + persistence helper that both the
/// dispatcher and the deliverable loop call.
/// </para>
/// </summary>
public sealed class TaskChannel
{
    private const int TranscriptCapPerWorkItem = 10_000;

    /// <summary>
    /// Kinds that are NEVER evicted by the per-work-item cap, and which the
    /// snapshot endpoint pins so they survive its recent-500 window. These
    /// are the sparse, high-value rows the UI depends on being present
    /// regardless of how many token/tool events pile up around them — the
    /// prompt bodies (Overview + Conversation panes), the iteration-boundary
    /// markers (iter pager), and the build/test gate results (Build tab). A
    /// single tool-heavy generator iteration can emit &gt;10 000 token/tool
    /// events on its own; evicting oldest-first would then delete THIS
    /// iteration's <c>task.prompt</c> (the first row of the iter) mid-run,
    /// leaving the Overview pane empty even though the run is live. Excluding
    /// these from eviction keeps
    /// <see cref="Endpoints.TaskExecutionEndpoints"/>'s pinning promise honest
    /// — pinning can only re-surface rows that still exist. The set is tiny
    /// per iteration (≈2 prompts + a few markers/gates), so protecting it
    /// across all iters is bounded.
    /// <para>Single source of truth: <c>TaskExecutionEndpoints</c> references
    /// this same set for its snapshot pinning so the two never drift.</para>
    /// <para>Typed as a concrete <see cref="HashSet{T}"/> (not an interface)
    /// so EF Core reliably translates <c>NeverEvictKinds.Contains(e.Kind)</c>
    /// to a SQL <c>IN</c> at both query sites. Treat as immutable — do not
    /// mutate after static init.</para>
    /// </summary>
    public static readonly HashSet<string> NeverEvictKinds = new(StringComparer.Ordinal)
    {
        "task.prompt", "task.delegated",
        "task.iteration.started", "task.iteration.scored",
        "task.iteration.passed", "task.iteration.rejected",
        "task.iteration.iterated", "task.iteration.stalled",
        // Build/test gate events — the authoritative pass/fail rows the Build
        // tab (BuildPane.tsx, FORGE_BUILD_EVENT_KINDS in filters.ts) renders.
        // SPARSE (a handful per iter) and payloads are small, so pinning +
        // never-evicting every gate across all iters is cheap. (Observed
        // 2026-06-01 on repo-scanner: iter-2 `task.test-gate.failed` existed
        // in the full transcript but the Build tab only showed iter-1's gates
        // because iter-2/3 had scrolled past the recent-500 window.)
        "task.build-gate.passed", "task.build-gate.failed", "task.build-gate.skipped",
        "task.build-failed.detected", "task.build-failed.aborted",
        "task.test-gate.passed", "task.test-gate.failed", "task.test-gate.skipped",
    };

    /// <summary>
    /// SignalR event name. Single name with a typed payload so clients
    /// only need one subscription per work item — the <c>kind</c> field on
    /// the payload distinguishes between iteration / token / tool /
    /// commit / worktree-tick events.
    /// </summary>
    public const string MethodName = "task.event";

    private readonly IHubContext<ForgeHub> hub;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<TaskChannel> logger;

    public TaskChannel(IHubContext<ForgeHub> hub, IServiceScopeFactory scopeFactory, ILogger<TaskChannel> logger)
    {
        this.hub = hub;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    /// <summary>SignalR group name for a work item's live channel.</summary>
    public static string GroupName(string workItemId) => $"task.{workItemId}";

    /// <summary>
    /// Persist a transcript event AND broadcast it to the matching SignalR
    /// group. Persistence is best-effort: a DB write failure should never
    /// stop the live tail (transient SQLite locks happen). The cap (10 000
    /// rows per work-item) is enforced lazily — every 100th write checks.
    /// </summary>
    public async Task EmitAsync(
        string workItemId,
        string kind,
        object? payload = null,
        string? agentId = null,
        string? crewId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return;

        var at = DateTimeOffset.UtcNow;
        var json = payload is null ? "{}" : JsonSerializer.Serialize(payload);

        // Persist (best-effort). The scope is freshly created so the helper
        // can be called from background services that don't already have one.
        // Phase 56 follow-up (operator-reported 2026-05-13 "loading full prompt
        // never resolves" on qq:MICROSOFT-TEAMS-MESS) -- capture the persisted
        // row id and ship it on the SignalR broadcast so the live store can
        // address the row directly via GET /api/work-items/{wi}/transcript/
        // {eventId:long}. Pre-fix the broadcast omitted id, the client
        // synthesised one from Date.now()+Math.random() (a float), and the
        // per-event endpoint's :long route constraint always 404'd.
        long persistedId = 0;
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = new TaskTranscriptEventRow
            {
                WorkItemId = workItemId,
                Kind = kind,
                AgentId = agentId,
                CrewId = crewId,
                PayloadJson = json,
                At = at,
            };
            db.TaskTranscriptEvents.Add(row);
            await db.SaveChangesAsync(ct);
            persistedId = row.Id;

            // Lazy cap eviction. We only check every ~100th write so the
            // common case is one INSERT, no DELETE.
            if (Random.Shared.Next(100) == 0)
            {
                await EvictOverCapAsync(db, workItemId, TranscriptCapPerWorkItem, ct);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "TaskTranscript persist failed for {WorkItemId}", workItemId);
        }

        // Broadcast. `id` is 0 when the persistence above threw -- the client
        // treats that as "not addressable" and skips the per-event refetch.
        try
        {
            await this.hub.Clients.Group(GroupName(workItemId)).SendAsync(
                MethodName,
                new
                {
                    id = persistedId,
                    workItemId,
                    kind,
                    agentId,
                    crewId,
                    payload,
                    at,
                },
                ct);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "TaskChannel broadcast failed for {WorkItemId}", workItemId);
        }
    }

    /// <summary>
    /// Evict oldest evictable rows for a work item once its transcript
    /// exceeds <paramref name="cap"/>. <see cref="NeverEvictKinds"/> are never
    /// deleted. Public + <paramref name="cap"/>-parameterised (mirrors the
    /// codebase's public-test-seam convention, e.g.
    /// <c>ChildProcessEnv.CaptureBaseline</c>) so tests can drive it
    /// deterministically with a small cap instead of seeding 10 000+ rows and
    /// relying on the probabilistic trigger.
    /// </summary>
    public static async Task EvictOverCapAsync(ProductDbContext db, string workItemId, int cap, CancellationToken ct)
    {
        var count = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(db.TaskTranscriptEvents, e => e.WorkItemId == workItemId, ct);
        if (count <= cap) return;

        var toEvict = count - cap;
        // Pull just the ids to evict (oldest first), then delete in one batch.
        // Protected kinds (prompts / iteration markers / gate results) are
        // excluded so they survive regardless of surrounding token/tool
        // volume — see NeverEvictKinds. Only the bulky, replaceable rows
        // (tokens, tool-calls, worktree-ticks) are candidates for eviction.
        var oldIds = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                db.TaskTranscriptEvents
                    .Where(e => e.WorkItemId == workItemId && !NeverEvictKinds.Contains(e.Kind))
                    .OrderBy(e => e.Id)
                    .Take(toEvict)
                    .Select(e => e.Id),
                ct);
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(db.TaskTranscriptEvents.Where(e => oldIds.Contains(e.Id)), ct);
        db.TaskTranscriptEvents.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }
}
