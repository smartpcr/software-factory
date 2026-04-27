// -----------------------------------------------------------------------
// <copyright file="PlanRubricConstants.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api;

/// <summary>
/// Phase 16.3 / 16.5 — locked plan-tree budgets. The values here are
/// the **single source of truth in code**; the matching numbers are
/// repeated verbatim in <c>agents/templates/plan-system.md</c> (the
/// preamble the plan-task generator sees) so the agent and the
/// server-side validator agree. Changing one without the other is caught
/// by <c>Phase16RubricConsistencyTests</c>.
/// </summary>
public static class PlanRubricConstants
{
    /// <summary>Each <c>phase</c> may contain at most this many <c>stage</c> children.</summary>
    public const int MaxStagesPerPhase = 20;

    /// <summary>Each <c>stage</c> may contain at most this many <c>step</c> children.</summary>
    public const int MaxStepsPerStage = 20;

    /// <summary>
    /// A <c>step</c> declares its planning-time file budget on
    /// <c>WorkItemRow.ExpectedFileChanges</c>. Phase 16.5 refuses creates above
    /// this cap; Phase 16.6 surfaces a soft-fail check on PRs that exceed it.
    /// </summary>
    public const int MaxFilesPerStep = 20;
}
