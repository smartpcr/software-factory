// -----------------------------------------------------------------------
// <copyright file="Phase85AgentLruRotationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 2026-05-25 — least-recently-used pair selection. The diversity picker
/// only avoided agents busy <i>right now</i>; once a task finished its
/// agents were eligible again and successive claims kept re-picking the
/// same generator + (default) evaluator. These tests pin the new LRU
/// ordering: among the ready candidates the picker prefers the one that
/// has done no work yet, then the one whose last invocation is oldest.
///
/// <para>Recency comes from <see cref="BusyAgentsHelper.LoadLastActiveAsync"/>
/// (MAX <c>agent_usage.StartedAt</c>). The seed shuffle still breaks ties
/// WITHIN an equal-recency bucket (covered by the Phase 49/50 distribution
/// tests, which run the same call sites with an empty ledger), and the
/// operator's configured default evaluator wins ties only — it no longer
/// pins unconditionally.</para>
/// </summary>
public sealed class Phase85AgentLruRotationTests
{
    private static string Iso(DateTimeOffset t) => t.ToString("o");

    // ── PickGenerator (pure) ───────────────────────────────────────────────

    [Fact]
    public void PickGenerator_PrefersNeverWorkedAgent()
    {
        var roster = new[] { "a1", "a2", "a3" };
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var lastActive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a1"] = Iso(now.AddMinutes(-5)),
            ["a2"] = Iso(now.AddMinutes(-1)),
            // a3 absent → never worked → must be picked first.
        };

        var (id, _) = PairDiversityResolver.PickGenerator(
            roster, busy, lead: null, seed: 0, lastActiveByAgent: lastActive);

