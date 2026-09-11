// -----------------------------------------------------------------------
// <copyright file="ChatProviderSetupService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Host-authorized provider checks share the supervisor's session; discovery never opens its own receiver.</summary>
public sealed class ChatProviderSetupService(ControlDbContext control, ForgeCapabilityAuthorizer authorizer,
    ChatConnectionSupervisor supervisor, TimeProvider clock, ChatRuntimeState runtime, ChatEngineCoordinator engine)
{
    public async Task<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(ForgeActor? actor, string connectionId, string? cursor, CancellationToken ct)
    {
        using var lease = await engine.EnterAsync("plugins", ct) ?? throw new ChatConfigurationException("plugin-engine-not-owner", 409);
        using var continuation = engine.ContinueLease();
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var connection = await this.ConnectionAsync(connectionId, ct);
        var session = await supervisor.GetAsync(connection, ct);
        if (session is null) return ChatProviderResult<ChatDestinationPage>.Fail(ChatFailureKind.InvalidConfiguration, this.FailureReason(connectionId));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await session.Provider.DiscoverAsync(cursor, timeout.Token);
        if (!result.Succeeded) runtime.RecordHealth(connectionId, "degraded", ChatConnectionSupervisor.SafeProviderReason(result.Failure?.Code), null, null);
        return result;
    }
    public async Task<ChatProviderResult<ChatDestination>> ValidateAsync(ForgeActor? actor, string connectionId, string destinationId, CancellationToken ct)
    {
        using var lease = await engine.EnterAsync("plugins", ct) ?? throw new ChatConfigurationException("plugin-engine-not-owner", 409);
        using var continuation = engine.ContinueLease();
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var connection = await this.ConnectionAsync(connectionId, ct);
        var destination = await control.ChatDestinations.SingleOrDefaultAsync(d => d.Id == destinationId && d.ConnectionId == connectionId && d.DeletedAt == null, ct)
            ?? throw new ChatConfigurationException("destination-not-found", 404);
        var session = await supervisor.GetAsync(connection, ct);
        if (session is null) return ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.InvalidConfiguration, this.FailureReason(connectionId));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var address = new ChatAddress(connectionId, destination.ExternalTenantId, destination.ChannelId);
        var result = await session.Provider.ValidateDestinationAsync(address, timeout.Token);
        if (result.Succeeded && result.Value!.Address != address) result = ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.InvalidConfiguration, "destination-mismatch");
        destination.AccessState = result.Succeeded ? "ready" : "unavailable"; destination.ValidatedAt = clock.GetUtcNow();
        if (result.Succeeded) destination.Kind = result.Value!.Kind;
        runtime.RecordHealth(connectionId, result.Succeeded ? "ready" : "degraded", result.Succeeded ? null : ChatConnectionSupervisor.SafeProviderReason(result.Failure?.Code), null, null);
        // Validation updates health for this immutable address, not its routing revision.
        await control.SaveChangesAsync(ct); return result;
    }
    private string FailureReason(string id) => runtime.Health.TryGetValue(id, out var health) ? ChatConnectionSupervisor.SafeProviderReason(health.Reason) : "session-unavailable";
    private async Task<ChatConnectionRow> ConnectionAsync(string id, CancellationToken ct) => await control.ChatConnections.AsNoTracking()
        .SingleOrDefaultAsync(c => c.Id == id && c.Enabled && c.DeletedAt == null, ct) ?? throw new ChatConfigurationException("connection-unavailable", 409);
}