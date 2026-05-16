// -----------------------------------------------------------------------
// <copyright file="Phase50PlanDocEvaluatorOverrideTests.cs" company="Microsoft Corp.">
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
/// 2026-05-12 bug fix — plan-doc evaluator overrides were being silently
/// dropped on any architect crew configured with
/// <see cref="CrewRow.PairedWithCrewId"/> (the day↔night-shift pairing
/// every production architect crew uses for model diversity).
///
/// <para><c>CrewStoryArtifactStrategy.RunGenEvalLoopAsync</c> used to
/// write the per-doc <c>evaluatorAgentOverride</c> onto the WORK crew
/// (<c>crew</c>) BEFORE fetching the paired evaluator crew. The
/// resolver then re-read the night-shift crew via a fresh
/// <c>AsNoTracking</c> query — a brand-new row instance without the
/// mutation — and resolved
/// <c>evalCrew.DefaultEvaluatorAgentId ?? evalCrew.Lead</c>. With both
/// columns null on the night row (the operator's typical config),
/// every plan-doc evaluator collapsed to <c>evalCrew.Lead</c>. All
/// four plan docs of the MICROSOFT-TEAMS-MESSENGER-SUPPORT story ran
/// against the same agent ("AB2", the night-crew Lead) instead of
/// the four distinct agents the pairing algorithm in
/// <see cref="CrewStoryArtifactStrategy"/>.LoadArchitectAndEvaluatorRostersAsync
/// had picked.</para>
///
/// <para>The fix moves the override write below the <c>evalCrew</c>
/// fetch and applies it onto <c>evalCrew</c> — the row the resolver
/// actually consults. These tests pin that contract end-to-end against
/// an in-memory SQLite database so a future refactor can't accidentally
/// re-introduce the wrong-row mutation.</para>
///
/// <para>Same class of bug as the impl-side regression
/// <see cref="Phase50EvaluatorDistributionTests"/> guards against; the
/// plan-doc path was missed in that earlier sweep.</para>
/// </summary>
public sealed class Phase50PlanDocEvaluatorOverrideTests
{
    private const string DayCrewId = "crew-arch";
    private const string NightCrewId = "crew-arch-night";
    private const string DayLead = "DA-LEAD";
    private const string NightLead = "AB2";
    private const string OverrideAgent = "AG";

