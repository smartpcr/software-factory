// -----------------------------------------------------------------------
// <copyright file="StickyEvaluator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Keep the evaluator of an in-progress gen↔eval loop when its run is resumed.
/// <para>
/// A run interrupted mid-loop (host restart, transport death, live-badge auto-resume)
/// is re-claimed by one of two paths: the crew queue, which picks the evaluator for
/// diversity (least-recently-used), and the orchestrator's incumbent recovery, which
/// reads the pair saved on the workstream. Neither looked at who was actually
/// evaluating, so a resumed loop switched evaluator (harness:DSH stage 1.3, 2026-11-19:
/// LL → JS after one restart, JS → LL after another) and the new evaluator re-read the
/// whole change cold. Diversity is for fresh claims; a loop in progress keeps its
/// evaluator's accumulated session.
/// </para>
/// </summary>
public static class StickyEvaluator
{
    /// <summary>
    /// The evaluator of the work item's latest scored iteration when the loop is still
    /// iterating and that agent can take it (not the generator, exists, not paused, and
    /// not saturated by OTHER work items — an open claim on this item is its own stale
    /// claim from the interrupted run). Null when there is no loop to continue.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        ControlDbContext control, ProductDbContext db, string workItemId, string? generatorId,
        IReadOnlyCollection<string>? allowedRoster = null, CancellationToken ct = default)
    {
        var latest = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId && d.EvaluatorAgentId != null && d.EvaluatorAgentId != "")
            .OrderByDescending(d => d.Id)
            .Select(d => new { d.EvaluatorAgentId, d.Verdict })
            .FirstOrDefaultAsync(ct);
        if (latest is null || latest.Verdict != "iterate") return null;

        var evaluator = latest.EvaluatorAgentId!;
        if (string.Equals(evaluator, generatorId, StringComparison.Ordinal)) return null;
        if (allowedRoster is not null && !allowedRoster.Contains(evaluator)) return null;

        var agent = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == evaluator, ct);
        if (agent is null || string.Equals(agent.State, "paused", StringComparison.OrdinalIgnoreCase)) return null;

        var otherOpenClaims = await control.AgentClaims.AsNoTracking()
            .CountAsync(c => c.AgentId == evaluator && c.ReleasedAt == null && c.WorkItemId != workItemId, ct);
        return otherOpenClaims < Math.Max(1, agent.MaxConcurrentTasks) ? evaluator : null;
    }
}