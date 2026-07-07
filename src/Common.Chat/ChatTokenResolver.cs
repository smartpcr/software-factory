// -----------------------------------------------------------------------
// <copyright file="ChatTokenResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves the chat bot token from (in order):
/// <list type="number">
///   <item>the bound <see cref="TelegramOptions.BotToken"/> (config /
///         environment-variable override); then</item>
///   <item>a DPAPI-encrypted blob at
///         <see cref="ChatTransportOptions.TokenBlobPath"/> (when set) —
///         encrypt-at-rest for hosts that don't want the token in config.
///         Windows-only; on any read/decrypt error the fallback is skipped
///         (logged), never thrown.</item>
/// </list>
/// Returns <c>null</c> when no token is available — callers treat that as
/// "bridge idle", not a crash.
/// </summary>
public static class ChatTokenResolver
{
    /// <summary>Resolves the bot token, or null when none is configured.</summary>
    public static string? Resolve(ChatTransportOptions options, ILogger? log = null)
    {
        if (options is null) return null;

        var configured = options.Telegram?.BotToken;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return TryReadDpapiBlob(options.TokenBlobPath, log);
    }

    private static string? TryReadDpapiBlob(string? path, ILogger? log)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
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
            log?.LogWarning(ex, "chat token blob present at {Path} but could not be read/decrypted; chat bridge stays idle.", path);
            return null;
        }
    }
}
