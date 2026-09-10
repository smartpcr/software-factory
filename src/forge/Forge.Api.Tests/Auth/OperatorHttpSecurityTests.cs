namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class OperatorHttpSecurityTests
{
    [Fact]
    public async Task Empty_Host_And_Forged_Headers_Cannot_Enroll_Link_Change_Roles_Or_Act()
    {
        await using var app = new ForgeWebApplicationFactory();
        using var client = app.AnonymousClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "admin");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/operator/status");
        Assert.Equal("Needs operator enrollment", status.GetProperty("state").GetString());
        foreach (var path in new[] { "/api/operator/enroll", "/api/operators/admin/revoke", "/api/chat/link-token", "/api/chat/test-send", "/api/workstreams/ws/cancel" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/me/role", new { role = "product-owner" })).StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Users.AnyAsync());
        Assert.False(app.Services.GetRequiredService<HostIdentityStore>().HasEnrollment);
    }

    [Fact]
    public async Task Real_Cookie_Pairing_Is_One_Use_HttpOnly_Strict_And_Logout_Revokes()
    {
        await using var app = new ForgeWebApplicationFactory();
        await app.EnrollAsync("operator");
        using var client = app.AnonymousClient();
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        var challenge = app.Services.GetRequiredService<HostIdentityStore>().IssueBrowserChallenge("operator");
        using var paired = await client.PostAsJsonAsync("/api/operator/pair", new { challenge });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var cookie = Assert.Single(paired.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("Forge.Operator=")));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(paired.Headers.CacheControl!.NoStore);
        await OperatorTestIdentity.RefreshCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/operator/pair", new { challenge })).StatusCode);
        Assert.Equal("Paired", (await client.GetFromJsonAsync<JsonElement>("/api/operator/status")).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/operator/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task Csrf_Origin_Host_All_Scope_And_Revocation_Deny_Before_Changes()
    {
        await using var app = new ForgeWebApplicationFactory();
        await app.EnrollAsync("admin", true);
        using var client = await app.PairedClientAsync("admin");
        client.DefaultRequestHeaders.Add("X-Forge-User", "forged");
        var csrf = client.DefaultRequestHeaders.GetValues(OperatorAuthentication.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(OperatorAuthentication.CsrfHeader);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/operators/admin/revoke", null)).StatusCode);
        client.DefaultRequestHeaders.Add(OperatorAuthentication.CsrfHeader, csrf);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/operators/admin/revoke", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operator/status")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5181");
        client.DefaultRequestHeaders.Host = "attacker.example:5180";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operator/status")).StatusCode);
        client.DefaultRequestHeaders.Host = null;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/operators/admin/revoke?productId=all", null)).StatusCode);
        Assert.True(app.Services.GetRequiredService<HostIdentityStore>().GetEnrollment("admin")!.Enabled);
        app.Services.GetRequiredService<OperatorSessions>().RevokeUser("admin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/operators/admin/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task Deleted_User_Cannot_Redeem_Challenge_Or_Use_Cached_Session()
    {
        await using var app = new ForgeWebApplicationFactory();
        await app.EnrollAsync("operator");
        using var paired = await app.PairedClientAsync("operator");
        var challenge = app.Services.GetRequiredService<HostIdentityStore>().IssueBrowserChallenge("operator");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Users.ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await paired.GetAsync("/api/products")).StatusCode);
        using var anonymous = app.AnonymousClient();
        await OperatorTestIdentity.RefreshCsrfAsync(anonymous);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/api/operator/pair", new { challenge })).StatusCode);
        Assert.Equal("Needs operator enrollment", (await anonymous.GetFromJsonAsync<JsonElement>("/api/operator/status")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Every_Registered_Action_Alias_Rejects_An_Enrolled_Operator_Without_Product_Roles()
    {
        await using var app = new ForgeWebApplicationFactory();
        await app.EnrollAsync("reader");
        using var client = await app.PairedClientAsync("reader");
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "missing-product");
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        foreach (var entry in ForgeEndpointCapabilities.Supported.Where(e => e.Value.TargetKind is not "self"))
        {
            var route = Assert.Single(routes.Where(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == entry.Key));
            var path = route.RoutePattern.RawText!.Replace("{id}", "ws").Replace("{storyId}", "story").Replace("{key}", "architecture");
            var method = route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];
            using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{entry.Key}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatSubscribers.AnyAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<ProductDbContext>().TaskQueue.AnyAsync());
    }
}
