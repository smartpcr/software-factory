// -----------------------------------------------------------------------
// <copyright file="Phase47NoProgressLivelockTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 47 — regression tests for the workstream-livelock bug discovered
/// 2026-05-09. One workstream had 139 deliverable rows for <c>maxIter=50</c>
/// and burned $9,825 across 4 days while the engineer agent produced zero
/// commits. Two interlocking bugs caused the livelock:
/// <list type="number">
///   <item>
///     <see cref="DeliverableLoop"/> resume guard required
///     <c>lastPersisted.Iter &lt; maxIter</c>; after a <c>stalled</c> row at
///     <c>iter == maxIter</c>, the next entry reset <c>iter</c> to 0 and
///     ran a fresh full-cycle of iterations. Iter histogram showed each
///     iter 1-36 occurring 3 times.
///   </item>
///   <item>
///     <see cref="Forge.Api.Services.Stories.StorySpawnOrchestrator"/>
///     only special-cased <c>pass</c>; <c>stalled</c> / <c>stalled-budget</c>
///     / <c>reject</c> fell through to <c>ProduceWorkstreamChangesAsync</c>
///     which re-entered the loop. (Tested in
///     <c>Phase47BlockedStatusTests</c>.)
///   </item>
/// </list>
/// </summary>
public class Phase47NoProgressLivelockTests
{
    private const string Fence = "```";

