// -----------------------------------------------------------------------
// <copyright file="Phase49LatePairAssignmentTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 49 follow-up — agent pair assignment for engineer workstreams
/// must:
/// <list type="number">
///   <item><b>Stay empty until the impl task is about to start</b> —
///     the orchestrator's claim sweep flips Status to <c>active</c> but
///     leaves <c>AgentsJson="[]"</c>; the pair is picked LATE inside
///     the parallel task right before the strategy invocation, with a
///     fresh busy-set snapshot.</item>
///   <item><b>Use random selection</b> — sequential claims against the
///     same on-shift roster pick DIFFERENT pairs (statistically; not
///     deterministic round-robin).</item>
///   <item><b>Never assign a busy agent</b> — a generator or evaluator
///     already in <c>busyAgents</c> is filtered out.</item>
///   <item><b>Respect gen ≠ eval</b> — Phase 39 invariant.</item>
/// </list>
///
/// <para>These tests exercise <see cref="PairDiversityResolver"/>
/// directly so the contract stays pinned regardless of how the
/// orchestrator wires the call. The orchestrator-side integration
/// (sequential claim → late assignment → late agent ID written to
/// AgentsJson) is covered by the existing
/// <c>Phase28OrchestratorTests</c> harness; this file pins the picker
/// math.</para>
/// </summary>
public class Phase49LatePairAssignmentTests
{
    private static readonly IReadOnlyList<string> FourMemberRoster = new[] { "A1", "A2", "A3", "A4" };
    private static readonly IReadOnlyList<string> EightMemberRoster = new[] { "G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8" };

