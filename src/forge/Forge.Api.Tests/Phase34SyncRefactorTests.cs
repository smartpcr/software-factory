// -----------------------------------------------------------------------
// <copyright file="Phase34SyncRefactorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 34 — sync-strategy refactor coverage:
///  - Per-repo cadence overrides (WI / PR / Build).
///  - Upstream-changedate cursor write-back vs local-clock LastSyncAt.
///  - GitHub initial-window default of 30 days (mirrors ADO).
///  - Phase34Migration adds the new columns idempotently.
/// </summary>
public class Phase34SyncRefactorTests
{
    // ---- 34.1 — IsDueForSync (work-item gate) ------------------------------

    [Fact]
    public void IsDueForSync_returns_true_when_no_prior_sync()
    {
        var repo = new RepoRow { Id = "r1", LastSyncAt = null };
        Assert.True(WorkItemSyncJob.IsDueForSync(repo, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsDueForSync_returns_true_for_stale_running_row()
    {
        // Stale 'running' bypasses the cadence gate so a crashed run can recover.
        var repo = new RepoRow
        {
            Id = "r1",
            LastSyncStatus = "running",
            LastSyncAt = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("o"),
        };
        Assert.True(WorkItemSyncJob.IsDueForSync(repo, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsDueForSync_honors_global_default_when_override_null()
    {
        var now = DateTimeOffset.UtcNow;
        var justSynced = new RepoRow
        {
            Id = "r1",
            LastSyncStatus = "idle",
            WorkItemSyncIntervalSeconds = null,
            LastSyncAt = now.AddSeconds(-(SyncConstants.WorkItemSyncIntervalSeconds - 60)).ToString("o"),
        };
        Assert.False(WorkItemSyncJob.IsDueForSync(justSynced, now));

        var dueByGlobal = new RepoRow
        {
            Id = "r2",
            LastSyncStatus = "idle",
            WorkItemSyncIntervalSeconds = null,
            LastSyncAt = now.AddSeconds(-(SyncConstants.WorkItemSyncIntervalSeconds + 60)).ToString("o"),
        };
        Assert.True(WorkItemSyncJob.IsDueForSync(dueByGlobal, now));
    }

    [Fact]
    public void IsDueForSync_honors_per_repo_override()
    {
        // 4-hour override: a repo synced 3 hours ago is NOT due; 5 hours ago IS due.
        var now = DateTimeOffset.UtcNow;
        var notDue = new RepoRow
        {
            Id = "r1",
            LastSyncStatus = "idle",
            WorkItemSyncIntervalSeconds = 14400, // 4 hr
            LastSyncAt = now.AddHours(-3).ToString("o"),
        };
        Assert.False(WorkItemSyncJob.IsDueForSync(notDue, now));

        var due = new RepoRow
        {
            Id = "r2",
            LastSyncStatus = "idle",
            WorkItemSyncIntervalSeconds = 14400,
            LastSyncAt = now.AddHours(-5).ToString("o"),
        };
        Assert.True(WorkItemSyncJob.IsDueForSync(due, now));
    }

    // ---- 34.2 — IsPrDueForSync ---------------------------------------------

    [Fact]
    public void IsPrDueForSync_returns_true_when_no_prior_sync()
    {
        var c = new PrSyncJob.PrSyncCandidate("p1", "ado", "1", "review", null, "r1", null);
        Assert.True(PrSyncJob.IsPrDueForSync(c, new Dictionary<string, int?>(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsPrDueForSync_honors_per_repo_override()
    {
        var now = DateTimeOffset.UtcNow;
        var intervals = new Dictionary<string, int?> { ["r1"] = 14400 }; // 4 hr override
        var notDue = new PrSyncJob.PrSyncCandidate(
            "p1", "ado", "1", "review", null, "r1", now.AddHours(-3).ToString("o"));
        var due = new PrSyncJob.PrSyncCandidate(
            "p2", "ado", "2", "review", null, "r1", now.AddHours(-5).ToString("o"));

        Assert.False(PrSyncJob.IsPrDueForSync(notDue, intervals, now));
        Assert.True(PrSyncJob.IsPrDueForSync(due, intervals, now));
    }

    [Fact]
    public void IsPrDueForSync_falls_back_to_global_when_no_override()
    {
        var now = DateTimeOffset.UtcNow;
        var intervals = new Dictionary<string, int?>(); // no override
        var dueByGlobal = new PrSyncJob.PrSyncCandidate(
            "p1", "ado", "1", "review", null, "r1",
            now.AddSeconds(-(SyncConstants.PrSyncIntervalSeconds + 60)).ToString("o"));
        Assert.True(PrSyncJob.IsPrDueForSync(dueByGlobal, intervals, now));
    }

    // ---- 34.3 — IsBuildDueForSweep -----------------------------------------

    [Fact]
    public void IsBuildDueForSweep_returns_true_when_no_prior_build_synced()
    {
        var c = new BuildSyncJob.BuildSweepCandidate("p1", "ado", "r1", "main", null);
        var lastByPr = new Dictionary<string, string?>(); // PR has no build rows yet
        var intervals = new Dictionary<string, int?>();
        Assert.True(BuildSyncJob.IsBuildDueForSweep(c, lastByPr, intervals, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsBuildDueForSweep_honors_per_repo_override()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BuildSyncJob.BuildSweepCandidate("p1", "ado", "r1", "main", null);
        var lastByPr = new Dictionary<string, string?> { ["p1"] = now.AddHours(-3).ToString("o") };
        var intervals = new Dictionary<string, int?> { ["r1"] = 14400 }; // 4 hr override
        Assert.False(BuildSyncJob.IsBuildDueForSweep(c, lastByPr, intervals, now));

        lastByPr["p1"] = now.AddHours(-5).ToString("o");
        Assert.True(BuildSyncJob.IsBuildDueForSweep(c, lastByPr, intervals, now));
    }

    // ---- 34.4 — Constants --------------------------------------------------

    [Fact]
    public void Default_intervals_are_2hr()
    {
        // Phase 34 sets the default cadence at 2 hr (7200s) for all three sync axes.
        Assert.Equal(7200, SyncConstants.WorkItemSyncIntervalSeconds);
        Assert.Equal(7200, SyncConstants.PrSyncIntervalSeconds);
        Assert.Equal(7200, SyncConstants.BuildSyncIntervalSeconds);
    }

    [Fact]
    public void Initial_windows_are_30d_for_both_providers()
    {
        Assert.Equal(30, SyncConstants.AdoInitialWindowDays);
        Assert.Equal(30, SyncConstants.GithubInitialWindowDays);
    }

    // ---- 34.5 — Phase34Migration idempotency -------------------------------

    [Fact]
    public async Task Phase34Migration_is_idempotent_and_adds_columns()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;

        await using var db = new ProductDbContext(opts);
        await db.Database.EnsureCreatedAsync();

        await Phase34Migration.ApplyAsync(db);
        // Second apply must be a no-op (idempotent).
        await Phase34Migration.ApplyAsync(db);

        var cols = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('repos')";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) cols.Add(r.GetString(0));
        }

        Assert.Contains("WorkItemSyncIntervalSeconds", cols);
        Assert.Contains("PrSyncIntervalSeconds", cols);
        Assert.Contains("BuildSyncIntervalSeconds", cols);
        Assert.Contains("LastUpstreamChangedDate", cols);
    }
}
