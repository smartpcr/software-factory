// -----------------------------------------------------------------------
// <copyright file="ImplSpawnGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 2026-05-07 — pure-function tests for <see cref="ImplSpawnGuard"/>. Locks
/// the in-flight + failure-backoff state machine that gates
/// <see cref="StorySpawnOrchestrator"/>'s 15-second tick. Reproduces the
/// production failure on WI-HIGH-MEM-CONSUMPTION (25 spawns over ~80 min
/// after the agent CLI subprocess crashed mid-stream) and asserts the
/// guard returns the right Decision in each branch.
/// </summary>
public sealed class ImplSpawnGuardTests
{
    private const string WsId = "ws-test";

    /// <summary>
    /// Build a fresh in-memory DbContext + open SQLite connection. Caller
    /// must Dispose both. The connection is held open so the in-memory DB
    /// lives for the duration of the test.
    /// </summary>
    private static async Task<(SqliteConnection conn, ControlDbContext control, ProductDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var ctlOpts = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(conn)
            .Options;
        var control = new ControlDbContext(ctlOpts);
        await control.Database.EnsureCreatedAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ProductDbContext(opts);
        var _productSchema = db.Database.GenerateCreateScript();
        foreach (var _stmt in _productSchema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var _trimmed = _stmt.Trim();
            if (_trimmed.Length > 0) await db.Database.ExecuteSqlRawAsync(_trimmed);
        }
        return (conn, control, db);
    }

