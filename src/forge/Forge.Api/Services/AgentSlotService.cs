// -----------------------------------------------------------------------
// <copyright file="AgentSlotService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 44 — centralized agent busy/idle tracking. Every agent invocation
/// must go through <see cref="TryClaimAsync"/> before starting and
/// <see cref="ReleaseAsync"/> (or <see cref="ReleaseByTaskAsync"/>) when
/// done. The claim table provides durable time-per-task history and the
/// live busy/idle state the UI needs.
/// </summary>
public class AgentSlotService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<AgentSlotService>? logger;

    public AgentSlotService(IServiceScopeFactory scopeFactory, ILogger<AgentSlotService>? logger = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    /// <summary>
    /// Attempts to claim <paramref name="agentId"/> for <paramref name="workItemId"/>.
    /// Returns the claim row on success, null if the agent is at capacity.
    /// Sets <c>AgentRow.State = "working"</c>.
    /// </summary>
    public async Task<AgentClaimRow?> TryClaimAsync(
        string agentId, string workItemId, string role = "generator", CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null)
        {
            this.logger?.LogWarning("TryClaim: agent {AgentId} not found", agentId);
            return null;
        }

        // Check capacity — open claims (ReleasedAt IS NULL) vs MaxConcurrentTasks.
        var openClaims = await db.AgentClaims
            .Where(c => c.AgentId == agentId && c.ReleasedAt == null)
            .CountAsync(ct);

        if (openClaims >= agent.MaxConcurrentTasks)
        {
            this.logger?.LogDebug(
                "TryClaim: agent {AgentId} at capacity ({Open}/{Max}), cannot claim {WorkItemId}",
                agentId, openClaims, agent.MaxConcurrentTasks, workItemId);
            return null;
        }

        // Check if this exact (agent, workItem) is already claimed.
        var existing = await db.AgentClaims
            .FirstOrDefaultAsync(c => c.AgentId == agentId && c.WorkItemId == workItemId && c.ReleasedAt == null, ct);
        if (existing is not null)
        {
            this.logger?.LogDebug("TryClaim: agent {AgentId} already claimed for {WorkItemId}", agentId, workItemId);
            return existing;
        }

        var claim = new AgentClaimRow
        {
            AgentId = agentId,
            WorkItemId = workItemId,
            Role = role,
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        db.AgentClaims.Add(claim);

        agent.State = "working";
        await db.SaveChangesAsync(ct);

        this.logger?.LogInformation(
            "Claimed agent {AgentId} for {WorkItemId} (role={Role}, open={Open})",
            agentId, workItemId, role, openClaims + 1);
        return claim;
    }

    /// <summary>
    /// Releases a specific agent from a specific work item. Updates token/cost
    /// totals on the claim row. Sets <c>AgentRow.State = "idle"</c> if no
    /// other open claims remain.
    /// </summary>
    public async Task ReleaseAsync(
        string agentId, string workItemId,
        long inputTokens = 0, long outputTokens = 0, decimal costUsd = 0,
        CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var claim = await db.AgentClaims
            .FirstOrDefaultAsync(c => c.AgentId == agentId && c.WorkItemId == workItemId && c.ReleasedAt == null, ct);
        if (claim is not null)
        {
            claim.ReleasedAt = DateTimeOffset.UtcNow;
            claim.InputTokens = inputTokens;
            claim.OutputTokens = outputTokens;
            claim.CostUsd = costUsd;
        }

        // If no open claims remain for this agent, flip to idle.
        var remaining = await db.AgentClaims
            .CountAsync(c => c.AgentId == agentId && c.ReleasedAt == null && (claim == null || c.Id != claim.Id), ct);
        if (remaining == 0)
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
            if (agent is not null && agent.State == "working")
            {
                agent.State = "idle";
            }
        }

        await db.SaveChangesAsync(ct);
        this.logger?.LogInformation(
            "Released agent {AgentId} from {WorkItemId} (remaining open={Remaining})",
            agentId, workItemId, remaining);
    }

    /// <summary>
    /// Releases ALL agents working on <paramref name="workItemId"/>. Used by
    /// cancel / fail / complete handlers.
    /// </summary>
    public async Task ReleaseByTaskAsync(string workItemId, CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var openClaims = await db.AgentClaims
            .Where(c => c.WorkItemId == workItemId && c.ReleasedAt == null)
            .ToListAsync(ct);

        var agentIds = openClaims.Select(c => c.AgentId).Distinct().ToList();
        foreach (var claim in openClaims)
        {
            claim.ReleasedAt = DateTimeOffset.UtcNow;
        }

        // For each affected agent, check if they have other open claims.
        foreach (var aid in agentIds)
        {
            var otherOpen = await db.AgentClaims
                .CountAsync(c => c.AgentId == aid && c.ReleasedAt == null && c.WorkItemId != workItemId, ct);
            if (otherOpen == 0)
            {
                var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == aid, ct);
                if (agent is not null && agent.State == "working")
                {
                    agent.State = "idle";
                }
            }
        }

        await db.SaveChangesAsync(ct);
        this.logger?.LogInformation(
            "Released {Count} claims for task {WorkItemId} (agents: {Agents})",
            openClaims.Count, workItemId, string.Join(", ", agentIds));
    }

    /// <summary>
    /// Returns true if the agent has capacity for another task.
    /// </summary>
    public async Task<bool> IsAvailableAsync(string agentId, CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) return false;
        if (agent.State == "paused") return false;

        var openClaims = await db.AgentClaims
            .CountAsync(c => c.AgentId == agentId && c.ReleasedAt == null, ct);
        return openClaims < agent.MaxConcurrentTasks;
    }

    /// <summary>
    /// Returns all currently open claims (agents actively working).
    /// </summary>
    public async Task<IReadOnlyList<AgentClaimRow>> GetActiveClaimsAsync(CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return await db.AgentClaims.AsNoTracking()
            .Where(c => c.ReleasedAt == null)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Startup recovery — release any claims that don't have a matching
    /// running invocation. Called on boot to clean up after crashes.
    /// </summary>
    public async Task RecoverOrphanedClaimsAsync(CancellationToken ct = default)
    {
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var orphans = await db.AgentClaims
            .Where(c => c.ReleasedAt == null)
            .ToListAsync(ct);

        if (orphans.Count == 0) return;

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in orphans)
        {
            claim.ReleasedAt = DateTimeOffset.UtcNow;
            agentIds.Add(claim.AgentId);
        }

        // Reset all affected agents to idle.
        var agents = await db.Agents
            .Where(a => agentIds.Contains(a.Id) && a.State == "working")
            .ToListAsync(ct);
        foreach (var agent in agents)
        {
            agent.State = "idle";
        }

        await db.SaveChangesAsync(ct);
        this.logger?.LogInformation(
            "Recovered {Count} orphaned agent claims on startup (agents: {Agents})",
            orphans.Count, string.Join(", ", agentIds));
    }
}
