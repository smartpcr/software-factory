// -----------------------------------------------------------------------
// <copyright file="SystemPromptSoulTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// End-to-end tests verifying that soul.md system prompts are loaded and
/// emitted correctly in task.prompt events. Covers the cross-machine deploy
/// scenario where the DB stores an absolute SoulPath from a different machine
/// and the fallback must re-resolve via the repo root.
/// </summary>
public class SystemPromptSoulTests
{
    [Fact]
    public void ResolveSoulPath_Returns_Null_When_Empty()
    {
        Assert.Null(ForgeSeed.ResolveSoulPath(null));
        Assert.Null(ForgeSeed.ResolveSoulPath(""));
        Assert.Null(ForgeSeed.ResolveSoulPath("   "));
    }

    [Fact]
    public void ResolveSoulPath_Returns_Direct_Path_When_File_Exists()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"soul-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var soulFile = Path.Combine(tmp, "soul.md");
        File.WriteAllText(soulFile, "You are a test agent.");
        try
        {
            var resolved = ForgeSeed.ResolveSoulPath(soulFile);
            Assert.Equal(soulFile, resolved);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ResolveSoulPath_Falls_Back_To_Repo_Root_When_Stored_Path_Missing()
    {
        // Simulate a SoulPath from another machine: absolute path that doesn't exist.
        var fakePath = @"Z:\nonexistent\repo\agents\AB\soul.md";

        // ResolveSoulPath should fall back to <repoRoot>/agents/AB/soul.md.
        // On this machine, the repo root should be found by FindRepoRoot().
        var repoRoot = ForgeSeed.FindRepoRoot();
        var expectedFallback = Path.Combine(repoRoot, "agents", "AB", "soul.md");

        var resolved = ForgeSeed.ResolveSoulPath(fakePath);

        if (File.Exists(expectedFallback))
        {
            // The agents/ folder exists — the fallback should resolve.
            Assert.Equal(expectedFallback, resolved);
        }
        else
        {
            // CI or deploy without agents/ — null is acceptable.
            Assert.Null(resolved);
        }
    }

    [Fact]
    public void ResolveSoulPath_Returns_Null_For_NonSoulMd_Filename()
    {
        // Paths that aren't soul.md should not trigger the fallback.
        var fakePath = @"Z:\nonexistent\repo\agents\AB\config.json";
        Assert.Null(ForgeSeed.ResolveSoulPath(fakePath));
    }

    [Fact]
    public async Task Seeded_Agent_Has_NonEmpty_SoulPath()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == "A1");
        Assert.NotNull(agent);
        Assert.False(string.IsNullOrEmpty(agent!.SoulPath), "Agent A1 should have a SoulPath set by seed.");
    }

    [Fact]
    public async Task Seeded_Agent_SoulPath_Resolves_To_Existing_File()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Check several seeded agents — their SoulPath (or fallback) should point
        // to an existing soul.md file when running in the repo tree.
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.SoulPath != null && a.SoulPath != "")
            .Take(5)
            .ToListAsync();

        Assert.NotEmpty(agents);

        var repoRoot = ForgeSeed.FindRepoRoot();
        var agentsDir = Path.Combine(repoRoot, "agents");
        if (!Directory.Exists(agentsDir))
        {
            // Skip gracefully on CI without agents/ folder.
            return;
        }

        foreach (var a in agents)
        {
            var resolved = ForgeSeed.ResolveSoulPath(a.SoulPath);
            Assert.True(
                !string.IsNullOrEmpty(resolved) && File.Exists(resolved),
                $"Agent {a.Id}: SoulPath '{a.SoulPath}' did not resolve to an existing file. " +
                $"Resolved: '{resolved}'. Repo root: '{repoRoot}'.");

            var content = await File.ReadAllTextAsync(resolved!);
            Assert.False(
                string.IsNullOrWhiteSpace(content),
                $"Agent {a.Id}: soul.md at '{resolved}' is empty.");
        }
    }

    [Fact]
    public async Task Agent_Api_Returns_Soul_Contents()
    {
        using var factory = new ForgeWebApplicationFactory();

        // Write a soul.md to a temp location and wire it to an agent.
        var tmpRoot = Path.Combine(Path.GetTempPath(), $"forge-soul-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpRoot);
        var soulContent = "You are a careful software engineer. Focus on correctness.";
        var soulFile = Path.Combine(tmpRoot, "test-soul.md");
        File.WriteAllText(soulFile, soulContent);

        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var row = await db.Agents.FirstAsync(a => a.Id == "E4");
                row.SoulPath = soulFile;
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var resp = await client.GetAsync("/api/agents/E4/soul");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("exists").GetBoolean());
            Assert.Equal(soulContent, doc.RootElement.GetProperty("contents").GetString());
        }
        finally
        {
            Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public void FindRepoRoot_Locates_Agents_Directory()
    {
        var root = ForgeSeed.FindRepoRoot();
        var agentsDir = Path.Combine(root, "agents");

        // When running from within the repo tree, agents/ should exist.
        // On CI without agents/, this just verifies FindRepoRoot doesn't throw.
        if (Directory.Exists(agentsDir))
        {
            Assert.True(
                File.Exists(Path.Combine(agentsDir, "README.md")),
                "agents/README.md should exist at the repo root.");
        }
    }
}