    private static async Task AddEventAsync(
        ControlDbContext control, ProductDbContext db, string kind, DateTimeOffset at, object? payload = null)
    {
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = WsId,
            Kind = kind,
            At = at,
            PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload),
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoEvents_AllowsSpawn()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: DateTimeOffset.UtcNow, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
        }
    }

    [Fact]
    public async Task SuccessFinish_AllowsNextSpawn()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind, t0);
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind, t0.AddMinutes(8),
                new { success = true });

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: DateTimeOffset.UtcNow, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
        }
    }

    [Fact]
    public async Task RecentStartedNoFinish_SkipsAsInFlight()
    {
        // Reproduces the production failure mode on WI-HIGH-MEM-CONSUMPTION:
        // a spawn-started exists with no spawn-finished, and the start is
        // young enough to be plausibly in-flight. The 15s tick MUST skip.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-2));

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipInFlight, r.Outcome);
            Assert.Contains("in-flight", r.Reason);
        }
    }

    [Fact]
    public async Task Phase71_RecentStartedNoFinish_WithDeadInvocation_AllowsRecovery()
    {
        // Phase 71 (2026-05-15) — the in-flight branch must ALSO honour
        // isInvocationActive. When the dispatcher confirms the task is
        // not alive (process crash, Forge.Api restart) AND a stale
        // spawn-started has no matching finish, allow recovery
        // IMMEDIATELY — the original drive is dead and waiting the full
        // budget+slack burns dead time.
        //
        // Observed 2026-05-15 on
        // failover-cluster:XRAFT:stage-4.1-grpc-transport-layer iter 7
        // (verdict=pass at 4:49:43, Forge.Api restarted 40s later;
        // workstream sat at 13min elapsed / 26min budget, PR never
        // opened, operator forced to wait or ACCEPT manually).
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            // 2-minute-old start with no finish — well within the 26-min
            // guard for a 13-point workstream, so pre-Phase-71 this
            // would have returned SkipInFlight.
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-2));

            Func<string, bool> deadInvocation = _ => false;

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 13,
                now: now, ct: default, isInvocationActive: deadInvocation);

            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
            Assert.Contains("recovery spawn", r.Reason);
            Assert.Contains("NOT alive", r.Reason);
        }
    }

    [Fact]
    public async Task Phase71_RecentStartedNoFinish_WithLiveInvocation_StillSkipsAsInFlight()
    {
        // Defensive: when isInvocationActive returns TRUE, the original
        // drive is genuinely still running. The Phase 71 fast-recovery
        // branch must NOT fire — that would double-spawn a workstream
        // that's still iterating. Falls through to the existing
        // elapsed < guardSeconds branch.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-2));

            Func<string, bool> liveInvocation = _ => true;

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 13,
                now: now, ct: default, isInvocationActive: liveInvocation);

            Assert.Equal(ImplSpawnGuard.Decision.SkipInFlight, r.Outcome);
            Assert.Contains("in-flight", r.Reason);
        }
    }

    [Fact]
    public async Task Phase71_NoIsActive_Wired_Preserves_Legacy_InFlight_Behaviour()
    {
        // When isInvocationActive is null (legacy callers / tests), the
        // Phase 71 fast-recovery branch is a no-op and the original
        // elapsed < guardSeconds skip-in-flight semantics still apply.
        // Pins the back-compat contract.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-2));

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 13,
                now: now, ct: default, isInvocationActive: null);

            Assert.Equal(ImplSpawnGuard.Decision.SkipInFlight, r.Outcome);
        }
    }

    [Fact]
    public async Task GuardWindowScalesWithBudget_LargeWsHasLongerWindow()
    {
        // For a 10-point workstream, BudgetForPhase("impl") = 10 * 60s * 2 =
        // 1200s = 20 min. With 120s slack the window is ~22 min. A spawn
        // started 18 min ago is still in-flight from the orchestrator's
        // perspective even though it's well past the 8-min floor.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-18));

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 10,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipInFlight, r.Outcome);
        }
    }

    [Fact]
    public async Task OldStartedNoFinish_AllowsRecoverySpawn()
    {
        // After the budget+slack window elapses, the guard releases so the
        // orchestrator can attempt recovery — a stuck invocation that never
        // emits a spawn-finished shouldn't block forever.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-60));

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 1,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
            Assert.Contains("overran", r.Reason);
        }
    }

    [Fact]
    public async Task RecentFailure_AppliesBackoff()
    {
        // After a CLI crash (success=false), the next tick must NOT spawn
        // immediately — that was the original retry-storm failure mode.
        // 30s after the failure we're still inside the 90s base backoff.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-3));
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                now.AddSeconds(-30),
                new { success = false, exceptionType = "System.IO.IOException" });

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipBackoff, r.Outcome);
        }
    }

    [Fact]
    public async Task FailureBackoffElapsed_AllowsRetry()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-5));
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                now.AddSeconds(-100), // > 90s base backoff
                new { success = false });

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
        }
    }

    [Fact]
    public async Task ConsecutiveFailures_ScalesBackoff()
    {
        // 3 consecutive failures → 3 * 90 = 270s backoff. 250s after the
        // most recent failure we're still inside the window.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            // Three back-to-back fail cycles, each ~5min apart.
            for (var i = 0; i < 3; i++)
            {
                await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                    now.AddMinutes(-15 + i * 5));
                await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                    now.AddMinutes(-13 + i * 5),
                    new { success = false });
            }
            // The most recent failure is at now - 3min = -180s.
            // For 1 failure window (90s) this would already be elapsed; with
            // 3 consecutive the window is 270s so 180s is still inside.

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipBackoff, r.Outcome);
            Assert.Contains("3 consecutive", r.Reason);
        }
    }

    [Fact]
    public async Task BackoffCapsAtMax()
    {
        // 100 consecutive failures × 90s = 9000s, but cap is 1800s.
        // 1700s after the failure we're inside; 1900s after we're released.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 100; i++)
            {
                await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                    now.AddSeconds(-(2000 + i)));
                await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                    now.AddSeconds(-(1900 + i)),
                    new { success = false });
            }
            // Most recent failure at now - 1900s — past the 1800s cap.

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
        }
    }

    [Fact]
    public async Task SuccessAfterFailure_ResetsBackoff()
    {
        // The most recent finished is success → no backoff applies even if
        // earlier finishes were failures. Counter resets on each success.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-30));
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                now.AddMinutes(-29), new { success = false });
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-15));
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                now.AddMinutes(-14), new { success = true });

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
        }
    }

    [Fact]
    public async Task MalformedFinishPayload_TreatedAsFailure()
    {
        // Defensive: if the payload can't be parsed, fail closed (apply
        // backoff) rather than risk re-opening the retry storm.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-2));
            db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
            {
                WorkItemId = WsId,
                Kind = ImplSpawnGuard.SpawnFinishedKind,
                At = now.AddSeconds(-30),
                PayloadJson = "not valid json",
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipBackoff, r.Outcome);
        }
    }

    [Fact]
    public async Task MissingSuccessField_TreatedAsFailure()
    {
        // A finish event without a `success` field can't confirm success →
        // fail closed.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddMinutes(-2));
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnFinishedKind,
                now.AddSeconds(-30),
                new { wsId = WsId }); // no success field

            var r = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 5,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipBackoff, r.Outcome);
        }
    }

    [Fact]
    public async Task GuardWindowFloor_AppliesForLowPointWs()
    {
        // A 0/1-point WS would have BudgetForPhase = 360s; +120s slack =
        // 480s. The floor is also 480s, so the window is 480s. A spawn
        // started 7min ago (420s) is still inside; 9min ago (540s) is past.
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            await AddEventAsync(control, db, ImplSpawnGuard.SpawnStartedKind,
                now.AddSeconds(-420));

            var r1 = await ImplSpawnGuard.CheckAsync(control, db, WsId, wsPoints: 0,
                now: now, ct: default);
            Assert.Equal(ImplSpawnGuard.Decision.SkipInFlight, r1.Outcome);
        }
    }

    [Fact]
    public void SyncConstants_HaveSensibleValues()
    {
        // Lock the constants the guard depends on so nobody silently lowers
        // them and reintroduces the retry storm.
        Assert.Equal(480, SyncConstants.InflightSpawnGuardMinSeconds);
        Assert.Equal(120, SyncConstants.InflightSpawnGuardBudgetSlackSeconds);
        Assert.Equal(90, SyncConstants.FailureBackoffBaseSeconds);
        Assert.Equal(1800, SyncConstants.FailureBackoffMaxSeconds);
    }
}
