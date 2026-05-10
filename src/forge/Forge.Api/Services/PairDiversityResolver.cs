// -----------------------------------------------------------------------
// <copyright file="PairDiversityResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Spec Phase 47 — picks (generator, evaluator) agent pairs for parallel
/// deliverables on the same crew so two concurrent claims never share an
/// agent on either side. The resolver is a pure helper: callers supply
/// the candidate roster + the currently-busy set + the crew's
/// fall-back lead, and get back the next agent to assign or a
/// well-defined "no free pair member" signal.
///
/// <para>Decision (Q47.3): no-free-pair fallback is an explicit SKIP, not
/// a silent share. A claim that can't form a diverse pair waits for the
/// next tick instead of re-using a busy agent — that's the bug operator
/// hit on the failover-cluster:XRAFT spawn (4 plan-docs serialised on
/// one evaluator).</para>
///
/// <para>Decision (Q47.2): the lead is a fallback for the GENERATOR only,
/// and only when no member is on-shift (Q18.3). For the evaluator, the
/// lead can be picked from the roster (if it's listed) but isn't a
/// special fallback — falling back to a busy default would re-introduce
/// the serialisation bug.</para>
/// </summary>
internal static class PairDiversityResolver
{
    /// <summary>
    /// Pick the next available generator agent for a claim.
    /// </summary>
    /// <param name="onShiftRoster">
    /// Candidate generator agents already filtered through the schedule
    /// gate (Phase 18.6). Order is the caller's preferred round-robin
    /// order — e.g. by member sort then last-active.
    /// </param>
    /// <param name="busyAgentIds">
    /// Set of agent IDs with a currently-active <c>agent_usage</c> row.
    /// Tested for set-membership only; mutability is the caller's choice.
    /// </param>
    /// <param name="lead">
    /// Crew lead's agent id, used as a Q18.3 fall-back when no roster
    /// member is on-shift. May be <c>null</c> on a leaderless crew.
    /// </param>
    /// <param name="seed">
    /// Round-robin seed. Caller passes <c>Environment.TickCount</c> in
    /// production; tests pass a deterministic value. Two calls in the
    /// same tick with the same roster + same busy set return the same
    /// agent — caller must mutate <paramref name="busyAgentIds"/> after
    /// a successful pick to keep batch ticks producing distinct claims.
    /// </param>
    /// <returns>
    /// <c>(AgentId, FellBackToLead)</c>. <c>AgentId</c> is non-null when
    /// a free agent (or the lead fallback) was found; null when no
    /// roster member is free AND the lead is null. The caller's
    /// responsibility to translate null into a <c>ClaimSkip</c>.
    /// </returns>
    public static (string? AgentId, bool FellBackToLead) PickGenerator(
        IReadOnlyList<string> onShiftRoster,
        IReadOnlyCollection<string> busyAgentIds,
        string? lead,
        int seed)
    {
        if (onShiftRoster is null) throw new ArgumentNullException(nameof(onShiftRoster));
        if (busyAgentIds is null) throw new ArgumentNullException(nameof(busyAgentIds));

        // Phase 18.6 path — when no member is on-shift the lead picks up
        // (Q18.3 24×7 exemption). Busy-filter is irrelevant here because
        // there's no roster to filter.
        if (onShiftRoster.Count == 0)
        {
            return string.IsNullOrEmpty(lead)
                ? (null, false)
                : (lead, true);
        }

        // Filter by busy set; preserve roster order so the round-robin
        // is deterministic against the seed.
        var free = new List<string>(onShiftRoster.Count);
        foreach (var member in onShiftRoster)
        {
            if (busyAgentIds.Contains(member)) continue;
            free.Add(member);
        }

        if (free.Count == 0)
        {
            // Q47.3 — no free member. The caller skips with
            // `no-free-pair-member`; the lead is NOT a fallback for
            // the busy-but-on-shift case (only for the off-shift case
            // above). Returning null here is the right signal to skip.
            return (null, false);
        }

        // Deterministic round-robin from the seed. Math.Abs guards
        // against int.MinValue (TickCount can wrap negative).
        var idx = (int)((uint)seed % (uint)free.Count);
        return (free[idx], false);
    }

