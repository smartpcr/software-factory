// -----------------------------------------------------------------------
// <copyright file="PrCoordsResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using System;
using Forge.Api.Persistence;

/// <summary>
/// Resolves <c>(RepositoryName, ProjectName)</c> for a PR-create call from a
/// <see cref="RepoRow"/>, keyed by the repo's provider kind. Both
/// <c>CrewQueueWorker</c> (delivery flow) and <c>WorkItemsEndpoints</c>
/// (agent-callable <c>POST /work-items/{id}/create-pr</c>) call this so a
/// new provider kind needs to be wired in exactly one place.
/// <para>
/// Mapping per provider:
/// <list type="bullet">
///   <item>ADO    → <c>RepositoryName = ExtractAdoRepoName(RemoteUrl)</c>, <c>ProjectName = AdoProject</c>.</item>
///   <item>GitHub → <c>RepositoryName = GhRepo</c>, <c>ProjectName = GhOwner</c>.</item>
/// </list>
/// </para>
/// </summary>
public static class PrCoordsResolver
{
    /// <summary>
    /// Returns <c>(RepositoryName, ProjectName)</c> for the given repo + kind.
    /// Both values may be <c>null</c> when the repo is null, the kind is
    /// unrecognized, or the provider-specific fields aren't populated — in
    /// those cases the caller falls back to the provider's global config
    /// (<c>Forge:Ado:Repo</c> / <c>Forge:GitHub:Repo</c>).
    /// </summary>
    public static (string? RepositoryName, string? ProjectName) Resolve(RepoRow? repo, string? repoKind)
    {
        if (repo is null) return (null, null);
        return repoKind switch
        {
            "ado" => (ExtractAdoRepoName(repo.RemoteUrl),
                      string.IsNullOrEmpty(repo.AdoProject) ? null : repo.AdoProject),
            "github" => (repo.GhRepo, repo.GhOwner),
            _ => (null, null),
        };
    }

    /// <summary>
    /// Extracts the ADO repository name from a remote URL.
    /// E.g. <c>https://dev.azure.com/org/project/_git/reponame</c> → <c>reponame</c>.
    /// Returns <c>null</c> when the URL is empty, malformed, or doesn't
    /// contain the <c>/_git/</c> segment ADO uses.
    /// </summary>
    public static string? ExtractAdoRepoName(string? remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl)) return null;
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return null;
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gitIdx = Array.IndexOf(segs, "_git");
        if (gitIdx >= 0 && gitIdx + 1 < segs.Length)
        {
            return segs[gitIdx + 1].TrimEnd('/');
        }
        return null;
    }
}
