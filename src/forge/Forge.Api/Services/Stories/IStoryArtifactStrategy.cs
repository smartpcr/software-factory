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
    /// <summary>Architect's plan-phase artifacts. Returns relative-path → file content.</summary>
    Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default);

    /// <summary>Architect's decomposition — split the plan into N parallel workstreams.</summary>
    Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default);

    /// <summary>Engineer's per-workstream change-set. Returns relative-path → file content.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
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

/// <summary>One workstream entry in a decomposition.</summary>
public sealed record DecompositionWorkstream(
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Deps,
    IReadOnlyList<string> Deliverables,
    string Timeline,
    string EstimatedEffort);

/// <summary>One review-comment payload the strategy wants posted.</summary>
public sealed record ReviewComment(string FilePath, int Line, string Body, string Severity);

/// <summary>Context handed to the engineer when responding to a review comment.</summary>
public sealed record ReviewCommentContext(string FilePath, int Line, string Body, string AuthoringAgentId);

/// <summary>Engineer's response: a single file edit + a reply body.</summary>
public sealed record ReviewFix(string FilePath, string NewContent, string ReplyBody);
