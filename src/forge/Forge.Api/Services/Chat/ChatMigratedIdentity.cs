// -----------------------------------------------------------------------
// <copyright file="ChatMigratedIdentity.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Migration aliases keep the original confirmed enrollment/link as their authority. Revocation takes effect immediately.</summary>
public sealed class ChatMigratedIdentity(ControlDbContext control, HostIdentityStore identities, ForgeActorResolver actors)
{
    public async Task<IReadOnlyList<OperatorLink>> LinksAsync(CancellationToken ct)
    {
        if (!await control.ChatMigrationState.AnyAsync(s => s.Owner == "plugins", ct)) return [];
        var active = identities.ActiveProviderLinks;
        var result = new List<OperatorLink>();
        foreach (var item in await control.ChatMigrationItems.AsNoTracking().Where(i => i.Kind == "link").ToListAsync(ct))
        {
            var source = JsonSerializer.Deserialize<OperatorLink>(item.SnapshotJson);
            if (source is not null && active.Contains(source)) result.Add(source with { TenantId = item.TargetId });
        }
        return result;
    }
    public async Task<ForgeActor?> ResolveAsync(string provider, string tenant, string sender, CancellationToken ct)
    {
        var links = await this.LinksAsync(ct);
        var candidates = links.Where(l => l.Provider == provider && l.TenantId == tenant && l.ProviderUserId == sender).ToArray();
        if (candidates.Select(l => l.UserId).Distinct().Count() != 1) return null;
        var source = identities.ActiveProviderLinks.SingleOrDefault(l => l.Id == candidates[0].Id);
        return source is null ? null : await actors.FromProviderAsync(source.Provider, source.TenantId, source.ProviderUserId, ct);
    }

    public async Task<ForgeActor?> ResolveLegacyAsync(string provider, string channel, string sender, CancellationToken ct)
    {
        if (provider != "telegram") return null;
        var state = await control.ChatMigrationState.AsNoTracking().SingleOrDefaultAsync(s => s.Owner == "legacy" && s.Phase == "legacy", ct);
        if (state is null || JsonSerializer.Deserialize<ChatMigrationSource>(state.SourceJson)?.Channels.Contains(channel) != true) return null;
        var bot = await control.ChatConnections.AsNoTracking().Where(c => c.Id == ChatMigrationService.ConnectionId && c.DeletedAt == null).Select(c => c.InstallationId).SingleOrDefaultAsync(ct);
        return bot is null ? null : await actors.FromProviderAsync(provider, bot, sender, ct);
    }
}