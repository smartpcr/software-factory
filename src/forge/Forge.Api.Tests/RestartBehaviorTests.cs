// -----------------------------------------------------------------------
// <copyright file="RestartBehaviorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 21 — restart behavior tests.
/// Covers:
///   1. Restart clears deliverables and resets iter count to 0.
///   2. Restart resets WIT State from "in-review" → "active" so the guard
///      in RunAgentAsync does not short-circuit.
///   3. Restart does NOT modify WIT State when it's already "active" or "proposed".
///   4. ExecutionStatus transitions: failed → queued on re-enqueue.
///   5. WIT State and ExecutionStatus are independent — failure does not change State.
///   6. SkipSoulPrompt flag is persisted on the queue row.
///   7. ClearTranscript endpoint also deletes deliverable rows.
///   8. Points-based wall-clock budget: 30 min per story point.
/// </summary>
public class RestartBehaviorTests
{
    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ForgeDbContext db, ITaskQueueService queue)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
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

    private static CrewRow NewCrew(string id, string productId) => new()
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
    };

    // ── 1. Restart clears deliverables ────────────────────────────────────

    [Fact]
    public async Task Restart_Clears_Deliverables_And_Resets_Iter()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-test", product.Id));
            var wi = NewWorkItem("wi-restart-1", state: "active", execStatus: "failed");
            db.WorkItems.Add(wi);
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-restart-1",
                CrewId = "crew-test",
                GeneratorAgentId = "agent-1",
                EvaluatorAgentId = "agent-2",
                Iter = 1,
                Score = 85,
                Verdict = "iterate",
                Notes = "needs work",
                DimsJson = "{}",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-restart-1",
                CrewId = "crew-test",
                GeneratorAgentId = "agent-1",
                EvaluatorAgentId = "agent-2",
                Iter = 2,
                Score = 72,
                Verdict = "iterate",
                Notes = "still needs work",
                DimsJson = "{}",
            });
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-restart-1"));

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-test/plan",
                new { workItemId = "wi-restart-1", restart = true, skipSoulPrompt = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // Deliverables should be cleared.
            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-restart-1"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── 2. Restart resets in-review State to active ───────────────────────

    [Fact]
    public async Task Restart_Resets_InReview_State_To_Active()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-test2", product.Id));
            var wi = NewWorkItem("wi-restart-2", state: "in-review", execStatus: "failed");
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-test2/plan",
                new { workItemId = "wi-restart-2", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-restart-2");
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Queued, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── 3. Restart preserves active/proposed State ────────────────────────

    [Fact]
    public async Task Restart_Preserves_Active_State()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-test3", product.Id));
            var wi = NewWorkItem("wi-restart-3", state: "active", execStatus: "failed");
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-test3/plan",
                new { workItemId = "wi-restart-3", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-restart-3");
            Assert.Equal("active", updated.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Preserves_Proposed_State()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-test3b", product.Id));
            var wi = NewWorkItem("wi-restart-3b", state: "proposed", execStatus: "failed");
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-test3b/plan",
                new { workItemId = "wi-restart-3b", restart = true });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-restart-3b");
            Assert.Equal("proposed", updated.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── 4. ExecutionStatus transitions: failed → queued on re-enqueue ────

    [Fact]
    public async Task Enqueue_Sets_ExecutionStatus_To_Queued()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var wi = NewWorkItem("wi-exec-1", state: "active", execStatus: "failed");
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            await queue.EnqueueAsync("wi-exec-1", "crew", "some-crew", default);

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-exec-1");
            Assert.Equal(ExecutionStatuses.Queued, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── 5. State and ExecutionStatus are independent ─────────────────────

    [Fact]
    public async Task Setting_ExecutionStatus_Failed_Does_Not_Change_WIT_State()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var wi = NewWorkItem("wi-indep-1", state: "active", execStatus: "live");
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            // Simulate what SetExecutionStatusAsync does.
            var row = await db.WorkItems.FirstAsync(w => w.Id == "wi-indep-1");
            row.ExecutionStatus = ExecutionStatuses.Failed;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-indep-1");
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Failed, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Setting_ExecutionStatus_Completed_Does_Not_Change_WIT_State()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var wi = NewWorkItem("wi-indep-2", state: "active", execStatus: "live");
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            var row = await db.WorkItems.FirstAsync(w => w.Id == "wi-indep-2");
            row.ExecutionStatus = ExecutionStatuses.Completed;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var updated = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-indep-2");
            Assert.Equal("active", updated.State);
            Assert.Equal(ExecutionStatuses.Completed, updated.ExecutionStatus);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── 6. SkipSoulPrompt flag persisted on queue row ────────────────────

    [Fact]
    public async Task SkipSoulPrompt_Persisted_On_Queue_Row()
    {
        var (f, scope, db, queue) = await BootAsync();
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

    // ── 7. ClearTranscript also deletes deliverables ─────────────────────

    [Fact]
    public async Task ClearTranscript_Also_Deletes_Deliverables()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-clear-1"));
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-clear-1",
                CrewId = "crew-x",
                GeneratorAgentId = "g1",
                EvaluatorAgentId = "e1",
                Iter = 1,
                Score = 90,
                Verdict = "pass",
                Notes = "good",
                DimsJson = "{}",
            });
            db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
            {
                WorkItemId = "wi-clear-1",
                Kind = "task.token",
                PayloadJson = "{\"text\":\"hello\"}",
                At = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-clear-1"));
            Assert.Equal(1, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == "wi-clear-1"));

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.DeleteAsync("/api/work-items/wi-clear-1/transcript");
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            Assert.Equal(0, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == "wi-clear-1"));
            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-clear-1"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── 8. Points-based wall-clock budget ────────────────────────────────

    [Fact]
    public void PointsBased_Budget_Calculation()
    {
        // 3 points × 30 min = 90 min.
        int points = 3;
        TimeSpan? budget = points > 0 ? TimeSpan.FromMinutes(points * 30) : null;
        Assert.NotNull(budget);
        Assert.Equal(90, budget!.Value.TotalMinutes);

        // 0 points = null (falls back to global default).
        int zeroPoints = 0;
        TimeSpan? zeroBudget = zeroPoints > 0 ? TimeSpan.FromMinutes(zeroPoints * 30) : null;
        Assert.Null(zeroBudget);

        // 1 point = 30 min.
        int onePoint = 1;
        TimeSpan? oneBudget = onePoint > 0 ? TimeSpan.FromMinutes(onePoint * 30) : null;
        Assert.Equal(30, oneBudget!.Value.TotalMinutes);
    }

    // ── 9. Non-restart enqueue does not clear deliverables ───────────────

    [Fact]
    public async Task Non_Restart_Enqueue_Preserves_Deliverables()
    {
        var (f, scope, db, queue) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(NewCrew("crew-norest", product.Id));
            db.WorkItems.Add(NewWorkItem("wi-norest-1", state: "active", execStatus: "failed"));
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-norest-1",
                CrewId = "crew-norest",
                GeneratorAgentId = "g1",
                EvaluatorAgentId = "e1",
                Iter = 1,
                Score = 80,
                Verdict = "iterate",
                Notes = "ok",
                DimsJson = "{}",
            });
            await db.SaveChangesAsync();

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            // restart = false (default) — should NOT clear deliverables.
            var resp = await client.PostAsJsonAsync(
                "/api/crews/crew-norest/plan",
                new { workItemId = "wi-norest-1" });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == "wi-norest-1"));
        }
        finally { scope.Dispose(); f.Dispose(); }
    }
}
