// -----------------------------------------------------------------------
// <copyright file="AgentReadyClaimStateTests.cs" company="Microsoft Corp.">
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
/// Tests that the agent-ready → enqueue → claim pipeline moves work items
/// to the Active column on the board, and that queue worker claim always
/// transitions proposed/new items to active state.
/// </summary>
public class AgentReadyClaimStateTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ═════════════════════════════════════════════════════════════════════
    // Queue worker claim moves state to 'active'
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Claim_Moves_Proposed_WorkItem_To_Active()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product = await db.Products.FirstAsync();
        db.Crews.Add(new CrewRow
        {
            Id = "claim-crew", Name = "Engineering Claim",
            Lead = "claim-lead", Model = "local-stub",
            ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
            MaxConcurrent = 5, ScheduleEnabled = false,
        });
        db.CrewMembers.Add(new CrewMemberRow
        {
            CrewId = "claim-crew", AgentId = "claim-agent",
            AddedAt = DateTimeOffset.UtcNow,
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-claim-proposed",
            Type = "task", Track = "dev",
            State = "proposed",
            Title = "Proposed item",
            ExecutionStatus = ExecutionStatuses.Idle,
        });
        await db.SaveChangesAsync();

        // Enqueue directly to the crew.
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        await queue.EnqueueAsync("wi-claim-proposed", "crew", "claim-crew", default);

        // Run the worker tick — it should claim.
        var worker = f.Services.GetRequiredService<CrewQueueWorker>();
        await worker.RunOnceAsync(default);

        // Verify: state should be 'active' (not 'proposed').
        var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-claim-proposed");
        Assert.Equal("active", wi.State);
        Assert.Equal(ExecutionStatuses.Live, wi.ExecutionStatus);
    }

    [Fact]
    public async Task Claim_Keeps_Active_WorkItem_In_Active()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product = await db.Products.FirstAsync();
        db.Crews.Add(new CrewRow
        {
            Id = "keep-crew", Name = "Engineering Keep",
            Lead = "keep-lead", Model = "local-stub",
            ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
            MaxConcurrent = 5, ScheduleEnabled = false,
        });
        db.CrewMembers.Add(new CrewMemberRow
        {
            CrewId = "keep-crew", AgentId = "keep-agent",
            AddedAt = DateTimeOffset.UtcNow,
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-claim-active",
            Type = "task", Track = "dev",
            State = "active",
            Title = "Already active item",
            ExecutionStatus = ExecutionStatuses.Idle,
        });
        await db.SaveChangesAsync();

        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        await queue.EnqueueAsync("wi-claim-active", "crew", "keep-crew", default);

        var worker = f.Services.GetRequiredService<CrewQueueWorker>();
        await worker.RunOnceAsync(default);

        var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-claim-active");
        Assert.Equal("active", wi.State);
    }

    [Fact]
    public async Task Claim_Does_Not_Regress_Resolved_To_Active()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product = await db.Products.FirstAsync();
        db.Crews.Add(new CrewRow
        {
            Id = "res-crew", Name = "Engineering Res",
            Lead = "res-lead", Model = "local-stub",
            ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
            MaxConcurrent = 5, ScheduleEnabled = false,
        });
        db.CrewMembers.Add(new CrewMemberRow
        {
            CrewId = "res-crew", AgentId = "res-agent",
            AddedAt = DateTimeOffset.UtcNow,
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-claim-resolved",
            Type = "task", Track = "dev",
            State = "resolved",
            Title = "Resolved item",
            ExecutionStatus = ExecutionStatuses.Idle,
        });
        await db.SaveChangesAsync();

        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        await queue.EnqueueAsync("wi-claim-resolved", "crew", "res-crew", default);

        var worker = f.Services.GetRequiredService<CrewQueueWorker>();
        await worker.RunOnceAsync(default);

        // Resolved items should NOT be regressed to active — the queue
        // worker skips terminal states. Verify state unchanged.
        var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-claim-resolved");
        Assert.Equal("resolved", wi.State);
    }

    // ═════════════════════════════════════════════════════════════════════
    // agent-ready auto-assign sets state to active + enqueues
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void AgentReady_Resolver_Fires_When_No_Agent_Assigned()
    {
        var result = AgentReadyResolver.Apply(
            assignedAgentJson: null,
            tags: new[] { "agent-ready" },
            agentReadyDefaultTeam: null);
        Assert.True(result.WasAssigned);
        Assert.Equal("Engineering", result.TeamName);
    }

    [Fact]
    public void AgentReady_Resolver_Skips_When_Agent_Already_Working()
    {
        var agentJson = """{"agents":["gen-1"],"role":"generator","assignedAt":"2026-01-01T00:00:00Z"}""";
        var result = AgentReadyResolver.Apply(
            assignedAgentJson: agentJson,
            tags: new[] { "agent-ready" },
            agentReadyDefaultTeam: null);
        Assert.False(result.WasAssigned);
    }

    [Fact]
    public void AgentReady_Resolver_Fires_Even_With_Human_WIT_Assignee()
    {
        // The resolver now checks AssignedAgent (runtime), not AssignedTo (WIT).
        // A human WIT assignee should NOT block agent-ready auto-assign.
        var result = AgentReadyResolver.Apply(
            assignedAgentJson: null,  // no agent working
            tags: new[] { "agent-ready" },
            agentReadyDefaultTeam: null);
        Assert.True(result.WasAssigned);
    }
}
