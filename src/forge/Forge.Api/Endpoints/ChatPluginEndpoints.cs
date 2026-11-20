// -----------------------------------------------------------------------
// <copyright file="ChatPluginEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Hosting;
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

        api.MapGet("/chat/authorization", async (ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct)
            => await service.AvailabilityAsync(await action.ActorAsync(ct), ct)).WithMetadata(host);
        api.MapPost("/chat/authorization", async (ChatAuthorizationStart request, ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct)
            => await service.BeginAsync(await action.ActorAsync(ct), request, ct)).WithMetadata(host);
        api.MapPost("/chat/authorization/telegram/manager", async (ChatTelegramManagerSetup request, HttpContext context,
            ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await service.ConfigureTelegramAsync(await action.ActorAsync(ct), request, ct);
        }).WithMetadata(host, new SensitiveRequestBody());
        api.MapPost("/chat/authorization/slack/setup", async (ChatAuthorizationStart request, HttpContext context,
            ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await service.BeginSlackSetupAsync(await action.ActorAsync(ct), request, ct);
        }).WithMetadata(host);
        api.MapPost("/chat/authorization/{id}/slack/approve", async (string id, ChatSlackSetupApproval request, HttpContext context,
            ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await service.ApproveSlackSetupAsync(await action.ActorAsync(ct), id, request, ct);
        }).WithMetadata(host, new SensitiveRequestBody());
        // Polling exchanges a one-time provider code, so it uses POST and the standard CSRF checks.
        api.MapPost("/chat/authorization/{id}/poll", async (string id, ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct)
            => await service.PollAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(host);
        api.MapPost("/chat/authorization/{id}/save", async (string id, ChatAuthorizationSave request, ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct)
            => await service.SaveAsync(await action.ActorAsync(ct), id, request, ct)).WithMetadata(host);
        api.MapDelete("/chat/authorization/{id}", async (string id, ForgeActionContext action, ChatAuthorizationService service, CancellationToken ct)
            => await service.CancelAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(host);
        app.MapGet("/chat/authorize/slack", async (HttpContext context, ChatAuthorizationService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            var query = context.Request.Query;
            var accepted = query["state"].Count == 1 && query["code"].Count <= 1 && query["error"].Count <= 1
                && await service.CallbackAsync(query["state"].ToString(), query["code"].Count == 0 ? null : query["code"].ToString(),
                    query["error"].Count == 0 ? null : query["error"].ToString(), ct);
            return Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Forge · Slack authorization</title>"
                + (accepted ? "<h1>Return to Forge</h1><p>You can close this window. Forge will show the result of your Slack authorization.</p>"
                    : "<h1>Authorization link expired</h1><p>Close this window and start again from Forge Settings.</p>"), "text/html", statusCode: accepted ? 200 : 400);
        }).AllowAnonymous();

        api.MapGet("/plugins", (WorkstreamPluginRegistry registry) => new { registry.IsAvailable, registry.Errors, Plugins = registry.Descriptors }).WithMetadata(self);
        api.MapGet("/chat/providers", (IChatProviderRegistry registry) => new { registry.IsAvailable, registry.Errors, registry.Providers }).WithMetadata(self);
        api.MapPost("/chat/connections/{id}/link-challenge", async (string id, ChatConnectionLinkRequest request, ForgeActionContext action, ChatLinkService service, CancellationToken ct)
            => await service.BeginAsync(await action.ActorAsync(ct), id, request.ExternalTenantId, request.ExternalUserId, ct)).WithMetadata(self);
        api.MapGet("/chat/connections/{id}/link-destinations", async (string id, ForgeActionContext action, ChatLinkService service, CancellationToken ct)
            => await service.SlackDestinationsAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(self);
        api.MapPost("/chat/connections/{id}/link-confirmation", async (string id, ChatSlackLinkRequest request, ForgeActionContext action, ChatLinkService service, CancellationToken ct)
            => await service.BeginSlackConfirmationAsync(await action.ActorAsync(ct), id, request, ct)).WithMetadata(self);
        api.MapDelete("/chat/connections/{id}/link-confirmation", async (string id, ForgeActionContext action, ChatLinkService service, CancellationToken ct) =>
        { await service.CancelSlackConfirmationAsync(await action.ActorAsync(ct), id, ct); return Results.NoContent(); }).WithMetadata(self);
        api.MapDelete("/chat/operator-links/self", async (ForgeActionContext action, ChatLinkService service, CancellationToken ct) =>
        { await service.RevokeAsync(await action.ActorAsync(ct), ct); return Results.NoContent(); }).WithMetadata(self);
        api.MapGet("/chat/management", async (ForgeActionContext action, ChatManagementService service, CancellationToken ct)
            => await service.ReadAsync(await action.ActorAsync(ct), ct)).WithMetadata(self);
        api.MapGet("/chat/migration/preview", async (ForgeActionContext action, ChatMigrationService service, CancellationToken ct)
            => await service.PreviewAsync(await action.ActorAsync(ct), ct)).WithMetadata(host);
        api.MapPost("/chat/migration/import", async (ChatMigrationImport request, ForgeActionContext action, ChatMigrationService service, CancellationToken ct)
            => await service.ImportAsync(await action.ActorAsync(ct), request, ct)).WithMetadata(host);
        api.MapPost("/chat/migration/cutover", async (ChatMigrationCutover request, ForgeActionContext action, ChatMigrationService service, CancellationToken ct)
            => await service.CutoverAsync(await action.ActorAsync(ct), request, ct)).WithMetadata(host);
        api.MapGet("/chat/migration/rollback-preview", async (ForgeActionContext action, ChatMigrationService service, CancellationToken ct)
            => await service.RollbackPreviewAsync(await action.ActorAsync(ct), ct)).WithMetadata(host);
        api.MapPost("/chat/migration/rollback", async (ChatMigrationRollback request, ForgeActionContext action, ChatMigrationService service, CancellationToken ct)
            => await service.RollbackAsync(await action.ActorAsync(ct), request, ct)).WithMetadata(host);
        api.MapPost("/chat/migration/retire-compatibility", async (ChatMigrationRollback request, ForgeActionContext action, ChatMigrationService service, CancellationToken ct)
            => await service.RetireCompatibilityAsync(await action.ActorAsync(ct), request.ExpectedRevision, ct)).WithMetadata(host);
        api.MapPost("/chat/preview", async (ChatPreviewRequest request, ForgeActionContext action, ChatManagementService service, CancellationToken ct)
            => await service.PreviewAsync(await action.ActorAsync(ct), request, ct)).WithMetadata(self);
        api.MapGet("/chat/connections/{id}/health", async (string id, ForgeActionContext action, ChatManagementService service, CancellationToken ct)
            => await service.HealthAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(host);
        api.MapPut("/chat/connections/{id}/credentials/{name}", async (string id, string name, ChatCredentialWrite request, ForgeActionContext action, ChatManagementService service, CancellationToken ct)
            => await service.CredentialAsync(await action.ActorAsync(ct), id, name, request, ct)).WithMetadata(host, new SensitiveRequestBody());
        api.MapGet("/chat/connections/{connectionId}/destinations/{id}/checks", async (string connectionId, string id, ForgeActionContext action, ChatConnectionCheckService service, CancellationToken ct)
            => await service.ReadAsync(await action.ActorAsync(ct), connectionId, id, ct)).WithMetadata(host);
        api.MapPost("/chat/connections/{connectionId}/destinations/{id}/checks", async (string connectionId, string id, ChatCheckRequest request, ForgeActionContext action, ChatConnectionCheckService service, CancellationToken ct)
            => await service.SendAsync(await action.ActorAsync(ct), connectionId, id, request, ct)).WithMetadata(host);
        api.MapGet("/products/{productId}/chat/summaries", async (string productId, ForgeActionContext action, ChatManagementService service, CancellationToken ct)
            => await service.SummariesAsync(await action.ActorAsync(ct), productId, ct)).WithMetadata(self);
        api.MapGet("/chat/configuration", async (IOptions<ChatPluginOptions> options, ChatEngineCoordinator engine, CancellationToken ct) =>
        {
            await engine.InitializeAsync(ct);
            return new { options.Value.Enabled, options.Value.Engine, options.Value.HostId, options.Value.UnavailableReason,
                InteractiveAvailable = ChatPluginInteractiveGate.AvailableFor(options.Value) };
        }).WithMetadata(host);
        api.MapPost("/chat/retention/purge-content", async (ForgeActionContext action, ChatRetentionService retention, CancellationToken ct)
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
        api.MapGet("/chat/connections/{connectionId}/discovery", async (string connectionId, string? cursor, ForgeActionContext action, ChatProviderSetupService service, CancellationToken ct)
            => await service.DiscoverAsync(await action.ActorAsync(ct), connectionId, cursor, ct)).WithMetadata(host);
        api.MapPost("/chat/connections/{connectionId}/destinations/{id}/validate", async (string connectionId, string id, ForgeActionContext action, ChatProviderSetupService service, CancellationToken ct)
            => await service.ValidateAsync(await action.ActorAsync(ct), connectionId, id, ct)).WithMetadata(host);
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
        api.MapGet(SpawnPath + "/setup", async (string productId, string spawnId, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.SetupAsync(await action.ActorAsync(ct), productId, spawnId, ct)).WithMetadata(read);
        api.MapPut(SpawnPath + "/setup", async (string productId, string spawnId, ChatSetupWrite request, ForgeActionContext action, ChatConfigurationService service, CancellationToken ct)
            => await service.SaveSetupAsync(await action.ActorAsync(ct), productId, spawnId, request, ct)).WithMetadata(write);
        api.MapGet(SpawnPath + "/history", async (string productId, string spawnId, ForgeActionContext action, ChatStatusService service, CancellationToken ct)
            => await service.SpawnHistoryAsync(await action.ActorAsync(ct), productId, spawnId, ct)).WithMetadata(read);
        api.MapGet(SpawnPath + "/status", async (string productId, string spawnId, ForgeActionContext action, ChatStatusService service, CancellationToken ct)
            => await service.SpawnAsync(await action.ActorAsync(ct), productId, spawnId, ct)).WithMetadata(read);
        api.MapGet("/chat/host/status", async (ForgeActionContext action, ChatStatusService service, CancellationToken ct)
            => await service.HostAsync(await action.ActorAsync(ct), ct)).WithMetadata(hostAlerts);
        api.MapPost("/chat/deliveries/{id}/retry", async (string id, ForgeActionContext action, ChatStatusService service, CancellationToken ct)
            => await service.RetryAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(self);
        api.MapPost("/chat/deliveries/{id}/skip", async (string id, ForgeActionContext action, ChatStatusService service, CancellationToken ct)
            => await service.SkipAsync(await action.ActorAsync(ct), id, ct)).WithMetadata(self);
        api.MapPost("/chat/notifications/{id}/resend", async (string id, ChatResendRequest request, ForgeActionContext action, ChatStatusService service, CancellationToken ct)
            => await service.ResendAsync(await action.ActorAsync(ct), id, request.RequestId, ct)).WithMetadata(self);
        api.MapPost(SpawnPath + "/purge-content", async (string productId, string spawnId, ForgeActionContext action, ChatRetentionService service, CancellationToken ct)
            => await service.PurgeSpawnContentAsync(await action.ActorAsync(ct), productId, spawnId, ct)).WithMetadata(write);
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

        // "Set up once": host-default story destinations (docs/chat/chat-per-workstream-conversations.md D2/D3).
        api.MapGet("/chat/host-defaults", async (ForgeActionContext action, ChatHostDefaultService service, CancellationToken ct)
            => await service.GetAsync(await action.ActorAsync(ct), ct)).WithMetadata(host);
        api.MapPut("/chat/host-defaults", async (ChatHostDefaultWrite request, ForgeActionContext action, ChatHostDefaultService service, CancellationToken ct)
            => await service.PutAsync(await action.ActorAsync(ct), request, ct)).WithMetadata(host);
        api.MapPost("/chat/host-defaults/apply", async (ForgeActionContext action, ChatHostDefaultService service, CancellationToken ct)
            => new { applied = await service.ApplyToExistingAsync(await action.ActorAsync(ct), ct) }).WithMetadata(host);

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