// -----------------------------------------------------------------------
// <copyright file="Phase66StallOnQuestionsTests.cs" company="Microsoft Corp.">
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
/// Phase 66 — when generator emits <c>## Open Questions</c> that the
/// operator has not answered, the deliverable loop MUST park as
/// <c>verdict="stalled-questions"</c> with
/// <c>ExecutionStatus=AwaitingAnswers</c> instead of demoting to iterate
/// and burning more iterations. Observed on
/// <c>code-intelligence:AGENT-MEMORY</c> plan-doc
/// <c>implementation-plan.md</c>: 105 iters, score 94, $1860 burned
/// before manual halt because the loop kept hitting the same
/// unanswered-questions gate and rewriting verdict→sloppy at iter cap.
///
/// <para>These tests pin the contract: stall on first emission of
/// unanswered questions, short-circuit subsequent calls without spending
/// a generator turn, and resume from the prior chain once the answers
/// land.</para>
/// </summary>
public class Phase66StallOnQuestionsTests
{
    private const string Fence = "```";

    private static string EvalJson(int score, string? notes = null)
    {
        var n = notes is null ? string.Empty : ", \"notes\": \"" + notes.Replace("\"", "\\\"") + "\"";
        return Fence + "json\n{ \"score\": " + score + n + " }\n" + Fence;
    }

    /// <summary>
    /// Build a generator output that includes a single Open Question
    /// (id=q1, type=open) wrapped in the canonical fenced JSON block the
    /// <see cref="OpenQuestionsExtractor"/> recognises.
    /// </summary>
    private static string GenWithQuestion(string id, string text)
        => $"Some content.\n\n" + Fence + "json\n"
            + "{\n  \"openQuestions\": [\n    { \"id\": \"" + id + "\", \"text\": \"" + text.Replace("\"", "\\\"") + "\", \"type\": \"open\" }\n  ]\n}\n"
            + Fence + "\n";

    private static string GenWithoutQuestion() => "Some content without any open questions.";

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
    /// Iter 1 ends with score≥90 (would normally be pass) but the
    /// generator emitted an unanswered question. The loop MUST persist
    /// the row with verdict=<c>"stalled-questions"</c> and break out
    /// instead of demoting to iterate and looping.
    /// </summary>
    [Fact(Skip = "Phase 66 open-question stall gate removed in commit cd396be0 — tests obsolete")]
    public async Task PostIter_Gate_Persists_StalledQuestions_When_Pass_Score_With_Unanswered()
    {
        const string wi = "wi-66-postiter-pass";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var genCallCount = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    genCallCount++;
                    return Task.FromResult(new GeneratorRunOutput(GenWithQuestion("q1", "Postgres or SQLite?"), 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(94), 0.2m)),
                MaxIterations: 10);

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("stalled-questions", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(94, result.FinalScore);
            // Loop exited immediately after iter 1 — no iter 2 generator turn.
            Assert.Equal(1, genCallCount);

            // Refresh from a fresh DbContext so we don't read cached state.
            using var scope = f.Services.CreateScope();
            var control_freshDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var freshDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await freshDb.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == wi).ToListAsync();
            Assert.Single(rows);
            Assert.Equal("stalled-questions", rows[0].Verdict);
            Assert.Contains("BLOCKED", rows[0].Notes);
            Assert.Contains("1 open question unanswered", rows[0].Notes);