    private static string EvalJson(int score) =>
        Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    private static async Task<(ForgeWebApplicationFactory f, ForgeDbContext db, DeliverableLoop loop)> SetupAsync(string workItemId)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId,
            Type = "step",
            State = "active",
            Title = "T",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (f, db, new DeliverableLoop(db));
    }

    /// <summary>
    /// The headline regression: a previously-persisted <c>stalled</c> row at
    /// <c>iter == maxIter</c> must not cause the loop to restart from
    /// <c>iter = 1</c>. Before Phase 47, this is exactly what produced the
    /// 139-deliverable / $9,825 livelock — 3 cycles of 50 iterations each.
    /// After the fix, the second <c>RunAsync</c> call short-circuits and
    /// returns the persisted <c>stalled</c> verdict immediately without
    /// invoking the generator or evaluator delegates.
    /// </summary>
    [Fact]
    public async Task Stalled_At_MaxIter_Does_Not_Restart_Loop_On_Replay()
    {
        var (f, db, loop) = await SetupAsync("wi-stalled-replay");
        try
        {
            // First run: stall the loop by always scoring 80 (iterate range).
            var firstReq = new DeliverableLoopRequest(
                "wi-stalled-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0m)));
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("stalled", first.FinalVerdict);
            var firstRowCount = await db.Deliverables
                .CountAsync(d => d.WorkItemId == "wi-stalled-replay");
            Assert.Equal(ScoringConstants.MaxIterations, firstRowCount);

            // Second run: same request, both delegates should be NEVER invoked.
            // Track call counts; if either fires, we have a regression.
            var genCalls = 0;
            var evalCalls = 0;
            var replayReq = new DeliverableLoopRequest(
                "wi-stalled-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => { genCalls++; return Task.FromResult(new GeneratorRunOutput("out", 0m)); },
                Evaluator: (_, _, _) => { evalCalls++; return Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0m)); });
            var replay = await loop.RunAsync(replayReq, CancellationToken.None);

            Assert.Equal("stalled", replay.FinalVerdict);
            Assert.Equal(ScoringConstants.MaxIterations, replay.Iterations);
            Assert.Equal(0, genCalls); // ← the actual fix
            Assert.Equal(0, evalCalls);
            // No new deliverable rows — the short-circuit returns without persisting.
            var afterRowCount = await db.Deliverables
                .CountAsync(d => d.WorkItemId == "wi-stalled-replay");
            Assert.Equal(firstRowCount, afterRowCount);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// <c>reject</c> is terminal regardless of iter; replay must short-circuit.
    /// Lead's responsibility to triage; the loop must not fight the verdict.
    /// </summary>
    [Fact]
    public async Task Reject_At_Any_Iter_Short_Circuits_On_Replay()
    {
        var (f, db, loop) = await SetupAsync("wi-reject-replay");
        try
        {
            var firstReq = new DeliverableLoopRequest(
                "wi-reject-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(40), 0m)));
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("reject", first.FinalVerdict);
            Assert.Equal(1, first.Iterations);

            var genCalls = 0;
            var replayReq = new DeliverableLoopRequest(
                "wi-reject-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => { genCalls++; return Task.FromResult(new GeneratorRunOutput("out", 0m)); },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(40), 0m)));
            var replay = await loop.RunAsync(replayReq, CancellationToken.None);

            Assert.Equal("reject", replay.FinalVerdict);
            Assert.Equal(0, genCalls);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// <c>pass</c> is terminal regardless of iter; replay must short-circuit
    /// AND mirror the WorkItem state side-effect (<c>"active"</c>) so a crash
    /// between the deliverable row and the state set is recoverable.
    /// </summary>
    [Fact]
    public async Task Pass_Replay_ShortCircuits_And_Restores_WorkItem_State()
    {
        var (f, db, loop) = await SetupAsync("wi-pass-replay");
        try
        {
            // First run passes on iter 1, sets WI state to "active".
            var firstReq = new DeliverableLoopRequest(
                "wi-pass-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)));
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("pass", first.FinalVerdict);

            // Simulate a crash that wiped state back to "proposed".
            var wi = await db.WorkItems.FirstAsync(w => w.Id == "wi-pass-replay");
            wi.State = "proposed";
            await db.SaveChangesAsync();

            // Replay short-circuits AND restores state to "active".
            var replayReq = new DeliverableLoopRequest(
                "wi-pass-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)));
            var replay = await loop.RunAsync(replayReq, CancellationToken.None);

            Assert.Equal("pass", replay.FinalVerdict);
            // State side effect was mirrored on the replay.
            await db.Entry(wi).ReloadAsync();
            Assert.Equal("active", wi.State);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// <c>stalled-budget</c> is terminal regardless of iter (operator must
    /// raise budget via Restart). Without this short-circuit, a re-entered
    /// loop resets <c>totalCost = 0m</c> and burns more dollars before
    /// re-tripping the same cap.
    /// </summary>
    [Fact]
    public async Task StalledBudget_Replay_Short_Circuits_Even_Below_MaxIter()
    {
        var (f, db, loop) = await SetupAsync("wi-budget-replay");
        try
        {
            // Cap at $1.00, each iter costs $0.60. Stalls at iter 2, well below maxIter=5.
            var firstReq = new DeliverableLoopRequest(
                "wi-budget-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.40m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0.20m)),
                MaxTaskBudgetUsd: 1.00m);
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("stalled-budget", first.FinalVerdict);
            Assert.Equal(2, first.Iterations);
            Assert.True(first.Iterations < ScoringConstants.MaxIterations);

            // Replay must short-circuit — no more burn.
            var genCalls = 0;
            var replayReq = new DeliverableLoopRequest(
                "wi-budget-replay", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => { genCalls++; return Task.FromResult(new GeneratorRunOutput("out", 0.40m)); },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0.20m)),
                MaxTaskBudgetUsd: 1.00m);
            var replay = await loop.RunAsync(replayReq, CancellationToken.None);

            Assert.Equal("stalled-budget", replay.FinalVerdict);
            Assert.Equal(0, genCalls);
            Assert.Equal(0m, replay.TotalCostUsd); // no new cost on replay
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Resume-after-stall semantics: when the operator bumps
    /// <see cref="TaskQueueRow.MaxIterOverride"/> on a stalled work item, the
    /// next <c>RunAsync</c> must NOT short-circuit — the bumped cap exceeds
    /// the persisted iter and the loop must run fresh iterations from
    /// <c>lastPersisted.Iter + 1</c>. Without this, the existing
    /// <c>POST /api/work-items/{id}/execution</c> Resume path silently no-ops.
    /// </summary>
    [Fact]
    public async Task Stalled_Replay_With_Bumped_MaxIterOverride_Resumes_Loop()
    {
        var (f, db, loop) = await SetupAsync("wi-resume-bump");
        try
        {
            // Stall at the default cap of 5.
            var firstReq = new DeliverableLoopRequest(
                "wi-resume-bump", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0m)));
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("stalled", first.FinalVerdict);
            Assert.Equal(ScoringConstants.MaxIterations, first.Iterations);

            // Operator raises the cap via a queue row override.
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = "wi-resume-bump",
                TargetKind = "team",
                TargetId = "Engineer",
                State = "queued",
                Kind = "work",
                MaxIterOverride = 8,
                EnqueuedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Replay: this iter should pass on the FIRST resumed iter (which is iter 6).
            var passOnIter6 = new DeliverableLoopRequest(
                "wi-resume-bump", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)));
            var replay = await loop.RunAsync(passOnIter6, CancellationToken.None);

            Assert.Equal("pass", replay.FinalVerdict);
            // Resumed at iter 6 (one past the persisted iter 5).
            Assert.Equal(6, replay.Iterations);
        }
        finally { f.Dispose(); }
    }
}
