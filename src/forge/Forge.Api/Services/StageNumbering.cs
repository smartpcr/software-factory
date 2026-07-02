// -----------------------------------------------------------------------
// <copyright file="StageNumbering.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Canonical <c>{phaseNum}.{stageNum}</c> labelling for a spawn's workstreams —
/// the server-side mirror of the UI's <c>computeStageNumbers</c>
/// (<c>Forge.UI/src/swarm/stageNumbering.ts</c>), so a chat alert names a stage
/// the same way the operator sees it on the swarm board (e.g. <c>1.2-e2e</c>).
///
/// <para>Rules: phase order = first-seen order of <c>PhaseAnchor</c> over the
/// (Sort-ordered) workstreams; only impl rows take a positional
/// <c>{phase}.{stage}</c> slot; a trailing qa-e2e row inherits
/// <c>{implSibling}-e2e</c> (matched by stripping the <c>/e2e</c> anchor suffix)
/// and never bumps the next impl stage's number.</para>
/// </summary>
public static class StageNumbering
{
    private const string DefaultPhase = "__workstreams__";
    private const string QaE2eCategory = "qa-e2e";
    private const string QaE2eAnchorSuffix = "/e2e";

    /// <summary>The subset of workstream fields the numbering needs.</summary>
    public sealed record StageWs(string Id, string? PhaseAnchor, string? PlanAnchor, string? Category);

    /// <summary>
    /// Map each workstream id → its <c>{phase}.{stage}</c> (or <c>{impl}-e2e</c>)
    /// label. <paramref name="workstreams"/> MUST be in <c>Sort</c> order (the
    /// same order the plan and board use). Ids absent from the map have no
    /// resolvable label (orphan qa-e2e); callers fall back to a default.
    /// </summary>
    public static Dictionary<string, string> Compute(IReadOnlyList<StageWs> workstreams)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var phaseOrder = new List<string>();

        foreach (var w in workstreams)
        {
            if (IsQaE2e(w)) continue; // numbered via its impl sibling below
            var pa = string.IsNullOrEmpty(w.PhaseAnchor) ? DefaultPhase : w.PhaseAnchor!;
            if (!buckets.TryGetValue(pa, out var list))
            {
                list = new List<string>();
                buckets[pa] = list;
                phaseOrder.Add(pa);
            }

            list.Add(w.Id);
        }

        for (var gi = 0; gi < phaseOrder.Count; gi++)
        {
            var ids = buckets[phaseOrder[gi]];
            for (var si = 0; si < ids.Count; si++)
            {
                labels[ids[si]] = $"{gi + 1}.{si + 1}";
            }
        }

        // qa-e2e rows inherit "{implSiblingNum}-e2e" (matched on the base anchor,
        // tolerant of a delta-split "@{hash}" suffix on the impl row).
        var implByAnchor = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in workstreams)
        {
            if (IsQaE2e(w)) continue;
            if (!string.IsNullOrEmpty(w.PlanAnchor)) implByAnchor[BaseAnchor(w.PlanAnchor!)] = w.Id;
        }

        foreach (var w in workstreams)
        {
            if (!IsQaE2e(w)) continue;
            if (string.IsNullOrEmpty(w.PlanAnchor) || !w.PlanAnchor!.EndsWith(QaE2eAnchorSuffix, StringComparison.Ordinal)) continue;
            var implAnchor = BaseAnchor(w.PlanAnchor[..^QaE2eAnchorSuffix.Length]);
            if (implByAnchor.TryGetValue(implAnchor, out var implId) && labels.TryGetValue(implId, out var implNum))
            {
                labels[w.Id] = $"{implNum}-e2e";
            }
        }

        return labels;
    }

    private static bool IsQaE2e(StageWs w) => string.Equals(w.Category, QaE2eCategory, StringComparison.Ordinal);

    private static string BaseAnchor(string anchor)
    {
        var at = anchor.LastIndexOf('@');
        return at > 0 ? anchor[..at] : anchor;
    }
}
