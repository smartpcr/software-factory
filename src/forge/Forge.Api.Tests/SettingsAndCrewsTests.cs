// -----------------------------------------------------------------------
// <copyright file="SettingsAndCrewsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Phase 10.11 — contract tests for /api/me/settings, /api/crews, and
/// PATCH /api/repos/{id}. Uses a per-test factory so the SQLite DB is fresh.
/// </summary>
public class SettingsAndCrewsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetMySettings_Returns_Empty_Defaults_On_Fresh_Db()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/me/settings");
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Got {(int)resp.StatusCode}: {body}");
        }
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(
            doc.RootElement.GetProperty("defaultAdoOrgUrl").ValueKind == JsonValueKind.Null,
            "defaultAdoOrgUrl should default to null");
        Assert.True(doc.RootElement.GetProperty("notificationsEnabled").GetBoolean());
    }

    [Fact]
    public async Task PatchMySettings_Persists_Default_Ado_Org_Project()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var patch = new
        {
            defaultAdoOrgUrl = "https://msazure.visualstudio.com",
            defaultAdoProject = "One",
        };
        var resp = await client.PatchAsJsonAsync("/api/me/settings", patch, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode);

        // Re-fetch to confirm persistence.
        var get = await client.GetAsync("/api/me/settings");
        var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("https://msazure.visualstudio.com", doc.RootElement.GetProperty("defaultAdoOrgUrl").GetString());
        Assert.Equal("One", doc.RootElement.GetProperty("defaultAdoProject").GetString());
    }

    [Fact]
    public async Task PatchMySettings_Without_User_Header_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var patch = new { defaultAdoOrgUrl = "https://x" };
        var resp = await client.PatchAsJsonAsync("/api/me/settings", patch, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ListCrews_Returns_Seeded_Four()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/crews");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = new HashSet<string>();
        foreach (var c in doc.RootElement.EnumerateArray()) ids.Add(c.GetProperty("id").GetString()!);
        Assert.Contains("crew-eng",  ids);
        Assert.Contains("crew-rev",  ids);
        Assert.Contains("crew-arch", ids);
        Assert.Contains("crew-qa",   ids);
    }

    [Fact]
    public async Task CreateCrew_Without_User_Header_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/crews",
            new { id = "crew-x", name = "Test" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CreateCrew_In_SingleUserDev_Allows_Writes()
    {
        // Bootstrap behaviour: when no signed-in users exist, the role gate is
        // open (single-user dev mode). The header value passes the writer check.
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsJsonAsync(
            "/api/crews",
            new { id = "crew-platform", name = "Platform", model = "claude-opus-4-7" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task PatchRepo_Updates_AdoOrg_AdoProject()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PatchAsJsonAsync(
            "/api/repos/atlas-platform",
            new { adoOrg = "newOrg", adoProject = "NewProject" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("newOrg", doc.RootElement.GetProperty("adoOrg").GetString());
        Assert.Equal("NewProject", doc.RootElement.GetProperty("adoProject").GetString());
    }

    [Fact]
    public async Task PatchRepo_NonAbsolute_CloneRoot_Returns_400()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PatchAsJsonAsync(
            "/api/repos/atlas-platform",
            new { cloneRoot = "relative/path" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RoleGate_Demotes_Once_Real_User_Exists()
    {
        // Once a UserRow exists with a non-PO role, writes from a different
        // identity hit the "human" fallback and get rejected.
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        // Insert a non-PO user via the DB to simulate a populated install.
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.ForgeDbContext>();
        db.Users.Add(new Forge.Api.Persistence.UserRow
        {
            Id = "u1",
            DisplayName = "Existing user",
            Email = "owner@forge.local",
            Role = "human",
        });
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Add("X-Forge-User", "owner@forge.local");
        var resp = await client.PostAsJsonAsync(
            "/api/crews",
            new { id = "crew-blocked", name = "Blocked" },
            JsonOpts);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
