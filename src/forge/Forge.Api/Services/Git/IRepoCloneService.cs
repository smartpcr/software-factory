// -----------------------------------------------------------------------
// <copyright file="IRepoCloneService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

using Forge.Api.Persistence;

/// <summary>
/// Phase 8.10 — silent auto-clone for a repo's <c>cloneRoot</c>.
/// Per the Q6 resolution, this runs without a confirmation prompt; per Q9
/// (multi-org sign-in) and the Phase-9 sign-in roadmap, the token used today
/// is the configured server-wide PAT (<c>Forge:Ado:Pat</c> / <c>Forge:GitHub:Pat</c>).
/// </summary>
public interface IRepoCloneService
{
    /// <summary>
    /// Returns immediately if <paramref name="repo"/>'s <c>cloneRoot</c>
    /// already contains a valid clone of the same remote. Otherwise runs
    /// <c>git clone {auth-injected remote} {cloneRoot}</c>, streaming
    /// <c>forge.repo.clone.{started,progress,finished,failed}</c> events to
    /// any connected SignalR client. On failure, the partially-cloned
    /// directory is removed so the next attempt starts from scratch.
    /// </summary>
    /// <returns>
    /// The clone outcome — Skipped (already cloned), Cloned, Mismatched (409),
    /// or Failed (with reason).
    /// </returns>
    Task<RepoCloneResult> EnsureClonedAsync(RepoRow repo, CancellationToken ct = default);
}

public enum RepoCloneOutcome
{
    Skipped,
    Cloned,
    Mismatched,
    Failed,
}

public sealed record RepoCloneResult(RepoCloneOutcome Outcome, string? Message = null);
