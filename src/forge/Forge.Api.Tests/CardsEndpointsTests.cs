// -----------------------------------------------------------------------
// <copyright file="CardsEndpointsTests.cs" company="Microsoft Corp.">
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
/// Phase 1.7 — contract tests for /api/cards endpoints.
/// </summary>
public class CardsEndpointsTests : IClassFixture<ForgeWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ForgeWebApplicationFactory factory;

    public CardsEndpointsTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetCards_Returns_All_Seeded_Cards()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/cards");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(23, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task GetCard_ById_Returns_Card()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/cards/" + Uri.EscapeDataString("#234"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("#234", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("build", doc.RootElement.GetProperty("col").GetString());
    }

    [Fact]
    public async Task GetCard_Unknown_Returns_404_ProblemDetails()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/cards/" + Uri.EscapeDataString("#nope"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PatchCard_Updates_Title_And_Pts()
    {
        using var client = this.factory.CreateClient();
        var patch = new { title = "OIDC token rotation (renamed)", pts = 8 };
        var resp = await client.PatchAsJsonAsync("/api/cards/" + Uri.EscapeDataString("#234"), patch, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("OIDC token rotation (renamed)", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(8, doc.RootElement.GetProperty("pts").GetInt32());
    }

    [Fact]
    public async Task MoveCard_Allowed_Move_Returns_200_And_Updates_Column()
    {
        using var client = this.factory.CreateClient();
        // backlog → design is a single-step forward move with no confirm.
        var resp = await client.PostAsJsonAsync(
            "/api/cards/" + Uri.EscapeDataString("#831") + "/move",
            new { toCol = "design", force = false },
            JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("design", doc.RootElement.GetProperty("card").GetProperty("col").GetString());
        Assert.True(doc.RootElement.GetProperty("policy").GetProperty("allow").GetBoolean());
    }

    [Fact]
    public async Task MoveCard_RequiresConfirm_Returns_422_With_PolicyPayload()
    {
        using var client = this.factory.CreateClient();
        // backlog → build skips one phase → server should ask the client to confirm.
        var resp = await client.PostAsJsonAsync(
            "/api/cards/" + Uri.EscapeDataString("#832") + "/move",
            new { toCol = "build", force = false },
            JsonOpts);

        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Skip phase?", doc.RootElement.GetProperty("policy").GetProperty("title").GetString());
        Assert.True(doc.RootElement.GetProperty("policy").GetProperty("confirm").GetBoolean());
    }

    [Fact]
    public async Task MoveCard_Force_Applies_The_Confirm_Move()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/cards/" + Uri.EscapeDataString("#825") + "/move",
            new { toCol = "build", force = true },
            JsonOpts);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("build", doc.RootElement.GetProperty("card").GetProperty("col").GetString());
    }
}
