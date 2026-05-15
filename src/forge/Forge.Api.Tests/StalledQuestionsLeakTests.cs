// -----------------------------------------------------------------------
// <copyright file="StalledQuestionsLeakTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-15 — regression suite for the three intertwined bugs that
/// wedged ws code-intelligence:AGENT-MEMORY 1.3 in <c>stalled</c> with
/// reason <c>"Sloppy-3-attempts: ..."</c> despite the WI eventually
/// passing (score 92, iter 3, no PR opened because the orchestrator
/// skips stalled rows):
/// <list type="number">
///   <item><b>Bug 1</b> — <see cref="ImplSpawnGuard"/> mis-counted
///   no-progress during operator-question parking → routed to sloppy
///   reassignment → bogus terminal failure stamp.</item>
///   <item><b>Bug 2</b> —
///   <c>TaskExecutionEndpoints.TryUnparkAndResumeAsync</c> workstream
///   branch did not clear the bogus stall when the operator answered
///   the parked questions.</item>
///   <item><b>Bug 3</b> —
///   <see cref="Forge.Api.Services.StalledMergedRecoveryReconciler"/>
///   had no recovery path for stalled rows whose WI passed but no PR
///   had been opened yet (Phase A required merged PR; Phase B required
///   shipped status).</item>
/// </list>
/// </summary>
public class StalledQuestionsLeakTests
{
    private const string WsId = "ws-stalled-questions-test";

    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }
        return f;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task SeedSpawnPairAsync(
        ForgeDbContext db, string wsId, DateTimeOffset startedAt, bool success)
    {
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = wsId,
            Kind = ImplSpawnGuard.SpawnStartedKind,
            PayloadJson = "{}",
            At = startedAt,
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = wsId,
            Kind = ImplSpawnGuard.SpawnFinishedKind,
            PayloadJson = success ? "{\"success\":true}" : "{\"success\":false}",
            At = startedAt.AddSeconds(1),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDeliverableAsync(
        ForgeDbContext db, string wsId, int iter, int score, string verdict, DateTimeOffset createdAt)
    {
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wsId,
            CrewId = "crew-x",
            GeneratorAgentId = "gen",
            EvaluatorAgentId = "eval",
            Iter = iter,
            Score = score,
            Verdict = verdict,
            DimsJson = "{}",
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedQuestionAsync(
        ForgeDbContext db, string wsId, string extId, int iter,
        DateTimeOffset createdAt, DateTimeOffset? answeredAt = null)
    {
        db.DeliverableQuestions.Add(new DeliverableQuestionRow
        {
            WorkItemId = wsId,
            ExternalId = extId,
            Iter = iter,
            Text = "test question",
            Kind = "open",
            Answer = answeredAt is null ? null : "answered",
            AnsweredAt = answeredAt,
            AnsweredBy = answeredAt is null ? null : "operator",
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    // Bug 1 — ImplSpawnGuard during question parking
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bug1_StalledQuestions_With_Unanswered_Returns_SkipAwaitingAnswers()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        // Iter 1 ran, evaluator emitted a question, verdict=stalled-questions
        await SeedDeliverableAsync(db, WsId, iter: 1, score: 0, verdict: "stalled-questions", createdAt: baseTime);
        await SeedQuestionAsync(db, WsId, extId: "q1", iter: 1, createdAt: baseTime.AddSeconds(1));
        // 5+ subsequent successful spawns produce no new deliverable
        for (var i = 1; i <= 5; i++)
        {
            await SeedSpawnPairAsync(db, WsId, baseTime.AddMinutes(i * 2), success: true);
        }

        var r = await ImplSpawnGuard.CheckAsync(
            db, WsId, wsPoints: 5, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.SkipAwaitingAnswers, r.Outcome);
        Assert.False(r.ShouldTriggerReassign);
        Assert.Equal(0, r.ConsecutiveNoProgress);
    }

    [Fact]
    public async Task Bug1_StalledQuestions_All_Answered_Uses_AnsweredAt_As_Progress_Boundary()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        await SeedDeliverableAsync(db, WsId, iter: 1, score: 0, verdict: "stalled-questions", createdAt: baseTime);
        // 5 pre-answer no-progress spawns
        for (var i = 1; i <= 5; i++)
        {
            await SeedSpawnPairAsync(db, WsId, baseTime.AddMinutes(i * 2), success: true);
        }
        // Operator answers at minute 12 — AFTER the 5 spawns at minutes 2,4,6,8,10
        var answerTime = baseTime.AddMinutes(12);
        await SeedQuestionAsync(db, WsId, extId: "q1", iter: 1, createdAt: baseTime.AddSeconds(1), answeredAt: answerTime);

        // First post-answer tick — no spawn yet after answerTime. Progress
        // boundary is the answer timestamp so pre-answer spawns do NOT
        // count toward no-progress. Allow is expected.
        var r = await ImplSpawnGuard.CheckAsync(
            db, WsId, wsPoints: 5, baseTime.AddMinutes(15), CancellationToken.None);

        Assert.Equal(ImplSpawnGuard.Decision.Allow, r.Outcome);
        Assert.Equal(0, r.ConsecutiveNoProgress);
        Assert.False(r.ShouldTriggerReassign);
    }

    [Fact]
    public async Task Bug1_StalledQuestions_PreAnswer_NoProgress_Does_Not_Trigger_Reassign()
    {
        // This is the exact regression: 5 pre-answer no-progress spawns
        // used to push ConsecutiveNoProgress to 5 and trip
        // ShouldTriggerReassign on the first post-answer tick. After fix,
        // SkipAwaitingAnswers triggers FIRST while questions are
        // unanswered, then post-answer the progress boundary advances.
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        await SeedDeliverableAsync(db, WsId, iter: 1, score: 0, verdict: "stalled-questions", createdAt: baseTime);
        // Question still unanswered + 5 no-progress spawns
        await SeedQuestionAsync(db, WsId, extId: "q1", iter: 1, createdAt: baseTime.AddSeconds(1));
        for (var i = 1; i <= 5; i++)
        {
            await SeedSpawnPairAsync(db, WsId, baseTime.AddMinutes(i * 2), success: true);
        }

        var r = await ImplSpawnGuard.CheckAsync(
            db, WsId, wsPoints: 5, DateTimeOffset.UtcNow, CancellationToken.None);

        // Must NOT trigger reassign even though we have 5+ no-progress
        // spawns; the awaiting-answers short-circuit comes first.
        Assert.False(r.ShouldTriggerReassign);
    }

    [Fact]
    public async Task Bug1_NoQuestions_Regression_Still_Detects_Genuine_NoProgress()
    {
        // Defense — guard the new progress-boundary path does NOT mask
        // legitimate no-progress detection when there are no questions.
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        // Old deliverable (NOT stalled-questions), then many post-deliverable spawns
        await SeedDeliverableAsync(db, WsId, iter: 1, score: 70, verdict: "iterate", createdAt: baseTime);
        for (var i = 1; i <= 5; i++)
        {
            await SeedSpawnPairAsync(db, WsId, baseTime.AddMinutes(i * 2), success: true);
        }

        var r = await ImplSpawnGuard.CheckAsync(
            db, WsId, wsPoints: 5, DateTimeOffset.UtcNow, CancellationToken.None);

        // No questions → progress boundary = latestDeliverableAt only →
        // 5 no-progress spawns trigger reassign as before.
        Assert.True(r.ShouldTriggerReassign);
        Assert.Equal(5, r.ConsecutiveNoProgress);
    }
}
