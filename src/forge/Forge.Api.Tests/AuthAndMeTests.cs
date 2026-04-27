// -----------------------------------------------------------------------
// <copyright file="AuthAndMeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Phase 9.12 — smoke tests for the auth + identity endpoints.
/// MSAL itself is not exercised (would require a real account / network); we
/// verify the unauthenticated paths instead — status returns "not connected"
/// shape, /api/me returns the placeholder identity, and the first-run
/// promotion logic doesn't fire when no provider is signed in.
/// </summary>
public class AuthAndMeTests
{
    [Fact(Skip = "Environment-dependent after Phase 9.11 — uses real MSAL cache; passes in CI but fails when an operator has cached an ADO token locally. Needs a stub IAuthService injection in the factory.")]
    public async Task AuthStatus_Unconfigured_Returns_Disconnected()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/auth/ado/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task AuthStatus_UnknownKind_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/auth/gitlab/status");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AuthSignIn_Without_User_Header_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/auth/ado/sign-in", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(Skip = "Environment-dependent after Phase 9.11 — same root cause as AuthStatus_Unconfigured: real MSAL/gh cache leaks into the test. Needs stub IAuthService injection.")]
    public async Task GetMe_Unauth_Returns_Placeholder()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // No provider connected → empty id, "Sign in" display name.
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Sign in", doc.RootElement.GetProperty("displayName").GetString());
        // Both providers report disconnected.
        var providers = doc.RootElement.GetProperty("providers");
        Assert.False(providers.GetProperty("ado").GetProperty("connected").GetBoolean());
        Assert.False(providers.GetProperty("github").GetProperty("connected").GetBoolean());
    }

    [Fact(Skip = "Stale after Phase 9.11 — GitHub auth no longer requires a client id (delegates to gh CLI). The 'OAuth client id missing' error path was deleted; replacement assertion needs a stub gh executable.")]
    public async Task GitHubSignIn_Without_ClientId_Returns_401_With_Helpful_Error()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsync("/api/auth/github/sign-in", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("OAuth client id", body);
    }

    [Fact]
    public async Task SignOut_Without_User_Header_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/auth/ado/sign-out", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task SignOut_Idempotent_When_Not_Signed_In()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        // No account cached — sign-out is a no-op and returns 204.
        var resp = await client.PostAsync("/api/auth/github/sign-out", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }
}
