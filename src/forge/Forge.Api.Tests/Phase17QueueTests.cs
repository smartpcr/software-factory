// -----------------------------------------------------------------------
// <copyright file="Phase17QueueTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 17 — persistent task queue tests.
/// Covers: state machine, ack timeout, idempotency, FIFO+priority, atomic
/// claim, restart resilience, reassign, strain, cancel, cross-crew authority.
/// </summary>
public class Phase17QueueTests
{
    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ControlDbContext control, ProductDbContext db, ITaskQueueService queue)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        return (f, scope, control, db, queue);
    }

    private static WorkItemRow NewWorkItem(string id, string priority = "med") => new()
    {
        Id = id,
        Type = "step",
        Track = "design",
        State = "active",
        Title = "T",
        Priority = priority,
        TagsJson = "[]",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task StateMachine_Happy_Path()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-1"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var enq = await queue.EnqueueAsync("wi-1", "team", "Architecture", default);
            Assert.Equal(TaskQueueStates.AwaitingAck, enq.State);
            Assert.NotNull(enq.AckRequiredBy);

            var acked = await queue.AcknowledgeAsync(enq.Id, "lead-1", auto: false, default);
            Assert.NotNull(acked);

            // Multi-crew teams stop at awaiting-route until a route happens.
            // Single-crew teams auto-fast-path to queued — pick up whichever
            // crew the route resolved to so the claim hits the right queue.
            string crewIdForClaim;
            if (acked!.State == TaskQueueStates.AwaitingRoute)
            {
                var routed = await queue.RouteToCrewAsync(enq.Id, "crew-x", "lead-1", default);
                Assert.NotNull(routed);
                Assert.Equal(TaskQueueStates.Queued, routed!.State);
                Assert.Equal("crew-x", routed.RoutedToCrewId);
                crewIdForClaim = "crew-x";
            }
            else
            {
                Assert.Equal(TaskQueueStates.Queued, acked.State);
                Assert.NotNull(acked.RoutedToCrewId);
                crewIdForClaim = acked.RoutedToCrewId!;
            }

            var claimed = await queue.ClaimNextAsync(crewIdForClaim, byAgentId: "agent-1", default);
            Assert.NotNull(claimed);
            Assert.Equal(TaskQueueStates.PickedUp, claimed!.State);

            var done = await queue.CompleteAsync(claimed.Id, default);
            Assert.NotNull(done);
            Assert.Equal(TaskQueueStates.Completed, done!.State);

            // Audit trail covers every transition.
            var history = await queue.GetHistoryAsync(enq.Id, default);
            var kinds = history.Select(h => h.Kind).ToHashSet();
            Assert.Contains("enqueued", kinds);
            Assert.Contains("acked", kinds);
            Assert.Contains("claimed", kinds);
            Assert.Contains("completed", kinds);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Idempotent_Enqueue_Returns_Existing_Open_Row()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-idem"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var first  = await queue.EnqueueAsync("wi-idem", "team", "Architecture", default);
            var second = await queue.EnqueueAsync("wi-idem", "team", "Architecture", default);
            Assert.Equal(first.Id, second.Id);

            // The partial UNIQUE means we never insert a second row.
            var rows = await db.TaskQueue.AsNoTracking().Where(r => r.WorkItemId == "wi-idem").ToListAsync();
            Assert.Single(rows);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Priority_Beats_Fifo()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.AddRange(
                NewWorkItem("wi-med", "med"),
                NewWorkItem("wi-high", "high"),
                NewWorkItem("wi-low", "low"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // Enqueue med first, then high, then low — purely team-routed
            // simulates the lead-routes-on-ack flow; we shortcut by directly
            // enqueueing crew-targeted rows so the test exercises ClaimNextAsync.
            await queue.EnqueueAsync("wi-med",  "crew", "crew-p", default);
            await queue.EnqueueAsync("wi-high", "crew", "crew-p", default);
            await queue.EnqueueAsync("wi-low",  "crew", "crew-p", default);

            var first  = await queue.ClaimNextAsync("crew-p", "agent-x", default);
            var second = await queue.ClaimNextAsync("crew-p", "agent-x", default);
            var third  = await queue.ClaimNextAsync("crew-p", "agent-x", default);

            Assert.Equal("wi-high", first?.WorkItemId);
            Assert.Equal("wi-med",  second?.WorkItemId);
            Assert.Equal("wi-low",  third?.WorkItemId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Atomic_Claim_Never_Double_Pops()
    {
        var (f, scope, control, _, _) = await BootAsync();
        try
        {
            using (var setupScope = f.Services.CreateScope())
            {
                var control_d = setupScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                var d = setupScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                d.WorkItems.Add(NewWorkItem("wi-race"));
                await d.SaveChangesAsync();
                var q = setupScope.ServiceProvider.GetRequiredService<ITaskQueueService>();
                await q.EnqueueAsync("wi-race", "crew", "crew-race", default);
            }

            // Two concurrent claims should produce one winner + one null.
            // Each call needs its own scope (DbContext isn't thread-safe).
            async Task<TaskQueueRow?> ClaimOnce()
            {
                using var s = f.Services.CreateScope();
                var q = s.ServiceProvider.GetRequiredService<ITaskQueueService>();
                return await q.ClaimNextAsync("crew-race", "agent-y", default);
            }

            var (a, b) = (ClaimOnce(), ClaimOnce());
            await Task.WhenAll(a, b);
            var winners = new[] { a.Result, b.Result }.Count(r => r is not null);
            Assert.Equal(1, winners);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Auto_Ack_On_Timeout_Sets_Auto_Acked_True()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-stale"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var row = await queue.EnqueueAsync("wi-stale", "team", "Architecture", default);
            // Simulate timeout: rewind AckRequiredBy by a minute.
            var tracked = await db.TaskQueue.FirstAsync(r => r.Id == row.Id);
            tracked.AckRequiredBy = DateTimeOffset.UtcNow.AddMinutes(-1);
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // The worker's RunOnceAsync should auto-ack.
            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            var result = await worker.RunOnceAsync(default);
            Assert.True(result.AckTimeouts >= 1);

            var refreshed = await queue.GetAsync(row.Id, default);
            Assert.NotNull(refreshed);
            Assert.True(refreshed!.AutoAcked);
            // After ack, single-crew architecture team auto-routes to its sole crew;
            // multi-crew test data may stop at awaiting-route. Either is acceptable.
            Assert.NotEqual(TaskQueueStates.AwaitingAck, refreshed.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Reassign_Moves_Routed_Crew_Without_Touching_Work_Item()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-reassign"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            await queue.EnqueueAsync("wi-reassign", "crew", "crew-a", default);
            var rows = await db.TaskQueue.AsNoTracking().Where(r => r.WorkItemId == "wi-reassign").ToListAsync();
            var row = rows.Single();

            var reassigned = await queue.ReassignAsync(row.Id, "crew-b", "lead-foo", default);
            Assert.NotNull(reassigned);
            Assert.Equal("crew-b", reassigned!.RoutedToCrewId);
            Assert.Equal(TaskQueueStates.Queued, reassigned.State);

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-reassign");
            // Work item's assignedTo isn't touched by a queue-row reassign.
            Assert.Null(wi.AssignedToJson);

            var history = await queue.GetHistoryAsync(row.Id, default);
            Assert.Contains(history, h => h.Kind == "reassigned");
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Cancel_Closes_Row_And_Leaves_Work_Item_State_Untouched()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-cancel"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var row = await queue.EnqueueAsync("wi-cancel", "crew", "crew-c", default);
            var cancelled = await queue.CancelAsync(row.Id, "lead-bar", "lost relevance", default);
            Assert.NotNull(cancelled);
            Assert.Equal(TaskQueueStates.Cancelled, cancelled!.State);

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-cancel");
            Assert.Equal("active", wi.State); // untouched

            // After cancel, a fresh enqueue is allowed (the partial-unique only
            // covers open states).
            var fresh = await queue.EnqueueAsync("wi-cancel", "crew", "crew-c", default);
            Assert.NotEqual(row.Id, fresh.Id);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Strain_Detection_Fires_When_Depth_Exceeds_Threshold()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            // Five queued items > threshold of 3.
            for (var i = 1; i <= 5; i++)
            {
                db.WorkItems.Add(NewWorkItem($"wi-strain-{i}"));
            }
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            for (var i = 1; i <= 5; i++)
            {
                await queue.EnqueueAsync($"wi-strain-{i}", "crew", "crew-strained", default);
            }

            var stats = await queue.GetCrewStatsAsync("crew-strained", default);
            Assert.Equal(5, stats.Depth);
            Assert.True(stats.Strained);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            // Crew row needed for the worker's strain sweep (resolves the lead).
            control.Crews.Add(new CrewRow
            {
                Id = "crew-strained", Name = "Engineering Strained",
                Lead = "lead-strain", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 1, ScheduleEnabled = false,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var first = await worker.RunOnceAsync(default);
            Assert.True(first.StrainSignals >= 1);

            // Debounce — second tick should not re-fire while still strained.
            var second = await worker.RunOnceAsync(default);
            Assert.Equal(0, second.StrainSignals);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Restart_Resilience_Ack_Timeouts_Catch_Up_On_Boot()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-boot"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // Stage a row with an already-elapsed ack deadline.
            var row = await queue.EnqueueAsync("wi-boot", "team", "Architecture", default);
            var tracked = await db.TaskQueue.FirstAsync(r => r.Id == row.Id);
            tracked.AckRequiredBy = DateTimeOffset.UtcNow.AddMinutes(-2);
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // The worker's tick simulates the boot-scan path (StartAsync runs
            // RunOnceAsync once before the timer takes over).
            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            var refreshed = await queue.GetAsync(row.Id, default);
            Assert.True(refreshed!.AutoAcked);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Capacity_Gate_Holds_When_MaxConcurrent_Saturated()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            control.Crews.Add(new CrewRow
            {
                Id = "crew-cap", Name = "Engineering Cap",
                Lead = "lead-cap", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 1, ScheduleEnabled = false,
            });
            db.WorkItems.Add(NewWorkItem("wi-busy"));
            db.WorkItems.Add(NewWorkItem("wi-pending"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // First row goes to picked-up to saturate the crew.
            var busy = await queue.EnqueueAsync("wi-busy", "crew", "crew-cap", default);
            await queue.ClaimNextAsync("crew-cap", "agent-z", default);

            // Second row stays queued.
            var pending = await queue.EnqueueAsync("wi-pending", "crew", "crew-cap", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            var tick = await worker.RunOnceAsync(default);
            Assert.Equal(0, tick.Claims); // capacity-blocked

            var refreshed = await queue.GetAsync(pending.Id, default);
            Assert.Equal(TaskQueueStates.Queued, refreshed!.State);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Crew_Stats_Returns_Sane_Eta_For_Empty_Queue()
    {
        var (f, scope, control, _, queue) = await BootAsync();
        try
        {
            var stats = await queue.GetCrewStatsAsync("crew-empty", default);
            Assert.Equal(0, stats.Depth);
            Assert.Equal(0, stats.EtaMinutes);
            Assert.False(stats.Strained);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task Endpoint_Acknowledge_Surfaces_409_When_Wrong_State()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-405"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            // Direct-to-crew row enters at 'queued', not awaiting-ack.
            var row = await queue.EnqueueAsync("wi-405", "crew", "crew-z", default);

            using var client = f.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forge-User", "tester");
            var resp = await client.PostAsJsonAsync($"/api/queue/{row.Id}/ack", new { });
            Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── Auto-route tests ──────────────────────────────────────────────────

    [Fact]
    public async Task AutoRoute_Single_Crew_Skips_AwaitingRoute()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            // Seed a team ("Solo") with exactly one crew whose Name starts with
            // the targetId → the ack fast-path auto-routes to queued.
            var productId = (await control.Products.FirstAsync()).Id;
            control.Crews.Add(new CrewRow
            {
                Id = "crew-solo", Name = "Solo",
                Lead = "lead-solo", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 2, ScheduleEnabled = false,
            });
            db.WorkItems.Add(NewWorkItem("wi-auto-single"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var row = await queue.EnqueueAsync("wi-auto-single", "team", "Solo", default);
            Assert.Equal(TaskQueueStates.AwaitingAck, row.State);

            var acked = await queue.AcknowledgeAsync(row.Id, "lead-solo", auto: false, default);
            Assert.NotNull(acked);
            Assert.Equal(TaskQueueStates.Queued, acked!.State);
            Assert.Equal("crew-solo", acked.RoutedToCrewId);

            var history = await queue.GetHistoryAsync(row.Id, default);
            Assert.Contains(history, h => h.Kind == "routed");
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task AutoRoute_Multi_Crew_Prefers_Generator_Side()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            var productId = (await control.Products.FirstAsync()).Id;
            control.Crews.Add(new CrewRow
            {
                Id = "crew-multi-gen", Name = "Multi",
                Lead = "lead-multi", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 2, ScheduleEnabled = false,
                PairRole = "generator", PairedWithCrewId = "crew-multi-eval",
            });
            control.Crews.Add(new CrewRow
            {
                Id = "crew-multi-eval", Name = "Multi (Night)",
                Lead = null, Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 2, ScheduleEnabled = false,
                PairRole = "evaluator", PairedWithCrewId = "crew-multi-gen",
            });
            db.WorkItems.Add(NewWorkItem("wi-auto-multi"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var row = await queue.EnqueueAsync("wi-auto-multi", "team", "Multi", default);
            var acked = await queue.AcknowledgeAsync(row.Id, "lead-multi", auto: false, default);
            Assert.NotNull(acked);

            // Multi-crew: auto-route picks the generator-side crew.
            Assert.Equal(TaskQueueStates.Queued, acked!.State);
            Assert.Equal("crew-multi-gen", acked.RoutedToCrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task AutoRoute_No_Matching_Crew_Stays_At_AwaitingRoute()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            db.WorkItems.Add(NewWorkItem("wi-auto-none"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            // "Phantom" has no crew whose Name starts with it → stays at awaiting-route.
            var row = await queue.EnqueueAsync("wi-auto-none", "team", "Phantom", default);
            var acked = await queue.AcknowledgeAsync(row.Id, "lead-x", auto: false, default);
            Assert.NotNull(acked);
            Assert.Equal(TaskQueueStates.AwaitingRoute, acked!.State);
            Assert.Null(acked.RoutedToCrewId);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task AutoRoute_Via_AckTimeout_Also_Routes()
    {
        var (f, scope, control, db, queue) = await BootAsync();
        try
        {
            var productId = (await control.Products.FirstAsync()).Id;
            control.Crews.Add(new CrewRow
            {
                Id = "crew-timeout-auto", Name = "Timeout",
                Lead = "lead-timeout", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 2, ScheduleEnabled = false,
            });
            db.WorkItems.Add(NewWorkItem("wi-auto-timeout"));
            await control.SaveChangesAsync(); await db.SaveChangesAsync();

            var row = await queue.EnqueueAsync("wi-auto-timeout", "team", "Timeout", default);

            // Auto-ack (simulates the timeout path) should also auto-route.
            var acked = await queue.AcknowledgeAsync(row.Id, byUserId: null, auto: true, default);
            Assert.NotNull(acked);
            Assert.True(acked!.AutoAcked);
            Assert.Equal(TaskQueueStates.Queued, acked.State);
            Assert.Equal("crew-timeout-auto", acked.RoutedToCrewId);

            var history = await queue.GetHistoryAsync(row.Id, default);
            Assert.Contains(history, h => h.Kind == "ack-timeout-auto");
            Assert.Contains(history, h => h.Kind == "routed");
        }
        finally { scope.Dispose(); f.Dispose(); }
    }
}
