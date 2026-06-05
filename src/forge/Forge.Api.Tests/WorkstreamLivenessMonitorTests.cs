// -----------------------------------------------------------------------
// <copyright file="WorkstreamLivenessMonitorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Regression coverage for <see cref="WorkstreamLivenessMonitor"/>. Pins
/// the predicate that catches the 2026-06-04 silent-stall class
/// (pri-postgres: 4h26m undetected; end-to-end-backfill-scenario: ~3 days
/// undetected) and the safety conditions that prevent false positives on
/// workstreams that are legitimately idle (open queue, recent activity,
/// unmet dep, parked execution status).
/// </summary>
public class WorkstreamLivenessMonitorTests
{
    private const string WsId = "ws-test-silent-stall-target";
    private const string RepoId = "repo-test";

    private static async Task<(SqliteConnection conn, ControlDbContext control, ProductDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var ctlOpts = new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options;
        var control = new ControlDbContext(ctlOpts);
        await control.Database.EnsureCreatedAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options;
        var db = new ProductDbContext(opts);
        // ProductDbContext shares the underlying SQLite file with the
        // control context (in-memory mode). EnsureCreated would noop on
        // the second context — use the generated script instead.
        var script = db.Database.GenerateCreateScript();
        foreach (var stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length == 0) continue;
            try { await db.Database.ExecuteSqlRawAsync(trimmed); }
            catch { /* table already created by ControlDbContext share */ }
        }
        return (conn, control, db);
    }

    private static async Task SeedIdleWorkstreamAsync(
        ProductDbContext db, DateTimeOffset updatedAt, string? depsJson = null, string? stallReason = null)
    {
        db.WorkItems.Add(new WorkItemRow
        {
            Id = WsId,
            Type = "task",
            State = "active",
            Title = "test silent stall target",
            DescriptionMd = string.Empty,
            ExecutionStatus = ExecutionStatuses.Idle,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            RepoId = RepoId,
        });
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = WsId,
            Name = "Test Silent Stall Target",
            Status = "active",
            RepoId = RepoId,
            Branch = "ws/test/silent-stall-target",
            DepsJson = depsJson,
            StallReason = stallReason,
        });
        await db.SaveChangesAsync();
    }

    private static WorkstreamLivenessMonitor NewMonitor()
    {
        var sp = new ServiceCollection()
            .AddSingleton<ProductFanout>()
            .AddLogging()
            .BuildServiceProvider();
        var config = new ConfigurationBuilder().Build();
        return new WorkstreamLivenessMonitor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<WorkstreamLivenessMonitor>.Instance);
    }

    [Fact]
    public async Task Detects_classic_silent_stall_before_claim()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-1));

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(1, detected);
            var ws = await db.Workstreams.FirstAsync(w => w.Id == WsId);
            Assert.Equal(WorkstreamStatuses.Stalled, ws.Status);
            Assert.NotNull(ws.StallReason);
            Assert.Contains(WorkstreamLivenessMonitor.StallCodeBeforeClaim, ws.StallReason);
            Assert.Contains($"POST /api/workstreams/{WsId}/restart", ws.StallReason);
        }
    }

    [Fact]
    public async Task Skips_when_open_queue_row_exists()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-2));
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = WsId,
                Kind = TaskQueueKinds.Work,
                TargetKind = "team",
                TargetId = "team-test",
                State = TaskQueueStates.Queued,
                EnqueuedAt = DateTimeOffset.UtcNow.AddHours(-2),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            });
            await db.SaveChangesAsync();

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(0, detected);
            var ws = await db.Workstreams.FirstAsync(w => w.Id == WsId);
            Assert.Equal("active", ws.Status);
            Assert.Null(ws.StallReason);
        }
    }

    [Theory]
    [InlineData(TaskQueueStates.AwaitingAck)]
    [InlineData(TaskQueueStates.AwaitingRoute)]
    [InlineData(TaskQueueStates.Queued)]
    [InlineData(TaskQueueStates.PickedUp)]
    [InlineData(TaskQueueStates.Blocked)]
    public async Task Skips_for_every_OpenState(string openState)
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-1));
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = WsId,
                Kind = TaskQueueKinds.Work,
                TargetKind = "team",
                TargetId = "team-test",
                State = openState,
                EnqueuedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            await db.SaveChangesAsync();

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(0, detected);
        }
    }

    [Theory]
    [InlineData(TaskQueueStates.Completed)]
    [InlineData(TaskQueueStates.Cancelled)]
    [InlineData(TaskQueueStates.Reassigned)]
    public async Task Detects_when_only_terminal_queue_rows_exist(string terminalState)
    {
        // Stale terminal queue rows must not suppress detection — pri-postgres
        // had a `cancelled` row from its earlier crashed run but was still
        // silently idle.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-1));
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = WsId,
                Kind = TaskQueueKinds.Work,
                TargetKind = "team",
                TargetId = "team-test",
                State = terminalState,
                EnqueuedAt = DateTimeOffset.UtcNow.AddHours(-2),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-2),
            });
            await db.SaveChangesAsync();

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(1, detected);
        }
    }

    [Fact]
    public async Task Skips_when_recent_transcript_event()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-1));
            // UpdatedAt is old (passes prefilter) but transcript activity is
            // fresh — the DeliverableLoop is mid-iter, not stalled.
            db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
            {
                WorkItemId = WsId,
                Kind = "task.token",
                At = DateTimeOffset.UtcNow.AddMinutes(-5),
                PayloadJson = "{}",
            });
            await db.SaveChangesAsync();

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(0, detected);
        }
    }

    [Fact]
    public async Task Skips_when_unmet_upstream_dep()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            // Seed the dep workstream first (still active).
            var depId = "ws-test-upstream-dep";
            db.WorkItems.Add(new WorkItemRow
            {
                Id = depId,
                Type = "task",
                State = "active",
                Title = "upstream",
                DescriptionMd = string.Empty,
                ExecutionStatus = ExecutionStatuses.Live,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                RepoId = RepoId,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = depId, Name = "upstream", Status = "active", RepoId = RepoId,
                Branch = "ws/dep", DepsJson = null,
            });
            await db.SaveChangesAsync();
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-1),
                depsJson: $"[\"{depId}\"]");

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            // Legitimately blocked on an upstream dep — must not be flagged.
            Assert.Equal(0, detected);
        }
    }

    [Fact]
    public async Task Skips_when_below_idle_threshold()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            // Touched 5 min ago — well under the 30 min threshold.
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddMinutes(-5));

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(0, detected);
        }
    }

    [Fact]
    public async Task Skips_when_workstream_already_stalled()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            // Already annotated — don't double-annotate / clobber a richer
            // reason set by another reconciler.
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-1),
                stallReason: "Build failed: missing dependency");

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(0, detected);
            var ws = await db.Workstreams.FirstAsync(w => w.Id == WsId);
            Assert.Equal("Build failed: missing dependency", ws.StallReason);
        }
    }

    [Theory]
    [InlineData(ExecutionStatuses.CooldownBudget)]
    [InlineData(ExecutionStatuses.PendingReassign)]
    [InlineData(ExecutionStatuses.AwaitingAnswers)]
    [InlineData(ExecutionStatuses.Live)]
    [InlineData(ExecutionStatuses.Failed)]
    [InlineData(ExecutionStatuses.Completed)]
    public async Task Skips_parked_execution_states(string parkedStatus)
    {
        // These statuses have their own dedicated reconcilers / terminal
        // contracts; the liveness monitor must not interfere with them.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var stale = DateTimeOffset.UtcNow.AddHours(-1);
            db.WorkItems.Add(new WorkItemRow
            {
                Id = WsId,
                Type = "task",
                State = "active",
                Title = "test parked",
                DescriptionMd = string.Empty,
                ExecutionStatus = parkedStatus,
                CreatedAt = stale,
                UpdatedAt = stale,
                RepoId = RepoId,
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = WsId, Name = "parked", Status = "active",
                RepoId = RepoId, Branch = "ws/parked",
            });
            await db.SaveChangesAsync();

            var detected = await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            Assert.Equal(0, detected);
        }
    }

    [Fact]
    public async Task StallReason_format_quotes_the_restart_endpoint_with_id()
    {
        // Operators read StallReason verbatim. Make sure the recovery
        // instruction is precise + machine-greppable (the [code] prefix
        // is a separate test above; this one pins the URL shape).
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedIdleWorkstreamAsync(db, DateTimeOffset.UtcNow.AddHours(-3));
            await NewMonitor().RunForProductAsync(
                control, db, "test-product", TimeSpan.FromMinutes(30), default);

            var ws = await db.Workstreams.FirstAsync(w => w.Id == WsId);
            Assert.NotNull(ws.StallReason);
            Assert.Contains($"POST /api/workstreams/{WsId}/restart", ws.StallReason);
            // Idle duration is formatted (h/m) — sanity check.
            Assert.Matches(@"idle for \d+[dhm]", ws.StallReason);
        }
    }
}
