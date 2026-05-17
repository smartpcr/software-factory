// -----------------------------------------------------------------------
// <copyright file="Phase47PairDiversityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md spec Phase 47 — pair-diversity for parallel
/// deliverables. Pure-helper tests for <see cref="PairDiversityResolver"/>
/// + <see cref="BusyAgentsHelper"/>; the wiring into
/// <see cref="Forge.Api.Services.CrewQueueWorker"/> and
/// <see cref="Forge.Api.Services.Stories.CrewStoryArtifactStrategy"/>
/// is exercised by the existing
/// <see cref="Phase39PairDiversityTests"/> suite (model diversity) and
/// the integration coverage in
/// <see cref="Phase16ExecutionTests"/>.
/// </summary>
public class Phase47PairDiversityTests
{
    private static readonly string[] FourMembers = ["m1", "m2", "m3", "m4"];

    // ── PairDiversityResolver.PickGenerator ────────────────────────────────

    [Fact]
    public void PickGenerator_skips_busy_members_and_returns_first_free()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal) { "m1" };

        var (id, fellBack) = PairDiversityResolver.PickGenerator(
            FourMembers, busy, lead: "L", seed: 0);

        Assert.NotNull(id);
        Assert.NotEqual("m1", id);
        Assert.False(fellBack);
    }

    [Fact]
    public void PickGenerator_returns_null_when_all_members_busy_and_no_lead()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal) { "m1", "m2", "m3", "m4" };

        var (id, fellBack) = PairDiversityResolver.PickGenerator(
            FourMembers, busy, lead: null, seed: 0);

        // Q47.3 — explicit skip rather than silent share.
        Assert.Null(id);
        Assert.False(fellBack);
    }

    [Fact]
    public void PickGenerator_returns_null_when_all_members_busy_even_with_lead()
    {
        // Q47.3 implication — the lead is NOT a fallback for the busy
        // case; only for the off-shift case (empty roster). Falling back
        // to the lead here would re-introduce the serialisation bug
        // (two parallel claims both shadow-assigned to the lead).
        var busy = new HashSet<string>(StringComparer.Ordinal) { "m1", "m2", "m3", "m4" };

        var (id, fellBack) = PairDiversityResolver.PickGenerator(
            FourMembers, busy, lead: "L", seed: 0);

        Assert.Null(id);
        Assert.False(fellBack);
    }

    [Fact]
    public void PickGenerator_falls_back_to_lead_when_roster_empty()
    {
        // Empty on-shift roster (Q18.3 case — Sunday or off-band). Lead
        // is the only fallback; busy-filter doesn't apply because there
        // is nothing to filter.
        var busy = new HashSet<string>(StringComparer.Ordinal);

        var (id, fellBack) = PairDiversityResolver.PickGenerator(
            Array.Empty<string>(), busy, lead: "L", seed: 0);

        Assert.Equal("L", id);
        Assert.True(fellBack);
    }

    [Fact]
    public void PickGenerator_returns_null_on_empty_roster_and_null_lead()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);

        var (id, fellBack) = PairDiversityResolver.PickGenerator(
            Array.Empty<string>(), busy, lead: null, seed: 0);

        Assert.Null(id);
        Assert.False(fellBack);
    }

    [Fact]
    public void PickGenerator_two_calls_with_mutation_pick_distinct_members()
    {
        // Simulate a single tick that picks two rows. Caller is expected
        // to mutate `busy` after each successful pick so the next pick
        // sees the newly-busy agent.
        var busy = new HashSet<string>(StringComparer.Ordinal);

        var (a, _) = PairDiversityResolver.PickGenerator(FourMembers, busy, lead: null, seed: 0);
        Assert.NotNull(a);
        busy.Add(a!);

        var (b, _) = PairDiversityResolver.PickGenerator(FourMembers, busy, lead: null, seed: 0);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
    }

    // ── PairDiversityResolver.PickEvaluator ────────────────────────────────

    [Fact]
    public void PickEvaluator_prefers_default_when_free()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);

        var (id, fellBackToDefault) = PairDiversityResolver.PickEvaluator(
            evaluatorRoster: FourMembers,
            busyAgentIds: busy,
            defaultEvalAgentId: "DefEval",
            generatorAgentId: "Gen");

        Assert.Equal("DefEval", id);
        Assert.True(fellBackToDefault);
    }

    [Fact]
    public void PickEvaluator_skips_default_when_busy_and_picks_next()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal) { "DefEval" };

        var (id, fellBackToDefault) = PairDiversityResolver.PickEvaluator(
            evaluatorRoster: FourMembers,
            busyAgentIds: busy,
            defaultEvalAgentId: "DefEval",
            generatorAgentId: "Gen");

        Assert.NotNull(id);
        Assert.NotEqual("DefEval", id);
        Assert.False(fellBackToDefault);
    }

    [Fact]
    public void PickEvaluator_excludes_generator_to_satisfy_pair_diversity()
    {
        // The generator is m1; evaluator pool happens to also include m1.
        // Resolver must skip m1 even when no busy filter would catch it.
        var busy = new HashSet<string>(StringComparer.Ordinal);

        var (id, _) = PairDiversityResolver.PickEvaluator(
            evaluatorRoster: FourMembers,
            busyAgentIds: busy,
            defaultEvalAgentId: null,
            generatorAgentId: "m1");

        Assert.NotEqual("m1", id);
        Assert.Contains(id, FourMembers);
    }

    [Fact]
    public void PickEvaluator_returns_null_when_only_candidate_equals_generator()
    {
        // Standalone crew with exactly one member; the only roster
        // candidate IS the generator. Resolver returns null so the caller
        // can decide (the worker's wrapper falls back to the legacy
        // DefaultEvaluatorAgentId chain — best-effort per Q47.3).
        var busy = new HashSet<string>(StringComparer.Ordinal);

        var (id, _) = PairDiversityResolver.PickEvaluator(
            evaluatorRoster: new[] { "m1" },
            busyAgentIds: busy,
            defaultEvalAgentId: null,
            generatorAgentId: "m1");

        Assert.Null(id);
    }

    [Fact]
    public void PickEvaluator_returns_null_when_every_candidate_busy()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal) { "DefEval", "m1", "m2", "m3", "m4" };

        var (id, _) = PairDiversityResolver.PickEvaluator(
            evaluatorRoster: FourMembers,
            busyAgentIds: busy,
            defaultEvalAgentId: "DefEval",
            generatorAgentId: "Gen");

        Assert.Null(id);
    }

    [Fact]
    public void PickEvaluator_throws_on_empty_generator_id()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            PairDiversityResolver.PickEvaluator(
                evaluatorRoster: FourMembers,
                busyAgentIds: busy,
                defaultEvalAgentId: "DefEval",
                generatorAgentId: string.Empty));
    }

    // ── BusyAgentsHelper ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadBusyAgentsAsync_returns_only_open_invocations()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Seed three usage rows: two open, one closed. Use distinct
        // agent IDs so the busy set is unambiguous. Id is auto-incremented
        // (long PK), so we omit it.
        var now = DateTimeOffset.UtcNow;
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-A", CrewId = "crew-X",
            TaskId = "t1", Model = "m", StartedAt = now.AddMinutes(-1), EndedAt = null,
            Status = "running",
        });
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-B", CrewId = "crew-X",
            TaskId = "t2", Model = "m", StartedAt = now.AddMinutes(-1), EndedAt = null,
            Status = "running",
        });
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-C", CrewId = "crew-X",
            TaskId = "t3", Model = "m", StartedAt = now.AddMinutes(-2),
            EndedAt = now.AddMinutes(-1),
            Status = "completed",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var busy = await BusyAgentsHelper.LoadBusyAgentsAsync(control, default);

        Assert.Contains("agent-A", busy);
        Assert.Contains("agent-B", busy);
        Assert.DoesNotContain("agent-C", busy);
    }

    [Fact]
    public async Task ReapStaleAsync_closes_rows_older_than_24h()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var now = DateTimeOffset.UtcNow;
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-stale", CrewId = "crew-X",
            TaskId = "old", Model = "m",
            StartedAt = now - TimeSpan.FromHours(48),
            EndedAt = null,
            Status = "running",
        });
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-fresh", CrewId = "crew-X",
            TaskId = "new", Model = "m",
            StartedAt = now - TimeSpan.FromMinutes(5),
            EndedAt = null,
            Status = "running",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reaped = await BusyAgentsHelper.ReapStaleAsync(control);

        Assert.Equal(1, reaped);

        // Verify only the stale row was closed; the fresh one still has
        // null EndedAt and would still register as busy.
        var stale = await control.AgentUsage.AsNoTracking().FirstAsync(u => u.AgentId == "agent-stale");
        Assert.NotNull(stale.EndedAt);
        var fresh = await control.AgentUsage.AsNoTracking().FirstAsync(u => u.AgentId == "agent-fresh");
        Assert.Null(fresh.EndedAt);

        var busy = await BusyAgentsHelper.LoadBusyAgentsAsync(control, default);
        Assert.DoesNotContain("agent-stale", busy);
        Assert.Contains("agent-fresh", busy);
    }

    [Fact]
    public async Task ReapAllOpenOnBootAsync_closes_every_open_row_regardless_of_age()
    {
        // Phase 48 follow-up — by definition no invocation can be in flight
        // at boot, so EVERY open row is orphaned. The 24h cap of
        // ReapStaleAsync would leave fresh-orphaned rows behind and the
        // pair-diversity picker would filter their agents out as busy,
        // freezing plan-doc loops.
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var now = DateTimeOffset.UtcNow;

        // Two open rows — one fresh (5 min old), one stale (48 h old).
        // Both must be closed by ReapAllOpenOnBootAsync.
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-fresh-orphan", CrewId = "crew-X",
            TaskId = "t-fresh", Model = "m",
            StartedAt = now - TimeSpan.FromMinutes(5),
            EndedAt = null,
            Status = "running",
        });
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-stale-orphan", CrewId = "crew-X",
            TaskId = "t-stale", Model = "m",
            StartedAt = now - TimeSpan.FromHours(48),
            EndedAt = null,
            Status = "running",
        });
        // Plus one already-closed row that the reaper must NOT touch.
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-completed", CrewId = "crew-X",
            TaskId = "t-done", Model = "m",
            StartedAt = now - TimeSpan.FromMinutes(10),
            EndedAt = now - TimeSpan.FromMinutes(8),
            Status = "completed",
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reaped = await BusyAgentsHelper.ReapAllOpenOnBootAsync(control);

        Assert.Equal(2, reaped);

        var bootBusy = await BusyAgentsHelper.LoadBusyAgentsAsync(control, default);
        Assert.Empty(bootBusy);
    }
}
