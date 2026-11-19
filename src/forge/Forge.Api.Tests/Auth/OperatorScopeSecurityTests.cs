namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class OperatorScopeSecurityTests
{
    [Fact]
    public async Task Role_And_Legacy_Link_Mutations_Use_Stable_Identity_And_Host_Admin_Authority()
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("admin", admin: true);
        await app.EnrollAsync("reader", productId: "p", role: "eng");
        using var admin = await app.PairedClientAsync("admin");
        using var reader = await app.PairedClientAsync("reader");
        reader.DefaultRequestHeaders.Add("X-Forge-User", "admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/operators/reader/roles/q", new { role = "product-owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/me/role", new { productId = "q", role = "product-owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/operators/reader/roles/q", new { role = "product-owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.DeleteAsync("/api/chat/link")).StatusCode);
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.Equal("product-owner", (await control.UserProductRoles.SingleAsync(r => r.UserId == "reader" && r.ProductId == "q")).Role);
        Assert.False(await control.UserProductRoles.AnyAsync(r => r.UserId == "admin"));
    }

    [Fact]
    public async Task Created_Product_Is_Granted_To_Its_Creator_And_Listed_Only_For_Them()
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("admin", admin: true, productId: "p");
        await app.EnrollAsync("reader", productId: "p", role: "eng");
        using var admin = await app.PairedClientAsync("admin");
        using var reader = await app.PairedClientAsync("reader");

        var created = await admin.PostAsJsonAsync("/api/products", new { id = "harness", name = "harness" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var adminList = await admin.GetFromJsonAsync<JsonElement>("/api/products");
        Assert.Contains(adminList.EnumerateArray(), p => p.GetProperty("id").GetString() == "harness");
        var readerList = await reader.GetFromJsonAsync<JsonElement>("/api/products");
        Assert.DoesNotContain(readerList.EnumerateArray(), p => p.GetProperty("id").GetString() == "harness");

        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var grant = await control.UserProductRoles.AsNoTracking().SingleAsync(r => r.ProductId == "harness");
        Assert.Equal("admin", grant.UserId);
        Assert.Equal(RequireRole.ProductOwner, grant.Role);
        Assert.Equal("admin", grant.GrantedBy);
    }

    [Fact]
    public async Task Aggregate_Reads_Include_Only_Granted_Products_And_Detail_Aliases_Cannot_Cross_Them()
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("po", productId: "p");
        using var client = await app.PairedClientAsync("po");
        foreach (var (path, expected) in new[] { ("/api/products", "p"), ("/api/stories/spawns?productId=all", "spawn-p"), ("/api/workstreams?productId=all", "ws-p") })
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expected, Assert.Single(rows.EnumerateArray()).GetProperty("id").GetString());
        }
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
        foreach (var path in new[] { "/api/stories/story-q/spawn", "/api/workstreams/ws-q/memory", "/api/products/q", "/api/repos/repo-q" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        foreach (var path in new[] { "/api/workstreams/ws-q/cancel", "/api/workstreams/ws-q/retry", "/api/stories/story-q/spawn/pause", "/api/work-items/ws-q/resume" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workstreams/ws-p/cancel?productId=all", new { })).StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.All(await scope.ServiceProvider.GetRequiredService<ProductDbContext>().Workstreams.ToListAsync(), w => Assert.Equal("stalled", w.Status));
    }

    [Fact]
    public async Task Cached_Success_Cannot_Bypass_A_Current_Role_Check()
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("po", productId: "p");
        using var client = await app.PairedClientAsync("po");
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "same-click");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/workstreams/ws-p/cancel", new { })).StatusCode);
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ControlDbContext>().UserProductRoles.ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workstreams/ws-p/cancel", new { })).StatusCode);
    }

    [Fact]
    public async Task Legacy_Realtime_And_Plugin_Availability_Fail_Closed()
    {
        await using var app = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("admin", admin: true, productId: "p");
        var sessions = app.Services.GetRequiredService<OperatorSessions>();
        var identities = app.Services.GetRequiredService<HostIdentityStore>();
        var principal = sessions.Issue(identities.GetEnrollment("admin")!);
        var access = app.Services.GetRequiredService<OperatorHubAccess>();
        Assert.False(await access.IsAllowedAsync(principal));
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        foreach (var productId in await control.Products.Where(p => p.Id != "p").Select(p => p.Id).ToListAsync())
            control.UserProductRoles.Add(new UserProductRoleRow { UserId = "admin", ProductId = productId, Role = "eng" });
        await control.SaveChangesAsync();
        Assert.True(await access.IsAllowedAsync(principal));
        sessions.RevokeUser("admin");
        Assert.False(await access.IsAllowedAsync(principal));
        Assert.False(ChatPluginInteractiveGate.AvailableFor(new ChatPluginOptions()));
        Assert.Throws<InvalidOperationException>(() => ChatPluginInteractiveGate.RequireAvailable(new ChatPluginOptions()));
    }
}
