// -----------------------------------------------------------------------
// <copyright file="ChatBridgeOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

/// <summary>
/// Phase 95 (chat bridge) — config bound from <c>Forge:ChatBridge:*</c>.
///
/// <para>The chat bridge lets Forge reach a human operator over a chat app
/// (Telegram first) to get answers to open questions and drive stalled-workstream
/// actions. Phase 1 ships the options, the transport, and an admin test-send
/// endpoint; the outbound/inbound pumps (a <c>BackgroundService</c>) land in
/// Phase 2, so nothing here is auto-started yet.</para>
///
/// <para><b>Disabled by default.</b> <see cref="Enabled"/> is <c>false</c> until
/// an operator configures a bot token — a blank token leaves the bridge idle
/// rather than crashing.</para>
/// </summary>
public sealed class ChatBridgeOptions
{
    public const string SectionName = "Forge:ChatBridge";

    /// <summary>Master switch. When false the bridge is registered but does nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chat platform. Only <c>telegram</c> is implemented today.</summary>
    public string Platform { get; set; } = "telegram";

    /// <summary>Telegram bot configuration.</summary>
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>How often the (Phase 2) outbound pump drains pending notifications.</summary>
    public TimeSpan OutboundTickInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a pending notification lives before it is marked <c>expired</c>.</summary>
    public TimeSpan NotificationTtl { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Max send attempts before a notification row is marked <c>failed</c>.</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>Telegram-specific chat-bridge options.</summary>
public sealed class TelegramOptions
{
    /// <summary>
    /// Bot token from BotFather. Prefer the env var
    /// <c>Forge__ChatBridge__Telegram__BotToken</c> or the DPAPI blob
    /// (<see cref="ChatTokenResolver"/>) over committing it to appsettings.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Bootstrap allow-list of numeric chat ids the bridge may talk to. The
    /// durable <c>chat_subscribers</c> table is the real allow-list (Phase 4);
    /// this seeds the first operator + the admin test-send default target.
    /// </summary>
    public List<long> AllowedChatIds { get; set; } = new();

    /// <summary>Long-poll <c>getUpdates</c> timeout (seconds). Telegram holds the request open this long.</summary>
    public int LongPollTimeoutSec { get; set; } = 25;

    /// <summary>Telegram Bot API base URL. Overridable so tests can point at a stub.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
}
