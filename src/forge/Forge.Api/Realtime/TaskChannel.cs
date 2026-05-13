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
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
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
                await EvictOverCapAsync(db, workItemId, ct);
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

    private static async Task EvictOverCapAsync(ForgeDbContext db, string workItemId, CancellationToken ct)
    {
        var count = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(db.TaskTranscriptEvents, e => e.WorkItemId == workItemId, ct);
        if (count <= TranscriptCapPerWorkItem) return;

        var toEvict = count - TranscriptCapPerWorkItem;
        // Pull just the ids to evict (oldest first), then delete in one batch.
        var oldIds = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                db.TaskTranscriptEvents
                    .Where(e => e.WorkItemId == workItemId)
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
