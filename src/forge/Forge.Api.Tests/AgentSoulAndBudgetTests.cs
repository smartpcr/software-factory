// -----------------------------------------------------------------------
// <copyright file="AgentSoulAndBudgetTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 11 Batch C — soul.md authoring, reassign + history, crew budgets.
/// </summary>
public class AgentSoulAndBudgetTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ListAgents_Returns_Seeded_Agents()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/agents");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains("E4", ids);
        Assert.Contains("R3", ids);
        Assert.Contains("XD", ids);
    }

    [Fact]
    public void Soul_Frontmatter_Parses_Tools_Block_Form()
    {
        var raw = "---\ntools:\n  - read_file\n  - write_file\n---\nYou are a careful engineer.\n";
        var (body, caps) = SoulService.ParseFrontMatter(raw);
        Assert.Equal(new[] { "read_file", "write_file" }, caps.ToArray());
        Assert.Equal("You are a careful engineer.", body.TrimEnd());
    }

    [Fact]
    public void Soul_Frontmatter_Parses_Tools_Flow_Form()
    {
        var raw = "---\ntools: [read_file, run_command]\n---\nbody";
        var (body, caps) = SoulService.ParseFrontMatter(raw);
        Assert.Equal(new[] { "read_file", "run_command" }, caps.ToArray());
        Assert.Equal("body", body);
    }

    [Fact]
    public async Task PutSoul_Roundtrips_Through_Filesystem()
    {
        using var factory = new ForgeWebApplicationFactory();

        // Seed a PO so the role gate accepts the PUT.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.Users.Add(new UserRow
            {
                Id = "user-po",
                DisplayName = "PO",
                Email = "po@forge.local",
                Role = "product-owner",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Override SoulOptions to a temp folder so we don't pollute LOCALAPPDATA.
        // Easiest: write via the seeded SoulPath on the AgentRow.
        var tmpRoot = Path.Combine(Path.GetTempPath(), $"forge-soul-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpRoot);
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var row = await db.Agents.FirstAsync(a => a.Id == "E4");
                row.SoulPath = Path.Combine(tmpRoot, "E4.md");
                await db.SaveChangesAsync();
            }

            var contents = "---\ntools: [read_file]\n---\nFocus on small diffs.";
            var put = await client.PutAsJsonAsync("/api/agents/E4/soul", new { contents }, JsonOpts);
            if (put.StatusCode != HttpStatusCode.OK)
            {
                var body = await put.Content.ReadAsStringAsync();
                throw new Xunit.Sdk.XunitException($"PUT failed {(int)put.StatusCode}: {body}");
            }

            // GET sees the same content with parsed capabilities.
            var get = await client.GetAsync("/api/agents/E4/soul");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("exists").GetBoolean());
            Assert.Equal(contents, doc.RootElement.GetProperty("contents").GetString());
            var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "read_file" }, caps);

            // Underlying file actually exists at the override path.
            Assert.True(File.Exists(Path.Combine(tmpRoot, "E4.md")));
        }
        finally
        {
            Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PutSoul_Without_PO_Returns_403()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.Users.Add(new UserRow { Id = "u-po", DisplayName = "PO", Email = "po@x", Role = "product-owner", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new UserRow { Id = "u-h", DisplayName = "H", Email = "h@x", Role = "human", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "h@x");
        var resp = await client.PutAsJsonAsync("/api/agents/E4/soul", new { contents = "x" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Reassign_Moves_Agent_And_Writes_History()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        var resp = await client.PostAsJsonAsync("/api/agents/E4/reassign", new { crewId = "crew-rev" }, JsonOpts);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Reassign failed {(int)resp.StatusCode}: {body}");
        }
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("crew-rev", doc.RootElement.GetProperty("crewId").GetString());

        // History row appears.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hist = await db.AgentHistory.AsNoTracking()
            .Where(h => h.AgentId == "E4")
            .OrderByDescending(h => h.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(hist);
        Assert.Equal("crew-eng", hist!.FromCrewId);
        Assert.Equal("crew-rev", hist.ToCrewId);
        Assert.Equal("claude-opus-4-7", hist.FromModel);
        Assert.Equal("claude-sonnet-4-6", hist.ToModel);
        Assert.Equal("po@forge.local", hist.Actor);
    }

    [Fact]
    public async Task Reassign_Rejects_Unknown_Crew()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await client.PostAsJsonAsync("/api/agents/E4/reassign", new { crewId = "ghost-crew" }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Crew_Budget_Endpoint_Returns_Spent_And_Remaining()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Set a daily budget and inject a fake usage row to exercise the math.
        await client.PatchAsJsonAsync("/api/crews/crew-eng", new { dailyBudgetUsd = 5m, maxTaskBudgetUsd = 1m }, JsonOpts);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.AgentUsage.Add(new AgentUsageRow
            {
                AgentId = "E4", CrewId = "crew-eng", TaskId = "234", Model = "claude-opus-4-7",
                InputTokens = 1000, OutputTokens = 500, CostUsd = 1.25m,
                StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, Status = "finished",
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/crews/crew-eng/budget");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(5m, doc.RootElement.GetProperty("dailyBudgetUsd").GetDecimal());
        Assert.Equal(1.25m, doc.RootElement.GetProperty("spentTodayUsd").GetDecimal());
        Assert.Equal(3.75m, doc.RootElement.GetProperty("remainingUsd").GetDecimal());
    }

    [Fact]
    public async Task StartInvocation_Refused_When_Daily_Budget_Exceeded()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Cap the crew at $0.50 for today and pre-spend $1.
        await client.PatchAsJsonAsync("/api/crews/crew-eng", new { dailyBudgetUsd = 0.5m }, JsonOpts);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.AgentUsage.Add(new AgentUsageRow
            {
                AgentId = "E4", CrewId = "crew-eng", TaskId = "any", Model = "claude-opus-4-7",
                InputTokens = 0, OutputTokens = 0, CostUsd = 1m,
                StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, Status = "finished",
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync("/api/agents/E4/invocations", new
        {
            taskId = "234",
            prompt = "blocked",
            model = "local-stub",
            crewId = "crew-eng",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.PaymentRequired, resp.StatusCode);
    }
}
