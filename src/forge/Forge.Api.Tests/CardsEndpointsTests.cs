// -----------------------------------------------------------------------
// <copyright file="CardsEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 1.7 — contract tests for /api/cards endpoints.
/// </summary>
public class CardsEndpointsTests : IClassFixture<ForgeWebApplicationFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ForgeWebApplicationFactory factory;

    public CardsEndpointsTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>
    /// Seeds the column ladder + the four named cards the tests below
    /// reference (#234 in `build`, #831/#832 in `backlog`, #825 in `design`).
    /// Idempotent so xUnit's per-class-fixture sharing doesn't double-insert.
    /// Replaces the now-retired prototype seed in <c>ForgeSeed</c>.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var scope = this.factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var existingColumns = await db.Columns.Select(c => c.Id).ToListAsync();
        var columns = new[]
        {
            new ColumnRow { Id = "backlog", Name = "Backlog", Sort = 0 },
            new ColumnRow { Id = "design",  Name = "Design",  Sort = 1 },
            new ColumnRow { Id = "build",   Name = "Build",   Sort = 2 },
            new ColumnRow { Id = "review",  Name = "Review",  Sort = 3 },
            new ColumnRow { Id = "done",    Name = "Done",    Sort = 4 },
        };
        foreach (var c in columns) if (!existingColumns.Contains(c.Id)) db.Columns.Add(c);

        var existingCards = await db.Cards.Select(c => c.Id).ToListAsync();
        var cards = new[]
        {
            new CardRow { Id = "#234", Epic = "EPIC-024", Col = "build",   Type = "story", Title = "OIDC token rotation", Pts = 5, Priority = "high" },
            new CardRow { Id = "#831", Epic = "EPIC-024", Col = "backlog", Type = "story", Title = "Tenant audit log retention policy", Pts = 5 },
            new CardRow { Id = "#832", Epic = "EPIC-024", Col = "backlog", Type = "task",  Title = "Add tenant ID to OTel span attributes", Pts = 2 },
            new CardRow { Id = "#825", Epic = "EPIC-026", Col = "design",  Type = "bug",   Title = "WMI quota violation telemetry", Pts = 3 },
        };
        foreach (var c in cards) if (!existingCards.Contains(c.Id)) db.Cards.Add(c);

        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Removed: GetCards_Returns_All_Seeded_Cards — asserted the 23-row prototype
    // seed; that seed (`ForgeSeed.EnsureSeededAsync`) was retired when prod
    // started carrying real operator data. Tests that need a card now seed it
    // themselves (see GetCard_ById_Returns_Card via IAsyncLifetime below).

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
