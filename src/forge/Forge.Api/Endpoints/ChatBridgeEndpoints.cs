// -----------------------------------------------------------------------
// <copyright file="ChatBridgeEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge) — admin/ops endpoints. Phase 1 ships only the
/// smoke-test send; the subscriber/link/outbox surfaces land with the inbound
/// work in later phases. All endpoints are <see cref="RequireRole.ProductOwner"/>-gated.
/// </summary>
public static class ChatBridgeEndpoints
{
    public static WebApplication MapChatBridgeEndpoints(this WebApplication app)
    {
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
            if (RequireRole.Check(ctx, control, RequireRole.ProductOwner) is { } gate) return gate;

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
        // chat account to this Forge user (Phase 4). Role-gated product-owner.
        app.MapPost("/api/chat/link-token", async (
            HttpContext ctx,
            ControlDbContext control,
            IOptionsMonitor<ChatBridgeOptions> options,
            CancellationToken ct) =>
        {
            if (RequireRole.Check(ctx, control, RequireRole.ProductOwner) is { } gate) return gate;

            var opt = options.CurrentValue;
            var user = ctx.Request.Headers.TryGetValue("X-Forge-User", out var u) ? u.ToString() : "operator";
            var token = Guid.NewGuid().ToString("N")[..8];

            control.ChatSubscribers.Add(new ChatSubscriberRow
            {
                Id = Guid.NewGuid().ToString("N"),
                Platform = opt.Platform,
                // Placeholder keeps the (Platform, ChatId) unique index happy until
                // /link swaps in the real chat id.
                ChatId = $"pending:{token}",
                ForgeUserId = user,
                LinkToken = token,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await control.SaveChangesAsync(ct);

            return Results.Ok(new { token, instructions = $"In the chat app, send: /link {token}" });
        })
            .WithTags("ChatBridge")
            .WithName("ChatBridgeLinkToken");

        return app;
    }
}

/// <summary>Body for <c>POST /api/chat/test-send</c>.</summary>
public sealed record ChatTestSendRequest(string? ChatId, string? Text);
