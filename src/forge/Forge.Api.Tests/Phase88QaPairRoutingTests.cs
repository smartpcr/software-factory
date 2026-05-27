// -----------------------------------------------------------------------
// <copyright file="Phase88QaPairRoutingTests.cs" company="Microsoft Corp.">
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
/// Phase 88 — <see cref="StorySpawnOrchestrator.ResolveImplPairAsync"/>
/// must route pair selection to the right crew based on workstream
/// category. Pre-Phase-88 the method hardcoded <c>crew-eng</c> for every
/// workstream, which routed qa-e2e rows through engineer agents that
/// included refusal-prone personas (<c>code-refactor-defender</c> HD3
/// emitted literally "DONE" with 0 tool calls for 65 iters burning
/// $458.91 on code-intelligence:CLEAN-CODE stage 6.1-e2e, 2026-05-25).
///
/// <para>These tests pin the routing contract end-to-end against an
/// in-memory SQLite DB so a future refactor can't accidentally
/// reintroduce the wrong-crew bug. The library-level pair-shuffle math
/// is covered by <see cref="Phase50EvaluatorDistributionTests"/>.</para>
/// </summary>
public sealed class Phase88QaPairRoutingTests
{
    private static readonly string[] EngMembers = new[] { "EA", "EB", "EC", "ED" };
    private static readonly string[] QaMembers = new[] { "QA1", "QA2", "QA3", "QA4" };

    private static async Task<(SqliteConnection conn, ControlDbContext control, ProductDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var ctlOpts = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(conn)
            .Options;
        var control = new ControlDbContext(ctlOpts);
        await control.Database.EnsureCreatedAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ProductDbContext(opts);
        var schema = db.Database.GenerateCreateScript();
        foreach (var stmt in schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = stmt.Trim();
            if (trimmed.Length > 0) await db.Database.ExecuteSqlRawAsync(trimmed);
        }
        return (conn, control, db);
    }

    /// <summary>
    /// Seed BOTH crew-eng and crew-qa with disjoint rosters so a passed
    /// category mis-route is immediately observable (the picked agent
    /// belongs to the wrong roster).
    /// </summary>
    private static async Task SeedBothCrewsAsync(ControlDbContext control, ProductDbContext db)
    {
        control.Crews.Add(new CrewRow
        {
            Id = "crew-eng",
            Name = "Engineer",
            Lead = "ELEAD",
        });
        control.Crews.Add(new CrewRow
        {
            Id = "crew-qa",
            Name = "QA",
            Lead = "QLEAD",
        });

        foreach (var a in EngMembers)
        {
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-eng", AgentId = a });
        }

        foreach (var a in QaMembers)
        {
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-qa", AgentId = a });
        }

        await control.SaveChangesAsync();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Headline regression: when workstreamCategory == "qa-e2e", the
    /// picker MUST pull from crew-qa (not crew-eng). Before the fix
    /// every category drew from crew-eng so the qa-e2e row got an
    /// engineer-persona generator (HD3 on the prod incident).
    /// </summary>
    [Fact]
    public async Task ResolveImplPairAsync_QaE2eCategory_PicksFromCrewQa()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedBothCrewsAsync(control, db);
            var allocation = new AgentAllocationLoader();
            var qaSet = new HashSet<string>(QaMembers, StringComparer.Ordinal);
            qaSet.Add("QLEAD"); // also a legitimate crew-qa pick via Lead-fallback
            var engSet = new HashSet<string>(EngMembers, StringComparer.Ordinal);
            engSet.Add("ELEAD");

            for (var i = 0; i < 30; i++)
            {
                var busy = new HashSet<string>(StringComparer.Ordinal);
                var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                    control, db, allocation, busy, seedOffset: i,
                    workstreamCategory: WorkstreamCategories.QaE2e,
                    CancellationToken.None);

                Assert.True(pair.Length >= 1,
                    $"trial {i}: expected at least a generator; got {pair.Length}");
                var gen = pair[0];
                Assert.True(qaSet.Contains(gen),
                    $"trial {i}: qa-e2e generator '{gen}' is not a crew-qa member or lead. "
                    + $"crew-qa = {{ {string.Join(", ", qaSet)} }}; crew-eng = {{ {string.Join(", ", engSet)} }}");
                Assert.False(engSet.Contains(gen),
                    $"trial {i}: qa-e2e generator '{gen}' belongs to crew-eng — routing regression.");
            }
        }
    }

    /// <summary>
    /// Counterpart regression guard: workstreamCategory == "impl" still
    /// routes through crew-eng (the legacy pre-Phase-88 behaviour).
    /// </summary>
    [Fact]
    public async Task ResolveImplPairAsync_ImplCategory_PicksFromCrewEng()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedBothCrewsAsync(control, db);
            var allocation = new AgentAllocationLoader();
            var qaSet = new HashSet<string>(QaMembers, StringComparer.Ordinal);
            qaSet.Add("QLEAD");
            var engSet = new HashSet<string>(EngMembers, StringComparer.Ordinal);
            engSet.Add("ELEAD");

            for (var i = 0; i < 30; i++)
            {
                var busy = new HashSet<string>(StringComparer.Ordinal);
                var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                    control, db, allocation, busy, seedOffset: i,
                    workstreamCategory: WorkstreamCategories.Impl,
                    CancellationToken.None);

                Assert.True(pair.Length >= 1,
                    $"trial {i}: expected at least a generator; got {pair.Length}");
                var gen = pair[0];
                Assert.True(engSet.Contains(gen),
                    $"trial {i}: impl generator '{gen}' is not a crew-eng member or lead.");
                Assert.False(qaSet.Contains(gen),
                    $"trial {i}: impl generator '{gen}' belongs to crew-qa — wrong-crew leak.");
            }
        }
    }

    /// <summary>
    /// Defensive guard: an invalid / non-matching category string MUST
    /// fall through to crew-eng (the historical default). The call-site
    /// sanitises invalid via <c>WorkstreamCategories.IsValid</c>, but if
    /// a future caller forgets to sanitise, the function-level default
    /// keeps the row claiming through the engineer pool instead of
    /// throwing or stalling.
    /// </summary>
    [Fact]
    public async Task ResolveImplPairAsync_UnknownCategory_FallsBackToCrewEng()
    {
        var (conn, control, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedBothCrewsAsync(control, db);
            var allocation = new AgentAllocationLoader();
            var qaSet = new HashSet<string>(QaMembers, StringComparer.Ordinal);
            qaSet.Add("QLEAD");
            var engSet = new HashSet<string>(EngMembers, StringComparer.Ordinal);
            engSet.Add("ELEAD");

            // Try several invalid category strings — empty, garbage,
            // case-mismatch on the known value (the impl const is the
            // lowercase string "impl"; "IMPL" does NOT match Ordinal).
            foreach (var category in new[] { string.Empty, "garbage", "IMPL", "QA-E2E", "review", "fixme" })
            {
                var busy = new HashSet<string>(StringComparer.Ordinal);
                var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                    control, db, allocation, busy, seedOffset: 1,
                    workstreamCategory: category,
                    CancellationToken.None);

                Assert.True(pair.Length >= 1,
                    $"category='{category}': expected at least a generator; got {pair.Length}");
                var gen = pair[0];
                Assert.True(engSet.Contains(gen),
                    $"category='{category}': fallback generator '{gen}' is not a crew-eng member. "
                    + "Unknown categories must default to crew-eng, not crew-qa.");
                Assert.False(qaSet.Contains(gen),
                    $"category='{category}': fallback generator '{gen}' belongs to crew-qa — "
                    + "unknown categories must not silently route to QA.");
            }
        }
    }
}
