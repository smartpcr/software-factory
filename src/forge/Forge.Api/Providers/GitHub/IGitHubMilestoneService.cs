// -----------------------------------------------------------------------
// <copyright file="IGitHubMilestoneService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.GitHub;

using Forge.Api.Contracts;
using Forge.Api.Persistence;

/// <summary>
/// Phase 15.4 — list milestones for a GitHub repo. Milestones are flat
/// (no prefix tree), so the result is a flat <see cref="DiscoveryNode"/>
/// list with <c>depth=0</c> on every entry.
/// </summary>
public interface IGitHubMilestoneService
{
    Task<IReadOnlyList<DiscoveryNode>> DiscoverAsync(RepoRow repo, CancellationToken ct = default);
}
