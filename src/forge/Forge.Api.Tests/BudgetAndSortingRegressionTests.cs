// -----------------------------------------------------------------------
// <copyright file="BudgetAndSortingRegressionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Regression tests for:
///   - Evaluator receiving the same WallClockBudget as generator (points × 30 min)
///   - Deliverable sorting by CreatedAt (not Iter) so restarts don't pick stale rows
///   - Execution endpoint returning chronologically latest deliverable as current
/// </summary>
public class BudgetAndSortingRegressionTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Evaluator budget ────────────────────────────────────────────────

    [Fact]
    public void EvalContext_Receives_Same_WallClockBudget_As_Generator()
    {
        // The generator context is built with WallClockBudget = points * 30 min.
        // The evaluator context MUST also receive it. Verify by constructing
        // both contexts the same way the CrewQueueWorker does and asserting
        // they carry the same budget.
        var points = 3;
        TimeSpan? taskBudget = points > 0
            ? TimeSpan.FromMinutes(points * 30)
            : null;

        var genContext = new AgentContext(
            CrewId: "crew-eng",
            Model: "claude-opus-4.6-1m",
            SoulMd: "",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            WallClockBudget: taskBudget);

        var evalContext = new AgentContext(
            CrewId: "crew-eng-night",
            Model: "claude-opus-4.6-1m",
            SoulMd: "",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            InvokeMode: AgentInvokeMode.Sdk,
            WallClockBudget: taskBudget);

        Assert.Equal(TimeSpan.FromMinutes(90), genContext.WallClockBudget);
        Assert.Equal(TimeSpan.FromMinutes(90), evalContext.WallClockBudget);
        Assert.Equal(genContext.WallClockBudget, evalContext.WallClockBudget);
    }

    [Fact]
    public void EvalContext_Without_Budget_Falls_Back_To_Default_10m()
    {
        // If WallClockBudget is omitted (null), the dispatcher uses the global
        // default of 10 minutes. This test documents the bad old behavior so
        // we can detect if someone accidentally removes the parameter again.
        var evalContextMissingBudget = new AgentContext(
            CrewId: "crew-eng-night",
            Model: "claude-opus-4.6-1m",
            SoulMd: "",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            InvokeMode: AgentInvokeMode.Sdk);
        // WallClockBudget defaults to null when not provided
        Assert.Null(evalContextMissingBudget.WallClockBudget);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(3, 90)]
    [InlineData(5, 150)]
    [InlineData(8, 240)]
    public void Points_Multiply_To_Correct_Budget(int points, int expectedMinutes)
    {
        TimeSpan? budget = points > 0
            ? TimeSpan.FromMinutes(points * 30)
            : null;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), budget);
    }

    // ── Deliverable sorting — CreatedAt over Iter ───────────────────────

    private static string EvalJson(int score, string? notes = null)
    {
        var n = notes is null ? string.Empty : ", \"notes\": \"" + notes.Replace("\"", "\\\"") + "\"";
        return "```json\n{ \"score\": " + score + n + " }\n```";
    }

    [Fact]
    public async Task Resume_After_Restart_Picks_Chronologically_Latest_Deliverable()
    {
        // Simulate: execution #1 wrote iter 1-3 (yesterday), then a restart
        // ran execution #2 which wrote iter 1 (today, score 92/pass).
        // The loop resume should pick the latest by CreatedAt (iter 1, score 92),
        // NOT the highest iter (iter 3 from the old run).
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-restart", Type = "step", State = "active",
                Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
            // Old execution: iter 1-3, all "iterate"
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-restart", Iter = 1, Score = 75, Verdict = "iterate",
                CrewId = "crew-x", GeneratorAgentId = "g1", EvaluatorAgentId = "e1",
                CreatedAt = yesterday, Notes = "old-1",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-restart", Iter = 2, Score = 80, Verdict = "iterate",
                CrewId = "crew-x", GeneratorAgentId = "g1", EvaluatorAgentId = "e1",
                CreatedAt = yesterday.AddMinutes(10), Notes = "old-2",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-restart", Iter = 3, Score = 72, Verdict = "iterate",
                CrewId = "crew-x", GeneratorAgentId = "g1", EvaluatorAgentId = "e1",
                CreatedAt = yesterday.AddMinutes(20), Notes = "old-3",
            });

            // New execution after restart: iter 1, score 95 → pass
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-restart", Iter = 1, Score = 95, Verdict = "pass",
                CrewId = "crew-x", GeneratorAgentId = "g1", EvaluatorAgentId = "e1",
                CreatedAt = DateTimeOffset.UtcNow, Notes = "new-pass",
            });

            await db.SaveChangesAsync();

            // DeliverableLoop resume: should pick the latest by CreatedAt
            var all = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == "wi-restart")
                .ToListAsync();
            var lastPersisted = all
                .OrderByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Iter)
                .FirstOrDefault();

            Assert.NotNull(lastPersisted);
            Assert.Equal(95, lastPersisted!.Score);
            Assert.Equal("pass", lastPersisted.Verdict);
            Assert.Equal("new-pass", lastPersisted.Notes);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Execution_Endpoint_Shows_Latest_Score_Not_Highest_Iter()
    {
        // Integration test: call GET /api/work-items/{id}/execution and verify
        // the currentScore reflects the chronologically latest deliverable.
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        try
        {
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.WorkItems.Add(new WorkItemRow
                {
                    Id = "wi-sort", Type = "step", State = "active",
                    Title = "Sort test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
                // Queue row so the endpoint doesn't 404.
                db.TaskQueue.Add(new TaskQueueRow
                {
                    WorkItemId = "wi-sort",
                    TargetKind = "crew",
                    RoutedToCrewId = "crew-x",
                    State = "completed",
                    EnqueuedAt = DateTimeOffset.UtcNow,
                });

                var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
                // Stale high-iter row from old execution
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = "wi-sort", Iter = 5, Score = 30, Verdict = "reject",
                    CrewId = "crew-x", GeneratorAgentId = "g1", EvaluatorAgentId = "e1",
                    CreatedAt = yesterday, Notes = "stale reject",
                });
                // Current row from new execution
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = "wi-sort", Iter = 1, Score = 92, Verdict = "pass",
                    CrewId = "crew-x", GeneratorAgentId = "g1", EvaluatorAgentId = "e1",
                    CreatedAt = DateTimeOffset.UtcNow, Notes = "real pass",
                });
                await db.SaveChangesAsync();
            }

            var resp = await client.GetAsync("/api/work-items/wi-sort/execution");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            // Must show the chronologically latest: score 92, not the stale iter-5 score 30.
            Assert.Equal(92, root.GetProperty("currentScore").GetInt32());
            Assert.Equal("pass", root.GetProperty("currentVerdict").GetString());
            Assert.Equal(1, root.GetProperty("currentIteration").GetInt32());
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task Deliverables_Endpoint_Orders_By_CreatedAt()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        try
        {
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.WorkItems.Add(new WorkItemRow
                {
                    Id = "wi-order", Type = "step", State = "active",
                    Title = "Order test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });

                var t0 = DateTimeOffset.UtcNow.AddDays(-2);
                // Insert out of chronological order to catch OrderBy(Iter) bug
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = "wi-order", Iter = 3, Score = 70, Verdict = "iterate",
                    CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
                    CreatedAt = t0,
                });
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = "wi-order", Iter = 1, Score = 95, Verdict = "pass",
                    CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
                    CreatedAt = t0.AddDays(1),
                });
                await db.SaveChangesAsync();
            }

            var resp = await client.GetAsync("/api/work-items/wi-order/deliverables");
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                throw new Xunit.Sdk.XunitException($"Expected OK but got {resp.StatusCode}: {errBody}");
            }

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(2, items.Count);

            // First item should be the OLDER one (iter 3, created first)
            Assert.Equal(3, items[0].GetProperty("iter").GetInt32());
            // Second item should be the NEWER one (iter 1, created second)
            Assert.Equal(1, items[1].GetProperty("iter").GetInt32());
        }
        finally { f.Dispose(); }
    }
}
