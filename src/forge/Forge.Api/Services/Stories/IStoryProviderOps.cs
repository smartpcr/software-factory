// -----------------------------------------------------------------------
// <copyright file="IStoryProviderOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 28 — narrow provider surface used by the
/// orchestrator. Production binds <c>DefaultStoryProviderOps</c> which
/// delegates to <c>IForgeProvider</c>; tests bind an in-memory stub that
/// records the calls + persists corresponding rows in the local DB so the
/// rest of the system (PrSyncJob, completion gates, etc.) sees them.
/// </summary>
public interface IStoryProviderOps
{
    /// <summary>Open a PR. Returns the persisted PrRow id.</summary>
    Task<string> OpenPrAsync(StoryPrRequest req, CancellationToken ct = default);

    /// <summary>Mark a PR merged in the local cache (and optionally upstream).</summary>
    Task MergePrAsync(string prId, CancellationToken ct = default);

    /// <summary>
    /// Phase 45 — change the target branch of an open PR. Used when the
    /// operator switches a spawn's base branch mid-flight: every still-open
    /// PR (plan / impl / qa / step-def) is repointed at the new base so a
    /// subsequent merge replays into the right tree.
    /// </summary>
    Task UpdatePrBaseBranchAsync(string prId, string newBaseBranch, CancellationToken ct = default);

    /// <summary>Approve a PR.</summary>
    Task ApprovePrAsync(string prId, string reviewerAgentId, CancellationToken ct = default);

    /// <summary>Post a review comment on a PR; returns the persisted PrCommentRow id.</summary>
    Task<long> PostPrCommentAsync(string prId, string filePath, int line, string body, string authorAgentId, CancellationToken ct = default);

    /// <summary>Mark a PR comment resolved.</summary>
    Task ResolvePrCommentAsync(long commentId, string resolverAgentId, CancellationToken ct = default);

    /// <summary>Transition a work item to <paramref name="state"/> (e.g. Resolved/Closed).</summary>
    Task TransitionWorkItemAsync(string workItemId, string state, CancellationToken ct = default);
}

/// <summary>Request shape for opening a PR via <see cref="IStoryProviderOps"/>.</summary>
public sealed record StoryPrRequest(
    string Branch,
    string BaseBranch,
    string Title,
    string Body,
    RepoRow? Repo,
    string? WorkItemId,
    string? Workstream,
    string? Epic,
    /// <summary>Phase 46 — when true, <see cref="PrRow.AutoMerge"/> is set on the created PR so <c>AutoMergeWatcher</c> picks it up.</summary>
    bool AutoMerge = false);
