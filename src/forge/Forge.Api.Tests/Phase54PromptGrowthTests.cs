// -----------------------------------------------------------------------
// <copyright file="Phase54PromptGrowthTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 54 — per-iteration prompt-size growth.
/// Verifies that:
/// <list type="bullet">
///  <item><description><see cref="DeliverableLoop.WriteUsageAsync"/> persists
///   the optional <c>TurnSamplesJson</c> column.</description></item>
///  <item><description>The loop emits one <c>task.iteration.prompt-growth</c>
///   event per iter with role-split delta input/output tokens.</description></item>
///  <item><description><c>task.prompt-bloat</c> fires only when iter &gt;= 2
///   AND <c>deltaInputTokens &gt; PromptBloatGrowthThreshold</c>.</description></item>
///  <item><description>The <c>GET /api/work-items/{id}/execution-snapshot</c>
///   endpoint returns a per-iter <c>promptGrowth</c> array ordered by
///   <c>StartedAt</c> within each cost category.</description></item>
/// </list>
/// </summary>
public class Phase54PromptGrowthTests
{
    private const string Fence = "```";

    private static string EvalJson(int score) =>
        Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ProductDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrate */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return (f, scope, db);
    }

    [Fact]
    public async Task WriteUsage_persists_TurnSamplesJson_on_each_role_row()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-p54-write", Type = "step", Track = "design", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop = new DeliverableLoop(loopDb, s.ServiceProvider.GetService<TaskChannel>());

            const string genSamples = "[{\"turnIndex\":1,\"inputTokens\":1000,\"outputTokens\":200,\"atUtc\":\"2026-05-12T00:00:00Z\"}]";
            const string evalSamples = "[{\"turnIndex\":1,\"inputTokens\":500,\"outputTokens\":100,\"atUtc\":\"2026-05-12T00:00:01Z\"}]";

            var req = new DeliverableLoopRequest(
                "wi-p54-write", "crew-x", "G1", "E1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.10m, 1000, 200, genSamples)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.05m, 500, 100, evalSamples)));

            var result = await loop.RunAsync(req, CancellationToken.None);
            Assert.Equal("pass", result.FinalVerdict);

            var usage = await db.AgentUsage.AsNoTracking()
                .Where(u => u.TaskId == "wi-p54-write")
                .ToListAsync();
            Assert.Equal(2, usage.Count);
            var gen = usage.Single(u => u.CostCategory == "generator");
            var eval = usage.Single(u => u.CostCategory == "evaluator");
            Assert.Equal(genSamples, gen.TurnSamplesJson);
            Assert.Equal(evalSamples, eval.TurnSamplesJson);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task WriteUsage_persists_null_TurnSamples_when_runner_did_not_supply_any()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-p54-null", Type = "step", Track = "design", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop = new DeliverableLoop(loopDb);

            var req = new DeliverableLoopRequest(
                "wi-p54-null", "crew-x", "G1", "E1",
                // No TurnSamplesJson — uses the default-null parameter.
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.10m, 1000, 200)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.05m, 500, 100)));

            await loop.RunAsync(req, CancellationToken.None);
            var usage = await db.AgentUsage.AsNoTracking()
                .Where(u => u.TaskId == "wi-p54-null")
                .ToListAsync();
            Assert.All(usage, u => Assert.Null(u.TurnSamplesJson));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Loop_emits_prompt_growth_event_per_iter_with_correct_deltas()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-p54-growth", Type = "step", Track = "design", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop = new DeliverableLoop(loopDb, s.ServiceProvider.GetService<TaskChannel>());

            // Iter 1: gen 1000 in / eval 500 in (no prior → delta == cumulative).
            // Iter 2: gen 1500 in / eval 700 in (small growth, well below 30K threshold).
            // Iter 3: pass at 95 → loop exits.
            var genIn = new[] { 1000L, 1500L };
            var evalIn = new[] { 500L, 700L };
            var scores = new[] { 80, 95 };
            var req = new DeliverableLoopRequest(
                "wi-p54-growth", "crew-x", "G1", "E1",
                Generator: (i, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.0m, genIn[i - 1], 100)),
                Evaluator: (i, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(scores[i - 1]), 0.0m, evalIn[i - 1], 50)));

            var result = await loop.RunAsync(req, CancellationToken.None);
            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(2, result.Iterations);

            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-p54-growth" && e.Kind == "task.iteration.prompt-growth")
                .OrderBy(e => e.Id)
                .ToListAsync();
            Assert.Equal(2, events.Count);

            using var doc1 = JsonDocument.Parse(events[0].PayloadJson);
            var p1 = doc1.RootElement;
            Assert.Equal(1, p1.GetProperty("iter").GetInt32());
            Assert.Equal(1000, p1.GetProperty("generator").GetProperty("inputTokens").GetInt64());
            Assert.Equal(1000, p1.GetProperty("generator").GetProperty("deltaInputTokens").GetInt64());
            Assert.Equal(500, p1.GetProperty("evaluator").GetProperty("inputTokens").GetInt64());
            Assert.Equal(500, p1.GetProperty("evaluator").GetProperty("deltaInputTokens").GetInt64());

            using var doc2 = JsonDocument.Parse(events[1].PayloadJson);
            var p2 = doc2.RootElement;
            Assert.Equal(2, p2.GetProperty("iter").GetInt32());
            Assert.Equal(1500, p2.GetProperty("generator").GetProperty("inputTokens").GetInt64());
            Assert.Equal(500, p2.GetProperty("generator").GetProperty("deltaInputTokens").GetInt64());
            Assert.Equal(700, p2.GetProperty("evaluator").GetProperty("inputTokens").GetInt64());
            Assert.Equal(200, p2.GetProperty("evaluator").GetProperty("deltaInputTokens").GetInt64());

            // No bloat event — both deltas are well below threshold.
            var bloat = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-p54-growth" && e.Kind == "task.prompt-bloat")
                .CountAsync();
            Assert.Equal(0, bloat);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Loop_emits_prompt_bloat_when_generator_growth_exceeds_threshold()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-p54-bloat", Type = "step", Track = "design", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop = new DeliverableLoop(loopDb, s.ServiceProvider.GetService<TaskChannel>());

            // Iter 1: gen 5K in. Iter 2: gen 50K in → delta 45K > 30K threshold.
            // Pass on iter 2 to keep the loop short.
            var genIn = new[] { 5_000L, 50_000L };
            var scores = new[] { 80, 95 };
            var req = new DeliverableLoopRequest(
                "wi-p54-bloat", "crew-x", "G1", "E1",
                Generator: (i, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m, genIn[i - 1], 100)),
                Evaluator: (i, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(scores[i - 1]), 0m, 200, 50)));

            await loop.RunAsync(req, CancellationToken.None);

            var bloat = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-p54-bloat" && e.Kind == "task.prompt-bloat")
                .OrderBy(e => e.Id)
                .ToListAsync();
            Assert.Single(bloat);

            using var doc = JsonDocument.Parse(bloat[0].PayloadJson);
            var p = doc.RootElement;
            Assert.Equal(2, p.GetProperty("iter").GetInt32());
            Assert.Equal("generator", p.GetProperty("role").GetString());
            Assert.Equal(45_000, p.GetProperty("delta").GetInt64());
            Assert.Equal(ScoringConstants.PromptBloatGrowthThreshold, p.GetProperty("threshold").GetInt64());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Loop_does_not_emit_prompt_bloat_on_first_iter_even_if_above_threshold()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-p54-iter1", Type = "step", Track = "design", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop = new DeliverableLoop(loopDb, s.ServiceProvider.GetService<TaskChannel>());

            // Iter 1 alone with absolute input 100K (passes immediately) — delta
            // from prior 0 would be +100K but we suppress the warning on the
            // first iter because there's no prior to compare against.
            var req = new DeliverableLoopRequest(
                "wi-p54-iter1", "crew-x", "G1", "E1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m, 100_000L, 200)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0m, 50_000L, 100)));

            var result = await loop.RunAsync(req, CancellationToken.None);
            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(1, result.Iterations);

            var bloat = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-p54-iter1" && e.Kind == "task.prompt-bloat")
                .CountAsync();
            Assert.Equal(0, bloat);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Snapshot_endpoint_returns_promptGrowth_array_in_iter_order()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-p54-snap", Type = "step", Track = "design", State = "active",
                Title = "T", Points = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ProductDbContext>();
            var loop = new DeliverableLoop(loopDb, s.ServiceProvider.GetService<TaskChannel>());

            // 3 iterations: gen input 1K → 3K → 5K; eval 800 → 1.2K → 1.5K; pass on 3.
            var genIn = new[] { 1_000L, 3_000L, 5_000L };
            var evalIn = new[] { 800L, 1_200L, 1_500L };
            var scores = new[] { 70, 82, 95 };
            var req = new DeliverableLoopRequest(
                "wi-p54-snap", "crew-x", "G1", "E1",
                Generator: (i, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0m, genIn[i - 1], 100)),
                Evaluator: (i, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(scores[i - 1]), 0m, evalIn[i - 1], 50)));

            await loop.RunAsync(req, CancellationToken.None);

            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-p54-snap/execution");
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
            var snap = await resp.Content.ReadFromJsonAsync<TaskExecutionSnapshot>();
            Assert.NotNull(snap);
            Assert.NotNull(snap!.PromptGrowth);
            Assert.Equal(3, snap.PromptGrowth!.Count);

            Assert.Equal(1, snap.PromptGrowth[0].Iter);
            Assert.Equal(1_000, snap.PromptGrowth[0].GeneratorInputTokens);
            Assert.Equal(800,   snap.PromptGrowth[0].EvaluatorInputTokens);
            Assert.Equal(2, snap.PromptGrowth[1].Iter);
            Assert.Equal(3_000, snap.PromptGrowth[1].GeneratorInputTokens);
            Assert.Equal(1_200, snap.PromptGrowth[1].EvaluatorInputTokens);
            Assert.Equal(3, snap.PromptGrowth[2].Iter);
            Assert.Equal(5_000, snap.PromptGrowth[2].GeneratorInputTokens);
            Assert.Equal(1_500, snap.PromptGrowth[2].EvaluatorInputTokens);

            Assert.Equal(ScoringConstants.PromptBloatGrowthThreshold, snap.PromptBloatGrowthThreshold);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public void PromptBloatGrowthThreshold_constant_has_expected_value()
    {
        // Documented contract: 30K input-token delta (iter-over-iter).
        // If this changes, update docs/forge_implementation.md Phase 54 +
        // the LiveExecutionView amber-threshold comment.
        Assert.Equal(30_000L, ScoringConstants.PromptBloatGrowthThreshold);
    }
}
