// -----------------------------------------------------------------------
// <copyright file="IChatTransport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

/// <summary>
/// Platform-agnostic chat transport seam. Telegram is the first implementation
/// (in Common.Chat.Telegram); Discord slots in later behind the same
/// contract. Consumers (outbound pumps, inbound dispatchers) depend only on
/// this interface so they never know which platform they're on.
/// </summary>
public interface IChatTransport
{
    /// <summary>Platform id (e.g. <c>telegram</c>) — apps stamp it onto their outbox rows.</summary>
    string Platform { get; }

    /// <summary>
    /// Send a message, optionally with an inline button keyboard. When
    /// <paramref name="threadId"/> is set (forum topics / threads), the message
    /// is posted into that topic thread. Never throws on a transport/HTTP
    /// failure — returns <see cref="ChatSendResult"/> with <c>Ok=false</c> so
    /// the caller can retry.
    /// </summary>
    Task<ChatSendResult> SendAsync(string chatId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct, string? threadId = null);

    /// <summary>
    /// Edit a previously-sent message (used to reflect resolution / disable
    /// buttons). Never throws; a no-op / "not modified" outcome is treated as
    /// success.
    /// </summary>
    Task<ChatSendResult> EditAsync(string chatId, string messageId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct);

    /// <summary>
    /// Create a forum topic / thread in a group chat and return its thread id
    /// (null on failure). On Telegram this requires the bot to be an admin
    /// with Manage-Topics.
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
    public bool OutcomeUnknown { get; init; }
    public static ChatSendResult Success(string? messageId) => new(true, messageId, null);

    public static ChatSendResult Failure(string error) => new(false, null, error);
    public static ChatSendResult Unknown(string error) => new(false, null, error) { OutcomeUnknown = true };
}

/// <summary>An inline action button: a label + the opaque callback payload echoed back on tap.</summary>
public sealed record ChatButton(string Label, string CallbackData);

/// <summary>
/// A normalized inbound update. Both a text reply and a button tap
/// (callback query) map into this shape; app dispatchers correlate via
/// <see cref="ReplyToMessageId"/> or <see cref="CallbackData"/>, and
/// <see cref="ThreadId"/> identifies the forum topic the update arrived in.
/// </summary>
public sealed record ChatInbound(
    long UpdateId,
    string ChatId,
    string FromId,
    string? Text,
    string? CallbackData,
    string? ReplyToMessageId,
    string? ThreadId = null);
