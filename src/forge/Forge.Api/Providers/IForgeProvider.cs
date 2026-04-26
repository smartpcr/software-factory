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
