// -----------------------------------------------------------------------
// <copyright file="Phase67NoProgressScoreZeroTests.cs" company="Microsoft Corp.">
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
/// Phase 67 — fast-stop on a single iter where the evaluator scored 0
/// AND the Phase 65 commit snapshot recorded zero file change on the
/// branch. The agent produced nothing useful in this iter; continuing
/// would burn $9+/iter rebuilding the same context.
///
/// <para>Observed 2026-05-15 on <c>code-intelligence:AGENT-MEMORY</c>
/// plan-doc <c>e2e-scenarios.md</c> iter 3 (12m52 / 600K tokens /
/// $9.81 / zero commits on branch / evaluator score 0). Phase 61's
/// 3-trailing-zero threshold would have waited two more iters at the
/// same cost each; Phase 67 fires after the first such iter.</para>
/// </summary>
public class Phase67NoProgressScoreZeroTests
{
    private const string Fence = "```";

    private static string EvalJson(int score, string? notes = null)
    {
        var n = notes is null ? string.Empty : ", \"notes\": \"" + notes.Replace("\"", "\\\"") + "\"";
        return Fence + "json\n{ \"score\": " + score + n + " }\n" + Fence;
    }

    private static async Task<(ForgeWebApplicationFactory f, ControlDbContext control, ProductDbContext db, DeliverableLoop loop)> SetupAsync(
        string workItemId)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId,
            Type = "step",
            State = "active",
            Title = "T",
            ExecutionStatus = ExecutionStatuses.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        return (f, control, db, new DeliverableLoop(control, db));
    }

    /// <summary>
    /// Iter 1 with score=0 AND OnIterationCommit reports zero file
    /// change → loop stops immediately with verdict=sloppy /
    /// reason=no-progress-score-zero. No iter 2 generator turn.
    /// </summary>
    [Fact]
    public async Task ScoreZero_With_Zero_FileChange_Triggers_Sloppy_On_Iter1()
    {
        const string wi = "wi-67-iter1-trigger";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var genCallCount = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    genCallCount++;
                    return Task.FromResult(new GeneratorRunOutput("empty", 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(0), 0.2m)),
                MaxIterations: 5,
                // Phase 65 commit snapshot — zero file change on the branch.
                OnIterationCommit: (iter, deliverableId, ct) =>
                    Task.FromResult<IterCommitInfo?>(new IterCommitInfo(
                        CommitSha: "abc123",
                        CommitMessage: "no-op iter",
                        FilesAdded: 0,
                        FilesModified: 0,
                        FilesDeleted: 0)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("sloppy", result.FinalVerdict);
            Assert.Equal(SloppyReasons.NoProgressScoreZero, result.SloppyReason);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(0, result.FinalScore);
            // Loop exited after iter 1 — no iter 2 generator turn.
            Assert.Equal(1, genCallCount);

            using var scope = f.Services.CreateScope();
            var control_freshDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var freshDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await freshDb.Deliverables.AsNoTracking().FirstAsync(d => d.WorkItemId == wi);
            Assert.Equal("sloppy", row.Verdict);
            Assert.Contains("no-progress score-zero", row.Notes);
            Assert.Equal(0, row.FilesAdded);
            Assert.Equal(0, row.FilesModified);
            Assert.Equal(0, row.FilesDeleted);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Score=0 but the snapshot reports file changes → gate does NOT
    /// fire. (The evaluator may have scored 0 for content reasons while
    /// the agent did change files. This case is Phase 61's territory
    /// after 3 consecutive zeros.)
    /// </summary>
    [Fact]
    public async Task ScoreZero_With_Some_FileChange_Does_Not_Trigger()
    {
        const string wi = "wi-67-files-changed";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var iters = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    iters++;
                    return Task.FromResult(new GeneratorRunOutput("text", 0.1m));
                },
                Evaluator: (_, _, _) =>
                {
                    // Iter 1: score 0; iter 2+: also 0 (will eventually
                    // trip Phase 61 at 3-trailing-zeros, not Phase 67).
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(0), 0.2m));
                },
                MaxIterations: 5,
                OnIterationCommit: (iter, _, _) =>
                    Task.FromResult<IterCommitInfo?>(new IterCommitInfo(
                        CommitSha: $"sha{iter}",
                        CommitMessage: $"iter {iter}",
                        FilesAdded: 1,
                        FilesModified: 0,
                        FilesDeleted: 0)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            // Phase 67 should NOT fire (file change > 0 every iter).
            // The loop ran to Phase 61 / iter cap. Verify by checking
            // we went past iter 1.
            Assert.True(iters >= 2, $"Expected loop to continue past iter 1; saw {iters} iters");
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Score>0 with zero file change → gate does NOT fire. (The score
    /// is non-zero, so the evaluator could grade SOMETHING; this is not
    /// the "agent gave up" pattern Phase 67 targets.)
    /// </summary>
    [Fact]
    public async Task NonZero_Score_With_Zero_FileChange_Does_Not_Trigger()
    {
        const string wi = "wi-67-nonzero-score";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var iters = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    iters++;
                    return Task.FromResult(new GeneratorRunOutput("text", 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(75), 0.2m)),
                MaxIterations: 3,
                OnIterationCommit: (iter, _, _) =>
                    Task.FromResult<IterCommitInfo?>(new IterCommitInfo(
                        CommitSha: $"sha{iter}",
                        CommitMessage: $"iter {iter}",
                        FilesAdded: 0,
                        FilesModified: 0,
                        FilesDeleted: 0)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            // Loop ran past iter 1 because score=75 is iterate, not 0.
            Assert.True(iters >= 2);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Score=0 + zero file change but the snapshot fields are null
    /// (OnIterationCommit not wired) → gate is conservatively skipped.
    /// We can't know whether the agent produced changes without the
    /// snapshot, so wedging the loop on a missing-callback config error
    /// would be worse than one wasted iter.
    /// </summary>
    [Fact]
    public async Task Null_Snapshot_Does_Not_Trigger_Even_With_ScoreZero()
    {
        const string wi = "wi-67-no-snapshot";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var iters = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    iters++;
                    return Task.FromResult(new GeneratorRunOutput("text", 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(0), 0.2m)),
                MaxIterations: 3
                // OnIterationCommit intentionally omitted → row.FilesAdded
                // / FilesModified / FilesDeleted stay null.
                );

            var result = await loop.RunAsync(req, CancellationToken.None);

            // Loop ran past iter 1 (Phase 67 conservatively skipped
            // because snapshot fields are null).
            Assert.True(iters >= 2);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// First snapshot reports null fields (callback returned null) →
    /// snapshotPresent is false → gate skipped. Mirrors the "callback
    /// failed mid-iter" path.
    /// </summary>
    [Fact]
    public async Task Snapshot_Returning_Null_Does_Not_Trigger()
    {
        const string wi = "wi-67-snap-null";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var iters = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    iters++;
                    return Task.FromResult(new GeneratorRunOutput("text", 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(0), 0.2m)),
                MaxIterations: 3,
                OnIterationCommit: (_, _, _) => Task.FromResult<IterCommitInfo?>(null));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.True(iters >= 2);
        }
        finally { f.Dispose(); }
    }
}
