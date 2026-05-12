// -----------------------------------------------------------------------
// <copyright file="Phase50EvaluatorDistributionTests.cs" company="Microsoft Corp.">
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
/// 2026-05-12 bug fix — engineer-workstream evaluator picks were pinned
/// to the paired (night-) crew's <c>Lead</c> because
/// <c>ResolveImplPairAsync</c> composed:
/// <code>
/// var defaultEval = crew.DefaultEvaluatorAgentId
///     ?? evaluatorCrew?.DefaultEvaluatorAgentId
///     ?? evaluatorCrew?.Lead;   // bug — Lead got first-dibs unconditionally
/// </code>
/// and <see cref="PairDiversityResolver.PickEvaluator"/> gives the
/// supplied <c>defaultEvalAgentId</c> the first slot in <c>ordered</c>
/// (post-default Fisher-Yates only shuffles the FALLBACK candidates). With
/// both <c>DefaultEvaluatorAgentId</c> columns null in production (the
/// operator's DB had no per-crew preferred evaluator configured),
/// <c>defaultEval</c> collapsed to <c>evaluatorCrew?.Lead</c> on every
/// call and PickEvaluator picked the Lead every single time the Lead was
/// not busy.
///
/// <para>The fix passes <c>null</c> to <c>PickEvaluator</c> when no
/// operator preference is set, so the Fisher-Yates shuffle covers the
/// WHOLE roster and the picker uniformly samples it. The Lead is now
/// only consulted as a last-resort fallback when PickEvaluator returns
/// null (every roster member busy).</para>
///
/// <para>These tests pin the call-site contract end-to-end against an
/// in-memory SQLite DB so a future refactor can't accidentally
/// reintroduce the Lead-as-default-eval promotion. The library-level
/// shuffle math is covered by
/// <see cref="Phase49LatePairAssignmentTests"/>.</para>
/// </summary>
public sealed class Phase50EvaluatorDistributionTests
{
    private static readonly string[] DayMembers = new[] { "DA", "DB", "DC", "DD" };
    private static readonly string[] NightMembers = new[] { "NA", "NB", "NC", "ND", "NE", "NF", "NG", "NH" };

