// -----------------------------------------------------------------------
// <copyright file="ChatBridgeOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;

/// <summary>
/// Phase 95 (chat bridge) — Forge-side config bound from <c>Forge:ChatBridge:*</c>.
///
/// <para>The transport half of the bridge lives in the reusable
/// <c>Common.Chat</c> library; the same config section is <b>also</b> bound to
/// <see cref="ChatTransportOptions"/> (the shapes agree on
/// <c>Enabled</c>/<c>Platform</c>/<c>Telegram</c>), and this type adds the
/// Forge-only outbox knobs (tick interval, TTL, retry cap).</para>
///
/// <para><b>Disabled by default.</b> <see cref="Enabled"/> is <c>false</c> until
/// an operator configures a bot token — a blank token leaves the bridge idle
/// rather than crashing.</para>
/// </summary>
public sealed class ChatBridgeOptions
{
    public const string SectionName = "Forge:ChatBridge";

    /// <summary>File name of the DPAPI token blob under <c>%LOCALAPPDATA%\Forge</c>.</summary>
    public const string TokenBlobFileName = "chat-token.bin";

    /// <summary>Master switch. When false the bridge is registered but does nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chat platform. Only <c>telegram</c> is implemented today.</summary>
    public string Platform { get; set; } = "telegram";

    /// <summary>Telegram bot configuration (shared shape with <see cref="ChatTransportOptions.Telegram"/>).</summary>
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>How often the outbound pump drains pending notifications.</summary>
    public TimeSpan OutboundTickInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a pending notification lives before it is marked <c>expired</c>.</summary>
    public TimeSpan NotificationTtl { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Max send attempts before a notification row is marked <c>failed</c>.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Default DPAPI blob path (<c>%LOCALAPPDATA%\Forge\chat-token.bin</c>) —
    /// stamped onto <see cref="ChatTransportOptions.TokenBlobPath"/> at wiring
    /// time, mirroring the MSAL cache location in <c>AdoAuthService</c>.
    /// </summary>
    public static string DefaultTokenBlobPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Forge",
        TokenBlobFileName);
}
