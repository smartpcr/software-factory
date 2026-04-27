// -----------------------------------------------------------------------
// <copyright file="IAdoClassificationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using Forge.Api.Contracts;
using Forge.Api.Persistence;

/// <summary>
/// Phase 15.2 — discover ADO classification nodes (iterations or areas)
/// against a repo's project, optionally filtered by a path prefix. The
/// result is flat (one entry per node) so the UI can render it as a list
/// + indent it by depth without recursing.
/// </summary>
public interface IAdoClassificationService
{
    /// <summary>"Iterations" or "Areas".</summary>
    Task<IReadOnlyList<DiscoveryNode>> DiscoverAsync(
        RepoRow repo,
        string kind,
        string? prefix,
        CancellationToken ct = default);
}
