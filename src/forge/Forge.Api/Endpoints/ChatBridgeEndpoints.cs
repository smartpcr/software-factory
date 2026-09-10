// -----------------------------------------------------------------------
// <copyright file="ChatBridgeEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// Legacy chat smoke-test and account-link endpoints.
/// Test sends require a host administrator; linking requires the paired operator.
/// </summary>
public static class ChatBridgeEndpoints
{
    public static WebApplication MapChatBridgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/chat/availability", () => Results.Ok(new
        {
            interactiveAvailable = ChatPluginInteractiveGate.IsAvailable,
            reason = ChatPluginInteractiveGate.Reason,
        })).WithName("ChatPluginAvailability");

        // POST /api/chat/test-send — prove an outbound round-trip to the chat
        // platform. Body: { chatId?, text? }. chatId defaults to the first
        // configured AllowedChatId.
        app.MapPost("/api/chat/test-send", async (
            HttpContext ctx,
            [FromBody] ChatTestSendRequest? req,
            ControlDbContext control,
            IChatTransport transport,
            IOptionsMonitor<ChatBridgeOptions> options,
            CancellationToken ct) =>
        {
            await ctx.RequestServices.GetRequiredService<ForgeActionContext>()
                .RequireAsync(ForgeCapabilities.ConnectionManage, new(), ct);

            var opt = options.CurrentValue;
            if (!opt.Enabled)
            {
                return Results.Problem(
                    title: "Chat bridge disabled",
                    detail: "Set Forge:ChatBridge:Enabled=true and configure a bot token to use the chat bridge.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var chatId = !string.IsNullOrWhiteSpace(req?.ChatId)
                ? req!.ChatId!.Trim()
                : opt.Telegram.AllowedChatIds.FirstOrDefault().ToString();
            if (string.IsNullOrWhiteSpace(chatId) || chatId == "0")
            {
                return Results.Problem(
                    title: "No target chat",
                    detail: "Provide chatId in the body or configure Forge:ChatBridge:Telegram:AllowedChatIds.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var text = string.IsNullOrWhiteSpace(req?.Text)
                ? "✅ Forge chat bridge test message."
                : req!.Text!;

            var result = await transport.SendAsync(chatId, text, buttons: null, ct);
            return result.Ok
                ? Results.Ok(new { ok = true, platform = transport.Platform, chatId, messageId = result.MessageId })
                : Results.Problem(title: "Send failed", detail: result.Error, statusCode: StatusCodes.Status502BadGateway);
        })
            .WithTags("ChatBridge")
            .WithName("ChatBridgeTestSend");

        // POST /api/chat/link-token — mint a one-time token for the current user.
        // The operator then sends `/link <token>` from the chat app to bind their
        // chat account to this authenticated Forge user.
        app.MapPost("/api/chat/link-token", async (
            HttpContext ctx, ChatLinkRequest req, ForgeActionContext action, HostIdentityStore identities,
            IOptionsMonitor<ChatBridgeOptions> options, CancellationToken ct) =>
        {
            var actor = await action.RequireAsync(ForgeCapabilities.LinkSelf, new(), ct);
            var opt = options.CurrentValue;
            if (!opt.Enabled || !opt.AllowRemoteActions)
                return Results.Problem(title: "Chat remote actions disabled", statusCode: 409);
            if (req.Provider != "telegram" || !long.TryParse(req.TenantId, out var chatId) || chatId == 0
                || !long.TryParse(req.ProviderUserId, out var userId) || userId <= 0
                || !opt.Telegram.AllowedChatIds.Contains(chatId))
                return Results.Problem(title: "Select a configured destination and exact provider user ID", statusCode: 400);
            ctx.Response.Headers.CacheControl = "no-store";
            var token = identities.IssueLinkChallenge(actor.UserId, req.Provider, req.TenantId, req.ProviderUserId);
            return Results.Ok(new { token, expiresInSeconds = 120, instructions = "Send /link followed by this token from the selected provider user in the selected destination." });
        }).WithTags("ChatBridge").WithName("ChatBridgeLinkToken");
        app.MapDelete("/api/chat/link", async (ForgeActionContext action, HostIdentityStore identities, CancellationToken ct) =>
        {
            var actor = await action.RequireAsync(ForgeCapabilities.LinkSelf, new(), ct);
            identities.RevokeLinks(actor.UserId);
            return Results.NoContent();
        }).WithTags("ChatBridge").WithName("ChatBridgeRevokeLink");

        return app;
    }
}

/// <summary>Body for <c>POST /api/chat/test-send</c>.</summary>
public sealed record ChatTestSendRequest(string? ChatId, string? Text);

public sealed record ChatLinkRequest(string Provider, string TenantId, string ProviderUserId);
