// -----------------------------------------------------------------------
// <copyright file="IForgeProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using Forge.Api.Contracts;

/// <summary>
/// Read-side abstraction over a remote work-item / PR / build source.
/// Phase 5 ships two implementations (<c>AdoProvider</c>, <c>GitHubProvider</c>)
/// plus a <c>NullForgeProvider</c> used when nothing is configured.
/// Phase 6 extends with write paths.
/// </summary>
public interface IForgeProvider
{
    /// <summary>Provider key — "ado" or "github" — used in webhook routes and config sections.</summary>
    string Kind { get; }

    /// <summary>True when the provider has all the configuration it needs to make calls.</summary>
    bool IsConfigured { get; }

    /// <summary>Returns mapped work items as cards for the configured project / repo.</summary>
    Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default);

    /// <summary>Returns a single work item by id, or null when not found.</summary>
    Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default);

    /// <summary>Returns mapped pull requests for the configured repo.</summary>
    Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default);

    /// <summary>Returns a single PR by id ("PR #123" or "123").</summary>
    Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default);

    /// <summary>Returns the most recent build status for a branch — null when nothing has run.</summary>
    Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default);

    /// <summary>Quick "can we talk to the API and authenticate?" probe used by /settings.</summary>
    Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default);

    // -- Phase 6 write surface ----------------------------------------------

    /// <summary>Creates a remote work item / issue. Returns the mapped <see cref="Card"/> with the new id.</summary>
    Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Patches a remote work item. Returns the refreshed mapped card.</summary>
    Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Posts a comment to the work item / issue (markdown body).</summary>
    Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Opens a PR from req.Branch into req.BaseBranch. Returns the mapped PR.</summary>
    Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Approves a PR.</summary>
    Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Requests changes on a PR.</summary>
    Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Posts a comment to a PR.</summary>
    Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Merges a PR. Returns the merged-state PR.</summary>
    Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Triggers a build for a branch. Returns the queued build status.</summary>
    Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>Cancels a running build for a branch.</summary>
    Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Phase 8.5 — server-driven repo picker. Returns repositories the
    /// currently-configured account can see, so the "Add repo" UI doesn't
    /// ask the operator to type a remote URL by hand. Implementations may
    /// return an empty list when the provider is not configured or the
    /// underlying API call fails.
    /// </summary>
    Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default);

    // -- Phase 12 Work Item Tracking sync surface -------------------------

    /// <summary>
    /// Phase 12.4 — pull a richer <see cref="WorkItem"/> projection used by
    /// the WIT sync service. Differs from <see cref="ListWorkItemsAsync"/>
    /// in three ways: it accepts arbitrary filters (areaPath / types /
    /// assignedTo / date range), it returns full work-item shape (parent,
    /// iteration, area, assignee, points, priority, tags), and it preserves
    /// the provider's revision marker for the conflict policy.
    /// </summary>
    Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(WorkItemSyncFilters filters, CancellationToken ct = default);

    /// <summary>
    /// Phase 14 follow-up — direct fetch of a single work item in the
    /// sync-shape, by provider reference. Bypasses the WIQL change filter
    /// so the operator can force-refresh a row that hasn't changed
    /// upstream (typical when the local schema evolved and the existing
    /// row needs to be re-mapped). Returns null if not found.
    /// </summary>
    Task<WorkItem?> GetWorkItemForSyncAsync(string providerRef, CancellationToken ct = default);

    /// <summary>
    /// Phase 23.4 / 23.5 — stream work items in upstream-paginated batches
    /// so the consumer can commit each batch in its own transaction and
    /// resume from <see cref="WorkItemSyncFilters.StartCursor"/> after a
    /// mid-walk failure. Default implementation (for legacy providers)
    /// emits a single terminal batch from
    /// <see cref="ListWorkItemsForSyncAsync"/>.
    /// </summary>
    async IAsyncEnumerable<WorkItemBatch> StreamWorkItemBatchesAsync(
        WorkItemSyncFilters filters,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var single = await this.ListWorkItemsForSyncAsync(filters, ct);
        yield return new WorkItemBatch(Index: 0, Items: single, OpaqueCursor: null);
    }
}

