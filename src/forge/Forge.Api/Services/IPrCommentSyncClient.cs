// -----------------------------------------------------------------------
// <copyright file="IPrCommentSyncClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;

/// <summary>
/// Phase 24 — provider-specific PR + comment-thread fetcher. One
/// implementation per provider ("ado", "github"); the <see cref="PrSyncJob"/>
/// dispatches by <see cref="IPrCommentSyncClient.Provider"/>.
/// </summary>
public interface IPrCommentSyncClient
{
    /// <summary>Provider key — must match <see cref="PrRow.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>True when configured. False short-circuits the run.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Pull upstream PR state + comments. Implementations consult
    /// <paramref name="pr"/>'s <see cref="PrRow.LastUpstreamUpdatedAt"/> as the
    /// delta cursor and skip the comment fetch when nothing has changed.
    /// </summary>
    Task<PrSyncSnapshot> FetchAsync(PrRow pr, CancellationToken ct);
}

/// <summary>Phase 24 — wire-shape result from <see cref="IPrCommentSyncClient.FetchAsync"/>.</summary>
public sealed record PrSyncSnapshot(
    string CanonicalState,
    string? UpstreamUpdatedAt,
    IReadOnlyList<PrSyncComment> Comments,
    bool CommentsFetched,
    /// <summary>Number of files changed in the PR (from provider detail). Null when not fetched.</summary>
    int? Files = null,
    /// <summary>Number of commits in the PR. Null when not fetched.</summary>
    int? Commits = null,
    /// <summary>Lines added. Null when not fetched.</summary>
    int? Additions = null,
    /// <summary>Lines deleted. Null when not fetched.</summary>
    int? Deletions = null,
    /// <summary>
    /// Current upstream base branch (e.g. "main"). Null when not fetched.
    /// Critical: when an operator retargets a PR on the provider side
    /// (e.g. from "feature/X" to "main"), the local row's Base goes stale
    /// and the merge endpoint's pre-rebase pulls from the wrong upstream
    /// branch — the provider then keeps rejecting the merge with a stale
    /// conflict message. ApplySnapshotAsync writes this back to PrRow.Base
    /// to keep the two views aligned.
    /// </summary>
    string? BaseBranch = null,
    /// <summary>Current upstream source branch. Null when not fetched.</summary>
    string? SourceBranch = null,
    /// <summary>Provider merge commit SHA. Null when not merged or provider omitted it.</summary>
    string? MergeCommitSha = null,
    /// <summary>Provider-captured target/base SHA at merge time. Null when not merged or unavailable.</summary>
    string? MergeBaseSha = null,
    /// <summary>Provider-captured source/head SHA at merge time. Null when not merged or unavailable.</summary>
    string? MergeHeadSha = null);

/// <summary>Phase 24 — provider-neutral comment shape for upsert.</summary>
public sealed record PrSyncComment(
    string ProviderCommentId,
    string ThreadId,
    string? AuthorId,
    string AuthorKind, // 'agent' | 'human' | 'system'
    string Body,
    string? FilePath,
    int? Line,
    string State, // 'active' | 'resolved' | 'wont-fix' | 'closed' | 'pending'
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ResolvedByUserId,
    DateTimeOffset? ResolvedAt,
    string? ParentProviderCommentId);
