// -----------------------------------------------------------------------
// <copyright file="ChatLinkService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Connection-scoped challenges and durable link history reuse the Stage 0B enrollment/link authority.</summary>
public sealed partial class ChatLinkService(ControlDbContext control, HostIdentityStore identities, ForgeActorResolver actors,
    ForgeCapabilityAuthorizer authorizer, ChatRuntimeState runtime, TimeProvider clock, ChatConnectionSupervisor supervisor, ChatMigratedIdentity migrated,
    IOptions<ChatPluginOptions> options)
{
    public async Task<ChatIdentityChallenge> BeginAsync(ForgeActor? actor, string connectionId, string tenantId, string senderId, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.LinkSelf, new(), ct);
        var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId && c.Enabled && c.DeletedAt == null, ct)
            ?? throw new ChatConfigurationException("connection-unavailable", 409);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(senderId) || tenantId.Length > 512 || senderId.Length > 512)
            throw new ChatConfigurationException("invalid-link-target");
        if (connection.ProviderId == "telegram" && tenantId != connection.InstallationId) throw new ChatConfigurationException("link-bot-mismatch");
        if (connection.ProviderId == "slack" && tenantId != connection.InstallationId) throw new ChatConfigurationException("link-workspace-mismatch");
        var token = identities.IssueLinkChallenge(actor!.UserId, connection.ProviderId, tenantId, senderId);
        control.ChatLinkChallenges.Add(new() { Hash = ChatEventCatalog.Hash(token), ConnectionId = connectionId, ConnectionRevision = connection.Revision,
            UserId = actor.UserId, ExternalTenantId = tenantId, ExternalUserId = senderId, ExpiresAt = clock.GetUtcNow().AddMinutes(2).ToUnixTimeMilliseconds(),
            PreviousLinkId = identities.FindLink(connection.ProviderId, tenantId, senderId)?.Id });
        await control.SaveChangesAsync(ct);
        var session = connection.ProviderId == "telegram" ? await supervisor.GetAsync(connection, ct) : null;
        var deepLink = session?.Provider is Common.Chat.Telegram.TelegramSession telegram ? $"https://t.me/{telegram.BotUsername}?start={token}" : null;
        return new(token, 120, deepLink, connection.ProviderId == "slack" ? "!forge link " + token : null);
    }

    public Task<ForgeActor?> RedeemAsync(string token, string connectionId, string tenantId, string senderId, CancellationToken ct)
        => this.RedeemHashAsync(ChatEventCatalog.Hash(token), connectionId, tenantId, senderId, ct);

    internal async Task<ForgeActor?> RedeemHashAsync(string hash, string connectionId, string tenantId, string senderId, CancellationToken ct, string? inboxId = null)
    {
        await runtime.InboxGate.WaitAsync(ct);
        try
        {
            var challenge = await control.ChatLinkChallenges.SingleOrDefaultAsync(c => c.Hash == hash, ct);
            var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId && c.Enabled && c.DeletedAt == null, ct);
            if (connection is null || challenge is null || challenge.ConnectionId != connectionId || challenge.ConnectionRevision != connection.Revision
                || challenge.ExternalTenantId != tenantId || challenge.ExternalUserId != senderId || challenge.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds()
                || challenge.State is not ("pending" or "redeeming")) return null;
            OperatorLink? link;
            if (challenge.State == "pending")
            {
                challenge.State = "redeeming"; await control.SaveChangesAsync(ct);
                link = identities.RedeemLinkChallengeHash(hash, connection.ProviderId, tenantId, senderId);
            }
            else
            {
                link = identities.FindLink(connection.ProviderId, tenantId, senderId);
                if (link?.Id == challenge.PreviousLinkId) link = null;
            }
            if (link?.UserId != challenge.UserId) { challenge.State = "rejected"; await control.SaveChangesAsync(ct); return null; }
            await this.RecordAsync(link, ct); challenge.State = "consumed";
            if (inboxId is not null && await control.ChatInbox.FindAsync([inboxId], ct) is { } inbox) inbox.Result = "linked";
            await control.SaveChangesAsync(ct);
            return await actors.FromProviderAsync(connection.ProviderId, tenantId, senderId, ct);
        }
        finally { runtime.InboxGate.Release(); }
    }

    public async Task<ForgeActor?> ResolveAsync(string provider, string tenant, string sender, CancellationToken ct)
    {
        var actor = await actors.FromProviderAsync(provider, tenant, sender, ct);
        if (actor is null) return await migrated.ResolveAsync(provider, tenant, sender, ct);
        var link = identities.FindLink(provider, tenant, sender);
        if (link is null) return null;
        await this.RecordAsync(link, ct);
        return actor;
    }

    private async Task RecordAsync(OperatorLink link, CancellationToken ct)
    {
        if (await control.ChatOperatorLinks.AnyAsync(l => l.Id == link.Id && l.RevokedAt == null, ct)) return;
        foreach (var old in await control.ChatOperatorLinks.Where(l => l.ProviderId == link.Provider && l.ExternalTenantId == link.TenantId
            && l.ExternalUserId == link.ProviderUserId && l.RevokedAt == null).ToListAsync(ct)) old.RevokedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await control.SaveChangesAsync(ct);
        control.ChatOperatorLinks.Add(new() { Id = link.Id, ProviderId = link.Provider, ExternalTenantId = link.TenantId, ExternalUserId = link.ProviderUserId,
            UserId = link.UserId, EnrollmentRevision = link.Revision, CreatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() });
        await control.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(ForgeActor? actor, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.LinkSelf, new(), ct);
        identities.RevokeLinks(actor!.UserId);
        foreach (var row in await control.ChatOperatorLinks.Where(l => l.UserId == actor.UserId && l.RevokedAt == null).ToListAsync(ct)) row.RevokedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var row in await control.ChatLinkChallenges.Where(c => c.UserId == actor.UserId && (c.State == "pending" || c.State == "cancelled")).ToListAsync(ct)) row.State = "revoked";
        await control.SaveChangesAsync(ct);
    }
}

public sealed record ChatConnectionLinkRequest(string ExternalTenantId, string ExternalUserId);public sealed record ChatIdentityChallenge(string Token, int ExpiresInSeconds, string? DeepLink, string? ChannelCommand);