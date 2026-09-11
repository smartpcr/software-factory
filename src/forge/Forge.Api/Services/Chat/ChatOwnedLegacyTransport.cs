// -----------------------------------------------------------------------
// <copyright file="ChatOwnedLegacyTransport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;

/// <summary>Also fences direct legacy test sends and topic creation outside the background pumps.</summary>
public sealed class ChatOwnedLegacyTransport(TelegramTransport inner, ChatEngineCoordinator engine) : IChatTransport
{
    public string Platform => inner.Platform;
    public async Task<ChatSendResult> SendAsync(string chatId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct, string? threadId = null)
    {
        using var lease = await engine.EnterAsync("legacy", ct);
        return lease is null ? ChatSendResult.Failure("legacy-engine-not-owner") : await inner.SendAsync(chatId, text, buttons, ct, threadId);
    }
    public async Task<ChatSendResult> EditAsync(string chatId, string messageId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct)
    {
        using var lease = await engine.EnterAsync("legacy", ct);
        return lease is null ? ChatSendResult.Failure("legacy-engine-not-owner") : await inner.EditAsync(chatId, messageId, text, buttons, ct);
    }
    public async Task<string?> CreateTopicAsync(string chatId, string name, CancellationToken ct)
    {
        using var lease = await engine.EnterAsync("legacy", ct);
        return lease is null ? null : await inner.CreateTopicAsync(chatId, name, ct);
    }
    public async Task<IReadOnlyList<ChatInbound>> PollUpdatesAsync(long offset, CancellationToken ct)
    {
        using var lease = await engine.EnterAsync("legacy", ct);
        return lease is null ? [] : await inner.PollUpdatesAsync(offset, ct);
    }
}