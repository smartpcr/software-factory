// -----------------------------------------------------------------------
// <copyright file="Phase24OriginalAgentPickupTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Threading.Tasks;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 24.19 — original-agent pickup, no round-robin (Q24.14). Pinning
/// to <c>WorkItemRow.AssignedAgent.agents[0]</c>; fallback chain when the
/// pin is unavailable; blocked when both pin AND lead are unavailable;
/// no auto round-robin to a fresh member.
/// </summary>
public class Phase24OriginalAgentPickupTests
{
    [Fact]
    public async Task EnqueueAsync_Persists_PreferredAgentId()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        // Seed a minimal work item so the partial-unique idempotency guard
        // doesn't trip.
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-pin-1",
            Title = "Pin test",
            State = "active",
            Type = "task",
        });
        await db.SaveChangesAsync();

        var row = await queue.EnqueueAsync(
            "wi-pin-1",
            "crew",
            "crew-A",
            ct: System.Threading.CancellationToken.None,
            kind: TaskQueueKinds.CommentResponse,
            preferredAgentId: "agent-original-X");

        Assert.Equal("agent-original-X", row.PreferredAgentId);
        Assert.Equal(TaskQueueKinds.CommentResponse, row.Kind);
        Assert.Equal(TaskQueueStates.Queued, row.State);
    }

    [Fact]
    public async Task EnqueueAsync_PreferredAgent_Defaults_To_Null_For_Work_Kind()
    {
        // Q24.14 — only comment-response (and future build-fix) carry a pin.
        // Regular 'work' enqueues stay round-robin.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        db.WorkItems.Add(new WorkItemRow { Id = "wi-pin-2", Title = "Work", State = "active", Type = "task" });
        await db.SaveChangesAsync();

        var row = await queue.EnqueueAsync(
            "wi-pin-2", "crew", "crew-A", System.Threading.CancellationToken.None);
        Assert.Null(row.PreferredAgentId);
    }

    [Fact]
    public async Task BlockAsync_Transitions_Queued_To_Blocked_With_LastError()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        db.WorkItems.Add(new WorkItemRow { Id = "wi-block-1", Title = "Block", State = "active", Type = "task" });
        await db.SaveChangesAsync();

        var enqueued = await queue.EnqueueAsync(
            "wi-block-1", "crew", "crew-A",
            System.Threading.CancellationToken.None,
            kind: TaskQueueKinds.CommentResponse,
            preferredAgentId: "agent-X");

        var blocked = await queue.BlockAsync(
            enqueued.Id,
            error: "comment-response.no_original_agent",
            reason: "preferred-agent-paused",
            ct: System.Threading.CancellationToken.None);

        Assert.NotNull(blocked);
        Assert.Equal(TaskQueueStates.Blocked, blocked!.State);
        Assert.Equal("comment-response.no_original_agent", blocked.LastError);
    }

    [Fact]
    public async Task BlockAsync_Refuses_Non_Queued_Row()
    {
        // Only queued → blocked is allowed; picked-up rows must be cancelled
        // (they have an active invocation).
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        db.WorkItems.Add(new WorkItemRow { Id = "wi-block-2", Title = "Block2", State = "active", Type = "task" });
        await db.SaveChangesAsync();

        var enqueued = await queue.EnqueueAsync(
            "wi-block-2", "crew", "crew-B", System.Threading.CancellationToken.None,
            kind: TaskQueueKinds.CommentResponse);

        // Move it out of queued by completing it (illegal in real flow but
        // fine for the precondition test — we just need state != queued).
        var picked = await queue.ClaimNextAsync("crew-B", "agent-X", System.Threading.CancellationToken.None);
        Assert.NotNull(picked);

        var attempt = await queue.BlockAsync(picked!.Id, "any", "any", System.Threading.CancellationToken.None);
        Assert.Null(attempt); // refused; CAS precondition fails
    }

    [Fact]
    public async Task Blocked_Counts_As_Open_For_Idempotency_Guard()
    {
        // Q24.14 — once a row is blocked, a second auto-enqueue for the same
        // work item returns the existing blocked row instead of creating a
        // duplicate. The operator's manual-assign endpoint is the unblocker.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        db.WorkItems.Add(new WorkItemRow { Id = "wi-block-3", Title = "Block3", State = "active", Type = "task" });
        await db.SaveChangesAsync();

        var first = await queue.EnqueueAsync(
            "wi-block-3", "crew", "crew-C", System.Threading.CancellationToken.None,
            kind: TaskQueueKinds.CommentResponse);

        var blocked = await queue.BlockAsync(
            first.Id, "comment-response.no_original_agent", null, System.Threading.CancellationToken.None);
        Assert.NotNull(blocked);

        var second = await queue.EnqueueAsync(
            "wi-block-3", "crew", "crew-C", System.Threading.CancellationToken.None,
            kind: TaskQueueKinds.CommentResponse);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TaskQueueStates.Blocked, second.State);
    }

    [Fact]
    public void TaskQueueStates_Blocked_Is_In_OpenStates()
    {
        // Q24.14 — see partial-unique idempotency guard at the SQL layer.
        Assert.Contains(TaskQueueStates.Blocked, TaskQueueStates.OpenStates);
    }

    [Fact]
    public async Task PreferredAgent_Pinning_Does_Not_Break_NonPinned_Rows()
    {
        // Regression — a regular 'work' row in the same queue must still
        // claim via round-robin without consulting PreferredAgentId.
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        db.WorkItems.Add(new WorkItemRow { Id = "wi-mix-1", Title = "Work", State = "active", Type = "task" });
        await db.SaveChangesAsync();

        await queue.EnqueueAsync(
            "wi-mix-1", "crew", "crew-D", System.Threading.CancellationToken.None);

        // Without an existing crew-members row the picker would skip this
        // crew on a real Sweep 2 tick, but ClaimNextAsync against a chosen
        // agent is the unit primitive — verify it still works regardless of
        // PreferredAgentId being absent.
        var claimed = await queue.ClaimNextAsync("crew-D", "agent-Y", System.Threading.CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal("agent-Y", claimed!.PickedUpByAgent);
        Assert.Null(claimed.PreferredAgentId);
    }
}
