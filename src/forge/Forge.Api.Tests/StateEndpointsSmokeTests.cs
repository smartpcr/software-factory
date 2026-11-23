// -----------------------------------------------------------------------
// <copyright file="StateEndpointsSmokeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Tests.Auth;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Smoke tests for the aggregated GET /api/state endpoint.
/// Asserts the seeded counts match the prototype's sample data exactly.
/// </summary>
public class StateEndpointsSmokeTests : IClassFixture<ForgeWebApplicationFactory>
{
    private readonly ForgeWebApplicationFactory factory;

    public StateEndpointsSmokeTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetState_Returns200()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/state");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"Status={resp.StatusCode} Body={body}");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    // Removed: GetState_HasExpectedCounts and GetState_FirstEpic_IsMultiTenantAuth
    // — both asserted the prototype `EnsureSeededAsync` payload (5 epics named
    // EPIC-024 etc., 23 cards, 9 PRs, …). That seed was retired when prod
    // started carrying real data; the assertions encode no useful invariant
    // outside that seed.

    [Fact]
    public async Task GetState_Returns_All_Top_Level_Properties()
    {
        // Replacement for GetState_HasExpectedCounts: verify the endpoint
        // shape (every documented top-level property is present and is an
        // array) without assuming any specific seed counts.
        using var client = this.factory.CreateClient();
        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/state")).Content.ReadAsStringAsync());
        var root = doc.RootElement;
        foreach (var key in new[] { "epics", "cards", "columns", "workstreams", "wsEvents", "prs", "wsStats", "overviewActivity", "overviewAgents" })
        {
            Assert.True(root.TryGetProperty(key, out var arr), $"missing top-level property '{key}'");
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        }
    }

    [Fact]
    public async Task GetState_Does_Not_Project_A_Storys_Own_Work_Item_As_A_Card()
    {
        // A spawned story has a work item whose Id is the story id and which has
        // no parent. It used to project as an orphan card under "No story".
        await using var f = new ForgeWebApplicationFactory();
        await CapabilityParityTests.SeedTargetsAsync(f);
        await f.EnrollAsync("po", productId: "p");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.ProductStories.Add(new ProductStoryRow { Id = "p:MIRROR", ProductId = "p", RepoId = "repo-p", Name = "mirror story", Status = "done", CreatedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow { Id = "p:MIRROR", RepoId = "repo-p", Type = "feature", Track = "dev", State = "closed", Title = "mirror story" });
            db.WorkItems.Add(new WorkItemRow { Id = "ws-mirror-1", RepoId = "repo-p", Type = "task", Track = "dev", State = "closed", Title = "child", FeatureRef = "p:MIRROR", ParentId = "p:MIRROR" });
            await db.SaveChangesAsync();
        }

        using var client = await f.PairedClientAsync("po");
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");
        using var response = await client.GetAsync("/api/state");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var doc = JsonDocument.Parse(body);
        var cardIds = doc.RootElement.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Contains("ws-mirror-1", cardIds);
        Assert.DoesNotContain("p:MIRROR", cardIds);
    }

    [Fact]
    public async Task GetState_Health_StaysOk()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }
}