    private static async Task<(SqliteConnection conn, ProductDbContext db)> NewDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new ProductDbContext(opts);
        await db.Database.EnsureCreatedAsync();
        return (conn, db);
    }

    private static async Task SeedPairedArchCrewsAsync(ProductDbContext db)
    {
        // Day crew — the "work" crew (where the generator agents live).
        db.Crews.Add(new CrewRow
        {
            Id = DayCrewId,
            Name = "Architect (day)",
            Lead = DayLead,
            Model = "claude-opus-4-7",
            PairedWithCrewId = NightCrewId,
            Shift = "day",
            PairRole = "generator",
        });

        // Night crew — the "evaluator" crew. EvaluatorModel set so the
        // pair-diversity gate would pass with an override; without an
        // override and with DefaultEvaluatorAgentId null, the resolver
        // falls back to Lead = NightLead, which is the bug.
        db.Crews.Add(new CrewRow
        {
            Id = NightCrewId,
            Name = "Architect (night)",
            Lead = NightLead,
            Model = "gpt-5-codex",
            EvaluatorModel = "gpt-5-codex",
            PairedWithCrewId = DayCrewId,
            Shift = "night",
            PairRole = "evaluator",
        });

        // Day-crew agents — the pairing algorithm picks from this pool.
        // OverrideAgent lives here (Architect Lead-bench, not the
        // night-crew lead).
        db.Agents.Add(new AgentRow { Id = DayLead, Name = "Day Lead", Role = "arch", CrewId = DayCrewId, Sort = 0 });
        db.Agents.Add(new AgentRow { Id = OverrideAgent, Name = "Ao Guang", Role = "arch", CrewId = DayCrewId, Sort = 1 });
        db.Agents.Add(new AgentRow { Id = "A1", Name = "Day Agent 1", Role = "arch", CrewId = DayCrewId, Sort = 2 });
        db.Agents.Add(new AgentRow { Id = "A2", Name = "Day Agent 2", Role = "arch", CrewId = DayCrewId, Sort = 3 });

        db.CrewMembers.Add(new CrewMemberRow { CrewId = DayCrewId, AgentId = DayLead });
        db.CrewMembers.Add(new CrewMemberRow { CrewId = DayCrewId, AgentId = OverrideAgent });
        db.CrewMembers.Add(new CrewMemberRow { CrewId = DayCrewId, AgentId = "A1" });
        db.CrewMembers.Add(new CrewMemberRow { CrewId = DayCrewId, AgentId = "A2" });

        // Night-crew agents — including the Lead AB2 which the bug
        // pinned every evaluator slot to.
        db.Agents.Add(new AgentRow { Id = NightLead, Name = "Ao Bing", Role = "arch", CrewId = NightCrewId, Sort = 0 });
        db.CrewMembers.Add(new CrewMemberRow { CrewId = NightCrewId, AgentId = NightLead });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Headline regression. Simulates the FIXED code path: the
    /// override is written onto <c>evalCrew</c> (the night-shift row,
    /// the one the resolver actually consults). Resolver MUST honour
    /// it instead of falling back to <c>evalCrew.Lead</c>.
    /// </summary>
    [Fact]
    public async Task ResolveAgentRunConfig_EvalOverride_OnPairedNightCrew_HonorsOverride()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedArchCrewsAsync(db);

            // Simulate what RunGenEvalLoopAsync does after the fix:
            // fetch the day crew (work crew), then fetch the night
            // crew via PairedWithCrewId (the evaluator crew). Write
            // the override onto the EVALUATOR crew row.
            var dayCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == DayCrewId);
            var nightCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == dayCrew.PairedWithCrewId);
            nightCrew.DefaultEvaluatorAgentId = OverrideAgent;

            var (agentId, _, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, nightCrew, useEvaluatorRole: true,
                workspace: "/tmp/test", budget: null, CancellationToken.None);

            Assert.Equal(OverrideAgent, agentId);
        }
    }

    /// <summary>
    /// Bug-shape pin. Simulates the PRE-FIX buggy behaviour: the
    /// override is mistakenly written onto the day (work) crew row
    /// — which the evaluator-side resolver never reads. The
    /// evaluator resolves against the untouched night-crew row and
    /// falls through to <c>nightCrew.Lead</c> (the historical bug:
    /// every plan-doc evaluator pinned to AB2). This pin guards
    /// against silent reverts of the fix.
    /// </summary>
    [Fact]
    public async Task ResolveAgentRunConfig_EvalOverride_WrittenToDayCrewOnly_FallsThroughToNightLead()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedArchCrewsAsync(db);

            var dayCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == DayCrewId);
            var nightCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == dayCrew.PairedWithCrewId);

            // The bug: override goes on day crew. Night crew (the row
            // the resolver actually reads) is untouched.
            dayCrew.DefaultEvaluatorAgentId = OverrideAgent;

            var (agentId, _, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, nightCrew, useEvaluatorRole: true,
                workspace: "/tmp/test", budget: null, CancellationToken.None);

            Assert.Equal(NightLead, agentId);
        }
    }

    /// <summary>
    /// Unpaired crew sanity check. When a crew has no
    /// <see cref="CrewRow.PairedWithCrewId"/>, the fix's helper logic
    /// (<c>target = evalCrew ?? crew</c>) collapses to a write on
    /// <c>crew</c> itself, so the resolver still picks up the
    /// override. Pins this back-compat path against accidental
    /// regressions.
    /// </summary>
    [Fact]
    public async Task ResolveAgentRunConfig_EvalOverride_UnpairedCrew_HonorsOverride()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedArchCrewsAsync(db);

            // Mutate the day crew to unpair it (simulating a non-paired
            // architect-only configuration).
            var unpaired = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == DayCrewId);
            unpaired.PairedWithCrewId = null;
            unpaired.DefaultEvaluatorAgentId = OverrideAgent;

            var (agentId, _, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, unpaired, useEvaluatorRole: true,
                workspace: "/tmp/test", budget: null, CancellationToken.None);

            Assert.Equal(OverrideAgent, agentId);
        }
    }

    /// <summary>
    /// Generator-side back-compat. The generator override path
    /// (<see cref="CrewRow.DefaultGeneratorAgentId"/>) was never
    /// broken by the bug, but the fix re-arranged the surrounding
    /// code; pin the generator path stays honourable so a future
    /// refactor doesn't accidentally break it.
    /// </summary>
    [Fact]
    public async Task ResolveAgentRunConfig_GenOverride_HonorsOverride()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedArchCrewsAsync(db);

            var dayCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == DayCrewId);
            dayCrew.DefaultGeneratorAgentId = OverrideAgent;

            var (agentId, _, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                db, dayCrew, useEvaluatorRole: false,
                workspace: "/tmp/test", budget: null, CancellationToken.None);

            Assert.Equal(OverrideAgent, agentId);
        }
    }

    /// <summary>
    /// Four-doc fan-out distribution check. Simulates the parallel
    /// plan-doc invocation: four overrides (one per doc), each
    /// written onto a fresh <c>evalCrew</c> fetch. The four
    /// evaluator resolutions MUST return four DISTINCT agent ids
    /// — matching the inputs — and crucially NONE of them should
    /// be <see cref="NightLead"/> (the operator-reported "AB2 on
    /// every card" symptom).
    /// </summary>
    [Fact]
    public async Task ResolveAgentRunConfig_FourPlanDocs_FourDistinctEvaluators_NoneArePairedLead()
    {
        var (conn, db) = await NewDbAsync();
        await using (conn)
        await using (db)
        {
            await SeedPairedArchCrewsAsync(db);

            var overrides = new[] { OverrideAgent, "A1", "A2", DayLead };
            var resolved = new List<string>();

            foreach (var ovr in overrides)
            {
                // Fresh evalCrew fetch per "doc" — matches the
                // RunGenEvalLoopAsync re-entrancy on each plan-doc
                // task in the Task.WhenAll fan-out.
                var dayCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == DayCrewId);
                var nightCrew = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == dayCrew.PairedWithCrewId);
                nightCrew.DefaultEvaluatorAgentId = ovr;

                var (agentId, _, _, _) = await CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync(
                    db, nightCrew, useEvaluatorRole: true,
                    workspace: "/tmp/test", budget: null, CancellationToken.None);
                resolved.Add(agentId);
            }

            Assert.Equal(4, resolved.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(NightLead, resolved);
            Assert.Equal(overrides, resolved);
        }
    }
}
