// -----------------------------------------------------------------------
// <copyright file="WorkstreamWorktreeSlug.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text;
using Forge.Api.Persistence;

/// <summary>
/// Phase 49 follow-up — compute the short, semantic worktree directory
/// name for an engineer workstream. The legacy convention
/// (<c>spawn-{branch-slug}</c>) baked the full
/// <c>ws/{product}-{story}/phase-{phaseSlug}-stage-{stageSlug}</c> branch
/// into the path and routinely produced 100+ character directory names,
/// which on Windows blew past <c>MAX_PATH</c> as soon as
/// <c>cargo build</c> wrote its <c>target/debug/.fingerprint/…</c> tree.
///
/// <para>The new convention is
/// <c>stage-{phaseNum}.{stageNum}-{anchorSlug}</c> where
/// <c>{phaseNum}.{stageNum}</c> is the workstream's positional address in
/// the plan and <c>{anchorSlug}</c> is the workstream's
/// <see cref="WorkstreamRow.PlanAnchor"/> with the leading <c>phase-</c>
/// stripped and <c>/</c> replaced by <c>-</c>. Example:
/// <c>stage-1.1-project-scaffolding-stage-cargo-workspace-and-crate-layout</c>
/// (~70 chars vs. the legacy
/// <c>spawn-ws-failover-cluster-XRAFT-phase-project-scaffolding-stage-cargo-workspace-and-crate-layout</c>
/// at ~104 chars).</para>
///
/// <para>Falls back to the legacy <c>spawn-{branch-slug}</c> shape when the
/// workstream lacks a <see cref="WorkstreamRow.PlanAnchor"/> (legacy
/// rows that predate the Phase 42 reconciler, or hand-created workstreams
/// from the <c>POST /api/workstreams</c> endpoint). The fallback preserves
/// the per-branch uniqueness invariant; only the readability suffers.</para>
///
/// <para>Phase/stage numbering matches the swarm-control phase-board UI
/// (<c>SwarmMasterPanel.stageNumOf</c>) exactly — group by
/// <see cref="WorkstreamRow.PhaseAnchor"/> preserving first-encounter
/// order, position-within-group is the stage number. The same caller-side
/// computation is used so operators see one consistent set of numbers
/// across the UI and the filesystem.</para>
/// </summary>
public static class WorkstreamWorktreeSlug
{
    /// <summary>
    /// Compute the worktree directory name (the leaf segment under
    /// <c>{cloneRoot}/.worktree/</c>) for <paramref name="workstream"/>.
    /// <paramref name="sameSpawnWorkstreams"/> must contain every
    /// workstream attached to the same <see cref="WorkstreamRow.StorySpawnId"/>
    /// (the caller queries those and passes them in so this helper stays
    /// pure / DB-free); the iteration order determines the
    /// phase / stage numbering, so callers should sort by
    /// <see cref="WorkstreamRow.Sort"/> ascending before invoking.
    /// </summary>
    public static string Compute(WorkstreamRow workstream, IReadOnlyList<WorkstreamRow> sameSpawnWorkstreams)
    {
        if (workstream is null) throw new ArgumentNullException(nameof(workstream));

        // Plan-anchored workstreams take the new short name.
        if (!string.IsNullOrWhiteSpace(workstream.PlanAnchor))
        {
            var (phaseNum, stageNum) = ComputePosition(workstream, sameSpawnWorkstreams);
            var anchorSlug = AnchorSlug(workstream.PlanAnchor!);
            return $"stage-{phaseNum}.{stageNum}-{anchorSlug}";
        }

        // Legacy fallback — no plan anchor → use the branch slug.
        return $"spawn-{SanitizeBranchSlug(workstream.Branch ?? workstream.Id)}";
    }

    /// <summary>
    /// Strip the leading <c>phase-</c> token (if present) and replace
    /// path separators so the anchor becomes a single filesystem-safe
    /// segment. Idempotent if the anchor doesn't start with <c>phase-</c>.
    /// </summary>
    private static string AnchorSlug(string planAnchor)
    {
        var s = planAnchor;
        if (s.StartsWith("phase-", StringComparison.Ordinal))
        {
            s = s["phase-".Length..];
        }
        return s.Replace('/', '-').Replace('\\', '-');
    }

    /// <summary>
    /// Walk <paramref name="sameSpawnWorkstreams"/> in
    /// <see cref="WorkstreamRow.Sort"/> order, bucket by
    /// <see cref="WorkstreamRow.PhaseAnchor"/> (preserving first-encounter
    /// order), and return the 1-based <c>(phaseNum, stageNum)</c> of
    /// <paramref name="target"/>. Rows without a PhaseAnchor land in
    /// a synthetic <c>__workstreams__</c> bucket so the numbering is
    /// always defined.
    /// </summary>
    private static (int PhaseNum, int StageNum) ComputePosition(
        WorkstreamRow target, IReadOnlyList<WorkstreamRow> sameSpawnWorkstreams)
    {
        const string SyntheticPhase = "__workstreams__";

        var phaseOrder = new List<string>();
        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var w in sameSpawnWorkstreams.OrderBy(w => w.Sort))
        {
            var pa = string.IsNullOrWhiteSpace(w.PhaseAnchor) ? SyntheticPhase : w.PhaseAnchor!;
            if (!buckets.TryGetValue(pa, out var list))
            {
                list = new List<string>();
                buckets[pa] = list;
                phaseOrder.Add(pa);
            }
            list.Add(w.Id);
        }

        var targetPhase = string.IsNullOrWhiteSpace(target.PhaseAnchor) ? SyntheticPhase : target.PhaseAnchor!;
        var phaseIdx = phaseOrder.IndexOf(targetPhase);
        if (phaseIdx < 0)
        {
            // target wasn't in the same-spawn list — caller error, but
            // fall back to ws-{Sort} so the path is still unique.
            return (1, Math.Max(1, target.Sort));
        }
        var stageIdx = buckets[targetPhase].IndexOf(target.Id);
        if (stageIdx < 0)
        {
            return (phaseIdx + 1, Math.Max(1, target.Sort));
        }
        return (phaseIdx + 1, stageIdx + 1);
    }

    /// <summary>
    /// Same logic as <c>RealStoryGitOps.SanitizeBranchSlug</c> —
    /// duplicated locally so this helper has no I/O dependencies.
    /// </summary>
    private static string SanitizeBranchSlug(string branch)
    {
        var sb = new StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }
}
