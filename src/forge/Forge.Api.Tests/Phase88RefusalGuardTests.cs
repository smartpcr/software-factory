// -----------------------------------------------------------------------
// <copyright file="Phase88RefusalGuardTests.cs" company="Microsoft Corp.">
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
/// Phase 88 — <see cref="DeliverableLoop"/> must detect agent refusals
/// (short denylist output + zero tool calls) and exit immediately with
/// <c>verdict="sloppy" + SloppyReason="agent-refusal"</c> so the
/// reassignment reconciler can rotate the pair. Without the guard,
/// HD3 on code-intelligence:CLEAN-CODE stage 6.1-e2e ran 65 iters of
/// literally "DONE" with 0 tool calls — burning $458.91 before the
/// operator manually restarted.
///
/// <para>Three layers of tests:</para>
/// <list type="number">
/// <item><b>Predicate</b> — <see cref="DeliverableLoop.IsRefusalLikeOutput"/>
///   classifies short denylist strings and rejects long strings + free-form
///   responses.</item>
/// <item><b>Loop trigger</b> — full DeliverableLoop run with a generator
///   that emits "DONE" / 0 tool calls produces exactly ONE deliverable
///   stamped sloppy.</item>
/// <item><b>Anti-trigger</b> — same "DONE" output but with tool calls
///   present (or null tool count from uninstrumented runner) must NOT
///   fire the guard.</item>
/// </list>
/// </summary>
public class Phase88RefusalGuardTests
{
    private const string Fence = "```";

