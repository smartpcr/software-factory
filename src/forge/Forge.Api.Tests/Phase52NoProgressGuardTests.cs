// -----------------------------------------------------------------------
// <copyright file="Phase52NoProgressGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 52 follow-up — guard against "successful no-op" orchestrator
/// re-spawn loops. The legacy <see cref="ImplSpawnGuard.CheckAsync"/>
/// allowed every spawn-finished:true back-to-back, which caused 3,180+
/// spawn events over 7 hours on a single workstream (XRAFT stage 3.1,
/// 2026-05-12). These tests verify the new path:
/// <list type="bullet">
///   <item>3+ consecutive successful spawns w/ no new deliverable →
///   <c>SkipNoProgressBackoff</c>.</item>
///   <item>5+ consecutive → <c>ShouldTriggerReassign=true</c>.</item>
///   <item>A new deliverable written between spawns resets the
///   counter.</item>
///   <item>Failure-finishes don't extend the no-progress streak.</item>
/// </list>
/// </summary>
public class Phase52NoProgressGuardTests
{
    private const string Ws = "ws-noprogress-test";

    private static async Task<(ForgeWebApplicationFactory f, ProductDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return (f, db);
    }

    private static async Task SeedSpawnPairAsync(
        ControlDbContext control, ProductDbContext db, string wsId, DateTimeOffset startedAt, bool success)
    {
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = wsId, Kind = ImplSpawnGuard.SpawnStartedKind,
            PayloadJson = "{}", At = startedAt,
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = wsId, Kind = ImplSpawnGuard.SpawnFinishedKind,
            PayloadJson = success ? "{\"success\":true}" : "{\"success\":false}",
            At = startedAt.AddSeconds(1),
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    private static async Task SeedDeliverableAsync(
        ControlDbContext control, ProductDbContext db, string wsId, int iter, int score, DateTimeOffset createdAt)
    {
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wsId, CrewId = "crew-x",
            GeneratorAgentId = "gen", EvaluatorAgentId = "eval",
            Iter = iter, Score = score, Verdict = "iterate",
            DimsJson = "{}", CreatedAt = createdAt,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Single_Successful_Spawn_With_Deliverable_Allows_Next_Tick()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Deliverable created first, then spawn after. Spawn ran BEFORE
        // deliverable was written → that spawn was the one that produced
        // the deliverable → progress.
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-20);
        await SeedSpawnPairAsync(control, db, Ws, baseTime, success: true);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 80, createdAt: baseTime.AddSeconds(30));