            // WorkItem.ExecutionStatus parked.
            var wiRow = await freshDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi);
            Assert.Equal(ExecutionStatuses.AwaitingAnswers, wiRow.ExecutionStatus);

            // The DeliverableQuestion row was persisted.
            var qs = await freshDb.DeliverableQuestions.AsNoTracking()
                .Where(q => q.WorkItemId == wi).ToListAsync();
            Assert.Single(qs);
            Assert.Null(qs[0].Answer);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Iter 1 emits a question, gets score=75 (iterate range). The loop
    /// still stalls (an iterate verdict can't converge while operator
    /// input is pending) — keeping iterate would burn additional rounds
    /// re-emitting the same question. Stall on iter 1, return immediately.
    /// </summary>
    [Fact(Skip = "Phase 66 open-question stall gate removed in commit cd396be0 — tests obsolete")]
    public async Task PostIter_Gate_Stalls_When_Iterate_Score_With_Unanswered()
    {
        const string wi = "wi-66-postiter-iterate";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var genCallCount = 0;
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) =>
                {
                    genCallCount++;
                    return Task.FromResult(new GeneratorRunOutput(GenWithQuestion("q-arch", "auth scheme?"), 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(75), 0.2m)),
                MaxIterations: 10);

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("stalled-questions", result.FinalVerdict);
            Assert.Equal(1, genCallCount);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// The pre-iter gate fires when a SECOND call to <c>RunAsync</c>
    /// finds the latest deliverable is <c>stalled-questions</c> AND the
    /// questions remain unanswered. ZERO generator + evaluator turns
    /// must be spent — this is the protection that prevents the
    /// 105-iter $1860 waste pattern.
    /// </summary>
    [Fact(Skip = "Phase 66 open-question stall gate removed in commit cd396be0 — tests obsolete")]
    public async Task PreIter_Gate_Short_Circuits_With_Zero_Agent_Cost_When_Still_Blocked()
    {
        const string wi = "wi-66-preiter";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            // First run: emit question, score 94, stalls.
            var firstReq = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(GenWithQuestion("qZ", "Z or W?"), 0.1m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(94), 0.2m)),
                MaxIterations: 10);
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("stalled-questions", first.FinalVerdict);

            // Second run: questions still unanswered. Generator + evaluator
            // MUST NOT be invoked. Use throw-delegates so any call would
            // fail loudly.
            var secondReq = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => throw new Xunit.Sdk.XunitException("Generator must NOT be invoked while questions are unanswered"),
                Evaluator: (_, _, _) => throw new Xunit.Sdk.XunitException("Evaluator must NOT be invoked while questions are unanswered"),
                MaxIterations: 10);
            var second = await loop.RunAsync(secondReq, CancellationToken.None);

            Assert.Equal("stalled-questions", second.FinalVerdict);
            Assert.Equal(0m, second.TotalCostUsd);
            Assert.Empty(second.DeliverableRowIds);

            // Only the iter-1 row remains; no extra row was persisted by the pre-iter gate.
            using var scope = f.Services.CreateScope();
            var control_freshDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var freshDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var rows = await freshDb.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == wi).ToListAsync();
            Assert.Single(rows);

            // Zero agent_usage rows (no generator turn, no evaluator turn).
            // Iter 1 wrote exactly 2 usage rows (gen + eval); the second
            // RunAsync call must NOT add any more.
            var usage = await control_freshDb.AgentUsage.AsNoTracking()
                .Where(u => u.TaskId == wi).ToListAsync();
            Assert.Equal(2, usage.Count);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Once the operator answers the question, the loop resumes:
    /// seeds the prior chain from the iter-1 row (the new "stalled-questions"
    /// is in the resume-eligible set) and continues at iter 2.
    /// </summary>
    [Fact(Skip = "Phase 66 open-question stall gate removed in commit cd396be0 — tests obsolete")]
    public async Task Resume_After_Answer_Seeds_Chain_And_Continues_From_Iter2()
    {
        const string wi = "wi-66-resume";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            // Iter 1: emit question, score 94 → stalled-questions.
            var firstReq = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(GenWithQuestion("qA", "A?"), 0.1m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(94), 0.2m)),
                MaxIterations: 10);
            var first = await loop.RunAsync(firstReq, CancellationToken.None);
            Assert.Equal("stalled-questions", first.FinalVerdict);

            // Operator answers the question via the DB directly (the
            // endpoint test below exercises the HTTP path).
            using (var s1 = f.Services.CreateScope())
            {
                var control_d1 = s1.ServiceProvider.GetRequiredService<ControlDbContext>();
                var d1 = s1.ServiceProvider.GetRequiredService<ProductDbContext>();
                var q = await d1.DeliverableQuestions.FirstAsync(q => q.WorkItemId == wi);
                q.Answer = "Use A";
                q.AnsweredAt = DateTimeOffset.UtcNow;
                q.AnsweredBy = "operator";
                await d1.SaveChangesAsync();
            }

            // Iter 2: pre-iter gate passes (unansweredCount==0), seeds
            // chain from iter 1, runs new gen+eval, score 95 → pass.
            int genIterSeen = 0;
            string priorNotes = string.Empty;
            using var scope2 = f.Services.CreateScope();
            var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop2 = new DeliverableLoop(control, db2);
            var secondReq = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (iter, prior, _) =>
                {
                    genIterSeen = iter;
                    priorNotes = prior;
                    return Task.FromResult(new GeneratorRunOutput(GenWithoutQuestion(), 0.1m));
                },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.2m)),
                MaxIterations: 10);
            var second = await loop2.RunAsync(secondReq, CancellationToken.None);

            Assert.Equal("pass", second.FinalVerdict);
            Assert.Equal(2, genIterSeen);
            // The operator's answer was fed into the generator's prelude
            // via Phase 39's `## Operator answers` block.
            Assert.Contains("Use A", priorNotes);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Reject verdict on iter 1 must NOT be demoted to stalled-questions
    /// — reject is the operator-triage terminal signal. Stalled-questions
    /// applies only to pass / iterate.
    /// </summary>
    [Fact(Skip = "Phase 66 open-question stall gate removed in commit cd396be0 — tests obsolete")]
    public async Task Reject_Verdict_Is_Preserved_Even_With_Unanswered()
    {
        const string wi = "wi-66-reject-preserved";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(GenWithQuestion("q1", "?"), 0.1m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(20), 0.2m)),
                MaxIterations: 10);

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("reject", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Iter-cap rewrite from "iterate" to "sloppy" at the bottom of the
    /// loop must NOT run when the loop exited early on stalled-questions
    /// — the early-return bypasses the cap-rewrite block entirely. This
    /// pins that a stalled-questions row is NEVER silently flipped to
    /// sloppy by the iter-cap epilogue.
    /// </summary>
    [Fact(Skip = "Phase 66 open-question stall gate removed in commit cd396be0 — tests obsolete")]
    public async Task IterCap_Sloppy_Rewrite_Is_Bypassed_By_StalledQuestions_EarlyReturn()
    {
        const string wi = "wi-66-no-sloppy-rewrite";
        var (f, control, db, loop) = await SetupAsync(wi);
        try
        {
            // MaxIterations=1 — if the loop didn't early-return on
            // stalled-questions, it would fall through to the iter-cap
            // rewrite and the deliverable verdict would become "sloppy".
            var req = new DeliverableLoopRequest(
                wi, "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput(GenWithQuestion("qX", "X?"), 0.1m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(94), 0.2m)),
                MaxIterations: 1);

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("stalled-questions", result.FinalVerdict);

            using var scope = f.Services.CreateScope();
            var control_freshDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var freshDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await freshDb.Deliverables.AsNoTracking().FirstAsync(d => d.WorkItemId == wi);
            // Must be stalled-questions, NEVER sloppy.
            Assert.Equal("stalled-questions", row.Verdict);
            Assert.DoesNotContain("SLOPPY", row.Notes);
        }
        finally { f.Dispose(); }
    }
}