    private static string EvalJson(int score) =>
        Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    private static async Task<(ForgeWebApplicationFactory f, ControlDbContext control, ProductDbContext db, DeliverableLoop loop)> SetupAsync(string workItemId)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
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
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
        return (f, control, db, new DeliverableLoop(control, db));
    }

    // ── 1. predicate — short denylist hits true; everything else hits false ──

    /// <summary>
    /// Verifies the denylist (case-insensitive, trailing-punctuation
    /// tolerant) and the length cap (>80 → always false even if the
    /// prefix is "DONE"). Long outputs are presumed legit because no
    /// model will spam a paragraph and ALSO commit nothing.
    /// </summary>
    [Theory]
    // empty / whitespace
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\n\t  ", true)]
    // exact denylist entries
    [InlineData("DONE", true)]
    [InlineData("done", true)]
    [InlineData("Done.", true)]
    [InlineData("done!", true)]
    [InlineData("ok", true)]
    [InlineData("OKAY", true)]
    [InlineData("n/a", true)]
    [InlineData("N/A", true)]
    [InlineData("na", true)]
    [InlineData("nothing to do", true)]
    [InlineData("No changes needed", true)]
    [InlineData("no changes required", true)]
    [InlineData("No action needed.", true)]
    [InlineData("No action required", true)]
    [InlineData("Complete", true)]
    [InlineData("completed", true)]
    [InlineData("Finished.", true)]
    [InlineData("  done.  ", true)]
    // legitimate brief evaluator responses must NOT trip the guard
    [InlineData("Reading the test file first.", false)]
    [InlineData("Wrote 3 tests to OnboardingTests.cs", false)]
    [InlineData("Added integration coverage for the new endpoint", false)]
    [InlineData("done done done", false)] // not in denylist (post-trim "done done done")
    [InlineData("not done", false)]
    // length cap — anything > 80 chars is always false even if it
    // STARTS with a denylist phrase. Refusing models are terse; long
    // outputs are evidence of real (or at least attempted) work.
    [InlineData("DONE. I read 12 files, ran the tests, and the failing assertion is on line 42 of foo.cs.", false)]
    public void IsRefusalLikeOutput_ClassifiesCorrectly(string input, bool expected)
    {
        var actual = DeliverableLoop.IsRefusalLikeOutput(input);
        Assert.Equal(expected, actual);
    }

    // ── 2. loop — refusal with 0 tool calls fires guard at iter 1 ──

    /// <summary>
    /// End-to-end through <see cref="DeliverableLoop.RunAsync"/>: when
    /// the generator emits "DONE" with <c>ToolCallCount = 0</c>, the
    /// loop MUST exit at iter 1 with verdict=sloppy +
    /// SloppyReason=agent-refusal, write exactly one deliverable row,
    /// and never invoke the evaluator (no evaluator cost on the row).
    /// </summary>
    [Fact]
    public async Task DeliverableLoop_RefusalWithZeroToolCalls_ExitsImmediatelySloppy()
    {
        var (f, _, db, loop) = await SetupAsync("wi-refuse-zero");
        try
        {
            var evaluatorCalled = false;
            var req = new DeliverableLoopRequest(
                "wi-refuse-zero", "crew-x", "HD3", "BH",
                Generator: (_, _, _) => Task.FromResult(
                    new GeneratorRunOutput("DONE", 0.10m, ToolCallCount: 0)),
                Evaluator: (_, _, _) =>
                {
                    evaluatorCalled = true;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.20m));
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            // Guard fired at iter 1 — sloppy verdict + correct reason.
            Assert.Equal("sloppy", result.FinalVerdict);
            Assert.Equal(SloppyReasons.AgentRefusal, result.SloppyReason);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(0, result.FinalScore);

            // Evaluator was never invoked — refusal short-circuits BEFORE
            // the evaluator cost line. Without the guard, this is the
            // single biggest waste on the 65-iter incident ($7/iter
            // × evaluator).
            Assert.False(evaluatorCalled,
                "Evaluator must NOT run on a refusal — short-circuits the loop.");

            // Exactly one deliverable row written; cost = generator only.
            var rows = await db.Deliverables.Where(d => d.WorkItemId == "wi-refuse-zero").ToListAsync();
            Assert.Single(rows);
            Assert.Equal("sloppy", rows[0].Verdict);
            Assert.Equal(0.10m, rows[0].GenCostUsd);
            Assert.Equal(0m, rows[0].EvalCostUsd);
            Assert.Contains(SloppyReasons.AgentRefusal, rows[0].Notes);
        }
        finally
        {
            f.Dispose();
        }
    }

    // ── 3. anti-trigger — refusal text BUT real tool calls → keep going ──

    /// <summary>
    /// Refusal-LIKE output but the runner reports tool calls — the guard
    /// MUST NOT fire because the agent actually did work (read files,
    /// ran tests, etc.). The evaluator runs and produces a normal score.
    /// </summary>
    [Fact]
    public async Task DeliverableLoop_RefusalTextWithToolCalls_ContinuesToEvaluator()
    {
        var (f, _, db, loop) = await SetupAsync("wi-refuse-text-but-tools");
        try
        {
            var evaluatorCalled = false;
            var req = new DeliverableLoopRequest(
                "wi-refuse-text-but-tools", "crew-x", "G", "E",
                Generator: (_, _, _) => Task.FromResult(
                    new GeneratorRunOutput("DONE", 0.10m, ToolCallCount: 3)),
                Evaluator: (_, _, _) =>
                {
                    evaluatorCalled = true;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.20m));
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.True(evaluatorCalled,
                "When tool calls > 0, the loop must continue to the evaluator regardless of brief output text.");
            Assert.Equal("pass", result.FinalVerdict);
        }
        finally
        {
            f.Dispose();
        }
    }

    /// <summary>
    /// Null <c>ToolCallCount</c> models legacy / uninstrumented callers
    /// (the existing Phase 14 / Phase 24 tests, runners that haven't
    /// been wired with the counter yet). The guard MUST be a no-op for
    /// these — otherwise we'd false-positive every brief legit output
    /// from older paths.
    /// </summary>
    [Fact]
    public async Task DeliverableLoop_RefusalTextWithNullToolCount_ContinuesToEvaluator()
    {
        var (f, _, db, loop) = await SetupAsync("wi-refuse-null-toolcount");
        try
        {
            var evaluatorCalled = false;
            var req = new DeliverableLoopRequest(
                "wi-refuse-null-toolcount", "crew-x", "G", "E",
                // ToolCallCount left null — legacy / uninstrumented runner.
                Generator: (_, _, _) => Task.FromResult(
                    new GeneratorRunOutput("DONE", 0.10m)),
                Evaluator: (_, _, _) =>
                {
                    evaluatorCalled = true;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(91), 0.20m));
                });

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.True(evaluatorCalled,
                "Null ToolCallCount = uninstrumented runner; guard must not fire.");
            Assert.Equal("pass", result.FinalVerdict);
        }
        finally
        {
            f.Dispose();
        }
    }
}
