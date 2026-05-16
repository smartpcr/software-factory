// -----------------------------------------------------------------------
// <copyright file="TranscriptRetentionJanitorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-07 — tests for <see cref="TranscriptRetentionJanitor"/>.
/// Covers the operator-stated retention policy:
/// <list type="bullet">
///   <item>Closed/completed WI → keep last 1 iter only.</item>
///   <item>Open WI with > 10 iters → keep last 10.</item>
///   <item>Open WI with ≤ 10 iters → keep all.</item>
/// </list>
/// Plus the cutoff-by-iteration-start-id rule, the deliverables-untouched
/// invariant, and the dry-run path.
/// </summary>
public class TranscriptRetentionJanitorTests
{
    private const string ItStartedKind = "task.iteration.started";
    private const string ToolResultKind = "task.toolresult";

    /// <summary>
    /// Helper — seed K iterations of transcript events for a single WI.
    /// Each iteration is one task.iteration.started followed by N
    /// task.toolresult rows. Returns the iteration.started Ids in order
    /// so tests can assert which boundary the cutoff hit.
    /// </summary>
    private static async Task<List<long>> SeedItersAsync(ProductDbContext db, string wid, int iterCount, int eventsPerIter, DateTimeOffset baseAt)
    {
        var iterStartIds = new List<long>();
        for (var iter = 1; iter <= iterCount; iter++)
        {
            var startedAt = baseAt.AddMinutes(iter * 10);
            var startRow = new TaskTranscriptEventRow
            {
                WorkItemId = wid,
                Kind = ItStartedKind,
                PayloadJson = $"{{\"iter\":{iter}}}",
                At = startedAt,
            };
            db.TaskTranscriptEvents.Add(startRow);
            await db.SaveChangesAsync();
            iterStartIds.Add(startRow.Id);
            for (var k = 0; k < eventsPerIter; k++)
            {
                db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
                {
                    WorkItemId = wid,
                    Kind = ToolResultKind,
                    PayloadJson = $"{{\"iter\":{iter},\"k\":{k}}}",
                    At = startedAt.AddSeconds(k + 1),
                });
            }
            await db.SaveChangesAsync();
        }
        return iterStartIds;
    }

    private static async Task SeedDeliverablesAsync(ProductDbContext db, string wid, int count)
    {
        for (var iter = 1; iter <= count; iter++)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = wid,
                CrewId = "crew-x",
                GeneratorAgentId = "G",
                EvaluatorAgentId = "E",
                Iter = iter,
                Score = 70 + iter,
                DimsJson = "{}",
                Verdict = "iterate",
                Notes = $"summary for iter {iter}",
                GeneratorSummary = $"gen-{iter}",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10 * (count - iter)),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Closed_WorkItem_Keeps_Only_Last_Iteration_Of_Transcript()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "wi-closed-multi-iter";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wid, Type = "task", Track = "dev", Title = "Closed",
                State = "closed", ExecutionStatus = ExecutionStatuses.Completed,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, wid, iterCount: 5, eventsPerIter: 4, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
            await SeedDeliverablesAsync(db, wid, 5);
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        Assert.Equal(1, stats.WorkItemsPruned);
        // 4 dropped iters × (1 iter-started + 4 toolresult) = 20 rows deleted.
        Assert.Equal(20, stats.RowsDeleted);

        using var verify = f.Services.CreateScope();
        var dbv = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var remainingByKind = await dbv.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == wid)
            .GroupBy(e => e.Kind).Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();
        var startCount = remainingByKind.FirstOrDefault(x => x.Key == ItStartedKind)?.Count ?? 0;
        var resultCount = remainingByKind.FirstOrDefault(x => x.Key == ToolResultKind)?.Count ?? 0;
        Assert.Equal(1, startCount);
        Assert.Equal(4, resultCount);

        // Deliverables MUST be untouched — they hold the per-iter
        // GeneratorSummary + Notes the operator wants preserved.
        var deliverables = await dbv.Deliverables.AsNoTracking().Where(d => d.WorkItemId == wid).ToListAsync();
        Assert.Equal(5, deliverables.Count);
        Assert.All(deliverables, d => Assert.False(string.IsNullOrEmpty(d.GeneratorSummary)));
        Assert.All(deliverables, d => Assert.False(string.IsNullOrEmpty(d.Notes)));
    }

    [Fact]
    public async Task Open_WorkItem_Over_Ten_Iters_Keeps_Last_Ten()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "wi-open-fifteen";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wid, Type = "task", Track = "dev", Title = "Open",
                State = "active", ExecutionStatus = ExecutionStatuses.Live,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, wid, iterCount: 15, eventsPerIter: 3, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
            await SeedDeliverablesAsync(db, wid, 15);
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        Assert.Equal(1, stats.WorkItemsPruned);
        // 5 dropped iters × (1 iter-started + 3 toolresult) = 20 rows deleted.
        Assert.Equal(20, stats.RowsDeleted);

        using var verify = f.Services.CreateScope();
        var dbv = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var startedRows = await dbv.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == wid && e.Kind == ItStartedKind)
            .OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(10, startedRows.Count);
    }

    [Fact]
    public async Task Open_WorkItem_With_Five_Iters_Is_Not_Pruned()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "wi-open-five";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wid, Type = "task", Track = "dev", Title = "Small open",
                State = "active", ExecutionStatus = ExecutionStatuses.Live,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, wid, iterCount: 5, eventsPerIter: 3, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        Assert.Equal(0, stats.WorkItemsPruned);
        Assert.Equal(0, stats.RowsDeleted);
    }

    [Fact]
    public async Task Closed_WorkItem_With_One_Iter_Is_Not_Pruned()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "wi-closed-one";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wid, Type = "task", Track = "dev", Title = "Closed-one",
                State = "closed", ExecutionStatus = ExecutionStatuses.Completed,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, wid, iterCount: 1, eventsPerIter: 3, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        Assert.Equal(0, stats.WorkItemsPruned);
        Assert.Equal(0, stats.RowsDeleted);
    }

    [Fact]
    public async Task Failed_And_Cancelled_Use_Open_Retention_Window()
    {
        // Failed/cancelled tasks aren't "complete" in the user's sense —
        // operators may inspect/resume them, and the deliverables row's
        // GeneratorSummary alone can be insufficient for failure
        // forensics. Use the 10-iter window so the failing iteration
        // tail stays inspectable.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string failedWid = "wi-failed-twelve";
        const string cancelledWid = "wi-cancelled-twelve";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = failedWid, Type = "task", Track = "dev", Title = "Failed",
                State = "active", ExecutionStatus = ExecutionStatuses.Failed,
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = cancelledWid, Type = "task", Track = "dev", Title = "Cancelled",
                State = "active", ExecutionStatus = ExecutionStatuses.Cancelled,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, failedWid, iterCount: 12, eventsPerIter: 2, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
            await SeedItersAsync(db, cancelledWid, iterCount: 12, eventsPerIter: 2, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        // 2 dropped iters × (1 + 2) = 6 rows per WI, 2 WIs total = 12 rows.
        Assert.Equal(2, stats.WorkItemsPruned);
        Assert.Equal(12, stats.RowsDeleted);
    }

    [Fact]
    public async Task Dry_Run_Counts_Without_Deleting()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "wi-dryrun-twelve";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wid, Type = "task", Track = "dev", Title = "Dry-run",
                State = "active", ExecutionStatus = ExecutionStatuses.Live,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, wid, iterCount: 12, eventsPerIter: 5, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: true, vacuum: false, default);

        Assert.True(stats.DryRun);
        Assert.Equal(1, stats.WorkItemsPruned);
        // 2 iters × (1 + 5) = 12 rows would be deleted.
        Assert.Equal(12, stats.RowsDeleted);

        using var verify = f.Services.CreateScope();
        var dbv = verify.ServiceProvider.GetRequiredService<ProductDbContext>();
        var remaining = await dbv.TaskTranscriptEvents.AsNoTracking()
            .CountAsync(e => e.WorkItemId == wid);
        Assert.Equal(12 * (1 + 5), remaining);
    }

    [Fact]
    public async Task WorkItem_Without_DbRow_Treated_As_Open()
    {
        // Synthetic / orphaned WorkItemIds (e.g. plan-doc:spawn-… with no
        // WorkItemRow yet) can still accumulate transcript. They should
        // be pruned with the open-task window — otherwise they bloat the
        // table forever.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "plan-doc:orphan-no-row";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            await SeedItersAsync(db, wid, iterCount: 12, eventsPerIter: 1, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var stats = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        // 2 dropped iters × (1 + 1) = 4 rows.
        Assert.Equal(1, stats.WorkItemsPruned);
        Assert.Equal(4, stats.RowsDeleted);
    }

    [Fact]
    public async Task Idempotent_Second_Run_Deletes_Nothing()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        const string wid = "wi-idempotent";
        using (var seed = f.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wid, Type = "task", Track = "dev", Title = "Idempotent",
                State = "active", ExecutionStatus = ExecutionStatuses.Live,
            });
            await db.SaveChangesAsync();
            await SeedItersAsync(db, wid, iterCount: 13, eventsPerIter: 2, baseAt: DateTimeOffset.UtcNow.AddDays(-1));
        }

        var janitor = f.Services.GetRequiredService<TranscriptRetentionJanitor>();
        var first = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);
        var second = await janitor.RunOnceAsync(dryRun: false, vacuum: false, default);

        Assert.True(first.RowsDeleted > 0);
        Assert.Equal(0, second.RowsDeleted);
    }
}
