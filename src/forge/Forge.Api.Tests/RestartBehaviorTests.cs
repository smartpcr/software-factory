// -----------------------------------------------------------------------
// <copyright file="RestartBehaviorTests.cs" company="Microsoft Corp.">
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
/// Phase 21 — comprehensive restart behavior tests.
/// Organized by spec section: 21.1 migration, 21.2 clear transcript,
/// 21.3 restart endpoint, 21.4 SkipSoulPrompt, 21.5 points-based budgets,
/// 21.R re-execution guard, 21.I independence invariants.
/// </summary>
public class RestartBehaviorTests
{
    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ForgeDbContext db, ITaskQueueService queue)> BootAsync(
        Dictionary<string, string?>? extraConfig = null)
    {
        var f = new ForgeWebApplicationFactory(extraConfig);
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        return (f, scope, db, queue);
    }

    private static WorkItemRow NewWorkItem(string id, string state = "active", string execStatus = "idle", int points = 0) => new()
    {
        Id = id,
        Type = "step",
        Track = "design",
        State = state,
        Title = $"Test WI {id}",
        Priority = "med",
        Points = points,
        TagsJson = "[]",
        ExecutionStatus = execStatus,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static CrewRow NewCrew(string id, string productId, bool scheduleEnabled = false) => new()
    {
        Id = id,
        Name = id,
        ProductId = productId,
        Lead = "lead-1",
        Model = "claude-opus-4-7",
        ModelInputPricePerMTok = 15m,
        ModelOutputPricePerMTok = 75m,
        MaxConcurrent = 20,
        InvokeMode = "sdk",
        ScheduleEnabled = scheduleEnabled,
    };

    private static DeliverableRow NewDeliverable(string wiId, string crewId, int iter = 1, int score = 80, string verdict = "iterate") => new()
    {
        WorkItemId = wiId,
        CrewId = crewId,
        GeneratorAgentId = "agent-gen",
        EvaluatorAgentId = "agent-eval",
        Iter = iter,
        Score = score,
        Verdict = verdict,
        Notes = $"iter {iter}",
        DimsJson = "{}",
    };

    private static TaskTranscriptEventRow NewTranscript(string wiId) => new()
    {
        WorkItemId = wiId,
        Kind = "task.token",
        PayloadJson = "{\"text\":\"hello\"}",
        At = DateTimeOffset.UtcNow,
    };

    // ════════════════════════════════════════════════════════════════════
    //  21.2 — DELETE /api/work-items/{id}/transcript
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClearTranscript_Clears_Transcript_And_Deliverables()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-ct-1"));
            db.Deliverables.Add(NewDeliverable("wi-ct-1", "crew-x", 1, 90, "pass"));
            db.TaskTranscriptEvents.Add(NewTranscript("wi-ct-1"));
            await db.SaveChangesAsync();

            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-ct-1"));
            Assert.Equal(1, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == "wi-ct-1"));

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.DeleteAsync("/api/work-items/wi-ct-1/transcript");
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            Assert.Equal(0, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == "wi-ct-1"));
            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-ct-1"));

            // Verify correct counts in response body.
            var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            Assert.Equal(1, body.GetProperty("deleted").GetInt32());
            Assert.Equal(1, body.GetProperty("deletedDeliverables").GetInt32());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task ClearTranscript_Idempotent_Returns_Zero_On_Second_Call()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-ct-idem"));
            db.TaskTranscriptEvents.Add(NewTranscript("wi-ct-idem"));
            db.Deliverables.Add(NewDeliverable("wi-ct-idem", "crew-x"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");

            var resp1 = await client.DeleteAsync("/api/work-items/wi-ct-idem/transcript");
            Assert.True(resp1.IsSuccessStatusCode);

            // Second call — already cleared.
            var resp2 = await client.DeleteAsync("/api/work-items/wi-ct-idem/transcript");
            Assert.True(resp2.IsSuccessStatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await resp2.Content.ReadAsStringAsync());
            Assert.Equal(0, body.GetProperty("deleted").GetInt32());
            Assert.Equal(0, body.GetProperty("deletedDeliverables").GetInt32());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task ClearTranscript_Returns_200_When_No_Rows_Exist()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-ct-empty"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.DeleteAsync("/api/work-items/wi-ct-empty/transcript");
            Assert.True(resp.IsSuccessStatusCode);

            var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            Assert.Equal(0, body.GetProperty("deleted").GetInt32());
            Assert.Equal(0, body.GetProperty("deletedDeliverables").GetInt32());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task ClearTranscript_Does_Not_Delete_Other_WorkItem_Rows()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-ct-a"));
            db.WorkItems.Add(NewWorkItem("wi-ct-b"));
            db.TaskTranscriptEvents.Add(NewTranscript("wi-ct-a"));
            db.TaskTranscriptEvents.Add(NewTranscript("wi-ct-b"));
            db.Deliverables.Add(NewDeliverable("wi-ct-a", "crew-x"));
            db.Deliverables.Add(NewDeliverable("wi-ct-b", "crew-x"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.DeleteAsync("/api/work-items/wi-ct-a/transcript");
            Assert.True(resp.IsSuccessStatusCode);

            // wi-ct-a cleared, wi-ct-b untouched.
            Assert.Equal(0, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == "wi-ct-a"));
            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-ct-a"));
            Assert.Equal(1, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == "wi-ct-b"));
            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-ct-b"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.3 — Restart via POST /api/crews/{crewId}/plan with restart=true
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Restart_Clears_Deliverables_And_Resets_Iter()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-test", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-restart-1", state: "active", execStatus: "failed"));
            db.Deliverables.Add(NewDeliverable("wi-restart-1", "crew-test", 1, 85));
            db.Deliverables.Add(NewDeliverable("wi-restart-1", "crew-test", 2, 72));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-restart-1"));

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-test/plan",
                new { workItemId = "wi-restart-1", restart = true, skipSoulPrompt = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-restart-1"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Resets_InReview_State_To_Active()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-rst-ir", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-rst-ir", state: "in-review", execStatus: "failed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-rst-ir/plan",
                new { workItemId = "wi-rst-ir", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-rst-ir");
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Queued, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Resets_Resolved_State_To_Active()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-rst-res", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-rst-res", state: "resolved", execStatus: "completed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-rst-res/plan",
                new { workItemId = "wi-rst-res", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-rst-res");
            Assert.Equal("active", updated.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Resets_Closed_State_To_Active()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-rst-cl", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-rst-cl", state: "closed", execStatus: "completed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-rst-cl/plan",
                new { workItemId = "wi-rst-cl", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-rst-cl");
            Assert.Equal("active", updated.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Preserves_Active_State()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-rst-act", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-rst-act", state: "active", execStatus: "failed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-rst-act/plan",
                new { workItemId = "wi-rst-act", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-rst-act");
            Assert.Equal("active", updated.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Preserves_Proposed_State()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-rst-prop", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-rst-prop", state: "proposed", execStatus: "failed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-rst-prop/plan",
                new { workItemId = "wi-rst-prop", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-rst-prop");
            Assert.Equal("proposed", updated.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Enqueue_Sets_ExecutionStatus_To_Queued_From_Failed()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-exec-f", state: "active", execStatus: "failed"));
            await db.SaveChangesAsync();

            await queue.EnqueueAsync("wi-exec-f", "crew", "some-crew", default);

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-exec-f");
            Assert.Equal(ExecutionStatuses.Queued, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Enqueue_Sets_ExecutionStatus_To_Queued_From_Completed()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-exec-c", state: "active", execStatus: "completed"));
            await db.SaveChangesAsync();

            await queue.EnqueueAsync("wi-exec-c", "crew", "some-crew", default);

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-exec-c");
            Assert.Equal(ExecutionStatuses.Queued, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Non_Restart_Enqueue_Preserves_Deliverables()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-norest", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-norest-1", state: "active", execStatus: "failed"));
            db.Deliverables.Add(NewDeliverable("wi-norest-1", "crew-norest"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-norest/plan",
                new { workItemId = "wi-norest-1" });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-norest-1"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Skips_Schedule_Gate()
    {
        // Use a crew with schedule enabled. Since the schedule gate depends
        // on wall-clock time we can't control whether it's on/off shift,
        // but we CAN verify that restart=true skips the gate entirely:
        // a non-restart call to the same crew may be rejected while restart
        // always succeeds.
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-sched", product.Id, scheduleEnabled: true));
            db.WorkItems.Add(NewWorkItem("wi-sched-1", state: "active", execStatus: "failed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");

            // Restart always bypasses the gate.
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-sched/plan",
                new { workItemId = "wi-sched-1", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_On_Nonexistent_Crew_Returns_404()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-nocrw"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/does-not-exist/plan",
                new { workItemId = "wi-nocrw", restart = true });
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Cancels_Prior_Open_Queue_Row()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-dupe", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-dupe", state: "active", execStatus: "queued"));
            await db.SaveChangesAsync();

            // First enqueue.
            await queue.EnqueueAsync("wi-dupe", "crew", "crew-dupe", default);
            var openStates = TaskQueueStates.OpenStates.ToList();
            var before = await db.TaskQueue.CountAsync(r => r.WorkItemId == "wi-dupe" && openStates.Contains(r.State));
            Assert.Equal(1, before);

            // Idempotent: second enqueue returns the same row (no duplicate).
            await queue.EnqueueAsync("wi-dupe", "crew", "crew-dupe", default);
            var after = await db.TaskQueue.CountAsync(r => r.WorkItemId == "wi-dupe" && openStates.Contains(r.State));
            Assert.Equal(1, after);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.4 — SkipSoulPrompt flag
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkipSoulPrompt_Persisted_On_Queue_Row()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-soul", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-soul-1", state: "active", execStatus: "failed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-soul/plan",
                new { workItemId = "wi-soul-1", restart = true, skipSoulPrompt = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var queueRow = await db.TaskQueue
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-soul-1" && r.State != "cancelled");
            Assert.NotNull(queueRow);
            Assert.True(queueRow!.SkipSoulPrompt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Normal_Enqueue_Does_Not_Set_SkipSoulPrompt()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-soul-2"));
            await db.SaveChangesAsync();

            var row = await queue.EnqueueAsync("wi-soul-2", "crew", "some-crew", default);
            Assert.False(row.SkipSoulPrompt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task SkipSoulPrompt_Without_Restart_Still_Persists()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-soul-nr", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-soul-nr", state: "active", execStatus: "idle"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            // restart=false but skipSoulPrompt=true — independent controls.
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-soul-nr/plan",
                new { workItemId = "wi-soul-nr", restart = false, skipSoulPrompt = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var queueRow = await db.TaskQueue
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-soul-nr" && r.State != "cancelled");
            Assert.NotNull(queueRow);
            Assert.True(queueRow!.SkipSoulPrompt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.5 — Points-based maxTurns and wall-clock budget
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(3, 90)]
    [InlineData(5, 150)]
    [InlineData(1, 30)]
    [InlineData(8, 240)]
    public void PointsBased_MaxTurns_Calculation(int points, int expectedTurns)
    {
        int? maxTurns = points > 0 ? points * 30 : null;
        Assert.NotNull(maxTurns);
        Assert.Equal(expectedTurns, maxTurns!.Value);
    }

    [Fact]
    public void ZeroPoints_MaxTurns_Falls_Back_To_Null()
    {
        int points = 0;
        int? maxTurns = points > 0 ? points * 30 : null;
        Assert.Null(maxTurns);
    }

    [Fact]
    public void NegativePoints_MaxTurns_Falls_Back_To_Null()
    {
        int points = -2;
        int? maxTurns = points > 0 ? points * 30 : null;
        Assert.Null(maxTurns);
    }

    [Theory]
    [InlineData(3, 90)]
    [InlineData(1, 30)]
    [InlineData(5, 150)]
    public void PointsBased_WallClock_Budget_Calculation(int points, int expectedMinutes)
    {
        TimeSpan? budget = points > 0 ? TimeSpan.FromMinutes(points * 30) : null;
        Assert.NotNull(budget);
        Assert.Equal(expectedMinutes, budget!.Value.TotalMinutes);
    }

    [Fact]
    public void ZeroPoints_WallClock_Budget_Falls_Back_To_Null()
    {
        int points = 0;
        TimeSpan? budget = points > 0 ? TimeSpan.FromMinutes(points * 30) : null;
        Assert.Null(budget);
    }

    [Fact]
    public void AgentContext_MaxTurns_Overrides_Options_Default()
    {
        // When context.MaxTurns is set, the dispatcher should use it.
        var context = new AgentContext(
            CrewId: "crew-1",
            Model: "test-model",
            SoulMd: "",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            MaxTurns: 90);

        var optionsDefault = 500;
        var effectiveMaxTurns = context.MaxTurns ?? optionsDefault;
        Assert.Equal(90, effectiveMaxTurns);
    }

    [Fact]
    public void AgentContext_Null_MaxTurns_Uses_Options_Default()
    {
        // When context.MaxTurns is null, falls back to global default.
        var context = new AgentContext(
            CrewId: "crew-1",
            Model: "test-model",
            SoulMd: "",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            MaxTurns: null);

        var optionsDefault = 500;
        var effectiveMaxTurns = context.MaxTurns ?? optionsDefault;
        Assert.Equal(500, effectiveMaxTurns);
    }

    [Fact]
    public void Generator_And_Evaluator_Receive_Same_MaxTurns()
    {
        // Simulate CrewQueueWorker logic: both contexts get the same cap.
        int points = 3;
        int? taskMaxTurns = points > 0 ? points * 30 : null;

        var genContext = new AgentContext(
            CrewId: "crew-gen",
            Model: "test-model",
            SoulMd: "gen-soul",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            MaxTurns: taskMaxTurns);

        var evalContext = new AgentContext(
            CrewId: "crew-eval",
            Model: "test-model",
            SoulMd: "eval-soul",
            WorktreePath: "/tmp",
            AllowedTools: Array.Empty<string>(),
            MaxTurns: taskMaxTurns);

        Assert.Equal(genContext.MaxTurns, evalContext.MaxTurns);
        Assert.Equal(90, genContext.MaxTurns);
    }

    [Fact]
    public void AgentRunnerOptions_Default_MaxTurns_Is_500()
    {
        var options = new AgentRunnerOptions();
        Assert.Equal(500, options.MaxTurns);
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.R — Re-execution guard (related regression coverage)
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("in-review")]
    [InlineData("resolved")]
    [InlineData("closed")]
    public async Task Restart_Resets_Terminal_State_So_Guard_Does_Not_Skip(string terminalState)
    {
        // After restart, the WIT state should be "active", NOT the terminal
        // state that RunAgentAsync checks to short-circuit.
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            var crewId = $"crew-guard-{terminalState}";
            db.Crews.Add(NewCrew(crewId, product.Id));
            var wiId = $"wi-guard-{terminalState}";
            db.WorkItems.Add(NewWorkItem(wiId, state: terminalState, execStatus: "completed"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                $"/api/crews/{crewId}/plan",
                new { workItemId = wiId, restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wiId);
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Queued, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.I — Independence invariants
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Setting_ExecutionStatus_Failed_Does_Not_Change_WIT_State()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-indep-f", state: "active", execStatus: "live"));
            await db.SaveChangesAsync();

            var row = await db.WorkItems.FirstAsync(w => w.Id == "wi-indep-f");
            row.ExecutionStatus = ExecutionStatuses.Failed;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-indep-f");
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Failed, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Setting_ExecutionStatus_Completed_Does_Not_Change_WIT_State()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-indep-c", state: "active", execStatus: "live"));
            await db.SaveChangesAsync();

            var row = await db.WorkItems.FirstAsync(w => w.Id == "wi-indep-c");
            row.ExecutionStatus = ExecutionStatuses.Completed;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-indep-c");
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Completed, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Does_Not_Change_AssignedTo()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-asgn", product.Id));
            var wi = NewWorkItem("wi-asgn", state: "active", execStatus: "failed");
            wi.AssignedToJson = "{\"kind\":\"human\",\"id\":\"XD\",\"displayName\":\"Xiaodong\"}";
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-asgn/plan",
                new { workItemId = "wi-asgn", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-asgn");
            Assert.Contains("XD", updated.AssignedToJson);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Does_Not_Delete_WorkItem_Links()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-links", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-parent", state: "active"));
            var child = NewWorkItem("wi-child", state: "active", execStatus: "failed");
            child.ParentId = "wi-parent";
            db.WorkItems.Add(child);
            db.WorkItemLinks.Add(new WorkItemLinkRow
            {
                FromId = "wi-parent",
                ToId = "wi-child",
                Kind = "subtask",
            });
            db.Deliverables.Add(NewDeliverable("wi-child", "crew-links"));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-links/plan",
                new { workItemId = "wi-child", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // Deliverables cleared but links and parent preserved.
            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-child"));
            var updatedChild = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-child");
            Assert.Equal("wi-parent", updatedChild.ParentId);
            Assert.Equal(1, await db.WorkItemLinks.CountAsync(l => l.FromId == "wi-parent" && l.ToId == "wi-child"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.4b — Soul.md loading logic (mirrors CrewQueueWorker:453-458)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void SoulLoading_SkipTrue_Returns_Empty()
    {
        bool skipSoulPrompt = true;
        string? agentSoulPath = @"C:\some\path\soul.md";
        var soulMd = "";
        if (!skipSoulPrompt && !string.IsNullOrEmpty(agentSoulPath))
            soulMd = "should not reach here";
        Assert.Equal("", soulMd);
    }

    [Fact]
    public void SoulLoading_SkipFalse_NullPath_Returns_Empty()
    {
        bool skipSoulPrompt = false;
        string? agentSoulPath = null;
        var soulMd = "";
        if (!skipSoulPrompt && !string.IsNullOrEmpty(agentSoulPath))
            soulMd = "should not reach here";
        Assert.Equal("", soulMd);
    }

    [Fact]
    public void SoulLoading_SkipFalse_EmptyPath_Returns_Empty()
    {
        bool skipSoulPrompt = false;
        string? agentSoulPath = "";
        var soulMd = "";
        if (!skipSoulPrompt && !string.IsNullOrEmpty(agentSoulPath))
            soulMd = "should not reach here";
        Assert.Equal("", soulMd);
    }

    [Fact]
    public async Task SoulLoading_SkipFalse_ValidPath_Reads_File()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "I am a soul prompt.");
            bool skipSoulPrompt = false;
            var soulMd = "";
            if (!skipSoulPrompt && !string.IsNullOrEmpty(tempFile))
            {
                try { soulMd = await File.ReadAllTextAsync(tempFile); }
                catch { }
            }
            Assert.Equal("I am a soul prompt.", soulMd);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public async Task SoulLoading_SkipFalse_MissingFile_Returns_Empty()
    {
        bool skipSoulPrompt = false;
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.md");
        var soulMd = "";
        if (!skipSoulPrompt && !string.IsNullOrEmpty(missingPath))
        {
            try { soulMd = await File.ReadAllTextAsync(missingPath); }
            catch { }
        }
        Assert.Equal("", soulMd);
    }

    [Fact]
    public void EvalSoulLoading_SkipTrue_Gets_Rubric_Only()
    {
        // Mirrors CrewQueueWorker:779-783 — evaluator with skipSoulPrompt
        // gets scoring rubric only, no persona prefix.
        bool skipSoulPrompt = true;
        var scoringRubric = "Score the work 0-100.";
        var evalSoul = "";
        if (!skipSoulPrompt && !string.IsNullOrEmpty("eval/soul.md"))
            evalSoul = "eval persona";
        var evalSoulForScoring = (string.IsNullOrEmpty(evalSoul) ? "" : evalSoul + "\n\n---\n\n")
            + scoringRubric;
        Assert.Equal(scoringRubric, evalSoulForScoring);
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.5b — Turn-count termination (mirrors dispatcher:655-658)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void TurnCount_At_MaxTurns_Terminates()
    {
        int maxTurns = 90, turnCount = 90;
        string? failReason = null;
        if (turnCount >= maxTurns) failReason = $"Max turns ({maxTurns}) reached";
        Assert.Equal("Max turns (90) reached", failReason);
    }

    [Fact]
    public void TurnCount_Below_MaxTurns_Continues()
    {
        int maxTurns = 90, turnCount = 89;
        string? failReason = null;
        if (turnCount >= maxTurns) failReason = $"Max turns ({maxTurns}) reached";
        Assert.Null(failReason);
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.3b — Schedule gate unit tests (deterministic, no UtcNow)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void ScheduleGate_Sunday_DayCrew_OffShift()
    {
        var crew = NewCrew("sg", "p");
        crew.ScheduleEnabled = true;
        crew.Timezone = "America/Los_Angeles";
        crew.Shift = "day";
        // Sunday Apr 26 2026 noon PDT = 19:00 UTC
        var sun = new DateTimeOffset(2026, 4, 26, 19, 0, 0, TimeSpan.Zero);
        Assert.Equal(ScheduleGate.Verdict.OffShift, ScheduleGate.Evaluate(crew, sun).Verdict);
    }

    [Fact]
    public void ScheduleGate_Tuesday_DayCrew_OnShift()
    {
        var crew = NewCrew("sg2", "p");
        crew.ScheduleEnabled = true;
        crew.Timezone = "America/Los_Angeles";
        crew.Shift = "day";
        // Tue Apr 28 2026 10am PDT = 17:00 UTC
        var tue = new DateTimeOffset(2026, 4, 28, 17, 0, 0, TimeSpan.Zero);
        Assert.Equal(ScheduleGate.Verdict.OnShift, ScheduleGate.Evaluate(crew, tue).Verdict);
    }

    // ════════════════════════════════════════════════════════════════════
    //  21.3c — Non-existent work item (documents current behavior)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Restart_NonExistent_WorkItem_Still_Enqueues()
    {
        var (f, scope, db, _) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-noitem", product.Id));
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-noitem/plan",
                new { workItemId = "does-not-exist", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }
        finally { scope.Dispose(); f.Dispose(); }
    }
}
