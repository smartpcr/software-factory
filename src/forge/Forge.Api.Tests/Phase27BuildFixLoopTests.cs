// -----------------------------------------------------------------------
// <copyright file="Phase27BuildFixLoopTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using FluentAssertions;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 27 Q27.7 / Q27.12 — verifies the queue
/// hook + deferral rules:
///   * an open <c>build-fix</c> row is idempotent (no duplicates),
///   * a pending row is auto-promoted on completion of an in-flight
///     comment-response or coding row.
/// </summary>
public class Phase27BuildFixLoopTests
{
    [Fact]
    public async Task BuildFix_pending_promotes_after_comment_response_completes()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        // Seed a work item with an assigned crew + agent.
        var wiId = "wi-buildfix-promo-1";
        db.WorkItems.Add(new WorkItemRow
        {
            Id = wiId,
            Type = "Task",
            Title = "Promotion test",
            State = "in-review",
            ExecutionStatus = "in-review",
            AssignedAgentJson = "{\"crewId\":\"crew-a\",\"agents\":[\"E1\"]}",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // Open comment-response row blocks build-fix promotion.
        var crRow = await queue.EnqueueAsync(wiId, "crew", "crew-a", default,
            kind: TaskQueueKinds.CommentResponse, preferredAgentId: "E1");

        // Stage a pending build-fix.
        db.BuildFixPending.Add(new BuildFixPendingRow
        {
            WorkItemId = wiId,
            BuildId = 42,
            EnqueuedAt = DateTimeOffset.UtcNow.ToString("o"),
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // While CR row is open, no build-fix exists.
        var hasBuildFix = await db.TaskQueue.AnyAsync(r =>
            r.WorkItemId == wiId && r.Kind == TaskQueueKinds.BuildFix);
        hasBuildFix.Should().BeFalse();

        // Move the CR row through claim → complete (must transition picked-up first).
        crRow.State = TaskQueueStates.PickedUp;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        var completed = await queue.CompleteAsync(crRow.Id, default);
        completed.Should().NotBeNull();

        // Pending row should be drained, build-fix queued.
        var pendingLeft = await db.BuildFixPending.AnyAsync(p => p.WorkItemId == wiId);
        pendingLeft.Should().BeFalse("the completion hook drains the pending row");
        var bfRow = await db.TaskQueue.FirstOrDefaultAsync(r =>
            r.WorkItemId == wiId && r.Kind == TaskQueueKinds.BuildFix);
        bfRow.Should().NotBeNull();
        bfRow!.PreferredAgentId.Should().Be("E1", "Q27.7 — pin preserved through promotion");

        // Pre-existing hosted-service disposal race — swallow.
        try { f.Dispose(); } catch (AggregateException) { }
        catch (ObjectDisposedException) { }
    }

    [Fact]
    public async Task BuildFix_idempotent_against_partial_unique_index()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();

        var wiId = "wi-buildfix-dup-1";
        db.WorkItems.Add(new WorkItemRow
        {
            Id = wiId, Type = "Task", Title = "Idempotency test",
            State = "in-review", ExecutionStatus = "in-review",
            AssignedAgentJson = "{\"crewId\":\"crew-a\",\"agents\":[\"E1\"]}",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var first = await queue.EnqueueAsync(wiId, "crew", "crew-a", default,
            kind: TaskQueueKinds.BuildFix, preferredAgentId: "E1");
        var second = await queue.EnqueueAsync(wiId, "crew", "crew-a", default,
            kind: TaskQueueKinds.BuildFix, preferredAgentId: "E1");

        first.Id.Should().Be(second.Id, "duplicate enqueues against the same open work item return the existing row");

        try { f.Dispose(); } catch (AggregateException) { }
        catch (ObjectDisposedException) { }
    }
}
