namespace Forge.Api.Tests.Infrastructure;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Real host enrollment and cookie pairing against isolated fixture state. Headers never identify an operator.</summary>
public static class OperatorTestIdentity
{
    public static HttpClient AnonymousClient(this WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:5180"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5181");
        return client;
    }

    public static async Task EnrollAsync(this WebApplicationFactory<Program> app, string id, bool admin = false,
        string? productId = null, string role = "product-owner")
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == id)) db.Users.Add(new UserRow { Id = id, DisplayName = id, Role = "human" });
        if (productId is not null)
        {
            var grant = await db.UserProductRoles.FindAsync(id, productId);
            if (grant is null) { grant = new UserProductRoleRow { UserId = id, ProductId = productId }; db.UserProductRoles.Add(grant); }
            grant.Role = role;
        }
        await db.SaveChangesAsync();
        app.Services.GetRequiredService<HostIdentityStore>().Enroll(id, admin);
    }

    public static async Task<HttpClient> PairedClientAsync(this WebApplicationFactory<Program> app, string id)
    {
        var client = app.AnonymousClient();
        await RefreshCsrfAsync(client);
        var challenge = app.Services.GetRequiredService<HostIdentityStore>().IssueBrowserChallenge(id);
        using var response = await client.PostAsJsonAsync("/api/operator/pair", new { challenge });
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove(OperatorAuthentication.CsrfHeader);
        client.DefaultRequestHeaders.Add(OperatorAuthentication.CsrfHeader, json.GetProperty("token").GetString());
        return client;
    }

    public static async Task RefreshCsrfAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/operator/csrf");
        client.DefaultRequestHeaders.Remove(OperatorAuthentication.CsrfHeader);
        client.DefaultRequestHeaders.Add(OperatorAuthentication.CsrfHeader, json.GetProperty("token").GetString());
    }

    public static void Link(this WebApplicationFactory<Program> app, string id, string tenant = "123", string sender = "123")
    {
        var identities = app.Services.GetRequiredService<HostIdentityStore>();
        var token = identities.IssueLinkChallenge(id, "telegram", tenant, sender);
        if (identities.RedeemLinkChallenge(token, "telegram", tenant, sender) is null) throw new InvalidOperationException("Fixture link failed");
    }
    public static async Task SeedProductAsync(this WebApplicationFactory<Program> app, string product = "p")
    {
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        if (!await control.Products.AnyAsync(p => p.Id == product))
            control.Products.Add(new ProductRow { Id = product, Slug = product, Name = product });
        if (!await control.RepoRegistry.AnyAsync(r => r.Id == "repo-" + product))
            control.RepoRegistry.Add(new RepoRegistryRow { Id = "repo-" + product, ProductId = product, ProductSlug = product,
                RemoteUrl = "https://example.invalid/" + product, CloneRoot = Path.Combine(Path.GetTempPath(), "forge-fixture-" + product) });
        if (!await db.Repos.AnyAsync(r => r.Id == "repo-" + product))
            db.Repos.Add(new RepoRow { Id = "repo-" + product, ProductId = product, RemoteUrl = "https://example.invalid/" + product,
                CloneRoot = Path.Combine(Path.GetTempPath(), "forge-fixture-" + product) });
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }

    public static async Task<ForgeActor> ProviderActorAsync(this WebApplicationFactory<Program> app, string tenant = "123", string sender = "123")
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", tenant, sender)
            ?? throw new InvalidOperationException("Fixture operator must be enrolled and linked explicitly.");
    }

}
