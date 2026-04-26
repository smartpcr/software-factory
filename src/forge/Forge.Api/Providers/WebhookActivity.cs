// -----------------------------------------------------------------------
// <copyright file="WebhookActivity.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using System.Collections.Concurrent;

/// <summary>
/// Tracks the per-provider "last webhook arrived" timestamp. The polling
/// hosted service consults it to decide whether the provider is "live"
/// (a webhook arrived recently → poll less aggressively or not at all)
/// or silent (fall back to active polling per Phase 5.8).
/// </summary>
public class WebhookActivity
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSeen = new();

    public void Mark(string providerKind) =>
        this.lastSeen[providerKind] = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeen(string providerKind) =>
        this.lastSeen.TryGetValue(providerKind, out var ts) ? ts : null;

    public bool IsRecent(string providerKind, TimeSpan window)
    {
        var ts = this.LastSeen(providerKind);
        return ts.HasValue && DateTimeOffset.UtcNow - ts.Value < window;
    }
}
