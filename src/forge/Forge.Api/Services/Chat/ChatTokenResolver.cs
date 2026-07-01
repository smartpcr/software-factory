// -----------------------------------------------------------------------
// <copyright file="ChatTokenResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 95 — resolves the chat bot token from (in order):
/// <list type="number">
///   <item>the bound <see cref="TelegramOptions.BotToken"/> (config /
///         env var <c>Forge__ChatBridge__Telegram__BotToken</c>); then</item>
///   <item>a DPAPI-encrypted blob at
///         <c>%LOCALAPPDATA%\Forge\chat-token.bin</c> — the same
///         encrypt-at-rest approach the MSAL cache uses in
///         <c>AdoAuthService</c>. Windows-only; on any read/decrypt error
///         the fallback is skipped (logged), never thrown.</item>
/// </list>
/// Returns <c>null</c> when no token is available — callers treat that as
/// "bridge idle", not a crash.
/// </summary>
public static class ChatTokenResolver
{
    public const string BlobFileName = "chat-token.bin";

    /// <summary>Resolves the bot token, or null when none is configured.</summary>
    public static string? Resolve(ChatBridgeOptions options, ILogger? log = null)
    {
        if (options is null) return null;

        var configured = options.Telegram?.BotToken;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return TryReadDpapiBlob(log);
    }

    /// <summary>Path of the DPAPI token blob under <c>%LOCALAPPDATA%\Forge</c>.</summary>
    public static string BlobPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Forge",
        BlobFileName);

    private static string? TryReadDpapiBlob(ILogger? log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var path = BlobPath();
            if (!File.Exists(path))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(plain).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "chat-token.bin present but could not be read/decrypted; chat bridge stays idle.");
            return null;
        }
    }
}
