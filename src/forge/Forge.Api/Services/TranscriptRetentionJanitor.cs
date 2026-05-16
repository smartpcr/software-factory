// -----------------------------------------------------------------------
// <copyright file="TranscriptRetentionJanitor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// 2026-05-07 — periodic janitor that bounds <c>task_transcript_event</c>
/// growth by retaining only the last K iterations of transcript per work
/// item. <c>deliverables</c> is NEVER touched, so the
/// <c>GeneratorSummary</c> / <c>Notes</c> / <c>DimsJson</c> fields persist
/// for every iter regardless of pruning — that's the canonical per-iter
/// signal the UI's Summary tab pages through.
/// <para>
/// Retention rules (per the operator-stated policy):
/// </para>
/// <list type="number">
///   <item>
///     Work item is in a <b>completed</b> terminal state
///     (<c>WorkItemRow.State == "closed"</c> or
///     <c>WorkItemRow.ExecutionStatus == "completed"</c>): keep events for
///     the LAST iteration only. The deliverable rows still hold the gen +
///     eval summary text for every prior iter.
///   </item>
///   <item>
///     Work item is NOT completed and has more than
///     <see cref="SyncConstants.TranscriptKeepLastItersForOpen"/>
///     iterations: keep events for the last
///     <see cref="SyncConstants.TranscriptKeepLastItersForOpen"/>
///     iterations.
///   </item>
///   <item>
///     Otherwise (open and ≤ keepN iters, or no iter markers): leave the
///     transcript alone. The pre-existing
///     <see cref="Forge.Api.Realtime.TaskChannel"/> 10k-row-per-WI cap
///     still bounds runaway growth within a single iteration.
///   </item>
/// </list>
/// <para>
/// Iter boundaries are derived from <c>task.iteration.started</c>
/// transcript events (their <c>Id</c>s are monotonic, robust to clock skew,
/// and live in the same row space as the rows being deleted). The
/// alternative — <c>deliverables.CreatedAt</c> — is fragile because the
/// deliverable row is persisted BEFORE the iteration's wrap-up events
/// (<c>scored</c> / <c>iterated</c> / <c>passed</c>) are emitted; using
/// the next iter's <c>iteration.started</c> Id avoids that off-by-one.
/// </para>
/// <para>
/// Deletes run in bounded batches
/// (<see cref="SyncConstants.TranscriptPruneBatchSize"/> rows per
/// statement) to keep SQLite write-lock duration small and predictable.
/// </para>
/// <para>
/// VACUUM is NOT run from the periodic tick — on a multi-GB database it
/// can stall writers for tens of seconds. Operators reclaim disk via the
/// admin endpoint <c>POST /api/admin/transcripts/prune?vacuum=true</c>.
/// </para>
/// </summary>
public sealed class TranscriptRetentionJanitor : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<TranscriptRetentionJanitor> logger;

    public TranscriptRetentionJanitor(
        IServiceScopeFactory scopeFactory,
        ILogger<TranscriptRetentionJanitor> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger initial run so service start isn't overloaded.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stats = await this.RunOnceAsync(dryRun: false, vacuum: false, stoppingToken);
                this.logger.LogInformation(
                    "Transcript janitor tick: scanned={Scanned} pruned={Pruned} rowsDeleted={Rows} ms={Ms}",
                    stats.WorkItemsScanned, stats.WorkItemsPruned, stats.RowsDeleted, stats.ElapsedMs);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Transcript janitor tick failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(SyncConstants.TranscriptJanitorIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Run one prune sweep across every work item. Exposed publicly so the
    /// admin endpoint and unit tests can trigger it deterministically.
    /// When <paramref name="dryRun"/> is true, computes cutoffs and the
    /// counts of rows that WOULD be deleted but does not modify the DB.
    /// </summary>
    public async Task<PruneStats> RunOnceAsync(bool dryRun, bool vacuum, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var stats = new PruneStats { DryRun = dryRun };
        var statsLock = new object();

        using var scope = this.scopeFactory.CreateScope();
        // Phase E — fan out per product so each product's transcript table
        // is pruned independently. Before Phase F splits the data, every
        // ProductDbContext binds to forge.db so behavior is identical to
        // the pre-Phase-E single-db scan; after Phase F, each product's
        // transcript lives in its own {slug}.db and this loop is the
        // only path that visits all of them.
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();

        await fanout.ForEachProductAsync(async (db, slug, fct) =>
        {
            await this.PruneOneProductAsync(db, slug, dryRun, vacuum, stats, statsLock, fct);
        }, ct);

        sw.Stop();
        stats.ElapsedMs = sw.ElapsedMilliseconds;
        return stats;
    }

    private async Task PruneOneProductAsync(
        ProductDbContext db,
        string slug,
        bool dryRun,
        bool vacuum,
        PruneStats stats,
        object statsLock,
        CancellationToken ct)
    {
        // Enumerate every WorkItemId that has any transcript event in
        // THIS product. We include WIs without deliverables
        // (failed-before-first-iter, synthetic IDs, malformed runs)
        // so they are pruned the same way as deliverable-bearing ones.
        var workItemIds = await db.TaskTranscriptEvents.AsNoTracking()
            .Select(e => e.WorkItemId)
            .Distinct()
            .ToListAsync(ct);

        lock (statsLock) stats.WorkItemsScanned += workItemIds.Count;

        foreach (var wid in workItemIds)
        {
            ct.ThrowIfCancellationRequested();
            var keepN = await this.ResolveKeepNAsync(db, wid, ct);

            // task.iteration.started events demarcate iteration boundaries.
            var iterStartIds = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == wid && e.Kind == "task.iteration.started")
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .ToListAsync(ct);

            if (iterStartIds.Count <= keepN) continue;
            var cutoffId = iterStartIds[iterStartIds.Count - keepN];

            var deleteCount = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == wid && e.Id < cutoffId)
                .CountAsync(ct);
            if (deleteCount == 0) continue;

            if (dryRun)
            {
                lock (statsLock)
                {
                    stats.WorkItemsPruned++;
                    stats.RowsDeleted += deleteCount;
                }
                continue;
            }

            // Bounded batched delete to keep SQLite write-lock duration small.
            var deletedTotal = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"DELETE FROM task_transcript_event
                       WHERE Id IN (
                           SELECT Id FROM task_transcript_event
                           WHERE WorkItemId = {wid} AND Id < {cutoffId}
                           ORDER BY Id
                           LIMIT {SyncConstants.TranscriptPruneBatchSize}
                       )",
                    ct);
                if (deleted == 0) break;
                deletedTotal += deleted;
            }

            if (deletedTotal > 0)
            {
                lock (statsLock)
                {
                    stats.WorkItemsPruned++;
                    stats.RowsDeleted += deletedTotal;
                }
            }
        }

        if (vacuum && !dryRun)
        {
            // Per-product VACUUM — only when caller opted in.
            int rowsForThisProduct;
            lock (statsLock) rowsForThisProduct = stats.RowsDeleted;
            if (rowsForThisProduct > 0)
            {
                lock (statsLock) stats.VacuumRan = true;
                await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
            }
        }
    }

    /// <summary>
    /// Decide the per-WI retention window. Closed/completed → 1 iter;
    /// every other state → <see cref="SyncConstants.TranscriptKeepLastItersForOpen"/>.
    /// Failed/cancelled deliberately use the open-task window because the
    /// operator may inspect them or even resume them later.
    /// </summary>
    private async Task<int> ResolveKeepNAsync(ProductDbContext db, string workItemId, CancellationToken ct)
    {
        var wi = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.State, w.ExecutionStatus })
            .FirstOrDefaultAsync(ct);
        if (wi is null) return SyncConstants.TranscriptKeepLastItersForOpen;

        var isCompleted =
            string.Equals(wi.State, "closed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(wi.ExecutionStatus, ExecutionStatuses.Completed, StringComparison.OrdinalIgnoreCase);
        return isCompleted
            ? SyncConstants.TranscriptKeepLastItersForClosed
            : SyncConstants.TranscriptKeepLastItersForOpen;
    }
}

/// <summary>
/// Result of one transcript-prune sweep — surfaced by the admin endpoint
/// and consumed by tests.
/// </summary>
public sealed record PruneStats
{
    public bool DryRun { get; init; }
    public int WorkItemsScanned { get; set; }
    public int WorkItemsPruned { get; set; }
    public int RowsDeleted { get; set; }
    public bool VacuumRan { get; set; }
    public long ElapsedMs { get; set; }
}
