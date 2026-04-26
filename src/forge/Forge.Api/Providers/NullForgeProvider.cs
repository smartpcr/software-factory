// -----------------------------------------------------------------------
// <copyright file="NullForgeProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using Forge.Api.Contracts;

/// <summary>
/// Used when neither ADO nor GitHub is configured — keeps the rest of the
/// host wired without forcing tests to create real PATs.
/// </summary>
public class NullForgeProvider : IForgeProvider
{
    public string Kind => "null";

    public bool IsConfigured => false;

    public Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());

    public Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<Card?>(null);

    public Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Pr>>(Array.Empty<Pr>());

    public Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<Pr?>(null);

    public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) =>
        Task.FromResult<BuildStatus?>(null);

    public Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderHealth(this.Kind, false, "no provider configured", null, 0, null, null));
}
