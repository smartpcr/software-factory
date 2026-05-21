// -----------------------------------------------------------------------
// <copyright file="Phase80StoryRepoTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 80.F — story-owns-repo + required repo binding. A story binds to one
/// repo at create time (active repo / Product.DefaultRepoId), a spawn inherits
/// the story's repo, and creating a story in a product with no repo is rejected
/// with <c>repo.required</c>.
/// </summary>
public class Phase80StoryRepoTests
{
    private const string Atlas = ForgeSeed.DefaultProductId;   // "atlas"
    private const string AtlasRepo = ForgeSeed.DefaultRepoId;  // "atlas-platform" (seeded; DefaultRepoId via RepoRegistrySync)

    private static HttpClient Writer(ForgeWebApplicationFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        return c;
    }

    [Fact]
    public async Task StoryCreate_BindsDefaultRepo_AndSpawnInheritsIt()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot: atlas + atlas-platform + DefaultRepoId */ }
        using var client = Writer(f);

        // No repoId in the body → binds to the product's DefaultRepoId.
        var create = await client.PostAsJsonAsync($"/api/products/{Atlas}/stories", new { name = "RS Bind A" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        string storyId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var story = await db.ProductStories.AsNoTracking().FirstAsync(s => s.Name == "RS Bind A");
            Assert.Equal(AtlasRepo, story.RepoId);
            storyId = story.Id;
        }

        // Spawn with no repo in the body → inherits the story's repo.
        var spawn = await client.PostAsJsonAsync($"/api/stories/{storyId}/spawn", new { });
        Assert.True(spawn.IsSuccessStatusCode, $"spawn returned {spawn.StatusCode}");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
            Assert.Equal(AtlasRepo, row.RepoId);
        }
    }

    [Fact]
    public async Task StoryCreate_ExplicitRepoId_Binds()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var client = Writer(f);

        var create = await client.PostAsJsonAsync($"/api/products/{Atlas}/stories",
            new { name = "RS Explicit", repoId = AtlasRepo });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var story = await db.ProductStories.AsNoTracking().FirstAsync(s => s.Name == "RS Explicit");
        Assert.Equal(AtlasRepo, story.RepoId);
    }

    [Fact]
    public async Task StoryCreate_NoRepoInProduct_Returns400_RepoRequired()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.Products.Add(new ProductRow { Id = "p-norepo", Name = "No Repo", Slug = "p-norepo" });
            await control.SaveChangesAsync();
        }
        using var client = Writer(f);

        var resp = await client.PostAsJsonAsync("/api/products/p-norepo/stories", new { name = "Should Fail" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("repo.required", await resp.Content.ReadAsStringAsync());
    }
}
