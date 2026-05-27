// -----------------------------------------------------------------------
// <copyright file="Phase53SequentialPlanDocPairRotationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 53 regression coverage for the sequential plan-doc path. Even though
/// docs run one at a time, each doc must keep its Phase-47 canonical roster
/// slot instead of every invocation asking for a one-element roster and taking
/// the same architect/evaluator pair.
/// </summary>
public sealed class Phase53SequentialPlanDocPairRotationTests
{
    [Fact]
    public async Task LoadPlanDocAgentPairAsync_SequentialDocs_UseCanonicalRosterSlots()
    {
        var (conn, services) = await NewServicesAsync();
        await using (conn)
        using (services)
        {
            await SeedArchitectCrewAsync(services);

            var strategy = new CrewStoryArtifactStrategy(
                services.GetRequiredService<IServiceScopeFactory>(),
                null!, null!, null!, null!, null!, null!, null!);

            var pairs = new List<(string? Gen, string? Eval)>();
            foreach (var docKey in PlanDocStateHelpers.PlanDocOrder)
            {
                pairs.Add(await strategy.LoadPlanDocAgentPairAsync(docKey, CancellationToken.None));
            }

            Assert.Equal(new[]
            {
                ("A0", "A1"),
                ("A1", "A2"),
                ("A2", "A3"),
                ("A3", "A0"),
            }, pairs);
            Assert.Equal(4, pairs.Distinct().Count());
        }
    }

    [Fact]
    public void PlanDocIndexOf_UsesCanonicalPhase53Order()
    {
        Assert.Equal(0, PlanDocStateHelpers.PlanDocIndexOf("architecture"));
        Assert.Equal(1, PlanDocStateHelpers.PlanDocIndexOf("tech-spec"));
        Assert.Equal(2, PlanDocStateHelpers.PlanDocIndexOf("implementation-plan"));
        Assert.Equal(3, PlanDocStateHelpers.PlanDocIndexOf("e2e-scenarios"));
        Assert.Equal(-1, PlanDocStateHelpers.PlanDocIndexOf("unknown"));
    }

    private static async Task<(SqliteConnection Conn, ServiceProvider Services)> NewServicesAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ControlDbContext>(o => o.UseSqlite(conn));
        services.AddDbContext<ProductDbContext>(o => o.UseSqlite(conn));
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        await control.Database.EnsureCreatedAsync();

        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var productSchema = db.Database.GenerateCreateScript();
        foreach (var stmt in productSchema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length > 0)
            {
                await db.Database.ExecuteSqlRawAsync(trimmed);
            }
        }

        return (conn, provider);
    }

    private static async Task SeedArchitectCrewAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        control.Crews.Add(new CrewRow
        {
            Id = "crew-arch",
            Name = "Architecture",
            Lead = "A0",
            Model = "claude-opus-4.7",
        });

        for (var i = 0; i < 4; i++)
        {
            var agentId = $"A{i}";
            control.Agents.Add(new AgentRow
            {
                Id = agentId,
                Name = agentId,
                Role = "arch",
                CrewId = "crew-arch",
                Sort = i,
            });
        }

        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }
}
