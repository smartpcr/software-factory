// -----------------------------------------------------------------------
// <copyright file="Phase56GateRecoveryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 56 — gate-failure recovery. Today (pre-Phase-56) the post-pass
/// gate suite in <c>CrewStoryArtifactStrategy.ProduceWorkstreamChangesAsync</c>
/// runs <c>build → quality (fmt/clippy) → test</c> AFTER the gen/eval loop
/// returns verdict=pass. Any failure throws <see cref="BuildFailedException"/>
/// directly, stalling the workstream — the agent never sees the failure.
/// <para>Operator-reported 2026-05-13 on
/// <c>ws-failover-cluster-xraft-phase-raft-consensus-engine-stage-raft-node-state-machine</c>:
/// iter 4/50 hit score 94 and STALLED because <c>cargo fmt --check --all</c>
/// failed at post-pass. Plenty of iter budget left, but no recovery path.</para>
/// <para>Phase 56 adds an <see cref="DeliverableLoopRequest.OnPassValidate"/>
/// callback that runs immediately before pair-stamp / state=active. A
/// non-empty return demotes pass→iterate, appends the rejection reason to
/// the deliverable Notes (which flows through <c>FormatPriorChain</c> into
/// the next iter's evaluator prompt), and continues the loop. The validator
/// may also throw to terminate with that exception — used when recovery
/// attempts are exhausted.</para>
/// <para>This test file pins the <see cref="DeliverableLoop"/>-level
/// mechanism. The CrewStoryArtifactStrategy-side wiring (per-iter chain,
/// post-pass recovery counter, BuildFailedException with Stage/Command)
/// is exercised end-to-end via Phase47e + Phase49 gate runs in production
/// and the integration test suite; pinning that path here would require
/// the full app-host + a cargo / dotnet toolchain.</para>
/// </summary>
public class Phase56GateRecoveryTests
{
    private const string Fence = "```";

    private static string EvalJson(int score, string? notes = null)
    {
        var n = notes is null ? string.Empty : ", \"notes\": \"" + notes.Replace("\"", "\\\"") + "\"";
        return Fence + "json\n{ \"score\": " + score + n + " }\n" + Fence;
    }