    private static async Task<(SqliteConnection conn, ForgeDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ForgeDbContext(opts);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    private static async Task SeedPairedCrewsAsync(
        ForgeDbContext db,
        string? dayDefaultEval = null,
        string? nightDefaultEval = null)
    {
        // crew-eng (day) paired with crew-eng-night.
        db.Crews.Add(new CrewRow
        {
            Id = "crew-eng",
            Name = "Engineer (day)",
            Lead = "DLEAD",
            DefaultEvaluatorAgentId = dayDefaultEval,
            PairedWithCrewId = "crew-eng-night",
        });
        db.Crews.Add(new CrewRow
        {
            Id = "crew-eng-night",
            Name = "Engineer (night)",
            Lead = "NLEAD",
            DefaultEvaluatorAgentId = nightDefaultEval,
            PairedWithCrewId = "crew-eng",
        });

        foreach (var a in DayMembers)
        {
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-eng", AgentId = a });
        }

        foreach (var a in NightMembers)
        {
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-eng-night", AgentId = a });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Headline regression: with no <c>DefaultEvaluatorAgentId</c> set on
    /// either side of the pair, 100 independent claims must spread the
    /// evaluator across the night-crew roster — NOT pin to
    /// <c>crew-eng-night.Lead</c>. Before the fix this test sees a single
    /// agent (NLEAD) on every claim; after the fix the picker uniformly
    /// samples the 8-member night roster.
    /// </summary>
    [Fact]
    public async Task ResolveImplPairAsync_NoDefaultEval_DistributesEvaluatorAcrossNightRoster()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedCrewsAsync(db);
            var allocation = new AgentAllocationLoader();
            var coverage = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < 100; i++)
            {
                // Fresh empty busy set on every claim — this models the
                // "agent_usage table is empty because the previous pick
                // hasn't started its invocation yet" race that originally
                // hid the bug.
                var busy = new HashSet<string>(StringComparer.Ordinal);
                var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                    db, allocation, busy, seedOffset: i, CancellationToken.None);

                Assert.True(pair.Length >= 2,
                    $"Expected pair to include both generator and evaluator; got {pair.Length} agents.");
                var eval = pair[1];
                coverage[eval] = coverage.TryGetValue(eval, out var n) ? n + 1 : 1;
            }

            // Pin: at least 5 of the 8 night-crew members got picked as
            // evaluator across the 100 trials. Before the fix only NLEAD
            // is ever picked (coverage == 1). The 5/8 threshold gives
            // safe headroom against RNG variance while still tripping
            // hard on the regression.
            Assert.True(coverage.Count >= 5,
                $"Expected evaluator to cover ≥ 5 of the 8 night-crew members; saw {coverage.Count}: " +
                string.Join(", ", coverage.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));

            // Direct anti-regression: NLEAD must not be over-represented
            // (would mean it's still getting first-dibs). With uniform
            // sampling NLEAD's share is ≈ 1/8 = 12-13 picks out of 100.
            // 50 is a generous ceiling that still catches the bug
            // (where NLEAD = 100/100).
            var nleadHits = coverage.TryGetValue("NLEAD", out var n2) ? n2 : 0;
            Assert.True(nleadHits < 50,
                $"NLEAD was picked {nleadHits}/100 times; the Lead is no longer supposed to get unconditional first-dibs.");
        }
    }

    /// <summary>
    /// Operator-configured <c>DefaultEvaluatorAgentId</c> still gets
    /// first-dibs. This is the LEGITIMATE first-dibs path documented in
    /// <see cref="PairDiversityResolver.PickEvaluator"/>: the operator
    /// said "prefer this agent as evaluator", so the picker honours that
    /// when the agent is free. Only the implicit Lead-as-default
    /// promotion is removed by the fix.
    /// </summary>
    [Fact]
    public async Task ResolveImplPairAsync_OperatorDefaultEval_StillWinsWhenFree()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            const string preferred = "NC"; // an arbitrary night-crew member
            await SeedPairedCrewsAsync(db, nightDefaultEval: preferred);
            var allocation = new AgentAllocationLoader();

            // 30 trials — the preferred agent should win the vast
            // majority. Allow some misses because PickGenerator could
            // happen to pick the preferred agent first (which then
            // excludes it from the evaluator set via gen != eval).
            var preferredHits = 0;
            const int trials = 30;
            for (var i = 0; i < trials; i++)
            {
                var busy = new HashSet<string>(StringComparer.Ordinal);
                var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                    db, allocation, busy, seedOffset: i, CancellationToken.None);
                if (pair.Length >= 2 && string.Equals(pair[1], preferred, StringComparison.Ordinal))
                {
                    preferredHits++;
                }
            }

            // Generator pool is the 4-member day crew, so the gen ≠ eval
            // collision against a night-roster preferred eval should
            // never happen. preferredHits == trials in practice; we
            // require >= 80% to keep the test stable across RNG noise.
            Assert.True(preferredHits >= (trials * 4 / 5),
                $"Operator-preferred evaluator '{preferred}' won {preferredHits}/{trials} — expected ≥ 80%.");
        }
    }

    /// <summary>
    /// Last-resort Lead fallback still works when every roster member is
    /// busy. The fix removed the Lead from the FIRST-DIBS slot in
    /// PickEvaluator, but kept it as the post-null fallback so a
    /// capacity-saturated pair still claims (Q47.3 — best-effort
    /// diversity).
    /// </summary>
    [Fact]
    public async Task ResolveImplPairAsync_AllRosterBusy_FallsBackToLead()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedCrewsAsync(db);
            var allocation = new AgentAllocationLoader();

            // Mark every roster member (both crews) busy.
            var busy = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in DayMembers) busy.Add(a);
            foreach (var a in NightMembers) busy.Add(a);

            var pair = await StorySpawnOrchestrator.ResolveImplPairAsync(
                db, allocation, busy, seedOffset: 1, CancellationToken.None);

            // Generator falls back to crew-eng.Lead = "DLEAD".
            // Evaluator falls back to crew-eng-night.Lead = "NLEAD"
            // via the last-resort path in the fixed code.
            Assert.True(pair.Length >= 2,
                $"Expected pair to include both generator and evaluator even when fully saturated; got {pair.Length}.");
            Assert.Equal("NLEAD", pair[1]);
        }
    }
}