        Assert.Equal("a3", id);
    }

    [Fact]
    public void PickGenerator_PrefersOldestWhenAllHaveWorked()
    {
        var roster = new[] { "a1", "a2", "a3" };
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var lastActive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a1"] = Iso(now.AddMinutes(-2)),
            ["a2"] = Iso(now.AddMinutes(-10)), // oldest
            ["a3"] = Iso(now.AddMinutes(-1)),
        };

        var (id, _) = PairDiversityResolver.PickGenerator(
            roster, busy, lead: null, seed: 0, lastActiveByAgent: lastActive);

        Assert.Equal("a2", id);
    }

    [Fact]
    public void PickGenerator_SkipsBusyEvenWhenItIsOldest()
    {
        var roster = new[] { "a1", "a2", "a3" };
        var busy = new HashSet<string>(StringComparer.Ordinal) { "a2" };
        var now = DateTimeOffset.UtcNow;
        var lastActive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a1"] = Iso(now.AddMinutes(-2)),
            ["a2"] = Iso(now.AddMinutes(-10)), // oldest but busy → excluded
            ["a3"] = Iso(now.AddMinutes(-1)),
        };

        var (id, _) = PairDiversityResolver.PickGenerator(
            roster, busy, lead: null, seed: 0, lastActiveByAgent: lastActive);

        Assert.Equal("a1", id); // next-oldest free member
    }

    // ── PickEvaluator (pure) ───────────────────────────────────────────────

    [Fact]
    public void PickEvaluator_RotatesByRecency_DefaultDoesNotPinWhenMoreRecent()
    {
        var roster = new[] { "e1", "e2", "e3" };
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var lastActive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["e1"] = Iso(now.AddMinutes(-1)), // configured default, but most recent
            ["e2"] = Iso(now.AddMinutes(-9)), // oldest
            ["e3"] = Iso(now.AddMinutes(-3)),
        };

        var (id, fellBackToDefault) = PairDiversityResolver.PickEvaluator(
            roster, busy, defaultEvalAgentId: "e1", generatorAgentId: "gen",
            seed: 0, lastActiveByAgent: lastActive);

        Assert.Equal("e2", id); // LRU wins over the default's first-dibs
        Assert.False(fellBackToDefault);
    }

    [Fact]
    public void PickEvaluator_DefaultWinsOnlyOnRecencyTie()
    {
        var roster = new[] { "e1", "e2", "e3" };
        var busy = new HashSet<string>(StringComparer.Ordinal);
        // Empty map → everyone is never-worked → equal recency → the
        // configured default breaks the tie.
        var lastActive = new Dictionary<string, string>(StringComparer.Ordinal);

        var (id, fellBackToDefault) = PairDiversityResolver.PickEvaluator(
            roster, busy, defaultEvalAgentId: "e3", generatorAgentId: "gen",
            seed: 0, lastActiveByAgent: lastActive);

        Assert.Equal("e3", id);
        Assert.True(fellBackToDefault);
    }

    [Fact]
    public void PickEvaluator_NeverPicksGenerator_UnderLru()
    {
        var roster = new[] { "g", "e2" };
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var lastActive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["g"] = Iso(now.AddMinutes(-30)), // oldest, but it's the generator
            ["e2"] = Iso(now.AddMinutes(-1)),
        };

        var (id, _) = PairDiversityResolver.PickEvaluator(
            roster, busy, defaultEvalAgentId: null, generatorAgentId: "g",
            seed: 0, lastActiveByAgent: lastActive);

        Assert.Equal("e2", id); // gen excluded despite being oldest
    }

    // ── LoadLastActiveAsync + ResolveImplPairAsync (integration) ────────────

    [Fact]
    public async Task LoadLastActiveAsync_ReturnsMaxStartedAt_AndOmitsNeverWorked()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            var now = DateTimeOffset.UtcNow;
            control.AgentUsage.Add(NewUsage("x", now.AddMinutes(-10), now.AddMinutes(-9)));
            control.AgentUsage.Add(NewUsage("x", now.AddMinutes(-2), now.AddMinutes(-1))); // latest for x
            control.AgentUsage.Add(NewUsage("y", now.AddMinutes(-5), endedAt: null));
            await control.SaveChangesAsync();

            var map = await BusyAgentsHelper.LoadLastActiveAsync(control, new[] { "x", "y", "z" });

            Assert.True(map.ContainsKey("x"));
            Assert.True(map.ContainsKey("y"));
            Assert.False(map.ContainsKey("z")); // never worked → absent

            // MAX wins: x's latest row (−2m), not its older (−10m) row.
            var xLatest = DateTimeOffset.Parse(map["x"], null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.True((xLatest - now.AddMinutes(-2)).Duration() < TimeSpan.FromSeconds(5),
                $"Expected x's latest ≈ now-2m; got {xLatest:o}.");
        }
    }

    [Fact]
    public async Task ResolveImplPairAsync_PicksNeverWorkedGeneratorFirst()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedCrewsAsync(control, db);

            // Day-crew generators DA/DB/DD have recent ledger rows; DC has
            // none, so LRU must pick DC as the generator.
            var now = DateTimeOffset.UtcNow;
            control.AgentUsage.Add(NewUsage("DA", now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(20)));
            control.AgentUsage.Add(NewUsage("DB", now.AddMinutes(-2), now.AddMinutes(-2).AddSeconds(20)));
            control.AgentUsage.Add(NewUsage("DD", now.AddMinutes(-3), now.AddMinutes(-3).AddSeconds(20)));
            await control.SaveChangesAsync();

            var allocation = new AgentAllocationLoader();
            var busy = new HashSet<string>(StringComparer.Ordinal);

            var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                control, db, allocation, busy, seedOffset: 0,
                workstreamCategory: WorkstreamCategories.Impl,
                CancellationToken.None);

            Assert.True(pair.Length >= 1);
            Assert.Equal("DC", pair[0]);
        }
    }

    [Fact]
    public async Task ResolveImplPairAsync_PicksOldestGeneratorWhenAllHaveWorked()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedCrewsAsync(control, db);

            // Every day-crew member has worked; DB is the oldest.
            var now = DateTimeOffset.UtcNow;
            control.AgentUsage.Add(NewUsage("DA", now.AddMinutes(-2), now.AddMinutes(-2).AddSeconds(20)));
            control.AgentUsage.Add(NewUsage("DB", now.AddMinutes(-30), now.AddMinutes(-30).AddSeconds(20))); // oldest
            control.AgentUsage.Add(NewUsage("DC", now.AddMinutes(-3), now.AddMinutes(-3).AddSeconds(20)));
            control.AgentUsage.Add(NewUsage("DD", now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(20)));
            await control.SaveChangesAsync();

            var allocation = new AgentAllocationLoader();
            var busy = new HashSet<string>(StringComparer.Ordinal);

            var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                control, db, allocation, busy, seedOffset: 0,
                workstreamCategory: WorkstreamCategories.Impl,
                CancellationToken.None);

            Assert.True(pair.Length >= 1);
            Assert.Equal("DB", pair[0]);
        }
    }

    // ── helpers (mirrors Phase50EvaluatorDistributionTests) ─────────────────

    private static readonly string[] DayMembers = new[] { "DA", "DB", "DC", "DD" };
    private static readonly string[] NightMembers = new[] { "NA", "NB", "NC", "ND", "NE", "NF", "NG", "NH" };

    private static AgentUsageRow NewUsage(string agentId, DateTimeOffset startedAt, DateTimeOffset? endedAt) =>
        new()
        {
            AgentId = agentId,
            CrewId = "crew-eng",
            TaskId = "t",
            Model = "m",
            StartedAt = startedAt,
            EndedAt = endedAt,
            Status = endedAt is null ? "running" : "completed",
        };

    private static async Task<(SqliteConnection conn, ControlDbContext control, ProductDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var ctlOpts = new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options;
        var control = new ControlDbContext(ctlOpts);
        await control.Database.EnsureCreatedAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options;
        var db = new ProductDbContext(opts);
        var schema = db.Database.GenerateCreateScript();
        foreach (var stmt in schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length > 0) await db.Database.ExecuteSqlRawAsync(trimmed);
        }
        return (conn, control, db);
    }

    private static async Task SeedPairedCrewsAsync(ControlDbContext control, ProductDbContext db)
    {
        control.Crews.Add(new CrewRow
        {
            Id = "crew-eng", Name = "Engineer (day)", Lead = "DLEAD",
            PairedWithCrewId = "crew-eng-night",
        });
        control.Crews.Add(new CrewRow
        {
            Id = "crew-eng-night", Name = "Engineer (night)", Lead = "NLEAD",
            PairedWithCrewId = "crew-eng",
        });
        foreach (var a in DayMembers) control.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-eng", AgentId = a });
        foreach (var a in NightMembers) control.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-eng-night", AgentId = a });
        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }
}
