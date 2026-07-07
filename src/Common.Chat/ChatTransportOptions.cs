// -----------------------------------------------------------------------
// <copyright file="ChatTransportOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

/// <summary>
/// Transport-level configuration for the chat bridge library. The host app
/// owns the config section name and binds this type from it (an app may bind
/// the same section to its own options type for app-side knobs — the shapes
/// just have to agree on the shared property names).
///
/// <para><b>Disabled by default.</b> <see cref="Enabled"/> is <c>false</c>
/// until an operator configures a bot token — a blank token leaves the bridge
/// idle rather than crashing.</para>
/// </summary>
public class ChatTransportOptions
{
    /// <summary>Master switch. When false the transport/pump stay registered but idle.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chat platform. Only <c>telegram</c> is implemented today.</summary>
    public string Platform { get; set; } = "telegram";

    /// <summary>Telegram bot configuration.</summary>
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>
    /// Optional path of a DPAPI-encrypted bot-token blob used as a fallback
    /// when <see cref="TelegramOptions.BotToken"/> is blank (see
    /// <see cref="ChatTokenResolver"/>). Null → no blob fallback. The host app
    /// picks the location (e.g. <c>%LOCALAPPDATA%\{App}\chat-token.bin</c>).
    /// </summary>
    public string? TokenBlobPath { get; set; }
}

/// <summary>Telegram-specific transport options.</summary>
public sealed class TelegramOptions
{
    /// <summary>
    /// Bot token from BotFather. Prefer an environment-variable override or
    /// the DPAPI blob (<see cref="ChatTokenResolver"/>) over committing it to
    /// appsettings.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Bootstrap allow-list of numeric chat ids the bridge may talk to. Apps
    /// typically keep a durable subscriber allow-list of their own; this seeds
    /// the first operator + the default test-send target.
    /// </summary>
    public List<long> AllowedChatIds { get; set; } = new();

    /// <summary>Long-poll <c>getUpdates</c> timeout (seconds). Telegram holds the request open this long.</summary>
    public int LongPollTimeoutSec { get; set; } = 25;

    /// <summary>Telegram Bot API base URL. Overridable so tests can point at a stub.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    /// <summary>
    /// Forum supergroup for per-route topics. When set (non-zero), the bridge
    /// runs in <b>group mode</b>: <see cref="ChatChannelMap"/> creates one
    /// forum topic per app route in this supergroup, and inbound senders are
    /// authorized by user id. When 0, single-chat / DM mode (send to
    /// <see cref="AllowedChatIds"/>[0]).
    /// </summary>
    public long SupergroupChatId { get; set; }

    /// <summary>User ids allowed to drive the app from the shared group (group-mode allow-list bootstrap).</summary>
    public List<long> AllowedUserIds { get; set; } = new();
}
