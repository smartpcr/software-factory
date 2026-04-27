// -----------------------------------------------------------------------
// <copyright file="WebhookSignatureTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Security.Cryptography;
using System.Text;
using Forge.Api.Providers;
using Xunit;

/// <summary>Phase 5.8 — HMAC + shared-secret webhook signature checks.</summary>
public class WebhookSignatureTests
{
    [Fact]
    public void VerifyGitHub_Accepts_Correct_Signature()
    {
        var secret = "shhh";
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        var hash = ComputeHmacSha256Hex(secret, body);
        Assert.True(WebhookSignature.VerifyGitHub("sha256=" + hash, secret, body));
    }

    [Fact]
    public void VerifyGitHub_Rejects_Wrong_Signature()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");
        Assert.False(WebhookSignature.VerifyGitHub("sha256=" + new string('0', 64), "shhh", body));
    }

    [Fact]
    public void VerifyGitHub_Rejects_Missing_Prefix()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var hash = ComputeHmacSha256Hex("shhh", body);
        Assert.False(WebhookSignature.VerifyGitHub(hash, "shhh", body));
    }

    [Fact]
    public void VerifyGitHub_Rejects_Empty_Inputs()
    {
        Assert.False(WebhookSignature.VerifyGitHub(null, "shhh", new byte[0]));
        Assert.False(WebhookSignature.VerifyGitHub("sha256=abc", string.Empty, new byte[0]));
    }

    [Fact]
    public void VerifyAdo_Equality_Match_Is_ConstantTime()
    {
        Assert.True(WebhookSignature.VerifyAdo("hello", "hello"));
        Assert.False(WebhookSignature.VerifyAdo("hellO", "hello"));
        Assert.False(WebhookSignature.VerifyAdo(null, "hello"));
        Assert.False(WebhookSignature.VerifyAdo("hello", string.Empty));
    }

    private static string ComputeHmacSha256Hex(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }
}
