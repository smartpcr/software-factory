// -----------------------------------------------------------------------
// <copyright file="IChatTransport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

/// <summary>
/// Phase 95 — platform-agnostic chat transport seam. Telegram is the first
/// implementation (<see cref="TelegramTransport"/>); Discord slots in later
/// behind the same contract. The (Phase 2) bridge service depends only on this
/// interface so the outbound/inbound pumps never know which platform they're on.
/// </summary>
public interface IChatTransport
{
    /// <summary>Platform id (e.g. <c>telegram</c>) — stamped onto notification rows.</summary>
    string Platform { get; }

    /// <summary>
    /// Send a message, optionally with an inline button keyboard. When
    /// <paramref name="threadId"/> is set (Part B forum topics), the message is
    /// posted into that topic thread. Never throws on a transport/HTTP failure —
    /// returns <see cref="ChatSendResult"/> with <c>Ok=false</c> so the caller can retry.
    /// </summary>
    Task<ChatSendResult> SendAsync(string chatId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct, string? threadId = null);

    /// <summary>
    /// Edit a previously-sent message (used to reflect resolution / disable
    /// buttons). Never throws; a no-op / "not modified" outcome is treated as
    /// success.
    /// </summary>
    Task<ChatSendResult> EditAsync(string chatId, string messageId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct);

    /// <summary>
    /// Part B — create a forum topic in a supergroup and return its
    /// <c>message_thread_id</c> (null on failure). Requires the bot to be an
    /// admin with Manage-Topics.
    /// </summary>
    Task<string?> CreateTopicAsync(string chatId, string name, CancellationToken ct);

    /// <summary>
    /// Long-poll for inbound updates newer than <paramref name="offset"/>.
    /// Returns an empty list on any transport failure (never throws).
    /// </summary>
    Task<IReadOnlyList<ChatInbound>> PollUpdatesAsync(long offset, CancellationToken ct);
}

/// <summary>Outcome of a send/edit. <see cref="MessageId"/> is set when <see cref="Ok"/> is true.</summary>
public sealed record ChatSendResult(bool Ok, string? MessageId, string? Error)
{
    public static ChatSendResult Success(string? messageId) => new(true, messageId, null);

    public static ChatSendResult Failure(string error) => new(false, null, error);
}

/// <summary>An inline action button: a label + the opaque callback payload echoed back on tap.</summary>
public sealed record ChatButton(string Label, string CallbackData);

/// <summary>
/// A normalized inbound update. Both a text reply and a button tap
/// (callback query) map into this shape; the (Phase 4) dispatcher correlates
/// via <see cref="ReplyToMessageId"/> or <see cref="CallbackData"/>.
/// </summary>
public sealed record ChatInbound(
    long UpdateId,
    string ChatId,
    string FromId,
    string? Text,
    string? CallbackData,
    string? ReplyToMessageId,
    string? ThreadId = null);
