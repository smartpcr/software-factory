// -----------------------------------------------------------------------
// <copyright file="ChatPluginEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Plugins;
using Forge.Api.Services.Chat;
using Microsoft.Extensions.Options;

public static class ChatPluginEndpoints
{
    public static WebApplication MapChatPluginEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ChatConfigurationException ex) { return Results.Problem(title: ex.Code, statusCode: ex.Status); }
        });
        var self = new ForgeEndpointCapability(ForgeCapabilities.LinkSelf, "self");
        var host = new ForgeEndpointCapability(ForgeCapabilities.ConnectionManage, "host");
        var hostAlerts = new ForgeEndpointCapability(ForgeCapabilities.HostAlertManage, "host");
        var read = new ForgeEndpointCapability(ForgeCapabilities.SpawnRead, "chat-spawn");
        var write = new ForgeEndpointCapability(ForgeCapabilities.SubscriptionManage, "chat-spawn");

        api.MapGet("/plugins", (WorkstreamPluginRegistry registry) => new { registry.IsAvailable, registry.Errors, Plugins = registry.Descriptors }).WithMetadata(self);
        api.MapGet("/chat/providers", (IChatProviderRegistry registry) => new { registry.IsAvailable, registry.Errors, registry.Providers }).WithMetadata(self);
        api.MapGet("/chat/configuration", (IOptions<ChatPluginOptions> options) => new
        {
            options.Value.Enabled, options.Value.Engine, options.Value.HostId, options.Value.UnavailableReason,
            InteractiveAvailable = ChatPluginInteractiveGate.IsAvailable,
        }).WithMetadata(host);
        api.MapPost("/chat/retention/purge-content", async (ForgeActionContext action, ChatSliceRetention retention, CancellationToken ct)
            => await retention.PurgeContentAsync(await action.ActorAsync(ct), ct)).WithMetadata(host);

        api.MapGet("/chat/connections", async (string? productId, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.ListConnectionsAsync(await action.ActorAsync(ct), productId, ct)).WithMetadata(self);
        api.MapGet("/chat/connections/{id}", async (string id, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.GetConnectionAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(host);
        api.MapPut("/chat/connections/{id}", async (string id, ChatConnectionWrite request, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.PutConnectionAsync(await action.ActorAsync(ct), id, request, ct)).WithMetadata(host);
        api.MapDelete("/chat/connections/{id}", async (string id, long expectedRevision, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct) =>
        {
            await service.DeleteConnectionAsync(await action.ActorAsync(ct), id, expectedRevision, ct);
            return Results.NoContent();
        }).WithMetadata(host);
        api.MapGet("/chat/connections/{connectionId}/destinations", async (string connectionId, string? productId, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.ListDestinationsAsync(await action.ActorAsync(ct), connectionId, productId, ct)).WithMetadata(self);
        api.MapPut("/chat/connections/{connectionId}/destinations/{id}", async (string connectionId, string id, ChatDestinationWrite request,
            ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.PutDestinationAsync(await action.ActorAsync(ct), connectionId, id, request, ct)).WithMetadata(host);
        api.MapDelete("/chat/connections/{connectionId}/destinations/{id}", async (string connectionId, string id, long expectedRevision,
            ForgeActionContext action, ChatConfigurationService service, CancellationToken ct) =>
        {
            await service.DeleteDestinationAsync(await action.ActorAsync(ct), connectionId, id, expectedRevision, ct);
            return Results.NoContent();
        }).WithMetadata(host);

        const string SpawnPath = "/products/{productId}/story-spawns/{spawnId}/plugins/chat";
        api.MapGet(SpawnPath + "/notification-setup", async (string productId, string spawnId, ForgeActionContext action, ChatTelegramSliceService service, CancellationToken ct)
            => await service.GetAsync(await action.ActorAsync(ct), productId, spawnId, ct)).WithMetadata(read);
        api.MapPut(SpawnPath + "/notification-setup", async (string productId, string spawnId, ChatNotificationSetupWrite request,
            ForgeActionContext action, ChatTelegramSliceService service, CancellationToken ct)
            => await service.SaveAsync(await action.ActorAsync(ct), productId, spawnId, request, ct)).WithMetadata(write);
        api.MapGet(SpawnPath, async (string productId, string spawnId, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.GetPluginAsync(await action.ActorAsync(ct), productId, spawnId, ct)).WithMetadata(read);
        api.MapPut(SpawnPath, async (string productId, string spawnId, ChatPluginWrite request, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.PutPluginAsync(await action.ActorAsync(ct), productId, spawnId, request, ct)).WithMetadata(write);
        api.MapPut(SpawnPath + "/subscriptions/{id}", async (string productId, string spawnId, string id, ChatSubscriptionWrite request,
            ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.PutSubscriptionAsync(await action.ActorAsync(ct), ChatConfigurationScope.Spawn(productId, spawnId), id, request, ct)).WithMetadata(write);
        api.MapDelete(SpawnPath + "/subscriptions/{id}", async (string productId, string spawnId, string id, long expectedRevision, long parentRevision,
            ForgeActionContext action, ChatConfigurationService service, CancellationToken ct) =>
        {
            await service.DeleteSubscriptionAsync(await action.ActorAsync(ct), ChatConfigurationScope.Spawn(productId, spawnId), id, expectedRevision, parentRevision, ct);
            return Results.NoContent();
        }).WithMetadata(write);

        api.MapGet("/chat/host/subscriptions", async (ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.GetHostSetupAsync(await action.ActorAsync(ct), ct)).WithMetadata(hostAlerts);
        api.MapPut("/chat/host/subscriptions/{id}", async (string id, ChatSubscriptionWrite request, IOptions<ChatPluginOptions> options,
            ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.PutSubscriptionAsync(await action.ActorAsync(ct), ChatConfigurationScope.Host(options.Value.HostId), id, request, ct)).WithMetadata(hostAlerts);
        api.MapDelete("/chat/host/subscriptions/{id}", async (string id, long expectedRevision, IOptions<ChatPluginOptions> options,
            ForgeActionContext action, ChatConfigurationService service, CancellationToken ct) =>
        {
            await service.DeleteSubscriptionAsync(await action.ActorAsync(ct), ChatConfigurationScope.Host(options.Value.HostId), id, expectedRevision, 0, ct);
            return Results.NoContent();
        }).WithMetadata(hostAlerts);
        return app;
    }
}