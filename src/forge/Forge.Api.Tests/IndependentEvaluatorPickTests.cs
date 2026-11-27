// -----------------------------------------------------------------------
// <copyright file="IndependentEvaluatorPickTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// The story path never lets the generator evaluate its own work. An unpaired
/// crew resolves its evaluator to <c>DefaultEvaluatorAgentId ?? Lead</c>, so
/// when the lead generated it also evaluated itself (23 of 122 qa-e2e runs in
/// November 2026 had crew-qa's lead Q2 on both sides).
/// <see cref="CrewStoryArtifactStrategy.PickIndependentEvaluatorAsync"/> picks
/// the replacement.
/// </summary>
public sealed class IndependentEvaluatorPickTests
{
    private const string CrewId = "crew-qa";
    private const string Lead = "Q2";

    private static async Task<(SqliteConnection conn, ControlDbContext control, ProductDbContext db, CrewRow crew)> SeedAsync(params string[] members)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);
        await control.Database.EnsureCreatedAsync();
        var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        foreach (var stmt in db.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length > 0) await db.Database.ExecuteSqlRawAsync(trimmed);
        }

        var crew = new CrewRow { Id = CrewId, Name = "QA", Lead = Lead, Model = "claude-opus-5.5", EvaluatorModel = "gpt-6.1-sol" };
        control.Crews.Add(crew);
        foreach (var m in members)
        {
            control.Agents.Add(new AgentRow { Id = m, Name = m, Role = "qa", CrewId = CrewId });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = CrewId, AgentId = m, AddedAt = DateTimeOffset.UtcNow });
        }

        await control.SaveChangesAsync();
        return (conn, control, db, crew);
    }

    [Fact]
    public async Task PicksAnotherMember_NeverTheGenerator()
    {
        var (conn, control, db, crew) = await SeedAsync(Lead, "WG", "DC");
        using (conn)
        {
            var picked = await CrewStoryArtifactStrategy.PickIndependentEvaluatorAsync(control, db, crew, Lead, "ws-1", default);

            Assert.Contains(picked, new[] { "WG", "DC" });
        }
    }

    [Fact]
    public async Task PrefersTheLeastRecentlyActiveMember()
    {
        var (conn, control, db, crew) = await SeedAsync(Lead, "WG", "DC");
        using (conn)
        {
            control.AgentUsage.Add(new AgentUsageRow
            {
                AgentId = "WG", CrewId = CrewId, TaskId = "t1", Model = "m",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), EndedAt = DateTimeOffset.UtcNow, Status = "finished",
            });
            await control.SaveChangesAsync();

            var picked = await CrewStoryArtifactStrategy.PickIndependentEvaluatorAsync(control, db, crew, Lead, "ws-1", default);

            Assert.Equal("DC", picked);
        }
    }

    [Fact]
    public async Task SkipsPausedMembers_AndReturnsNullWhenOnlyTheGeneratorRemains()
    {
        var (conn, control, db, crew) = await SeedAsync(Lead, "WG");
        using (conn)
        {
            var wg = await control.Agents.FirstAsync(a => a.Id == "WG");
            wg.State = "paused";
            await control.SaveChangesAsync();

            var picked = await CrewStoryArtifactStrategy.PickIndependentEvaluatorAsync(control, db, crew, Lead, "ws-1", default);

            Assert.Null(picked);
        }
    }

    [Fact]
    public async Task KeepsTheEvaluatorOfAnInFlightLoop()
    {
        var (conn, control, db, crew) = await SeedAsync(Lead, "WG", "DC");
        using (conn)
        {
            control.AgentUsage.Add(new AgentUsageRow
            {
                AgentId = "DC", CrewId = CrewId, TaskId = "t1", Model = "m",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), EndedAt = DateTimeOffset.UtcNow, Status = "finished",
            });
            await control.SaveChangesAsync();
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "ws-1", CrewId = CrewId, GeneratorAgentId = Lead, EvaluatorAgentId = "DC",
                Iter = 1, Score = 70, Verdict = "iterate", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var picked = await CrewStoryArtifactStrategy.PickIndependentEvaluatorAsync(control, db, crew, Lead, "ws-1", default);

            Assert.Equal("DC", picked);
        }
    }
}