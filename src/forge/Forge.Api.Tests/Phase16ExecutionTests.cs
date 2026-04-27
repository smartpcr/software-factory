// -----------------------------------------------------------------------
// <copyright file="Phase16ExecutionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 16 — crew execution &amp; live observability tests.
///   * Auto-dispatch: assignee == lead → row picked up + auto_dispatched_at set.
///   * Pair routing: paired-mode loop tags evaluator-side cost rows correctly.
///   * Plan validator: 21st stage / step / file-budget breach refused.
///   * Coding budget: actual > expected emits over_budget + audit row.
///   * Live channel: persisted transcript replays in order.
///   * Schedule gate: off-shift work is parked.
/// </summary>
public class Phase16ExecutionTests
{
    private const string Fence = "```";

    private static string EvalJson(int score, string? notes = null)
    {
        var n = notes is null ? string.Empty : ", \"notes\": \"" + notes.Replace("\"", "\\\"") + "\"";
        return Fence + "json\n{ \"score\": " + score + n + " }\n" + Fence;
    }

    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ForgeDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return (f, scope, db);
    }

    // ── Pair endpoints ────────────────────────────────────────────────────

    [Fact]
    public async Task Pair_Two_Arch_Crews_Sets_Symmetric_Rows()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow { Id = "arch-gen", Name = "Architecture Alpha", ProductId = product.Id, Lead = "L1", Model = "claude-opus-4-7", ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m });
            db.Crews.Add(new CrewRow { Id = "arch-eval", Name = "Architecture Beta",  ProductId = product.Id, Lead = "L2", Model = "claude-sonnet-4-6", ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync("/api/crews/arch-gen/pair", new { otherCrewId = "arch-eval" });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var a = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == "arch-gen");
            var b = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == "arch-eval");
            Assert.Equal("generator", a.PairRole);
            Assert.Equal("evaluator", b.PairRole);
            Assert.Equal("arch-eval", a.PairedWithCrewId);
            Assert.Equal("arch-gen",  b.PairedWithCrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Pair_Refuses_QA_Crew()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow { Id = "qa-1",   Name = "QA Sentinels",        ProductId = product.Id, Lead = "L1", Model = "x", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m });
            db.Crews.Add(new CrewRow { Id = "arch-1", Name = "Architecture Alpha",  ProductId = product.Id, Lead = "L2", Model = "y", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync("/api/crews/arch-1/pair", new { otherCrewId = "qa-1" });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Pair_Refuses_Cross_Product()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            // Seed a second product so the two crews come from different ones.
            db.Products.Add(new ProductRow { Id = "p2", Name = "Other", Slug = "other", CreatedAt = DateTimeOffset.UtcNow });
            db.Crews.Add(new CrewRow { Id = "arch-a", Name = "Architecture A", ProductId = "p2", Lead = "L1", Model = "x", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m });
            // The seeded "Architecture" crew already exists from ForgeSeed; pick another arch crew on a different product.
            var seededArch = await db.Crews.FirstAsync(c => c.Name.StartsWith("Architecture") && c.ProductId != "p2");
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync($"/api/crews/{seededArch.Id}/pair", new { otherCrewId = "arch-a" });
            Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Plan validator ────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_Tree_21st_Stage_Returns_400()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow { Id = "phase-x", Type = "phase", Track = "design", State = "active", Title = "P", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            for (var i = 1; i <= PlanRubricConstants.MaxStagesPerPhase; i++)
            {
                db.WorkItems.Add(new WorkItemRow { Id = $"stage-{i}", Type = "stage", Track = "design", State = "active", Title = $"S{i}", ParentId = "phase-x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            }
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync("/api/work-items", new
            {
                id = "stage-21",
                track = "design",
                type  = "stage",
                title = "S21",
                parentId = "phase-x",
            });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("tree_over_capacity", body);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Step_With_21_Files_Returns_400()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow { Id = "stage-y", Type = "stage", Track = "design", State = "active", Title = "S", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync("/api/work-items", new
            {
                id = "step-bad",
                track = "design",
                type  = "step",
                title = "Too many files",
                parentId = "stage-y",
                expectedFileChanges = PlanRubricConstants.MaxFilesPerStep + 1,
            });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("step_over_file_budget", body);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Step_Within_Budget_Persists_ExpectedFileChanges()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow { Id = "stage-ok", Type = "stage", Track = "design", State = "active", Title = "S", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync("/api/work-items", new
            {
                id = "step-ok",
                track = "design",
                type  = "step",
                title = "OK",
                parentId = "stage-ok",
                expectedFileChanges = 7,
            });
            Assert.True(resp.IsSuccessStatusCode);
            var json = await resp.Content.ReadAsStringAsync();
            Assert.Contains("\"expectedFileChanges\":7", json);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Coding budget check ───────────────────────────────────────────────

    [Fact]
    public async Task Coding_Budget_Over_Triggers_Audit_And_Warning()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "step-coding",
                Type = "step",
                Track = "design",
                State = "active",
                Title = "Code",
                ExpectedFileChanges = 5,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var channel = f.Services.GetRequiredService<TaskChannel>();
            var result = await StepBudgetChecker.CheckAsync("step-coding", actualFileCount: 12, db, channel, CancellationToken.None);
            Assert.False(result.WithinBudget);
            Assert.Equal(5, result.Expected);
            Assert.Equal(12, result.Actual);

            // Audit row recorded.
            var audit = await db.Audit.AsNoTracking()
                .Where(a => a.Path == "/api/work-items/step-coding/coding-budget")
                .FirstAsync();
            Assert.Contains("expected", audit.RequestBodySnippet);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── DeliverableLoop paired-mode ──────────────────────────────────────

    [Fact]
    public async Task Paired_Loop_Tags_Cost_Categories_Per_Side()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow { Id = "wi-pair", Type = "step", Track = "design", State = "active", Title = "P", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            // Use a fresh DI scope so DeliverableLoop picks up the registered
            // TaskChannel + writes through this scope's DbContext.
            using var s = f.Services.CreateScope();
            var loopDb = s.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var loop = new DeliverableLoop(loopDb, s.ServiceProvider.GetService<TaskChannel>());

            var req = new DeliverableLoopRequest(
                "wi-pair", "gen-crew", "G1", "E1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.10m)),
                Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(95), 0.05m)),
                MaxTaskBudgetUsd: null,
                EvaluatorCrewId: "eval-crew");

            var result = await loop.RunAsync(req, CancellationToken.None);
            Assert.Equal("pass", result.FinalVerdict);

            var usage = await db.AgentUsage.AsNoTracking()
                .Where(u => u.TaskId == "wi-pair")
                .ToListAsync();
            Assert.Equal(2, usage.Count);
            Assert.Single(usage, u => u.CostCategory == "generator" && u.CrewId == "gen-crew" && u.AgentId == "G1");
            Assert.Single(usage, u => u.CostCategory == "evaluator" && u.CrewId == "eval-crew" && u.AgentId == "E1");
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Loop_Pins_Evaluator_Across_Iterations()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow { Id = "wi-pin", Type = "step", Track = "design", State = "active", Title = "P", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var loop = new DeliverableLoop(db);
            var iter = 0;
            var req = new DeliverableLoopRequest(
                "wi-pin", "crew-x", "G1", "E1",
                Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.01m)),
                Evaluator: (i, _, _) =>
                {
                    iter = i;
                    // Iter 1 → 80 (iterate); iter 2 → 95 (pass)
                    var s = i == 1 ? 80 : 95;
                    return Task.FromResult(new EvaluatorRunOutput(EvalJson(s), 0.01m));
                });
            var result = await loop.RunAsync(req, CancellationToken.None);
            Assert.Equal("pass", result.FinalVerdict);
            Assert.Equal(2, result.Iterations);

            var rows = await db.Deliverables.AsNoTracking().Where(d => d.WorkItemId == "wi-pin").OrderBy(d => d.Iter).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("E1", r.EvaluatorAgentId));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Live channel persistence ─────────────────────────────────────────

    [Fact]
    public async Task TaskChannel_Persists_And_Replays_Transcript()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            db.WorkItems.Add(new WorkItemRow { Id = "wi-tc", Type = "step", Track = "design", State = "active", Title = "T", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var ch = f.Services.GetRequiredService<TaskChannel>();
            await ch.EmitAsync("wi-tc", "task.delegated", new { leadId = "L" });
            await ch.EmitAsync("wi-tc", "task.token", new { delta = "hello" }, agentId: "G1", crewId: "crew-x");
            await ch.EmitAsync("wi-tc", "task.token", new { delta = " world" }, agentId: "G1", crewId: "crew-x");

            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-tc/transcript");
            Assert.True(resp.IsSuccessStatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("task.delegated", body);
            Assert.Contains("task.token", body);
            Assert.Contains("hello", body);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Auto-dispatcher ──────────────────────────────────────────────────

    [Fact]
    public async Task AutoDispatcher_Stamps_Auto_Dispatched_At_For_Lead_Assigned_WorkItem()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            // Seed a crew with a lead + one member; assign a work item to the lead.
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "ad-crew", Name = "Engineering Squad",
                ProductId = product.Id,
                Lead = "lead-a", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                MaxConcurrent = 5, ScheduleEnabled = false, // bypass schedule gate for the test
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "ad-crew", AgentId = "member-a", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-auto",
                Type = "step",
                Track = "design",
                State = "active",
                Title = "Auto",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"lead-a\",\"displayName\":\"Lead A\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var result = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, result.Dispatched);

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-auto");
            Assert.NotNull(wi.AutoDispatchedAt);

            // Audit row records the dispatch.
            var audit = await db.Audit.AsNoTracking().Where(a => a.Path.Contains("auto-dispatched")).FirstAsync();
            Assert.Contains("wi-auto", audit.RequestBodySnippet);

            // Idempotency: re-running the tick must NOT re-dispatch.
            var second = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, second.Dispatched);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task AutoDispatcher_Skips_When_Capacity_Full()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "ad-crew-full", Name = "Engineering Full",
                ProductId = product.Id,
                Lead = "L", Model = "x", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 1, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "ad-crew-full", AgentId = "M", AddedAt = DateTimeOffset.UtcNow });
            // Pre-existing in-flight deliverable saturates the cap.
            db.WorkItems.Add(new WorkItemRow { Id = "wi-busy", Type = "step", Track = "design", State = "active", Title = "B", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.Deliverables.Add(new DeliverableRow { WorkItemId = "wi-busy", CrewId = "ad-crew-full", GeneratorAgentId = "M", EvaluatorAgentId = "M", Iter = 1, Score = 80, Verdict = "iterate", CreatedAt = DateTimeOffset.UtcNow });
            // The candidate we hope to (not) dispatch.
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-blocked",
                Type = "step", Track = "design", State = "active", Title = "X",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"L\",\"displayName\":\"Lead\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var result = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, result.Dispatched);
            Assert.Equal(1, result.CapacityBlocked);

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-blocked");
            Assert.Null(wi.AutoDispatchedAt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public void Porcelain_Parser_Handles_Branch_Ahead_Behind_And_Untracked()
    {
        var sample =
            "# branch.ab +3 -1\n" +
            "1 .M N... 100644 100644 100644 abc def src/foo.cs\n" +
            "1 A. N... 100644 100644 100644 abc def src/new.cs\n" +
            "1 .D N... 100644 100644 000000 abc def src/gone.cs\n" +
            "? unknown.txt\n";
        var p = WorktreeChangeFeed.ParsePorcelainV2(sample);
        Assert.Equal(3, p.Ahead);
        Assert.Equal(1, p.Behind);
        Assert.Contains("src/foo.cs",  p.ModifiedPaths);
        Assert.Contains("src/new.cs",  p.AddedPaths);
        Assert.Contains("src/gone.cs", p.DeletedPaths);
        Assert.Contains("unknown.txt", p.AddedPaths);
    }

    [Fact]
    public void TryGetAssigneeId_Honors_Camel_And_Pascal_Case()
    {
        Assert.Equal("agent-1", LeadAutoDispatcher.TryGetAssigneeId("{\"kind\":\"agent\",\"id\":\"agent-1\"}"));
        Assert.Equal("agent-2", LeadAutoDispatcher.TryGetAssigneeId("{\"Kind\":\"agent\",\"Id\":\"agent-2\"}"));
        Assert.Null(LeadAutoDispatcher.TryGetAssigneeId(null));
        Assert.Null(LeadAutoDispatcher.TryGetAssigneeId(""));
        Assert.Null(LeadAutoDispatcher.TryGetAssigneeId("not-json"));
    }
}
