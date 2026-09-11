// -----------------------------------------------------------------------
// <copyright file="ChatMigrationCompatibility.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed record ChatMigrationTestSendResult(bool Ok, string Platform, string ChatId, string? MessageId, ChatCheckView Check);

public sealed class ChatMigrationCompatibility(ControlDbContext control, ChatLinkService links, ChatConnectionCheckService checks)
{
    private async Task<(ChatConnectionRow Connection, ChatDestinationRow Destination)> TargetAsync(string? channel, CancellationToken ct)
    {
        var state = await control.ChatMigrationState.AsNoTracking().SingleOrDefaultAsync(s => s.Owner == "plugins" && s.Phase != "compatibility-retired", ct)
            ?? throw new ChatConfigurationException("select-plugin-connection", 409);
        var source = JsonSerializer.Deserialize<ChatMigrationSource>(state.SourceJson)!;
        var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == ChatMigrationService.ConnectionId, ct);
        channel = string.IsNullOrWhiteSpace(channel) ? source.GlobalChannel : channel.Trim();
        var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.ConnectionId == connection.Id && d.ChannelId == channel && d.DeletedAt == null, ct)
            ?? throw new ChatConfigurationException("destination-not-found", 404);
        return (connection, destination);
    }
    public async Task<ChatMigrationTestSendResult> TestSendAsync(ForgeActor? actor, string? channel, string? text, CancellationToken ct)
    {
        var target = await this.TargetAsync(channel, ct);
        var result = await checks.SendAsync(actor, target.Connection.Id, target.Destination.Id,
            new(Guid.NewGuid().ToString("N"), target.Connection.Revision, target.Destination.Revision, text), ct);
        var message = await control.ChatNotifications.AsNoTracking().Where(n => n.Id == result.Id).Select(n => n.ChatMessageId).SingleAsync(ct);
        return new(result.State == "sent", "telegram", target.Destination.ChannelId, message, result);
    }
    public async Task<object> LinkAsync(ForgeActor? actor, string provider, string tenant, string sender, CancellationToken ct)
    {
        if (provider != "telegram") throw new ChatConfigurationException("legacy-provider-unsupported");
        var target = await this.TargetAsync(tenant, ct);
        return await links.BeginAsync(actor, target.Connection.Id, target.Connection.InstallationId, sender, ct);
    }
}