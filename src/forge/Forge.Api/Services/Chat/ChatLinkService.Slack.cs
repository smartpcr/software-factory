// -----------------------------------------------------------------------
// <copyright file="ChatLinkService.Slack.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat.Slack;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatLinkService
{
    public async Task<IReadOnlyList<ChatDestinationRow>> SlackDestinationsAsync(ForgeActor? actor, string connectionId, CancellationToken ct)
    {
        var connection = await this.SlackConnectionAsync(actor, connectionId, ct);
        return await control.ChatDestinations.AsNoTracking().Where(d => d.ConnectionId == connection.Id
            && d.ExternalTenantId == connection.InstallationId && d.DeletedAt == null).OrderBy(d => d.DisplayName).ToListAsync(ct);
    }

    public async Task<ChatSlackLinkConfirmation> BeginSlackConfirmationAsync(ForgeActor? actor, string connectionId, ChatSlackLinkRequest request, CancellationToken ct)
    {
        var connection = await this.SlackConnectionAsync(actor, connectionId, ct);
        if (!ChatPluginInteractiveGate.AvailableFor(options.Value)) throw new ChatConfigurationException("host-chat-disabled", 409);
        if (request.ExternalUserId is not { Length: > 1 and <= 128 } sender || sender[0] is not ('U' or 'W') || !sender.All(char.IsAsciiLetterOrDigit))
            throw new ChatConfigurationException("invalid-link-target");
        var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == request.DestinationId
            && d.ConnectionId == connection.Id && d.ExternalTenantId == connection.InstallationId && d.DeletedAt == null, ct)
            ?? throw new ChatConfigurationException("destination-required");
        await supervisor.RefreshAsync(ct);
        var session = await supervisor.GetAsync(connection, ct);
        if (session?.Provider is not SlackConnectionSession slack) throw new ChatConfigurationException("session-unavailable", 409);
        await this.CancelSlackConfirmationAsync(actor, connectionId, ct);
        var started = clock.GetUtcNow();
        var challenge = await this.BeginAsync(actor, connection.Id, connection.InstallationId, sender, ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var sent = await slack.RequestIdentityLinkAsync(new(connection.Id, connection.InstallationId, destination.ChannelId), sender, challenge.Token, timeout.Token);
            if (!sent.Succeeded) throw new ChatConfigurationException(sent.Failure?.Code ?? "slack-send-outcome-unknown", 409);
            // The provider accepted a private request; only a later user callback proves the account.
            var remaining = Math.Max(0, 120 - (int)Math.Ceiling((clock.GetUtcNow() - started).TotalSeconds));
            return new(remaining, $"https://slack.com/app_redirect?team={Uri.EscapeDataString(connection.InstallationId)}&channel={Uri.EscapeDataString(destination.ChannelId)}", destination.DisplayName);
        }
        catch
        {
            var failed = await control.ChatLinkChallenges.SingleAsync(c => c.Hash == ChatEventCatalog.Hash(challenge.Token), CancellationToken.None);
            if (failed.State == "pending") { failed.State = "cancelled"; await control.SaveChangesAsync(CancellationToken.None); }
            throw;
        }
    }

    public async Task CancelSlackConfirmationAsync(ForgeActor? actor, string connectionId, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.LinkSelf, new(), ct);
        await runtime.InboxGate.WaitAsync(ct);
        try
        {
            foreach (var row in await control.ChatLinkChallenges.Where(c => c.ConnectionId == connectionId && c.UserId == actor!.UserId && c.State == "pending").ToListAsync(ct))
                row.State = "cancelled";
            if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(ct);
        }
        finally { runtime.InboxGate.Release(); }
    }

    private async Task<ChatConnectionRow> SlackConnectionAsync(ForgeActor? actor, string connectionId, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.LinkSelf, new(), ct);
        var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId
            && c.ProviderId == "slack" && c.Enabled && c.DeletedAt == null, ct) ?? throw new ChatConfigurationException("connection-unavailable", 409);
        if (!await authorizer.IsAllowedAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct))
        {
            var readable = await authorizer.ReadableProductIdsAsync(actor, ct);
            if (!await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connectionId && readable.Contains(g.ProductId), ct))
                throw new ChatConfigurationException("connection-unavailable", 403);
        }
        return connection;
    }
}

public sealed record ChatSlackLinkRequest(string DestinationId, string ExternalUserId);
public sealed record ChatSlackLinkConfirmation(int ExpiresInSeconds, string DeepLink, string ChannelName);