        // Use a "now" that's well past any failure-backoff window so a
        // legitimate failure path wouldn't itself skip.
        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: baseTime.AddMinutes(15), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.Allow, result.Outcome);
        Assert.Equal(0, result.ConsecutiveNoProgress);
        Assert.False(result.ShouldTriggerReassign);
    }

    [Fact]
    public async Task Two_Consecutive_NoProgress_Successes_Still_Allowed()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Deliverable at T=0. Two successful spawns at T+5min and T+10min
        // (after the deliverable, no new deliverable produced) → 2
        // consecutive no-progress, but under threshold of 3.
        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 80, createdAt: baseTime);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(5), success: true);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(10), success: true);

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: baseTime.AddMinutes(15), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.Allow, result.Outcome);
        Assert.Equal(2, result.ConsecutiveNoProgress);
        Assert.False(result.ShouldTriggerReassign);
    }

    [Fact]
    public async Task Three_Consecutive_NoProgress_Successes_Trigger_Backoff()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Three successful no-progress spawns. Backoff threshold = 3,
        // backoff window = base (90s) × 3 = 270s. "now" is only 60s
        // after the most recent finish → SkipNoProgressBackoff fires.
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 80, createdAt: baseTime);
        for (var i = 1; i <= 3; i++)
        {
            await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(i * 5), success: true);
        }
        var lastFinishAt = baseTime.AddMinutes(15).AddSeconds(1); // matches SeedSpawnPair's finish offset

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: lastFinishAt.AddSeconds(60), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.SkipNoProgressBackoff, result.Outcome);
        Assert.Equal(3, result.ConsecutiveNoProgress);
        Assert.False(result.ShouldTriggerReassign); // not yet at reassign threshold
        Assert.Contains("no-progress backoff", result.Reason);
    }

    [Fact]
    public async Task Five_Consecutive_NoProgress_Successes_Trigger_Reassign()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 80, createdAt: baseTime);
        for (var i = 1; i <= 5; i++)
        {
            await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(i * 5), success: true);
        }

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: baseTime.AddMinutes(30), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.SkipNoProgressBackoff, result.Outcome);
        Assert.Equal(5, result.ConsecutiveNoProgress);
        Assert.True(result.ShouldTriggerReassign);
        Assert.Contains("routing through Phase 52", result.Reason);
    }

    [Fact]
    public async Task New_Deliverable_Resets_The_NoProgress_Counter()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Pattern: D1 → 4 no-progress spawns → D2 → 1 spawn after.
        // The 4 spawns are AFTER D1 but BEFORE D2; the 1 final spawn is
        // AFTER D2. Counter should only see the 1 final no-progress.
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 70, createdAt: baseTime);
        for (var i = 1; i <= 4; i++)
        {
            await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(i * 5), success: true);
        }
        // Fresh deliverable AFTER the 4 spawns — this is the progress signal.
        var d2At = baseTime.AddMinutes(4 * 5 + 2);
        await SeedDeliverableAsync(control, db, Ws, iter: 2, score: 75, createdAt: d2At);
        // One more no-progress spawn AFTER D2.
        await SeedSpawnPairAsync(control, db, Ws, d2At.AddMinutes(5), success: true);

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: d2At.AddMinutes(10), CancellationToken.None);

        // Only 1 no-progress after the latest deliverable.
        Assert.Equal(1, result.ConsecutiveNoProgress);
        Assert.Equal(ImplSpawnGuard.Decision.Allow, result.Outcome);
        Assert.False(result.ShouldTriggerReassign);
    }

    [Fact]
    public async Task Failure_Finish_Breaks_NoProgress_Streak()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Pattern: D1 → 2 no-progress successes → 1 failure → 1 success.
        // Walking backward from the most recent finish: that's a success,
        // count=1. Next finish: failure — break the streak. So
        // ConsecutiveNoProgress=1 (under backoff threshold).
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 70, createdAt: baseTime);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(5), success: true);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(10), success: true);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(15), success: false);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(20), success: true);

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: baseTime.AddMinutes(25), CancellationToken.None);

        Assert.Equal(1, result.ConsecutiveNoProgress);
        Assert.Equal(ImplSpawnGuard.Decision.Allow, result.Outcome);
    }

    [Fact]
    public async Task Failure_Path_Still_Takes_Precedence_When_Most_Recent_Is_Failure()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Most-recent finish is a failure → goes through the original
        // failure-backoff path (not the no-progress path).
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 70, createdAt: baseTime);
        await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(5), success: false);

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: baseTime.AddMinutes(5).AddSeconds(60), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.SkipBackoff, result.Outcome);
        Assert.Contains("failure backoff", result.Reason);
    }

    [Fact]
    public async Task Cooldown_Elapsed_Allows_Probe_After_NoProgress_Backoff()
    {
        await using var f = (await BootAsync()).f;
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Three no-progress successes; "now" is well past base × 3 = 270s.
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await SeedDeliverableAsync(control, db, Ws, iter: 1, score: 80, createdAt: baseTime);
        for (var i = 1; i <= 3; i++)
        {
            await SeedSpawnPairAsync(control, db, Ws, baseTime.AddMinutes(i * 5), success: true);
        }
        var lastFinishAt = baseTime.AddMinutes(15).AddSeconds(1);

        var result = await ImplSpawnGuard.CheckAsync(
            control, db, Ws, wsPoints: 5, now: lastFinishAt.AddSeconds(400), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.Allow, result.Outcome);
        Assert.Equal(3, result.ConsecutiveNoProgress);
        Assert.Contains("cool-down elapsed", result.Reason);
    }

    [Fact]
    public void Constants_Are_Locked()
    {
        // Sanity: if these change, callers need to know.
        Assert.Equal(3, SyncConstants.NoProgressBackoffThreshold);
        Assert.Equal(5, SyncConstants.NoProgressReassignThreshold);
    }
}
