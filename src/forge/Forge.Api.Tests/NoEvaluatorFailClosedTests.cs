// -----------------------------------------------------------------------
// <copyright file="NoEvaluatorFailClosedTests.cs" company="Microsoft Corp.">
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
/// Unreviewed work never passes on its own. Before 2026-11-27 both the queue
/// worker and the story strategy returned a synthetic 95 "pass" when no
/// evaluator could run, and the queue worker never resolved an evaluator for a
/// standalone crew (QA, Review) at all, so a sloppy-reassigned QA attempt on
/// harness:DSH-M2 stage 5.9-e2e merged on $0 of evaluation.
/// </summary>
public class NoEvaluatorFailClosedTests
{
    private const string Fence = "```";

    private static string EvalJson(int score) => Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ControlDbContext control, ProductDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        return (f, scope, scope.ServiceProvider.GetRequiredService<ControlDbContext>(), scope.ServiceProvider.GetRequiredService<ProductDbContext>());
    }

    private static WorkItemRow WorkItem(string id) => new()
    {
        Id = id, Type = "task", Track = "dev", State = "active", Title = "T",
        DescriptionMd = "d", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Loop_UnavailableEvaluator_StallsInsteadOfPassing()
    {
        var (f, scope, control, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(WorkItem("wi-noeval"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            var loop = scope.ServiceProvider.GetRequiredService<IDeliverableLoop>();

            var result = await loop.RunAsync(new DeliverableLoopRequest(
                "wi-noeval", "crew-x", "gen-1", "gen-1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("work", 0.5m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(string.Empty, 0m, UnavailableReason: "none"))),
                CancellationToken.None);

            Assert.Equal("stalled", result.FinalVerdict);
            Assert.Equal(0, result.FinalScore);
            Assert.Equal(1, result.Iterations);
            Assert.Equal(0.5m, result.TotalCostUsd);

            var row = await db.Deliverables.AsNoTracking().SingleAsync(d => d.WorkItemId == "wi-noeval");
            Assert.Equal("stalled", row.Verdict);
            Assert.Equal(0, row.Score);
            Assert.Equal(DeliverableLoop.EvaluatorUnavailableKind, row.FailureKind);
            Assert.StartsWith("NO EVALUATOR: none", row.Notes);

            var pair = await db.AgentPairAssignments.AsNoTracking().SingleAsync(p => p.WorkItemId == "wi-noeval");
            Assert.Equal(AgentPairAssignmentOutcomes.Cancelled, pair.Outcome);
            Assert.Equal(DeliverableLoop.EvaluatorUnavailableKind, pair.SloppyReason);

            var kinds = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-noeval").Select(e => e.Kind).ToListAsync();
            Assert.Contains("task.iteration.stalled-evaluator", kinds);
            Assert.DoesNotContain("task.iteration.passed", kinds);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Loop_RetryAfterEvaluatorConfigured_ResumesAtNextIterationAndCanPass()
    {
        var (f, scope, control, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(WorkItem("wi-noeval-retry"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            var loop = scope.ServiceProvider.GetRequiredService<IDeliverableLoop>();
            var generated = new List<int>();

            var first = await loop.RunAsync(new DeliverableLoopRequest(
                "wi-noeval-retry", "crew-x", "gen-1", "gen-1",
                Generator: (iter, _, _) => { generated.Add(iter); return Task.FromResult(new GeneratorRunOutput("work", 0.1m)); },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(string.Empty, 0m, UnavailableReason: "none"))),
                CancellationToken.None);
            Assert.Equal("stalled", first.FinalVerdict);

            var second = await loop.RunAsync(new DeliverableLoopRequest(
                "wi-noeval-retry", "crew-x", "gen-1", "eval-1",
                Generator: (iter, _, _) => { generated.Add(iter); return Task.FromResult(new GeneratorRunOutput("work", 0.1m)); },
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.2m))),
                CancellationToken.None);

            Assert.Equal("pass", second.FinalVerdict);
            Assert.Equal(new[] { 1, 2 }, generated);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task QueueWorker_StandaloneCrewWithNoOtherMember_StallsAndOpensNoPr()
    {
        var (f, scope, control, db) = await BootAsync();
        try
        {
            control.Crews.Add(new CrewRow
            {
                Id = "solo-crew", Name = "Solo Crew", Lead = "solo-lead", Model = "local-stub",
                ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m, MaxConcurrent = 5, ScheduleEnabled = false,
            });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "solo-crew", AgentId = "solo-agent", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(WorkItem("wi-solo"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            await scope.ServiceProvider.GetRequiredService<ITaskQueueService>().EnqueueAsync("wi-solo", "crew", "solo-crew", default);
            await f.Services.GetRequiredService<CrewQueueWorker>().RunOnceAsync(default);

            var row = await WaitForDeliverableAsync(db, "wi-solo");
            Assert.Equal("stalled", row.Verdict);
            Assert.Equal(DeliverableLoop.EvaluatorUnavailableKind, row.FailureKind);
            Assert.Equal(0, row.Score);

            var kinds = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-solo").Select(e => e.Kind).ToListAsync();
            Assert.DoesNotContain("task.iteration.passed", kinds);
            Assert.DoesNotContain("task.pr-opened", kinds);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task QueueWorker_StandaloneCrew_RoutesEvaluationToAnotherMember()
    {
        // The live standalone crews (QA, Review) set EvaluatorModel to a model
        // other than Model, so the evaluator runs. Offline tests only have
        // local-stub, so the queue's model-diversity rule stops the evaluator
        // turn here; what this proves is that the evaluation is routed to the
        // other member of the same crew and nothing auto-passes.
        var (f, scope, control, db) = await BootAsync();
        try
        {
            control.Crews.Add(new CrewRow
            {
                Id = "duo-crew", Name = "Duo Crew", Lead = "duo-lead", Model = "local-stub",
                ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m, MaxConcurrent = 5, ScheduleEnabled = false,
            });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "duo-crew", AgentId = "duo-a", AddedAt = DateTimeOffset.UtcNow });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "duo-crew", AgentId = "duo-b", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(WorkItem("wi-duo"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            await scope.ServiceProvider.GetRequiredService<ITaskQueueService>().EnqueueAsync("wi-duo", "crew", "duo-crew", default);
            await f.Services.GetRequiredService<CrewQueueWorker>().RunOnceAsync(default);

            var events = await WaitForEventAsync(db, "wi-duo", "task.failed", "task.iteration.passed", "task.iteration.stalled-evaluator");
            var kinds = events.Select(e => e.Kind).ToList();
            Assert.DoesNotContain("task.iteration.passed", kinds);
            Assert.DoesNotContain("task.iteration.stalled-evaluator", kinds);
            Assert.Contains(events, e => e.Kind == "task.failed" && e.PayloadJson.Contains("Pair-diversity rule violated"));

            using var started = System.Text.Json.JsonDocument.Parse(events.First(e => e.Kind == "task.iteration.started").PayloadJson);
            var gen = started.RootElement.GetProperty("generatorAgentId").GetString();
            var eval = started.RootElement.GetProperty("evaluatorAgentId").GetString();
            Assert.Equal("duo-crew", started.RootElement.GetProperty("evaluatorCrewId").GetString());
            Assert.Contains(gen, new[] { "duo-a", "duo-b" });
            Assert.Contains(eval, new[] { "duo-a", "duo-b" });
            Assert.NotEqual(gen, eval);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    private static async Task<List<TaskTranscriptEventRow>> WaitForEventAsync(ProductDbContext db, string workItemId, params string[] terminalKinds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == workItemId).OrderBy(e => e.Id).ToListAsync();
            if (events.Any(e => terminalKinds.Contains(e.Kind))) return events;
            await Task.Delay(250);
        }

        throw new TimeoutException($"None of [{string.Join(", ", terminalKinds)}] for {workItemId} within 30s.");
    }

    private static async Task<DeliverableRow> WaitForDeliverableAsync(ProductDbContext db, string workItemId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var row = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == workItemId).OrderBy(d => d.Id).FirstOrDefaultAsync();
            if (row is not null) return row;
            await Task.Delay(250);
        }

        var events = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == workItemId && e.Kind != "task.token").OrderBy(e => e.Id)
            .Select(e => e.Kind + ": " + e.PayloadJson).ToListAsync();
        var trail = string.Join(Environment.NewLine, events.Select(e => e.Length > 200 ? e[..200] : e));
        throw new TimeoutException($"No deliverable row for {workItemId} within 30s. Events:{Environment.NewLine}{trail}");
    }
}