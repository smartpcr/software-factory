// -----------------------------------------------------------------------
// <copyright file="Phase14DeliverableLoopTests.cs" company="Microsoft Corp.">
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
/// Phase 14.4 — generator↔evaluator iteration loop tests. Drives
/// <see cref="DeliverableLoop"/> with synthetic generator + evaluator
/// delegates so the orchestration logic can be exercised without a live
/// agent runner.
/// </summary>
public class Phase14DeliverableLoopTests
{
    private const string Fence = "```";

    private static string EvalJson(int score, string? notes = null)
    {
        var n = notes is null ? string.Empty : ", \"notes\": \"" + notes.Replace("\"", "\\\"") + "\"";
        return Fence + "json\n{ \"score\": " + score + n + " }\n" + Fence;
    }

    private static async Task<(ForgeWebApplicationFactory f, ForgeDbContext db, DeliverableLoop loop)> SetupAsync(
        string workItemId = "wi-1", string workItemState = "active")
    {
        var f = new ForgeWebApplicationFactory();
        // Force the host to spin up so the DB is created + migrated.
        using (f.CreateClient()) { /* triggers boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId, Type = "step", State = workItemState,
            Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (f, db, new DeliverableLoop(db));
    }

    [Fact]
    public async Task Pass_On_First_Iter_Closes_Work_Item_And_Stops()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-pass");
        try
        {
            var req = new DeliverableLoopRequest(
                "wi-pass", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.1m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.2m)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(95, result.FinalScore);
            Assert.Equal(0.3m, result.TotalCostUsd);

            // The deliverable loop only marks the WI as "active" on a terminal
            // pass — the post-loop delivery phase moves it through
            // in-review → resolved once branch+PR succeed (DeliverableLoop.cs:319).
            var wi = await db.WorkItems.FirstAsync(w => w.Id == "wi-pass");
            Assert.Equal("active", wi.State);

            var rows = await db.Deliverables.Where(d => d.WorkItemId == "wi-pass").ToListAsync();
            Assert.Single(rows);
            Assert.Equal("pass", rows[0].Verdict);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Iterates_Through_Sub90_Until_Pass()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-iter");
        try
        {
            // Sequence: 75 → 82 → 91. Pass on iter 3.
            var sequence = new[] { 75, 82, 91 };
            var req = new DeliverableLoopRequest(
                "wi-iter", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (iter, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(sequence[iter - 1]), 0m)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(3, result.Iterations);
            Assert.Equal(91, result.FinalScore);

            var rows = await db.Deliverables.Where(d => d.WorkItemId == "wi-iter").OrderBy(d => d.Iter).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.Equal("iterate", rows[0].Verdict);
            Assert.Equal("iterate", rows[1].Verdict);
            Assert.Equal("pass",    rows[2].Verdict);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Sub70_On_First_Iter_Rejects_And_Bounces_Work_Item()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-rej", workItemState: "active");
        try
        {
            var req = new DeliverableLoopRequest(
                "wi-rej", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(50), 0m)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("reject", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);

            var wi = await db.WorkItems.FirstAsync(w => w.Id == "wi-rej");
            Assert.Equal("proposed", wi.State); // bounced back
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Stalls_After_5_Iterations_Without_Pass()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-stall");
        try
        {
            // All 5 iters return 80 (iterate range). Loop should stall on iter 5.
            var req = new DeliverableLoopRequest(
                "wi-stall", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0m)));

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("stalled", result.FinalVerdict);
            Assert.Equal(ScoringConstants.MaxIterations, result.Iterations);

            var rows = await db.Deliverables.Where(d => d.WorkItemId == "wi-stall").OrderBy(d => d.Iter).ToListAsync();
            Assert.Equal(5, rows.Count);
            // Last row's verdict was rewritten to 'stalled'.
            Assert.Equal("stalled", rows[4].Verdict);
            // Earlier rows stay as 'iterate'.
            Assert.All(rows.Take(4), r => Assert.Equal("iterate", r.Verdict));
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Budget_Cap_Stops_Early_With_StalledBudget()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-budget");
        try
        {
            // Each iter costs $0.40 gen + $0.20 eval = $0.60. Cap at $1.00 → stop after iter 2 ($1.20).
            var req = new DeliverableLoopRequest(
                "wi-budget", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.40m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(80), 0.20m)),
                MaxTaskBudgetUsd: 1.00m);

            var result = await loop.RunAsync(req, CancellationToken.None);

            Assert.Equal("stalled-budget", result.FinalVerdict);
            Assert.Equal(2, result.Iterations);
            Assert.True(result.TotalCostUsd >= 1.00m);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Score_Rounded_Down_To_69_Is_Reject_Not_Iterate()
    {
        // Boundary: 69 rejects (parser test covers DeriveVerdict; this asserts the loop wires it).
        var (f, db, loop) = await SetupAsync(workItemId: "wi-69");
        try
        {
            var req = new DeliverableLoopRequest(
                "wi-69", "crew-x", "gen-1", "eval-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(69), 0m)));

            var result = await loop.RunAsync(req, CancellationToken.None);
            Assert.Equal("reject", result.FinalVerdict);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Notes_From_Eval_Are_Threaded_Into_Next_Generator()
    {
        var (f, db, loop) = await SetupAsync(workItemId: "wi-notes");
        try
        {
            var sequence = new[] { 80, 95 };
            var capturedGenNotes = new List<string>();

            var req = new DeliverableLoopRequest(
                "wi-notes", "crew-x", "gen-1", "eval-1",
                Generator: (iter, prevNotes, _) =>
                {
                    capturedGenNotes.Add(prevNotes);
                    return Task.FromResult(new GeneratorRunOutput("out", 0m));
                },
                Evaluator: (iter, _, _) => Task.FromResult(new EvaluatorRunOutput(
                    EvalJson(sequence[iter - 1], notes: "fix " + iter), 0m)));

            await loop.RunAsync(req, CancellationToken.None);

            // iter 1 gets empty notes; iter 2 gets a chained block containing
            // the previous evaluator's notes plus the iteration header so the
            // generator sees the trajectory, not just the last line.
            Assert.Equal(2, capturedGenNotes.Count);
            Assert.Equal(string.Empty, capturedGenNotes[0]);
            Assert.Contains("Iteration 1", capturedGenNotes[1]);
            Assert.Contains("score 80", capturedGenNotes[1]);
            Assert.Contains("fix 1", capturedGenNotes[1]);
        }
        finally { f.Dispose(); }
    }
}
