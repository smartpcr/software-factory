// -----------------------------------------------------------------------
// <copyright file="IStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 28 — pluggable strategy for the artifacts
/// each crew produces during a story spawn. The default implementation
/// generates deterministic, realistic markdown / feature / step files so
/// integration tests can drive the full lifecycle without an LLM. A
/// production deployment can swap a different binding into DI to call a
/// real generator (e.g. crew agents via <c>IAgentRunner</c>).
/// </summary>
public interface IStoryArtifactStrategy
{
    /// <summary>
    /// Architect's plan-phase artifacts. Returns relative-path → file content.
    /// When <paramref name="forceResume"/> is true, the workspace-cache
    /// short-circuit (which skips the LLM if the file is already on disk)
    /// is bypassed — used by the spawn-retry endpoint after a non-pass
    /// verdict so the architect crew re-iterates instead of replaying the
    /// stalled artifact unchanged.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default);

    /// <summary>
    /// Resume a single plan-doc artifact's gen↔eval loop. Unlike
    /// <see cref="ProducePlanArtifactsAsync"/> (which runs all 4 docs),
    /// this re-runs only the specified <paramref name="key"/> with
    /// forceResume semantics (workspace cache bypassed). Used by the
    /// per-doc Resume button on the conversation tab.
    /// </summary>
    Task ResumeSingleArtifactAsync(
        StorySpawnRow spawn, WorkItemRow story, string key, CancellationToken ct = default);

    /// <summary>Architect's decomposition — split the plan into N parallel workstreams.</summary>
    Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default);

    /// <summary>Engineer's per-workstream change-set. Returns relative-path → file content.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);

    /// <summary>
    /// Read cached workspace files for a workstream whose gen/eval loop
    /// already passed. Used by the orchestrator to commit + open PR without
    /// re-running the loop.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);

    /// <summary>Reviewer's comments to post on a workstream PR. Empty = approve.</summary>
    Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default);

    /// <summary>Engineer's reply / fix to a single open review comment.</summary>
    Task<ReviewFix> ProduceReviewFixAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default);

    /// <summary>QA's `.feature` files for the spawn.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default);

    /// <summary>Engineer's step-definition files for a single workstream after the QA PR merges.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);
}

/// <summary>
/// One workstream entry in a decomposition. <see cref="PlanAnchor"/> +
/// <see cref="PlanContentHash"/> (Phase 42) are populated when the entry came
/// from <c>implementation-plan.md</c>; null on the LLM-only fallback path.
/// When non-null, <see cref="StorySpawnOrchestrator.PersistDecompositionAsync"/>
/// uses the anchor to build a stable workstream id so the reconciler can
/// later find the row.
/// </summary>
public sealed record DecompositionWorkstream(
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Deps,
    IReadOnlyList<string> Deliverables,
    string Timeline,
    string EstimatedEffort,
    string? PlanAnchor = null,
    string? PlanContentHash = null);

/// <summary>One review-comment payload the strategy wants posted.</summary>
public sealed record ReviewComment(string FilePath, int Line, string Body, string Severity);

/// <summary>Context handed to the engineer when responding to a review comment.</summary>
public sealed record ReviewCommentContext(string FilePath, int Line, string Body, string AuthoringAgentId);

/// <summary>Engineer's response: a single file edit + a reply body.</summary>
public sealed record ReviewFix(string FilePath, string NewContent, string ReplyBody);
