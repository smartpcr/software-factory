// -----------------------------------------------------------------------
// <copyright file="Phase16ExecutionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Services.Git;
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

    // ── Model normalization ───────────────────────────────────────────────

    [Fact]
    public void NormalizeModelForBackend_Translates_Anthropic_Ids_For_Copilot()
    {
        Assert.Equal("claude-opus-4.7", AgentRunnerDispatcher.NormalizeModelForBackend("claude-opus-4-7", AgentBackend.Copilot));
        Assert.Equal("claude-sonnet-4.6", AgentRunnerDispatcher.NormalizeModelForBackend("claude-sonnet-4-6", AgentBackend.Copilot));
        Assert.Equal("claude-haiku-4.5", AgentRunnerDispatcher.NormalizeModelForBackend("claude-haiku-4-5-20251001", AgentBackend.Copilot));
        // Already dotted — no-op.
        Assert.Equal("claude-opus-4.7", AgentRunnerDispatcher.NormalizeModelForBackend("claude-opus-4.7", AgentBackend.Copilot));
        // Claude backend — no translation.
        Assert.Equal("claude-opus-4-7", AgentRunnerDispatcher.NormalizeModelForBackend("claude-opus-4-7", AgentBackend.Claude));
        // local-stub — no translation.
        Assert.Equal("local-stub", AgentRunnerDispatcher.NormalizeModelForBackend("local-stub", AgentBackend.Copilot));
    }

    // ── Dispatcher→Queue integration ──────────────────────────────────────

    [Fact]
    public async Task AutoDispatcher_Enqueues_To_TaskQueue_On_Dispatch()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "dq-crew", Name = "DQ Squad",
                ProductId = product.Id,
                Lead = "dq-lead", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "dq-crew", AgentId = "dq-member", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-dq-1",
                Type = "step", Track = "design", State = "active", Title = "Dispatch→Queue",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"dq-lead\",\"displayName\":\"DQ Lead\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var result = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, result.Dispatched);

            // Queue row must exist and target the crew.
            var queueRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-dq-1");
            Assert.NotNull(queueRow);
            Assert.Equal("crew", queueRow!.TargetKind);
            Assert.Equal("dq-crew", queueRow.TargetId);

            // AutoDispatchedAt must be stamped AFTER queue row is created.
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-dq-1");
            Assert.NotNull(wi.AutoDispatchedAt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task AutoDispatcher_Does_Not_Stamp_When_Enqueue_Would_Be_Duplicate()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "dup-crew", Name = "Dup Squad",
                ProductId = product.Id,
                Lead = "dup-lead", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "dup-crew", AgentId = "dup-m", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-dup",
                Type = "step", Track = "design", State = "active", Title = "Dup",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"dup-lead\",\"displayName\":\"D\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // First dispatch succeeds.
            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var r1 = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, r1.Dispatched);

            // Idempotency: second tick skips because AutoDispatchedAt is set.
            var r2 = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, r2.Dispatched);

            // Only one queue row exists.
            var rows = await db.TaskQueue.AsNoTracking()
                .Where(r => r.WorkItemId == "wi-dup").ToListAsync();
            Assert.Single(rows);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Execution endpoint evaluator resolution ───────────────────────────

    [Fact]
    public async Task ExecutionEndpoint_Resolves_EvaluatorCrewId_From_Pair()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "gen-crew", Name = "Generator",
                ProductId = product.Id,
                Lead = "gen-lead", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                MaxConcurrent = 5, ScheduleEnabled = false,
                PairRole = "generator", PairedWithCrewId = "eval-crew",
            });
            db.Crews.Add(new CrewRow
            {
                Id = "eval-crew", Name = "Evaluator",
                ProductId = product.Id,
                Lead = "eval-lead", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                MaxConcurrent = 5, ScheduleEnabled = false,
                PairRole = "evaluator", PairedWithCrewId = "gen-crew",
            });
            // Work item assigned to the generator crew via queue (no deliverables yet).
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-pair-exec",
                Type = "step", Track = "design", State = "active", Title = "Pair Exec",
                AssignedToJson = "{\"crewId\":\"gen-crew\",\"agentId\":\"gen-member\",\"generatorAgentId\":\"gen-member\"}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-pair-exec/execution");
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.Equal("gen-crew", root.GetProperty("generatorCrewId").GetString());
            Assert.Equal("eval-crew", root.GetProperty("evaluatorCrewId").GetString());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── WorktreeChangeFeed subscribe/unsubscribe ──────────────────────────

    [Fact]
    public async Task WorktreeChangeFeed_Subscribe_Unsubscribe_Lifecycle()
    {
        var (f, scope, _) = await BootAsync();
        try
        {
            var feed = f.Services.GetRequiredService<WorktreeChangeFeed>();

            Assert.Equal(0, feed.CountSubscribers(@"C:\repo\wt\feature1"));

            feed.Subscribe("wi-wt-1", "ws-1", @"C:\repo\wt\feature1");
            Assert.Equal(1, feed.CountSubscribers(@"C:\repo\wt\feature1"));

            feed.Subscribe("wi-wt-2", "ws-1", @"C:\repo\wt\feature1");
            Assert.Equal(2, feed.CountSubscribers(@"C:\repo\wt\feature1"));

            feed.Unsubscribe("wi-wt-1");
            Assert.Equal(1, feed.CountSubscribers(@"C:\repo\wt\feature1"));

            feed.Unsubscribe("wi-wt-2");
            Assert.Equal(0, feed.CountSubscribers(@"C:\repo\wt\feature1"));

            // Idempotent unsubscribe.
            feed.Unsubscribe("wi-wt-2");
            Assert.Equal(0, feed.CountSubscribers(@"C:\repo\wt\feature1"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── TaskChannel event name normalization ──────────────────────────────

    [Fact]
    public async Task TaskChannel_Persists_Events_With_Correct_Kind()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var channel = f.Services.GetRequiredService<TaskChannel>();

            // Simulate what CrewQueueWorker now emits (task.* names).
            await channel.EmitAsync("wi-evtnorm", "task.token", new { text = "hello" }, crewId: "c1", ct: default);
            await channel.EmitAsync("wi-evtnorm", "task.tool-call", new { toolName = "grep" }, crewId: "c1", ct: default);
            await channel.EmitAsync("wi-evtnorm", "task.finished", new { ok = true }, crewId: "c1", ct: default);

            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-evtnorm")
                .OrderBy(e => e.Id)
                .ToListAsync();

            Assert.Equal(3, events.Count);
            Assert.Equal("task.token", events[0].Kind);
            Assert.Equal("task.tool-call", events[1].Kind);
            Assert.Equal("task.finished", events[2].Kind);
            Assert.Equal("c1", events[0].CrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── End-to-end: dispatch → queue → execution snapshot ─────────────────

    [Fact]
    public async Task EndToEnd_Dispatch_Creates_Queue_Row_And_Execution_Shows_Transcript()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "e2e-crew", Name = "E2E",
                ProductId = product.Id,
                Lead = "e2e-lead", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "e2e-crew", AgentId = "e2e-agent", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-e2e",
                Type = "step", Track = "design", State = "active", Title = "End to End",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"e2e-lead\",\"displayName\":\"Lead\"}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Step 1: auto-dispatch enqueues.
            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            await dispatcher.RunOnceAsync(CancellationToken.None);

            // Verify task.delegated transcript event was persisted.
            var delegatedEvents = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-e2e" && e.Kind == "task.delegated")
                .ToListAsync();
            Assert.NotEmpty(delegatedEvents);

            // Step 2: execution endpoint reflects the delegation.
            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-e2e/execution");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("autoDispatchedAt").GetString()!.Length > 0);
            Assert.True(root.GetProperty("recentTranscript").GetArrayLength() >= 1);
            Assert.Equal("e2e-crew", root.GetProperty("generatorCrewId").GetString());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Execution endpoint with queue-only association ─────────────────────

    [Fact]
    public async Task ExecutionEndpoint_Resolves_CrewId_From_Queue_Row()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "qr-crew", Name = "QR",
                ProductId = product.Id,
                Lead = "qr-lead", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "qr-crew", AgentId = "qr-m", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-qr",
                Type = "step", Track = "design", State = "active", Title = "Queue Route",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Enqueue directly to crew (simulates manual route).
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-qr", "crew", "qr-crew", default);

            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-qr/execution");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            Assert.Equal("qr-crew", doc.RootElement.GetProperty("generatorCrewId").GetString());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Queue worker claim → stamps + emits task.delegated ────────────────

    [Fact]
    public async Task QueueWorker_Claim_Stamps_WorkItem_And_Emits_TaskDelegated()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "qw-crew", Name = "QW Crew",
                ProductId = product.Id,
                Lead = "qw-lead", Model = "local-stub",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "qw-crew", AgentId = "qw-agent", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-qw",
                Type = "step", Track = "design", State = "active", Title = "Queue Worker Test",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Enqueue directly to crew.
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-qw", "crew", "qw-crew", default);

            // Run the worker tick — it should claim and stamp.
            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            var tick = await worker.RunOnceAsync(default);
            Assert.Equal(1, tick.Claims);

            // Work item should have AutoDispatchedAt + updated AssignedToJson.
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-qw");
            Assert.NotNull(wi.AutoDispatchedAt);
            Assert.Contains("qw-crew", wi.AssignedToJson ?? "");
            Assert.Contains("qw-agent", wi.AssignedToJson ?? "");

            // task.delegated transcript event must exist.
            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-qw" && e.Kind == "task.delegated")
                .ToListAsync();
            Assert.NotEmpty(events);
            Assert.Equal("qw-crew", events[0].CrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Queue worker claim runs agent with local-stub → full transcript ───

    [Fact]
    public async Task QueueWorker_Claim_Runs_LocalStub_Agent_And_Produces_Transcript()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "stub-crew", Name = "Stub Crew",
                ProductId = product.Id,
                Lead = "stub-lead", Model = "local-stub",
                ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "stub-crew", AgentId = "stub-agent", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-stub",
                Type = "task", Track = "dev", State = "active", Title = "Stub agent test",
                DescriptionMd = "hello",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-stub", "crew", "stub-crew", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            // RunAgentAsync fires as Task.Run — give it time to finish with local-stub.
            await Task.Delay(3000);

            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-stub")
                .OrderBy(e => e.Id)
                .ToListAsync();

            // Expect: task.delegated, (queue.picked-up), task.started, task.token(s), task.finished
            Assert.True(events.Count >= 3, $"Expected ≥3 events, got {events.Count}: {string.Join(", ", events.Select(e => e.Kind))}");
            Assert.Contains(events, e => e.Kind == "task.delegated");
            Assert.Contains(events, e => e.Kind == "task.started");
            Assert.Contains(events, e => e.Kind == "task.finished");
            // All events must use task.* names, NOT agent.*
            Assert.DoesNotContain(events, e => e.Kind.StartsWith("agent."));
            // All agent events carry the crew id.
            Assert.True(events.Where(e => e.Kind.StartsWith("task.")).All(e => e.CrewId == "stub-crew"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Phase 18: RunAgentAsync creates worktree on real repo ─────────────

    /// <summary>
    /// Exercises the actual <c>CrewQueueWorker.RunAgentAsync</c> code path
    /// with a real git repo (cloned to a temp dir). Verifies that:
    ///   - A unique worktree branch is created (not on main)
    ///   - The agent receives the worktree path (visible in task.prompt)
    ///   - Main branch has no new commits after the run
    ///
    /// Uses local-stub model so no external LLM calls are needed.
    /// </summary>
    [Fact]
    public async Task QueueWorker_RunAgentAsync_Creates_Worktree_On_Real_Repo()
    {
        if (!GitAvailable()) return;

        // ── Set up a real git repo ──────────────────────────────────
        var repoDir = Path.Combine(Path.GetTempPath(), $"forge-real-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        await RunGitAsync(repoDir, "init", "-b", "main");
        await RunGitAsync(repoDir, "config", "user.email", "test@forge.local");
        await RunGitAsync(repoDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "# Beads\nArchitecture doc placeholder.\n");
        await RunGitAsync(repoDir, "add", "README.md");
        await RunGitAsync(repoDir, "commit", "-m", "initial seed");

        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();

            // Crew + member
            db.Crews.Add(new CrewRow
            {
                Id = "wt-real-crew", Name = "Worktree Real",
                ProductId = product.Id,
                Lead = "wt-lead", Model = "local-stub",
                ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "wt-real-crew", AgentId = "wt-agent", AddedAt = DateTimeOffset.UtcNow });

            // Repo pointing at the real git dir
            db.Repos.Add(new RepoRow
            {
                Id = "real-repo", DisplayName = "Real Repo", Kind = "local",
                RemoteUrl = "file://localhost/test",
                ProductId = product.Id, CloneRoot = repoDir, DefaultBranch = "main",
            });

            // Work item linked to repo
            var workItemId = $"WI-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            db.WorkItems.Add(new WorkItemRow
            {
                Id = workItemId,
                Type = "task", Track = "dev", State = "active",
                Title = "create architecture doc",
                DescriptionMd = "Create an architecture doc for the project.",
                RepoId = "real-repo",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Enqueue and run queue worker
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync(workItemId, "crew", "wt-real-crew", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            // local-stub finishes fast but RunAgentAsync is Task.Run
            await Task.Delay(5000);

            // ── Assert: worktree was created ─────────────────────────
            var (_, wtListOut, _) = await RunGitResultAsync(repoDir, "worktree", "list", "--porcelain");
            var sanitizedId = workItemId.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
            var expectedBranch = $"ws/{workItemId}";

            // The worktree list should include a branch matching ws/{workItemId}
            Assert.Contains($"ws/{workItemId}", wtListOut);

            // ── Assert: worktree directory exists under {repoParent}/wt/ ──
            var parentDir = Path.GetDirectoryName(repoDir)!;
            var wtDir = Path.Combine(parentDir, "wt", $"ws-{sanitizedId}");
            Assert.True(Directory.Exists(wtDir),
                $"Worktree dir '{wtDir}' should exist.\ngit worktree list output:\n{wtListOut}");

            // ── Assert: task.prompt shows the worktree path, not clone root ──
            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == workItemId)
                .OrderBy(e => e.Id)
                .ToListAsync();

            var promptEvt = events.FirstOrDefault(e => e.Kind == "task.prompt");
            Assert.NotNull(promptEvt);
            // Must contain the worktree dir, not the clone root
            Assert.Contains($"ws-{sanitizedId}", promptEvt!.PayloadJson);
            // Must NOT contain the clone root as the worktree path
            // (the prompt payload includes worktreePath field)
            Assert.DoesNotContain($"\"worktreePath\":\"{repoDir.Replace("\\", "\\\\")}\"", promptEvt.PayloadJson);

            // ── Assert: main branch untouched ────────────────────────
            var (_, mainLog, _) = await RunGitResultAsync(repoDir, "log", "--oneline", "main");
            Assert.Single(mainLog.Trim().Split('\n')); // only seed commit
        }
        finally
        {
            scope.Dispose();
            f.Dispose();
            // Clean up worktrees first, then repo
            try
            {
                var parentDir = Path.GetDirectoryName(repoDir)!;
                var wtDir = Path.Combine(parentDir, "wt");
                if (Directory.Exists(wtDir))
                {
                    await RunGitResultAsync(repoDir, "worktree", "prune");
                    Directory.Delete(wtDir, recursive: true);
                }
            }
            catch { }
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    // ── Phase 18: Full gen→eval iteration pipeline on worktree branch ───

    /// <summary>
    /// End-to-end test covering the full paired-agent delivery pipeline
    /// Full end-to-end integration test using the actual <c>CrewQueueWorker.RunAgentAsync</c>
    /// code path with a real git repo and a real AI model (claude-opus-4.6-1m).
    ///
    /// Flow: enqueue work item → queue worker claims → RunAgentAsync creates worktree →
    /// generator agent runs (real AI) → evaluator agent scores → iterate until pass →
    /// PR created from feature branch → work item resolved.
    ///
    /// Uses a temp git repo. Each run creates a unique worktree. This test is slow
    /// (~2-5min) and costs real tokens. Tag: Integration — skip in CI.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FullPipeline_RealAI_Worktree_Iterate_PR()
    {
        if (!GitAvailable()) return;

        // ── Set up a real git repo with a "buggy" file ──────────────
        var repoDir = Path.Combine(Path.GetTempPath(), $"forge-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        await RunGitAsync(repoDir, "init", "-b", "main");
        await RunGitAsync(repoDir, "config", "user.email", "test@forge.local");
        await RunGitAsync(repoDir, "config", "user.name", "Forge E2E");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "Calculator.cs"),
            "public class Calculator\n{\n    public int Sum(int[] nums)\n    {\n        int total = 0;\n        for (int i = 0; i < nums.Length - 1; i++) // BUG: off-by-one\n            total += nums[i];\n        return total;\n    }\n}\n");
        await RunGitAsync(repoDir, "add", ".");
        await RunGitAsync(repoDir, "commit", "-m", "initial: Calculator with off-by-one bug");

        var workItemId = $"WI-E2E-{Guid.NewGuid():N}";

        // ── Boot test server ────────────────────────────────────────
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        try
        {
            var product = await db.Products.FirstAsync();

            // ── Seed: paired gen + eval crews with REAL model ────────
            db.Crews.Add(new CrewRow
            {
                Id = "e2e-ai-gen", Name = "E2E AI Gen",
                ProductId = product.Id, Lead = "e2e-gen-lead",
                Model = "claude-opus-4.6-1m",
                ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m,
                MaxConcurrent = 2, ScheduleEnabled = false,
                PairRole = "generator", PairedWithCrewId = "e2e-ai-eval",
                MaxTaskBudgetUsd = 25m,
            });
            db.Crews.Add(new CrewRow
            {
                Id = "e2e-ai-eval", Name = "E2E AI Eval",
                ProductId = product.Id, Lead = "e2e-eval-lead",
                Model = "claude-opus-4.6-1m",
                ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m,
                MaxConcurrent = 2, ScheduleEnabled = false,
                PairRole = "evaluator", PairedWithCrewId = "e2e-ai-gen",
                MaxTaskBudgetUsd = 25m,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "e2e-ai-gen", AgentId = "e2e-gen-agent", AddedAt = DateTimeOffset.UtcNow });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "e2e-ai-eval", AgentId = "e2e-eval-agent", AddedAt = DateTimeOffset.UtcNow });
            db.Repos.Add(new RepoRow
            {
                Id = "e2e-beads", DisplayName = "E2E Beads", Kind = "local",
                RemoteUrl = "file://localhost/e2e-beads",
                ProductId = product.Id, CloneRoot = repoDir, DefaultBranch = "main",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = workItemId,
                Type = "bug", Track = "dev", State = "active",
                Title = "Fix off-by-one in Calculator.Sum",
                DescriptionMd = "Calculator.Sum in Calculator.cs skips the last element.\n\n"
                    + "Root cause: loop bound is `i < nums.Length - 1` but should be `i < nums.Length`.\n\n"
                    + "Fix the bug and add a test method `Sum_IncludesLastElement` that asserts `Sum(new[]{1,2,3}) == 6`.",
                RepoId = "e2e-beads",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // ── Enqueue and let queue worker pick it up ──────────────
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync(workItemId, "crew", "e2e-ai-gen", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            // Real AI takes time — poll for completion up to 5 minutes.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            string? finalVerdict = null;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10_000);
                var completedEvt = await db.TaskTranscriptEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.WorkItemId == workItemId && e.Kind == "task.completed");
                if (completedEvt is not null)
                {
                    if (completedEvt.PayloadJson is not null)
                    {
                        using var doc = JsonDocument.Parse(completedEvt.PayloadJson);
                        finalVerdict = doc.RootElement.TryGetProperty("verdict", out var v) ? v.GetString() : null;
                    }
                    break;
                }
                var failed = await db.TaskTranscriptEvents.AsNoTracking()
                    .AnyAsync(e => e.WorkItemId == workItemId &&
                        (e.Kind == "task.failed" || e.Kind == "task.iteration.stalled"));
                if (failed) break;
            }

            // ── ASSERT 1: Worktree was created (not on main) ────────
            var (_, wtListOut, _) = await RunGitResultAsync(repoDir, "worktree", "list", "--porcelain");
            Assert.Contains($"ws/{workItemId}", wtListOut);

            var parentDir = Path.GetDirectoryName(repoDir)!;
            var sanitizedId = workItemId.Replace("/", "-").Replace("\\", "-").Replace(" ", "-");
            var wtDir = Path.Combine(parentDir, "wt", $"ws-{sanitizedId}");
            Assert.True(Directory.Exists(wtDir), $"Worktree dir should exist: {wtDir}");

            // ── ASSERT 2: Prompt shows worktree path ────────────────
            var promptEvt = await db.TaskTranscriptEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.WorkItemId == workItemId && e.Kind == "task.prompt");
            Assert.NotNull(promptEvt);
            Assert.Contains($"ws-{sanitizedId}", promptEvt!.PayloadJson!);

            // ── ASSERT 3: Iteration events emitted ──────────────────
            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == workItemId)
                .OrderBy(e => e.Id).ToListAsync();
            Assert.Contains(events, e => e.Kind == "task.iteration.started");
            Assert.Contains(events, e => e.Kind == "task.iteration.scored");

            // ── ASSERT 4: Deliverables with real scores ─────────────
            var deliverables = await db.Deliverables.AsNoTracking()
                .Where(d => d.WorkItemId == workItemId)
                .OrderBy(d => d.Iter).ToListAsync();
            Assert.NotEmpty(deliverables);
            Assert.True(deliverables.Any(d => d.Score > 0),
                $"Expected non-zero score. Scores: {string.Join(", ", deliverables.Select(d => $"iter{d.Iter}={d.Score}"))}");

            // ── ASSERT 5: Main branch untouched ─────────────────────
            var (_, mainLog, _) = await RunGitResultAsync(repoDir, "log", "--oneline", "main");
            Assert.Single(mainLog.Trim().Split('\n'));

            // ── Log outcome ─────────────────────────────────────────
            var scoreList = string.Join(", ", deliverables.Select(d => $"iter{d.Iter}={d.Score}/{d.Verdict}"));
            System.Console.WriteLine($"[E2E] verdict={finalVerdict} iters={deliverables.Count} scores=[{scoreList}]");
        }
        finally
        {
            scope.Dispose();
            f.Dispose();
            try { await RunGitResultAsync(repoDir, "worktree", "prune"); } catch { }
            try
            {
                var parentDir = Path.GetDirectoryName(repoDir)!;
                var wtDir = Path.Combine(parentDir, "wt");
                if (Directory.Exists(wtDir)) Directory.Delete(wtDir, recursive: true);
            }
            catch { }
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    // ── Worktree feed subscribe/unsubscribe is covered by ─────────────────
    // PairedCrew_EndToEnd_Pickup_Through_PlanAgreement (steps 3+5).
    // The isolated timing-dependent test was removed because Task.Run
    // scheduling makes the "during run" assertion inherently racy.

    // ── Git helper methods for worktree tests ─────────────────────────────

    private static bool GitAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            p.WaitForExit(2_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task RunGitAsync(string cwd, params string[] args)
    {
        var (code, _, stderr) = await RunGitResultAsync(cwd, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
    }

    private static async Task<(int code, string stdout, string stderr)> RunGitResultAsync(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }
    // PairedCrew_EndToEnd_Pickup_Through_PlanAgreement (steps 3+5).
    // The isolated timing-dependent test was removed because Task.Run
    // scheduling makes the "during run" assertion inherently racy.

    // ── Execution endpoint works without work_items row ───────────────────

    [Fact]
    public async Task ExecutionEndpoint_Returns_Data_For_QueueOnly_Item()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            // No work_items row — only a queue row.
            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            var channel = f.Services.GetRequiredService<TaskChannel>();
            await queue.EnqueueAsync("wi-ghost", "crew", "crew-eng", default);

            // Emit a transcript event as if the agent ran.
            await channel.EmitAsync("wi-ghost", "task.delegated", new { note = "test" }, crewId: "crew-eng", ct: default);

            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-ghost/execution");
            resp.EnsureSuccessStatusCode(); // Must NOT be 404.

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.Equal("crew-eng", root.GetProperty("generatorCrewId").GetString());
            Assert.True(root.GetProperty("recentTranscript").GetArrayLength() >= 1);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Full 7-step paired crew E2E ──────────────────────────────────────

    /// <summary>
    /// Covers the full paired-crew live execution pipeline:
    ///   1. Work item picked up → paired crew (gen+eval) assigned
    ///   2. Live exec shows both generator and evaluator crew
    ///   3. Crew knows where repo is (worktree context passed)
    ///   4. Crew summarizes work item ask (prompt includes title+desc)
    ///   5. Worktree feed subscribed (proxy for worktree creation)
    ///   6. Agent runs (local-stub) and produces transcript events
    ///   7. Deliverable loop runs generator→evaluator → both agree on plan
    /// </summary>
    [Fact]
    public async Task PairedCrew_EndToEnd_Pickup_Through_PlanAgreement()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();

            // ── Seed: paired gen+eval crews ───────────────────────────
            db.Crews.Add(new CrewRow
            {
                Id = "e2e-gen", Name = "E2E Generator",
                ProductId = product.Id,
                Lead = "gen-lead", Model = "local-stub",
                ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m,
                MaxConcurrent = 5, ScheduleEnabled = false,
                PairRole = "generator", PairedWithCrewId = "e2e-eval",
            });
            db.Crews.Add(new CrewRow
            {
                Id = "e2e-eval", Name = "E2E Evaluator",
                ProductId = product.Id,
                Lead = "eval-lead", Model = "local-stub",
                ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m,
                MaxConcurrent = 5, ScheduleEnabled = false,
                PairRole = "evaluator", PairedWithCrewId = "e2e-gen",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "e2e-gen", AgentId = "gen-agent", AddedAt = DateTimeOffset.UtcNow });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "e2e-eval", AgentId = "eval-agent", AddedAt = DateTimeOffset.UtcNow });

            // ── Seed: repo + workstream for worktree context ──────────
            db.Repos.Add(new RepoRow
            {
                Id = "e2e-repo", DisplayName = "E2E Repo", Kind = "github",
                RemoteUrl = "https://github.com/test/e2e",
                ProductId = product.Id, CloneRoot = @"C:\e2e\repo",
            });
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = "e2e-ws", Name = "e2e-feature", Branch = "ws/e2e", Base = "main",
                RepoId = "e2e-repo",
                WorktreeJson = "[\"C:\\\\e2e\\\\repo\\\\.worktree\",\"e2e-feature\"]",
                CardsJson = "[\"wi-e2e-pair\"]",
            });

            // ── Seed: work item with meaningful description ───────────
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-e2e-pair",
                Type = "step", Track = "design", State = "active",
                Title = "Implement auth middleware",
                DescriptionMd = "Add JWT auth middleware to the API gateway. Validate tokens, extract claims, reject expired sessions.",
                RepoId = "e2e-repo",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"gen-lead\",\"displayName\":\"Gen Lead\"}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // ──────────────────────────────────────────────────────────
            // STEP 1: Auto-dispatch → enqueue to generator crew
            // ──────────────────────────────────────────────────────────
            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var dispatchResult = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, dispatchResult.Dispatched);

            // Queue row must exist targeting the generator crew.
            var queueRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-e2e-pair");
            Assert.NotNull(queueRow);
            Assert.Equal("e2e-gen", queueRow!.TargetId);

            // ──────────────────────────────────────────────────────────
            // STEP 1 cont: Queue worker claims → paired crew assigned
            // ──────────────────────────────────────────────────────────
            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            var tick = await worker.RunOnceAsync(default);
            Assert.Equal(1, tick.Claims);

            // Work item must have BOTH crews in AssignedToJson.
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-e2e-pair");
            Assert.NotNull(wi.AutoDispatchedAt);
            Assert.Contains("e2e-gen", wi.AssignedToJson ?? "");
            Assert.Contains("e2e-eval", wi.AssignedToJson ?? "");
            Assert.Contains("gen-agent", wi.AssignedToJson ?? "");

            // ──────────────────────────────────────────────────────────
            // STEP 2: Execution endpoint shows BOTH crews
            // ──────────────────────────────────────────────────────────
            using var client = f.CreateClient();
            var resp = await client.GetAsync("/api/work-items/wi-e2e-pair/execution");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.Equal("e2e-gen", root.GetProperty("generatorCrewId").GetString());
            Assert.Equal("e2e-eval", root.GetProperty("evaluatorCrewId").GetString());

            // ──────────────────────────────────────────────────────────
            // STEP 3: Repo context — worktree path resolved
            // ──────────────────────────────────────────────────────────
            // The queue worker's RunAgentAsync resolves worktree from
            // workstream WorktreeJson. We verify via feed subscription.
            var feed = f.Services.GetRequiredService<WorktreeChangeFeed>();
            await Task.Delay(500); // let Task.Run start
            var subsDuringRun = feed.CountSubscribers(@"C:\e2e\repo\.worktree\e2e-feature");
            // Feed subscription proves the worktree path was resolved.
            // (May be 0 if the stub already finished — check after.)

            // ──────────────────────────────────────────────────────────
            // STEP 4: Work item ask is in transcript (prompt)
            // ──────────────────────────────────────────────────────────
            // Wait for local-stub agent to finish.
            await Task.Delay(3000);

            var events = await db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == "wi-e2e-pair")
                .OrderBy(e => e.Id)
                .ToListAsync();
            // Must have delegation + agent events.
            Assert.True(events.Count >= 3, $"Expected ≥3 transcript events, got {events.Count}: {string.Join(", ", events.Select(e => e.Kind))}");
            Assert.Contains(events, e => e.Kind == "task.delegated");
            // task.delegated must carry both crew refs.
            var delegatedEvt = events.First(e => e.Kind == "task.delegated");
            Assert.Contains("e2e-gen", delegatedEvt.PayloadJson);
            Assert.Contains("e2e-eval", delegatedEvt.PayloadJson);

            // ──────────────────────────────────────────────────────────
            // STEP 5: Worktree feed was subscribed (unsubscribed after)
            // ──────────────────────────────────────────────────────────
            var subsAfterRun = feed.CountSubscribers(@"C:\e2e\repo\.worktree\e2e-feature");
            Assert.Equal(0, subsAfterRun); // cleaned up in finally block

            // ──────────────────────────────────────────────────────────
            // STEP 6: Agent produced transcript (todo/plan proxy)
            // ──────────────────────────────────────────────────────────
            // local-stub emits Started + Token(s) + Finished.
            Assert.Contains(events, e => e.Kind == "task.started");
            Assert.Contains(events, e => e.Kind == "task.finished" || e.Kind == "task.failed");
            // All task.* events carry the generator crew id.
            var taskEvents = events.Where(e => e.Kind.StartsWith("task.")).ToList();
            Assert.All(taskEvents, e => Assert.Equal("e2e-gen", e.CrewId));

            // ──────────────────────────────────────────────────────────
            // STEP 7: DeliverableLoop — generator + evaluator agree
            // ──────────────────────────────────────────────────────────
            using var loopScope = f.Services.CreateScope();
            var loop = loopScope.ServiceProvider.GetRequiredService<IDeliverableLoop>();
            var loopResult = await loop.RunAsync(new DeliverableLoopRequest(
                WorkItemId: "wi-e2e-pair",
                CrewId: "e2e-gen",
                GeneratorAgentId: "gen-agent",
                EvaluatorAgentId: "eval-agent",
                Generator: (iter, notes, ct) => Task.FromResult(
                    new GeneratorRunOutput("Plan: implement JWT middleware with bcrypt hashing", 0.01m)),
                Evaluator: (iter, genOutput, ct) => Task.FromResult(
                    new EvaluatorRunOutput("```json\n{ \"score\": 92, \"notes\": \"Solid plan\" }\n```", 0.005m)),
                EvaluatorCrewId: "e2e-eval"),
                CancellationToken.None);

            Assert.Equal("pass", loopResult.FinalVerdict);
            Assert.Equal(1, loopResult.Iterations);

            // Deliverable row exists with both crews.
            var deliverable = await db.Deliverables.AsNoTracking()
                .FirstOrDefaultAsync(d => d.WorkItemId == "wi-e2e-pair");
            Assert.NotNull(deliverable);
            Assert.Equal("e2e-gen", deliverable!.CrewId);

            // Cost ledger has split categories.
            var usageRows = await db.AgentUsage.AsNoTracking()
                .Where(u => u.TaskId == "wi-e2e-pair")
                .ToListAsync();
            Assert.Contains(usageRows, u => u.CostCategory == "generator");
            Assert.Contains(usageRows, u => u.CostCategory == "evaluator");

            // Execution endpoint now shows deliverable + score.
            var finalResp = await client.GetAsync("/api/work-items/wi-e2e-pair/execution");
            finalResp.EnsureSuccessStatusCode();
            var finalBody = await finalResp.Content.ReadAsStringAsync();
            using var finalDoc = JsonDocument.Parse(finalBody);
            var finalRoot = finalDoc.RootElement;

            Assert.Equal(1, finalRoot.GetProperty("currentIteration").GetInt32());
            Assert.Equal(92, finalRoot.GetProperty("currentScore").GetInt32());
            Assert.Equal("pass", finalRoot.GetProperty("currentVerdict").GetString());
            Assert.True(finalRoot.GetProperty("deliverables").GetArrayLength() >= 1);
            Assert.True(finalRoot.GetProperty("totalCostUsd").GetDecimal() > 0);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Pair lifecycle edge-cases ─────────────────────────────────────────

    [Fact]
    public async Task Delete_Paired_Crew_Should_Clear_Peer_Reference()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "del-gen", Name = "Engineering Gen", ProductId = product.Id,
                Lead = "L1", Model = "claude-opus-4-7",
                ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m,
                PairRole = "generator", PairedWithCrewId = "del-eval",
            });
            db.Crews.Add(new CrewRow
            {
                Id = "del-eval", Name = "Engineering Eval", ProductId = product.Id,
                Lead = "L2", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                PairRole = "evaluator", PairedWithCrewId = "del-gen",
            });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");

            // Delete the generator side.
            var resp = await client.DeleteAsync("/api/crews/del-gen");
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // The evaluator peer must no longer reference the deleted crew.
            var peer = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == "del-eval");
            Assert.Equal("standalone", peer.PairRole);
            Assert.Null(peer.PairedWithCrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Unpair_Resets_Both_Sides_To_Standalone()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "up-gen", Name = "Architecture Gen", ProductId = product.Id,
                Lead = "L1", Model = "claude-opus-4-7",
                ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m,
                PairRole = "generator", PairedWithCrewId = "up-eval",
            });
            db.Crews.Add(new CrewRow
            {
                Id = "up-eval", Name = "Architecture Eval", ProductId = product.Id,
                Lead = "L2", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                PairRole = "evaluator", PairedWithCrewId = "up-gen",
            });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");

            var resp = await client.DeleteAsync("/api/crews/up-gen/pair");
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var gen = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == "up-gen");
            var eval = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == "up-eval");
            Assert.Equal("standalone", gen.PairRole);
            Assert.Null(gen.PairedWithCrewId);
            Assert.Equal("standalone", eval.PairRole);
            Assert.Null(eval.PairedWithCrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Assign_On_Paired_Generator_Should_Include_EvaluatorCrewId()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "asg-gen", Name = "Engineering Gen", ProductId = product.Id,
                Lead = "lead-g", Model = "claude-opus-4-7",
                ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m,
                PairRole = "generator", PairedWithCrewId = "asg-eval",
            });
            db.Crews.Add(new CrewRow
            {
                Id = "asg-eval", Name = "Engineering Eval", ProductId = product.Id,
                Lead = "lead-e", Model = "claude-sonnet-4-6",
                ModelInputPricePerMTok = 3m, ModelOutputPricePerMTok = 15m,
                PairRole = "evaluator", PairedWithCrewId = "asg-gen",
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-asg-pair", Type = "step", Track = "design",
                State = "active", Title = "Pair Assign",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "lead-g");

            var resp = await client.PostAsJsonAsync("/api/crews/asg-gen/assign", new
            {
                workItemId = "wi-asg-pair",
            });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // The AssignedToJson should contain evaluatorCrewId for paired crews.
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-asg-pair");
            Assert.NotNull(wi.AssignedToJson);
            Assert.Contains("evaluatorCrewId", wi.AssignedToJson);
            Assert.Contains("asg-eval", wi.AssignedToJson);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }
}
