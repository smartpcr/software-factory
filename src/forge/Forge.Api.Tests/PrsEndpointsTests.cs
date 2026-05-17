// -----------------------------------------------------------------------
// <copyright file="PrsEndpointsTests.cs" company="Microsoft Corp.">
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
/// Phase 4.7 — contract tests for /api/prs.
/// </summary>
public class PrsEndpointsTests : IClassFixture<ForgeWebApplicationFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ForgeWebApplicationFactory factory;

    public PrsEndpointsTests(ForgeWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>
    /// Seeds the two named PRs the tests below reference (PR #234 in review,
    /// PR #237 with a build). The prototype seed used to provide these;
    /// removed when prod started carrying real data.
    /// </summary>
    public async Task InitializeAsync()
    {
        using var scope = this.factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var existing = await db.Prs.Select(p => p.Id).ToListAsync();

        if (!existing.Contains("PR #234"))
        {
            db.Prs.Add(new PrRow
            {
                Id = "PR #234", Col = "review", Epic = "EPIC-024", EpicColor = "024",
                Workstream = "ws-oidc-rotation", Base = "main", Branch = "ws/oidc-rotation",
                Title = "OIDC token rotation handler with key rollover",
                Additions = 240, Deletions = 18, Files = 6, Commits = 3, Age = "2h",
                AutoMerge = false,
                BuildJson = JsonSerializer.Serialize(new { status = "passed", checks = new[] { "ci", "lint" }, log = new[] { "tests passed", "build ok" } }),
                ReviewJson = JsonSerializer.Serialize(new { reviewers = new[] { new { id = "R3", name = "Cortex" } }, comments = 2 }),
                ImplJson = JsonSerializer.Serialize(new { description = "OIDC rotation: rotate signing keys without invalidating in-flight tokens." }),
            });
        }
        if (!existing.Contains("PR #237"))
        {
            db.Prs.Add(new PrRow
            {
                Id = "PR #237", Col = "review", Epic = "EPIC-021", EpicColor = "021",
                Workstream = "ws-retry-jitter", Base = "main", Branch = "ws/retry-jitter",
                Title = "Webhook retry with exponential backoff",
                Additions = 90, Deletions = 4, Files = 3, Commits = 2, Age = "1h",
                BuildJson = JsonSerializer.Serialize(new { status = "passed" }),
                ImplJson = JsonSerializer.Serialize(new { description = "Backoff + jitter on webhook retry." }),
            });
        }
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Removed: GetPrs_Returns_All_Seeded — asserted the 9-row prototype seed;
    // retired with the rest of the prototype data when prod carries real PRs.

    [Fact]
    public async Task GetPrDetails_Returns_Description_And_Build_Log()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/prs/" + Uri.EscapeDataString("PR #234") + "/details");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("PR #234", root.GetProperty("pr").GetProperty("id").GetString());
        Assert.True(root.GetProperty("description").GetString()!.Length > 0);
        Assert.True(root.GetProperty("buildLogTail").GetArrayLength() > 0);
    }

    [Fact]
    public async Task SetAutoMerge_Without_User_Header_Returns_401()
    {
        using var client = this.factory.CreateClient();
        var req = new { autoMerge = true };
        var resp = await client.PostAsJsonAsync("/api/prs/" + Uri.EscapeDataString("PR #234") + "/auto-merge", req, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task SetAutoMerge_With_User_Header_Persists_The_Flag()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        // PR #234 has autoMerge=false in the seed.
        var req = new { autoMerge = true };
        var resp = await client.PostAsJsonAsync("/api/prs/" + Uri.EscapeDataString("PR #234") + "/auto-merge", req, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Re-read.
        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/prs/" + Uri.EscapeDataString("PR #234"))).Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("autoMerge").GetBoolean());

        // Toggle back so other test orderings stay deterministic.
        var rb = await client.PostAsJsonAsync("/api/prs/" + Uri.EscapeDataString("PR #234") + "/auto-merge", new { autoMerge = false }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, rb.StatusCode);
    }

    [Fact]
    public async Task RerunBuild_Returns_202_And_Sets_Build_Running()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsync("/api/prs/" + Uri.EscapeDataString("PR #237") + "/build/rerun", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("running", doc.RootElement.GetProperty("build").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancelBuild_Returns_202_And_Sets_Build_Queued()
    {
        using var client = this.factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");
        var resp = await client.PostAsync("/api/prs/" + Uri.EscapeDataString("PR #237") + "/build/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("queued", doc.RootElement.GetProperty("build").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetPr_Unknown_Returns_404()
    {
        using var client = this.factory.CreateClient();
        var resp = await client.GetAsync("/api/prs/" + Uri.EscapeDataString("PR #nope"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
