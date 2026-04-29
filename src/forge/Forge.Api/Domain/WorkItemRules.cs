// -----------------------------------------------------------------------
// <copyright file="WorkItemRules.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Domain;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 12 — domain rules for the WIT type / track / hierarchy invariants.
/// Centralises the Q12.1 (revised) decisions so endpoints don't drift.
/// </summary>
public static class WorkItemRules
{
    public static readonly string[] DevTypes = { "epic", "feature", "task", "bug" };

    public static readonly string[] DesignTypes = { "phase", "stage", "step" };

    public static readonly string[] AllTypes =
    {
        "epic", "feature", "task", "bug",
        "phase", "stage", "step",
    };

    public static readonly string[] AllStates = { "proposed", "active", "in-review", "resolved", "closed" };

    public static readonly string[] AllTracks = { "dev", "design" };

    public static readonly string[] AllPriorities = { "low", "med", "high" };

    /// <summary>Track a type belongs to.</summary>
    public static string TrackOf(string type) => type switch
    {
        "phase" or "stage" or "step" => "design",
        _ => "dev",
    };

    /// <summary>
    /// Hierarchy rank used to validate parent-child constraints. A child's
    /// rank must be strictly greater than its parent's, and they must share
    /// a track unless both are top-level (rank 0).
    /// </summary>
    public static int Rank(string type) => type switch
    {
        "epic" or "phase"           => 0,
        "feature" or "stage"        => 1,
        "task" or "bug" or "step"   => 2,
        _                            => -1,
    };

    /// <summary>
    /// Returns null when the parent assignment is valid; otherwise an error
    /// message suitable for a 400 / 409 response body.
    /// </summary>
    public static string? ValidateParent(WorkItemRow child, WorkItemRow? parent)
    {
        if (parent is null) return null; // top-level — always allowed

        if (parent.Id == child.Id) return "An item cannot be its own parent.";

        if (Rank(child.Type) <= Rank(parent.Type))
        {
            return $"Type '{child.Type}' (rank {Rank(child.Type)}) cannot be a child of type '{parent.Type}' (rank {Rank(parent.Type)}).";
        }

        // Cross-track linking only allowed at the top level (rank 0). Once
        // you're below the container layer, child must share parent's track.
        if (TrackOf(child.Type) != TrackOf(parent.Type))
        {
            return $"Cross-track parent (child track '{TrackOf(child.Type)}' ↔ parent track '{TrackOf(parent.Type)}') is not allowed below the top-level container.";
        }

        return null;
    }

    /// <summary>
    /// Phase 16.5 — enforce the plan-tree budgets. Returns null if the proposed
    /// row is within budget for its parent (or has no parent). Otherwise returns
    /// a structured problem-detail title that callers turn into 400. The
    /// <c>parent.Id</c> is the only thing the caller needs from the parent —
    /// we re-query the child count so the check is race-free against
    /// concurrent inserts as long as the SQL is in the same transaction.
    /// </summary>
    public static async Task<string?> ValidatePlanTreeBudgetAsync(
        ForgeDbContext db,
        WorkItemRow child,
        WorkItemRow? parent,
        CancellationToken ct)
    {
        // Top-level rows (no parent) are always within budget.
        if (parent is null) return null;

        // Phase 16.5 — only the design ladder has the 20/20/20 caps. Dev rows
        // (epic / feature / task / bug) keep the existing rank check from
        // ValidateParent and don't get a budget cap at v0.1.
        if (TrackOf(child.Type) != "design") return null;

        // stage under phase: ≤ MaxStagesPerPhase children.
        if (child.Type == "stage" && parent.Type == "phase")
        {
            var count = await db.WorkItems.AsNoTracking()
                .CountAsync(w => w.ParentId == parent.Id && w.Type == "stage", ct);
            if (count >= PlanRubricConstants.MaxStagesPerPhase)
            {
                return $"Phase '{parent.Id}' already has {count} stages (cap {PlanRubricConstants.MaxStagesPerPhase}). Split into a second phase.";
            }
        }

        // step under stage: ≤ MaxStepsPerStage children.
        if (child.Type == "step" && parent.Type == "stage")
        {
            var count = await db.WorkItems.AsNoTracking()
                .CountAsync(w => w.ParentId == parent.Id && w.Type == "step", ct);
            if (count >= PlanRubricConstants.MaxStepsPerStage)
            {
                return $"Stage '{parent.Id}' already has {count} steps (cap {PlanRubricConstants.MaxStepsPerStage}). Split into a second stage.";
            }
        }

        return null;
    }

    /// <summary>
    /// Phase 16.5 — enforce the per-step file-change cap. Returns null when
    /// the row is within budget; otherwise a problem-detail title.
    /// </summary>
    public static string? ValidateStepFileBudget(WorkItemRow row)
    {
        if (row.Type != "step") return null;
        if (row.ExpectedFileChanges is null) return null;
        if (row.ExpectedFileChanges.Value <= 0)
        {
            return $"Step '{row.Id}' must declare expectedFileChanges > 0.";
        }
        if (row.ExpectedFileChanges.Value > PlanRubricConstants.MaxFilesPerStep)
        {
            return $"Step '{row.Id}' declared {row.ExpectedFileChanges.Value} files; cap is {PlanRubricConstants.MaxFilesPerStep}. Split into multiple steps.";
        }
        return null;
    }

    /// <summary>
    /// Walk the parent chain to detect cycles. Returns the offending ancestor
    /// id when a cycle would form, otherwise null. <paramref name="proposedParentId"/>
    /// is the candidate parent for <paramref name="childId"/>.
    /// </summary>
    public static async Task<string?> DetectCycleAsync(
        ForgeDbContext db,
        string childId,
        string? proposedParentId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(proposedParentId)) return null;
        if (proposedParentId == childId) return childId;

        // Walk up from proposed parent; if we ever land on the child, cycle.
        var visited = new HashSet<string> { childId };
        var cursor = proposedParentId;
        while (!string.IsNullOrEmpty(cursor))
        {
            if (!visited.Add(cursor)) return cursor; // cycle through some ancestor
            var next = await db.WorkItems.AsNoTracking()
                .Where(w => w.Id == cursor)
                .Select(w => w.ParentId)
                .FirstOrDefaultAsync(ct);
            if (next is null) break;
            if (next == childId) return childId;
            cursor = next;
        }
        return null;
    }
}