    /// <summary>
    /// Pick the next available evaluator agent for a claim. Order
    /// preference is: <paramref name="defaultEvalAgentId"/> first (when
    /// present and free), then the rest of the roster in supplied order.
    /// </summary>
    /// <param name="evaluatorRoster">
    /// Candidate evaluator agents (typically the paired crew's members,
    /// or the same crew's members minus the picked generator for a
    /// standalone crew). The default evaluator agent does NOT need to
    /// be in this list — the resolver inserts it at the head when
    /// non-null.
    /// </param>
    /// <param name="busyAgentIds">See <see cref="PickGenerator"/>.</param>
    /// <param name="defaultEvalAgentId">
    /// Crew's preferred evaluator (<c>CrewRow.DefaultEvaluatorAgentId</c>).
    /// May be <c>null</c> when the crew was created without the field.
    /// </param>
    /// <param name="generatorAgentId">
    /// The agent already claimed as the generator for this row. Excluded
    /// from the evaluator pool (Phase 39 gen ≠ eval invariant). Required
    /// — empty / null is rejected with <see cref="ArgumentException"/>
    /// because picking an evaluator without knowing the generator can't
    /// safely satisfy diversity.
    /// </param>
    /// <returns>
    /// <c>(AgentId, FellBackToDefault)</c>. <c>AgentId</c> is non-null
    /// when a free evaluator was found; null when every candidate
    /// (default + roster, minus the generator) is busy. The caller
    /// translates null into a <c>ClaimSkip</c>.
    /// </returns>
    public static (string? AgentId, bool FellBackToDefault) PickEvaluator(
        IReadOnlyList<string> evaluatorRoster,
        IReadOnlyCollection<string> busyAgentIds,
        string? defaultEvalAgentId,
        string generatorAgentId)
    {
        if (evaluatorRoster is null) throw new ArgumentNullException(nameof(evaluatorRoster));
        if (busyAgentIds is null) throw new ArgumentNullException(nameof(busyAgentIds));
        if (string.IsNullOrEmpty(generatorAgentId))
        {
            throw new ArgumentException(
                "generatorAgentId must be non-empty so the evaluator picker can enforce gen != eval.",
                nameof(generatorAgentId));
        }

        // Build the candidate order: default first (when non-null and
        // not the same as the generator), then the rest of the roster
        // in supplied order, deduplicated. The default's free-ness is
        // tested below same as any other candidate.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(evaluatorRoster.Count + 1);

        if (!string.IsNullOrEmpty(defaultEvalAgentId)
            && !string.Equals(defaultEvalAgentId, generatorAgentId, StringComparison.Ordinal)
            && seen.Add(defaultEvalAgentId))
        {
            ordered.Add(defaultEvalAgentId);
        }

        foreach (var candidate in evaluatorRoster)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (string.Equals(candidate, generatorAgentId, StringComparison.Ordinal)) continue;
            if (!seen.Add(candidate)) continue;
            ordered.Add(candidate);
        }

        // First non-busy wins. The "fell back to default" flag is true
        // only when we picked the very first ordered candidate AND it
        // was the supplied default — useful for telemetry without
        // needing to compare strings on the call site.
        for (var i = 0; i < ordered.Count; i++)
        {
            var candidate = ordered[i];
            if (busyAgentIds.Contains(candidate)) continue;
            var fellBackToDefault =
                i == 0
                && !string.IsNullOrEmpty(defaultEvalAgentId)
                && string.Equals(candidate, defaultEvalAgentId, StringComparison.Ordinal);
            return (candidate, fellBackToDefault);
        }

        return (null, false);
    }
}
