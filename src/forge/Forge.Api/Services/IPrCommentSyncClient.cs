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
    int? Deletions = null);

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