    private static async Task<(ForgeWebApplicationFactory f, ProductDbContext db, DeliverableLoop loop)> SetupAsync(
        string workItemId)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
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
    /// When the pass-validator returns null, the loop accepts pass on
    /// iter 1 as today. Baseline / back-compat: a caller that does not
    /// wire <see cref="DeliverableLoopRequest.OnPassValidate"/> at all
    /// gets exactly today's behavior (Phase14 tests pin that path).
    /// </summary>
    [Fact]
    public async Task PassValidator_NullReturn_AcceptsPass()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-null");
        try
        {
            var validatorCalls = 0;
            var req = new DeliverableLoopRequest(
                "wi-validator-null", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) =>
                {
                    validatorCalls++;
                    return Task.FromResult<string?>(null);
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(1, validatorCalls);

            var wi = await db.WorkItems.FirstAsync(w => w.Id == "wi-validator-null");
            Assert.Equal("active", wi.State);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// A non-empty rejection string from the validator demotes pass to
    /// iterate, appends the rejection to the deliverable's Notes, and
    /// the loop continues to iter N+1.
    /// </summary>
    [Fact]
    public async Task PassValidator_NonEmptyReturn_DemotesPassToIterate()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-demote");
        try
        {
            // Iter 1: evaluator says 95 (pass) → validator rejects.
            // Iter 2: evaluator says 94 (pass) → validator accepts (null).
            // Final verdict: pass, with 2 iters total.
            const string rejectionText = "GATE REJECTED PASS: cargo fmt --check --all (exit 1)";
            var validatorCalls = 0;
            var req = new DeliverableLoopRequest(
                "wi-validator-demote", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) =>
                {
                    validatorCalls++;
                    // First pass-attempt rejects; second accepts.
                    return Task.FromResult<string?>(validatorCalls == 1 ? rejectionText : null);
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(2, result.Iterations);
            Assert.Equal(2, validatorCalls);

            var rows = await db.Deliverables
                .Where(d => d.WorkItemId == "wi-validator-demote")
                .OrderBy(d => d.Iter)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal("iterate", rows[0].Verdict);
            Assert.Equal("pass", rows[1].Verdict);
            Assert.Contains(rejectionText, rows[0].Notes);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// The rejection text on the demoted row must flow into the next
    /// iter's evaluator notes via <c>FormatPriorChain</c>. This is the
    /// path that lets the AGENT see the gate failure — without it the
    /// next iter generator + evaluator would be blind to why the prior
    /// pass got rejected.
    /// </summary>
    [Fact]
    public async Task PassValidator_RejectionFlowsIntoPriorChain()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-chain");
        try
        {
            const string rejectionText = "POST-PASS QUALITY GATE REJECTED PASS (recovery attempt 1/3)";
            // The Generator delegate's `prevNotes` arg carries FormatPriorChain's
            // output -- that's the path the rejection text rides into the next
            // iter. (The Evaluator delegate's second arg is the current iter's
            // generator output, not the chain.)
            string? capturedIterTwoChain = null;
            var validatorCalls = 0;

            var req = new DeliverableLoopRequest(
                "wi-validator-chain", "crew-x", "gen-1", "eval-1",
                Generator: (iter, prevNotes, _) =>
                {
                    if (iter == 2)
                    {
                        capturedIterTwoChain = prevNotes;
                    }
                    return Task.FromResult(new GeneratorRunOutput("out", 0m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) =>
                {
                    validatorCalls++;
                    return Task.FromResult<string?>(validatorCalls == 1 ? rejectionText : null);
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(2, result.Iterations);
            Assert.NotNull(capturedIterTwoChain);
            Assert.Contains(rejectionText, capturedIterTwoChain);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// When the pass-validator throws (e.g. recovery exhausted), the
    /// exception propagates out of <see cref="DeliverableLoop.RunAsync"/>
    /// unchanged. The orchestrator's <c>BuildFailedException</c> catch
    /// then routes the workstream to <c>stalled</c> with the enriched
    /// stall reason (stage / command).
    /// </summary>
    [Fact]
    public async Task PassValidator_Throws_PropagatesException()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-throw");
        try
        {
            var thrown = new BuildFailedException("Recovery exhausted")
            {
                ExitCode = 1,
                ErrorCount = 3,
                ErrorSummary = "fmt errors here",
                Stage = "quality",
                Command = "cargo fmt --check --all",
                LastIteration = 2,
            };

            var req = new DeliverableLoopRequest(
                "wi-validator-throw", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) => throw thrown);

            var caught = await Assert.ThrowsAsync<BuildFailedException>(
                () => loop.RunAsync(req, CancellationToken.None));

            Assert.Same(thrown, caught);
            Assert.Equal("quality", caught.Stage);
            Assert.Equal("cargo fmt --check --all", caught.Command);

            // Phase 56 follow-up -- before the exception propagates, the
            // loop must persist the deliverable row with Verdict="iterate"
            // (NOT the original "pass" set at row creation, and NOT
            // "stalled" which StorySpawnOrchestrator.DriveOneWorkstreamImplAsync
            // line 2154 treats as a TERMINAL triage state that refuses
            // to re-enter the gen/eval loop). With verdict="iterate",
            // the operator's Retry button (which just flips
            // ws.Status="active") triggers the orchestrator's else
            // branch at line 2293 → ProduceWorkstreamChangesAsync →
            // RunGenEvalLoopAsync → DeliverableLoop seeding (line 348)
            // picks up at iter+1 with the full prior chain so the agent
            // gets another shot at fixing the gate. The exception
            // message is also captured in Notes so the next iter's
            // evaluator sees why the prior pass got rejected.
            var rows = await db.Deliverables
                .Where(d => d.WorkItemId == "wi-validator-throw")
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal("iterate", rows[0].Verdict);
            Assert.Contains("GATE EXHAUSTED", rows[0].Notes ?? string.Empty);
            Assert.Contains(thrown.Message, rows[0].Notes ?? string.Empty);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// When the validator throws a non-<see cref="BuildFailedException"/>
    /// (e.g. unexpected runner crash), the same demote-then-rethrow path
    /// fires: row.Verdict="iterate" lands on disk before the exception
    /// propagates. Pins that the catch is generic — operators see the
    /// row as recoverable via Retry regardless of which exception type
    /// the validator threw.
    /// </summary>
    [Fact]
    public async Task PassValidator_GenericThrow_AlsoDemotes()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-generic");
        try
        {
            var req = new DeliverableLoopRequest(
                "wi-validator-generic", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) => throw new InvalidOperationException("runner crashed"));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => loop.RunAsync(req, CancellationToken.None));

            var rows = await db.Deliverables
                .Where(d => d.WorkItemId == "wi-validator-generic")
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal("iterate", rows[0].Verdict);
            Assert.Contains("runner crashed", rows[0].Notes ?? string.Empty);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Multiple consecutive rejections from the validator demote pass→iterate
    /// each time, up to the loop's iteration cap. This pins the cap behavior:
    /// even with the validator permanently rejecting, the loop still bounds
    /// runtime at <see cref="ScoringConstants.MaxIterations"/> and exits
    /// with verdict=<c>sloppy</c> (iter-cap reason), NOT
    /// <c>BuildFailedException</c>. The recovery-exhausted throw path
    /// (covered by <see cref="PassValidator_Throws_PropagatesException"/>)
    /// is the caller's responsibility — the loop itself never enforces
    /// a recovery cap; the validator wires that policy in its closure.
    /// </summary>
    [Fact]
    public async Task PassValidator_PermanentReject_HitsLoopIterCap()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-cap");
        try
        {
            const string rejectionText = "POST-PASS GATE REJECTED PASS";
            var validatorCalls = 0;
            var req = new DeliverableLoopRequest(
                "wi-validator-cap", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) =>
                {
                    validatorCalls++;
                    return Task.FromResult<string?>(rejectionText);
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            // Every iter scored pass (95) and got demoted; the loop
            // exhausts its iter cap and exits with sloppy/iter-cap. Each
            // iter invoked the validator exactly once.
            Assert.Equal("sloppy", result.FinalVerdict);
            Assert.Equal(SloppyReasons.IterCap, result.SloppyReason);
            Assert.Equal(ScoringConstants.MaxIterations, result.Iterations);
            Assert.Equal(ScoringConstants.MaxIterations, validatorCalls);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Whitespace-only return from the validator is treated as null
    /// (pass accepted). Guards against accidentally introducing a
    /// demotion when the caller intended a no-op. Same convention as
    /// the existing unanswered-questions / checkbox demotion gates use
    /// internally (string.IsNullOrWhiteSpace).
    /// </summary>
    [Fact]
    public async Task PassValidator_WhitespaceReturn_AcceptsPass()
    {
        var (f, db, loop) = await SetupAsync("wi-validator-ws");
        try
        {
            var req = new DeliverableLoopRequest(
                "wi-validator-ws", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m)),
                OnPassValidate: (_, _) => Task.FromResult<string?>("   \r\n\t "));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// <see cref="BuildFailedException"/> gains <c>Stage</c> + <c>Command</c>
    /// properties so the orchestrator's catch handler can publish them
    /// on the spawn-finished transcript event AND enrich the workstream
    /// row's <c>StallReason</c>. Stage is one of
    /// <c>"build" | "quality" | "test"</c>; null on legacy paths that
    /// pre-date Phase 56.
    /// </summary>
    [Fact]
    public void BuildFailedException_CarriesStageAndCommand()
    {
        var ex = new BuildFailedException("post-pass quality gate exhausted")
        {
            ExitCode = 1,
            ErrorCount = 5,
            WarningCount = 0,
            ErrorSummary = "Diff in xraft-core/src/node.rs:1203: …",
            TrippedByConsecutiveIters = false,
            ConsecutiveBrokenIters = 0,
            LastIteration = 4,
            Stage = "quality",
            Command = "cargo fmt --check --all",
        };

        Assert.Equal("quality", ex.Stage);
        Assert.Equal("cargo fmt --check --all", ex.Command);
        Assert.Equal(4, ex.LastIteration);
        Assert.Equal(5, ex.ErrorCount);
    }

    /// <summary>
    /// Legacy <see cref="BuildFailedException"/> construction (no
    /// Stage/Command set) yields nulls so the orchestrator's enrichment
    /// fall-through (treats null as "build" by default) keeps working
    /// for any pre-Phase-56 call sites.
    /// </summary>
    [Fact]
    public void BuildFailedException_LegacyConstruction_LeavesStageNull()
    {
        var ex = new BuildFailedException("legacy throw")
        {
            ExitCode = 1,
            ErrorCount = 1,
            ErrorSummary = "x",
            LastIteration = 1,
        };

        Assert.Null(ex.Stage);
        Assert.Null(ex.Command);
    }
}
