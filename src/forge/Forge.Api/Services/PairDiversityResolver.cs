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
    /// <param name="lastActiveByAgent">
    /// Optional least-recently-used input: each candidate's
    /// <c>MAX(agent_usage.StartedAt)</c> as ISO-8601 text (see
    /// <see cref="BusyAgentsHelper.LoadLastActiveAsync"/>). When supplied,
    /// the free candidates are ordered <b>oldest-first</b> — an agent
    /// ABSENT from the map has never run and sorts first, otherwise the
    /// smaller (older) timestamp wins. The <paramref name="seed"/> shuffle
    /// breaks ties WITHIN an equal-recency bucket (e.g. a fresh roster
    /// where nobody has run) so concurrent / repeated claims still spread.
    /// When <c>null</c> the picker keeps the legacy pure seed round-robin.
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
        int seed,
        IReadOnlyDictionary<string, string>? lastActiveByAgent = null)
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

        // Least-recently-used selection (when the caller supplies recency).
        // Picks the free agent that has run least recently (or never),
        // breaking equal-recency ties with the seed shuffle.
        if (lastActiveByAgent is not null)
        {
            return (PickLeastRecentlyUsed(free, lastActiveByAgent, seed), false);
        }

        // Deterministic round-robin from the seed. Math.Abs guards
        // against int.MinValue (TickCount can wrap negative).
        var idx = (int)((uint)seed % (uint)free.Count);
        return (free[idx], false);
    }

    /// <summary>
    /// Pick the next available evaluator agent for a claim. Order
    /// preference is: <paramref name="defaultEvalAgentId"/> first (when
    /// present and free), then the rest of the roster — shuffled when
    /// <paramref name="seed"/> is provided so concurrent claims pick
    /// distinct evaluators rather than all landing on the same roster
    /// head.
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
    /// <param name="seed">
    /// Optional shuffling seed. When non-zero, the post-default roster
    /// is shuffled with a Fisher-Yates pass before "first non-busy
    /// wins" picks. The default evaluator (when supplied) still gets
    /// first dibs so the operator's per-crew preferred evaluator wins
    /// when it's free — only the fallback rotates. Pass
    /// <c>Random.Shared.Next()</c> in production for uniform
    /// randomization; tests pass a deterministic value.
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
        string generatorAgentId,
        int seed = 0,
        IReadOnlyDictionary<string, string>? lastActiveByAgent = null)
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

        // Materialise the post-default roster so we can shuffle it
        // without touching the default (which keeps first-dibs).
        var defaultCount = ordered.Count;
        foreach (var candidate in evaluatorRoster)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (string.Equals(candidate, generatorAgentId, StringComparison.Ordinal)) continue;
            if (!seen.Add(candidate)) continue;
            ordered.Add(candidate);
        }

        if (lastActiveByAgent is not null)
        {
            // Least-recently-used ordering of the WHOLE candidate list.
            // Primary key = recency (oldest / never-run first); the
            // operator-configured default evaluator is demoted to a
            // TIEBREAK that only wins WITHIN an equal-recency bucket
            // (decision 2026-05-25 — the evaluator rotates by LRU like the
            // generator instead of pinning to the default whenever free).
            // The seed shuffle breaks remaining ties (equal recency AND
            // equal default-ness, e.g. a fresh roster) — LINQ OrderBy is
            // stable, so the pre-shuffle survives as the final tiebreak.
            var shuffled = new List<string>(ordered);
            ShuffleInPlace(shuffled, seed);
            ordered = shuffled
                .OrderBy(a => lastActiveByAgent.TryGetValue(a, out var t) ? t : string.Empty, StringComparer.Ordinal)
                .ThenByDescending(a =>
                    !string.IsNullOrEmpty(defaultEvalAgentId)
                    && string.Equals(a, defaultEvalAgentId, StringComparison.Ordinal))
                .ToList();
        }
        else if (seed != 0 && ordered.Count > defaultCount + 1)
        {
            // Phase 49 follow-up — Fisher-Yates shuffle on the post-default
            // slice when a seed is provided. Without this, the picker
            // always returns the same fallback when the default is busy,
            // recreating the "everyone gets the same pair" bug for the
            // evaluator side that already plagued the generator side.
            var rng = new Random(seed);
            for (var i = ordered.Count - 1; i > defaultCount; i--)
            {
                var j = defaultCount + rng.Next(i - defaultCount + 1);
                (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
            }
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

    /// <summary>
    /// Order free candidates oldest-first and return the head. An agent
    /// absent from <paramref name="lastActiveByAgent"/> has never run and
    /// sorts before any timestamped agent (empty string &lt; any ISO date);
    /// otherwise the smaller (older) <c>StartedAt</c> wins. The seed shuffle
    /// is applied first so equal-recency agents (a fresh roster where every
    /// candidate is absent) still spread across concurrent / repeated
    /// claims rather than collapsing onto one — LINQ <c>OrderBy</c> is a
    /// stable sort, so the shuffle survives as the intra-bucket tiebreak.
    /// </summary>
    private static string PickLeastRecentlyUsed(
        IReadOnlyList<string> free,
        IReadOnlyDictionary<string, string> lastActiveByAgent,
        int seed)
    {
        var shuffled = new List<string>(free);
        ShuffleInPlace(shuffled, seed);
        var head = shuffled[0];
        var headKey = lastActiveByAgent.TryGetValue(head, out var ht) ? ht : string.Empty;
        for (var i = 1; i < shuffled.Count; i++)
        {
            var key = lastActiveByAgent.TryGetValue(shuffled[i], out var t) ? t : string.Empty;
            if (string.CompareOrdinal(key, headKey) < 0)
            {
                head = shuffled[i];
                headKey = key;
            }
        }

        return head;
    }

    /// <summary>
    /// In-place Fisher-Yates shuffle seeded deterministically. A
    /// <paramref name="seed"/> of 0 (or a list of &lt; 2 items) is a no-op
    /// so callers that want a stable order can opt out.
    /// </summary>
    private static void ShuffleInPlace(List<string> items, int seed)
    {
        if (seed == 0 || items.Count < 2) return;
        var rng = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
