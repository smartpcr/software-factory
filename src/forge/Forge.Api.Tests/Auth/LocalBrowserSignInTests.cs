// -----------------------------------------------------------------------
// <copyright file="LocalBrowserSignInTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class LocalBrowserSignInTests
{
    private sealed class Peer(bool trusted) : ILocalBrowserIdentity
    {
        public bool IsHostUser(HttpContext context) => trusted;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Local_Socket_Proves_Host_User_For_IPv4_And_IPv6(bool ipv6)
    {
        var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var listener = new TcpListener(ip, 0);
        listener.Start();
        using var client = new TcpClient(ip.AddressFamily);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await listener.AcceptTcpClientAsync();
        var remote = (IPEndPoint)accepted.Client.RemoteEndPoint!;
        var local = (IPEndPoint)accepted.Client.LocalEndPoint!;
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote.Address;
        context.Connection.RemotePort = remote.Port;
        context.Connection.LocalIpAddress = local.Address;
        context.Connection.LocalPort = local.Port;
        var identity = new LocalBrowserIdentity();
        Assert.True(identity.IsHostUser(context));
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        Assert.False(identity.IsHostUser(context));
        Assert.False(identity.IsHostUser(new DefaultHttpContext()));
    }

    [Theory]
    [InlineData("unverified-peer")]
    [InlineData("proxy")]
    [InlineData("forwarded")]
    [InlineData("no-csrf")]
    [InlineData("no-origin")]
    [InlineData("revoked")]
    [InlineData("deleted")]
    [InlineData("multiple")]
    public async Task Untrusted_Or_Ambiguous_Local_Login_Is_Denied(string scenario)
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ILocalBrowserIdentity>(new Peer(scenario != "unverified-peer"))));
        if (scenario != "no-enrollment") await app.EnrollAsync("existing");
        if (scenario == "multiple") await app.EnrollAsync("second");
        if (scenario == "revoked") app.Services.GetRequiredService<HostIdentityStore>().RevokeEnrollment("existing");
        if (scenario == "deleted")
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Users.ExecuteDeleteAsync();
        }
        using var client = app.AnonymousClient();
        if (scenario == "proxy") client.DefaultRequestHeaders.Add("X-Forge-Proxy", "vite");
        if (scenario == "forwarded") client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        if (scenario != "no-csrf") await OperatorTestIdentity.RefreshCsrfAsync(client);
        if (scenario == "no-origin") client.DefaultRequestHeaders.Remove("Origin");
        using var response = await client.PostAsync("/api/operator/local-sign-in", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task Configured_Remote_UI_Can_Pair_Through_Proxy_But_Cannot_Borrow_Host_Identity()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Configure<OperatorAuthenticationOptions>(o => o.RemoteBrowserOrigins = ["http://xd003:5181"]);
            s.AddSingleton<ILocalBrowserIdentity>(new Peer(true));
        }));
        await app.EnrollAsync("existing");
        using var client = app.AnonymousClient();
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://xd003:5181");
        client.DefaultRequestHeaders.Add("X-Forge-Proxy", "vite");
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/operator/local-sign-in", null)).StatusCode);
        var challenge = app.Services.GetRequiredService<HostIdentityStore>().IssueBrowserChallenge("existing");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operator/pair", new { challenge })).StatusCode);
        Assert.Equal("Paired", (await client.GetFromJsonAsync<JsonElement>("/api/operator/status")).GetProperty("state").GetString());
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://untrusted:5181");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operator/status")).StatusCode);
    }

    [Fact]
    public async Task Verified_Local_User_Gets_Existing_Account_Cookie_Without_Code_Or_New_Grants()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ILocalBrowserIdentity>(new Peer(true))));
        await app.EnrollAsync("existing");
        using var client = app.AnonymousClient();
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        using var response = await client.PostAsync("/api/operator/local-sign-in", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("Forge.Operator=")));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/operator/status");
        Assert.Equal("Paired", status.GetProperty("state").GetString());
        Assert.Equal("existing", status.GetProperty("userId").GetString());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.False(await db.UserProductRoles.AnyAsync());
        app.Services.GetRequiredService<HostIdentityStore>().RevokeEnrollment("existing");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task First_Local_Login_Uses_Windows_Identity_Regardless_Of_Unrelated_Accounts(int existingUsers)
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ILocalBrowserIdentity>(new Peer(true))));
        using var client = app.AnonymousClient();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        for (var i = 0; i < existingUsers; i++)
            db.Users.Add(new UserRow { Id = "existing-" + i, DisplayName = i == 0 ? Environment.UserName : "Unrelated", Role = "human" });
        await db.SaveChangesAsync();
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        using var response = await client.PostAsync("/api/operator/local-sign-in", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identities = app.Services.GetRequiredService<HostIdentityStore>();
        Assert.True(identities.HasEnrollment);
        Assert.Equal(Math.Max(1, existingUsers), await db.Users.CountAsync());
        Assert.False(await db.UserProductRoles.AnyAsync());
        {
            var id = Assert.Single(identities.EnrolledUserIds);
            if (existingUsers > 0) Assert.Equal("existing-0", id);
            Assert.False(identities.GetEnrollment(id)!.HostAdmin);
            await OperatorTestIdentity.RefreshCsrfAsync(client);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/operator/local-sign-in", null)).StatusCode);
            Assert.Equal(Math.Max(1, existingUsers), await db.Users.CountAsync());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task First_Login_Cannot_Bootstrap_From_Unverified_Or_Proxied_Peer(bool trusted, bool proxied)
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ILocalBrowserIdentity>(new Peer(trusted))));
        using var client = app.AnonymousClient();
        if (proxied) client.DefaultRequestHeaders.Add("X-Forge-Proxy", "vite");
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/operator/local-sign-in", null)).StatusCode);
        Assert.False(app.Services.GetRequiredService<HostIdentityStore>().HasEnrollmentHistory);
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Users.AnyAsync());
    }
}
