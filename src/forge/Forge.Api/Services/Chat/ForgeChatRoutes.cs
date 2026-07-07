// -----------------------------------------------------------------------
// <copyright file="ForgeChatRoutes.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

/// <summary>
/// Forge's app-route-key scheme for the <c>Common.Chat</c> channel map. A chat
/// channel (Telegram forum topic) binds to a story spawn, and the key carries
/// the owning product so inbound dispatch can open the right per-product DB:
/// <c>spawn/{productId}/{spawnId}</c>. Workstream / work-item granularity is
/// resolved *inside* the spawn scope via the notification outbox (reply
/// correlation), not the route key.
/// </summary>
public static class ForgeChatRoutes
{
    private const string SpawnPrefix = "spawn/";

    /// <summary>Route key for a story spawn's channel.</summary>
    public static string Spawn(string productId, string spawnId) => $"{SpawnPrefix}{productId}/{spawnId}";

    /// <summary>Parse a spawn route key back into its product + spawn ids.</summary>
    public static bool TryParseSpawn(string? routeKey, out string productId, out string spawnId)
    {
        productId = string.Empty;
        spawnId = string.Empty;
        if (routeKey is null || !routeKey.StartsWith(SpawnPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = routeKey[SpawnPrefix.Length..];
        var sep = rest.IndexOf('/');
        if (sep <= 0 || sep >= rest.Length - 1)
        {
            return false;
        }

        productId = rest[..sep];
        spawnId = rest[(sep + 1)..];
        return true;
    }
}
