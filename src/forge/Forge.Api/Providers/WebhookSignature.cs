// -----------------------------------------------------------------------
// <copyright file="WebhookSignature.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC verification helpers for inbound webhooks.
/// GitHub uses <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> over the raw body.
/// ADO's Service Hooks use HTTP basic auth or shared-secret in the body —
/// we accept an <c>X-Forge-Hook-Secret</c> header equality match (constant-time).
/// </summary>
public static class WebhookSignature
{
    /// <summary>Verifies a GitHub-style HMAC SHA-256 signature header.</summary>
    public static bool VerifyGitHub(string? signatureHeader, string secret, byte[] rawBody)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrEmpty(secret)) return false;
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        Span<byte> computed = stackalloc byte[32];
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
        {
            hmac.TryComputeHash(rawBody, computed, out _);
        }
        var expected = Convert.ToHexString(computed).ToLowerInvariant();
        var actual = signatureHeader[prefix.Length..].ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));
    }

    /// <summary>Constant-time equality match for the ADO shared-secret header.</summary>
    public static bool VerifyAdo(string? headerSecret, string secret) =>
        !string.IsNullOrEmpty(secret) && !string.IsNullOrWhiteSpace(headerSecret) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(headerSecret),
            Encoding.UTF8.GetBytes(secret));
}
