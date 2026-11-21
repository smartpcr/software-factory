// -----------------------------------------------------------------------
// <copyright file="IssuePairingCodeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The host user's signed-in browser may hand a pairing code to another browser; no other
/// caller may mint one, because a code is a fresh eight-hour session for its user.
/// </summary>
public sealed class IssuePairingCodeTests
{
    private const string Endpoint = "/api/operator/issue-pairing-code";
    private const string RemoteOrigin = "http://xd003:5181";

    private sealed class Peer(bool trusted) : ILocalBrowserIdentity
    {
        public bool IsHostUser(HttpContext context) => trusted;
    }

    private static WebApplicationFactory<Program> Host(ForgeWebApplicationFactory factory, bool trustedPeer) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Configure<OperatorAuthenticationOptions>(o => o.RemoteBrowserOrigins = [RemoteOrigin]);
            s.AddSingleton<ILocalBrowserIdentity>(new Peer(trustedPeer));
        }));

    private static HttpClient RemoteClient(WebApplicationFactory<Program> app)
    {
        var client = app.AnonymousClient();
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", RemoteOrigin);
        client.DefaultRequestHeaders.Add("X-Forge-Proxy", "vite");
        return client;
    }

    private static async Task<HttpStatusCode> RedeemAsync(HttpClient client, string? challenge)
    {
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/operator/pair", new { challenge });
        return response.StatusCode;
    }

    [Fact]
    public async Task Host_Browser_Issues_One_Use_Code_That_Pairs_A_Remote_Browser_As_The_Same_User()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Host(factory, trustedPeer: true);
        await app.EnrollAsync("existing");
        await app.EnrollAsync("other");
        using var host = await app.PairedClientAsync("existing");

        using var response = await host.PostAsync(Endpoint, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var challenge = body.GetProperty("challenge").GetString();
        Assert.Matches("^[0-9A-Fa-f]{64}$", challenge);
        Assert.Equal(120, body.GetProperty("expiresInSeconds").GetInt32());

        using var remote = RemoteClient(app);
        Assert.Equal(HttpStatusCode.OK, await RedeemAsync(remote, challenge));
        var status = await remote.GetFromJsonAsync<JsonElement>("/api/operator/status");
        Assert.Equal("Paired", status.GetProperty("state").GetString());
        Assert.Equal("existing", status.GetProperty("userId").GetString());

        using var second = RemoteClient(app);
        Assert.Equal(HttpStatusCode.Forbidden, await RedeemAsync(second, challenge));
    }

    [Theory]
    [InlineData("unverified-peer")]
    [InlineData("proxy")]
    [InlineData("forwarded")]
    [InlineData("remote-origin")]
    [InlineData("no-csrf")]
    public async Task Only_The_Signed_In_Host_Browser_Can_Mint_A_Code(string scenario)
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Host(factory, trustedPeer: scenario != "unverified-peer");
        await app.EnrollAsync("existing");
        using var client = await app.PairedClientAsync("existing");
        if (scenario == "proxy") client.DefaultRequestHeaders.Add("X-Forge-Proxy", "vite");
        if (scenario == "forwarded") client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.10");
        if (scenario == "remote-origin")
        {
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", RemoteOrigin);
        }
        if (scenario == "no-csrf") client.DefaultRequestHeaders.Remove(OperatorAuthentication.CsrfHeader);

        // Issuing supersedes the user's outstanding code, so a code issued beforehand
        // still redeeming afterwards proves the denied request minted nothing.
        var earlier = app.Services.GetRequiredService<HostIdentityStore>().IssueBrowserChallenge("existing");
        using var response = await client.PostAsync(Endpoint, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("challenge", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var remote = RemoteClient(app);
        Assert.Equal(HttpStatusCode.OK, await RedeemAsync(remote, earlier));
    }

    [Fact]
    public async Task Signed_Out_Host_Browser_Cannot_Mint_A_Code()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Host(factory, trustedPeer: true);
        await app.EnrollAsync("existing");
        using var client = app.AnonymousClient();
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Endpoint, null)).StatusCode);
    }

    [Fact]
    public async Task Revoked_Operator_Cannot_Mint_A_Code_From_A_Live_Host_Session()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Host(factory, trustedPeer: true);
        await app.EnrollAsync("existing");
        using var client = await app.PairedClientAsync("existing");
        app.Services.GetRequiredService<HostIdentityStore>().RevokeEnrollment("existing");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Endpoint, null)).StatusCode);
    }
}