    /// <summary>
    /// Headline regression: two sequential claims against the same
    /// roster should pick DIFFERENT generators when the picker is
    /// invoked with a unique seed per claim (workstream Sort) and the
    /// previous claim's agent was added to the busy set. This is the
    /// operator's "1.2 → 1.3 should NOT both pick the same agent"
    /// requirement.
    /// </summary>
    [Fact]
    public void PickGenerator_SequentialClaims_PickDifferentAgentsWhenBusySetIsMutated()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);

        // Claim #1
        var (gen1, _) = PairDiversityResolver.PickGenerator(FourMemberRoster, busy, lead: null, seed: 42);
        Assert.NotNull(gen1);
        busy.Add(gen1!); // simulate the orchestrator's lock-then-mutate step

        // Claim #2 — same roster, busy now contains gen1
        var (gen2, _) = PairDiversityResolver.PickGenerator(FourMemberRoster, busy, lead: null, seed: 43);
        Assert.NotNull(gen2);
        Assert.NotEqual(gen1, gen2);
    }

    /// <summary>
    /// Four sequential claims against a 4-member roster should each
    /// pick a different agent — exhausts the roster with full diversity.
    /// </summary>
    [Fact]
    public void PickGenerator_NSequentialClaims_ExhaustsTheRoster()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<string>();

        for (var i = 0; i < FourMemberRoster.Count; i++)
        {
            var (agent, _) = PairDiversityResolver.PickGenerator(FourMemberRoster, busy, lead: null, seed: i + 1);
            Assert.NotNull(agent);
            Assert.DoesNotContain(agent, picked);
            picked.Add(agent!);
            busy.Add(agent!);
        }

        Assert.Equal(FourMemberRoster.Count, picked.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// When every roster member is busy AND no lead is supplied,
    /// PickGenerator returns null so the orchestrator can SKIP the
    /// claim (Q47.3) rather than silently doubling up.
    /// </summary>
    [Fact]
    public void PickGenerator_ReturnsNull_WhenEveryMemberBusyAndNoLead()
    {
        var busy = new HashSet<string>(FourMemberRoster, StringComparer.Ordinal);
        var (agent, fellBackToLead) = PairDiversityResolver.PickGenerator(FourMemberRoster, busy, lead: null, seed: 7);
        Assert.Null(agent);
        Assert.False(fellBackToLead);
    }

    /// <summary>
    /// PickEvaluator with a non-zero seed shuffles the post-default
    /// candidate slice. Across many invocations with distinct seeds we
    /// should hit at least 3 of 4 evaluator candidates — Pulls the
    /// statistical guarantee away from "first non-busy wins always
    /// returns the same agent".
    /// </summary>
    [Fact]
    public void PickEvaluator_WithRandomSeed_DistributesAcrossRoster()
    {
        // 8 roster members so the picker has plenty of choices; busy
        // set is empty so every candidate is free. defaultEvalAgentId
        // unset so the shuffle covers the whole post-default slice.
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 1; i <= 200; i++)
        {
            var (agent, _) = PairDiversityResolver.PickEvaluator(
                EightMemberRoster,
                busy,
                defaultEvalAgentId: null,
                generatorAgentId: "outside-roster", // not in eval set, so doesn't filter
                seed: i);
            Assert.NotNull(agent);
            hits[agent!] = hits.TryGetValue(agent!, out var n) ? n + 1 : 1;
        }

        // We don't require perfect uniformity, but every roster member
        // should have been picked at least once across 200 distinct
        // seeds. If the shuffle is broken (e.g. seed ignored), only one
        // member would ever win and this assertion fires.
        var coverage = hits.Count;
        Assert.True(coverage >= 6,
            $"Expected at least 6 of 8 roster members to be picked across 200 random seeds; only saw {coverage}: {string.Join(", ", hits.Keys)}");
    }

    /// <summary>
    /// Even with the random seed, the default evaluator keeps
    /// first-dibs when it's free — operator's per-crew preferred
    /// evaluator preference isn't overridden by the shuffle. Only the
    /// fallback when the default is busy gets randomized.
    /// </summary>
    [Fact]
    public void PickEvaluator_DefaultEvaluator_StillWinsWhenFree()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var (agent, fellBackToDefault) = PairDiversityResolver.PickEvaluator(
            EightMemberRoster,
            busy,
            defaultEvalAgentId: "DEFAULT_EVAL",
            generatorAgentId: "G1",
            seed: 12345);
        Assert.Equal("DEFAULT_EVAL", agent);
        Assert.True(fellBackToDefault);
    }

    /// <summary>
    /// PickEvaluator with the generator in the roster excludes it from
    /// candidates — Phase 39 gen ≠ eval invariant. Across many seeds
    /// the generator should NEVER come out as the evaluator.
    /// </summary>
    [Fact]
    public void PickEvaluator_NeverPicksGenerator_EvenWithSeedShuffle()
    {
        var busy = new HashSet<string>(StringComparer.Ordinal);
        const string gen = "G3";

        for (var i = 1; i <= 100; i++)
        {
            var (agent, _) = PairDiversityResolver.PickEvaluator(
                EightMemberRoster,
                busy,
                defaultEvalAgentId: null,
                generatorAgentId: gen,
                seed: i);
            Assert.NotEqual(gen, agent);
        }
    }

    /// <summary>
    /// Random generator-side picks across many independent claims with
    /// distinct seeds should also distribute — paired with the
    /// evaluator-side shuffle test above this rules out "everyone gets
    /// the same pair" on either axis.
    /// </summary>
    [Fact]
    public void PickGenerator_WithRandomSeed_DistributesAcrossRoster()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 1; i <= 200; i++)
        {
            var busy = new HashSet<string>(StringComparer.Ordinal);
            var (agent, _) = PairDiversityResolver.PickGenerator(
                EightMemberRoster, busy, lead: null, seed: i);
            Assert.NotNull(agent);
            hits[agent!] = hits.TryGetValue(agent!, out var n) ? n + 1 : 1;
        }

        Assert.True(hits.Count >= 6,
            $"Expected at least 6 of 8 roster members to be picked across 200 distinct seeds; saw {hits.Count}: {string.Join(", ", hits.Keys)}");
    }
}