/// <summary>
/// Phase 12.4 / 12.10 — narrowing filters for a sync run. Empty values
/// mean "any". UTC dates apply to the provider's "last updated" column.
/// Phase 23 adds <see cref="AreaPaths"/> for multi-area-path scoping
/// (ADO only) and <see cref="StartCursor"/> for cursor-driven resume.
/// </summary>
public sealed record WorkItemSyncFilters(
    string? AreaPath = null,
    IReadOnlyList<string>? Types = null,
    string? AssignedTo = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    /// <summary>Phase 23.4 — multi-area-path scope for ADO; OR'd in the WIQL.</summary>
    IReadOnlyList<string>? AreaPaths = null,
    /// <summary>Phase 23.6 — opaque resume token written by a prior batch.</summary>
    string? StartCursor = null,
    // ── Per-repo provider scope (Phase 23 follow-up) ──────────────────────
    // When non-null, these override the global Forge:Ado / Forge:GitHub
    // settings for THIS sync invocation only. Required when more than one
    // repo (and therefore more than one ADO project / GitHub repo) is
    // attached to a single Forge instance — without these every repo's
    // sync would silently run against the global config.
    /// <summary>ADO org URL override, e.g. <c>https://dev.azure.com/microsoft</c>.</summary>
    string? AdoOrgUrl = null,
    /// <summary>ADO team-project override, e.g. <c>One</c>.</summary>
    string? AdoProject = null,
    /// <summary>ADO repository name override (PR endpoints).</summary>
    string? AdoRepository = null,
    /// <summary>GitHub <c>owner/repo</c> override.</summary>
    string? GitHubRepo = null);

/// <summary>
/// Phase 23.4 / 23.5 — one batch returned by
/// <see cref="IForgeProvider.StreamWorkItemBatchesAsync"/>. Each batch is
/// the unit of cursor commit: when the consumer persists a batch's items,
/// it should also persist <see cref="OpaqueCursor"/> as
/// <c>repo.LastSyncCursor</c> so a subsequent failure resumes from the
/// next batch instead of replaying the whole walk.
/// </summary>
public sealed record WorkItemBatch(
    int Index,
    IReadOnlyList<WorkItem> Items,
    string? OpaqueCursor);

// -- Phase 6 request DTOs --------------------------------------------------

public sealed record NewWorkItemRequest(
    string Title,
    string Type,
    string? AreaPath = null,
    int? StoryPoints = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null);

public sealed record UpdateWorkItemRequest(
    string? Title = null,
    string? State = null,
    string? AssigneeUniqueName = null,
    int? StoryPoints = null,
    IReadOnlyList<string>? Tags = null);

public sealed record NewPullRequestRequest(
    string Branch,
    string BaseBranch,
    string Title,
    string Body,
    bool Draft = false,
    /// <summary>
    /// Override the provider's default repository name for this PR.
    /// Used when the work item's repo differs from the global config.
    /// </summary>
    string? RepositoryName = null,
    /// <summary>
    /// Override the provider's default project for this PR.
    /// </summary>
    string? ProjectName = null,
    /// <summary>
    /// ADO work item ID to link to the PR. When set, the PR is created
    /// with a work-item reference so ADO shows the link automatically.
    /// </summary>
    int? WorkItemRef = null);

/// <summary>
/// Phase 6.11 — passes the per-request user PAT to provider write paths.
/// When <see cref="UserPat"/> is non-null, the provider authenticates as
/// the user; otherwise it falls back to the server PAT (read-only).
/// </summary>
public sealed record ProviderCallContext(string? UserId, string? UserPat, string? IdempotencyKey)
{
    public static readonly ProviderCallContext Anonymous = new(null, null, null);
}

/// <summary>Outcome of <see cref="IForgeProvider.TestConnectionAsync"/> + ambient health snapshot.</summary>
public sealed record ProviderHealth(
    string Kind,
    bool Ok,
    string? Message,
    DateTimeOffset? LastSync,
    int LastErrorCount,
    int? RateLimitRemaining,
    int? RateLimitLimit);
