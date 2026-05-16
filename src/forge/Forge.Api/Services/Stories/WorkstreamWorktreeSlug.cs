// -----------------------------------------------------------------------
// <copyright file="WorkstreamWorktreeSlug.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Compute the worktree directory name for an engineer workstream.
/// Canonical convention is
/// <c>stage-{phaseNum}.{stageNum}-{stageTitle}</c>, where
/// <c>{stageTitle}</c> is the stage portion of the workstream's
/// <see cref="WorkstreamRow.PlanAnchor"/> (with the leading
/// <c>stage-</c> stripped). Example: a workstream on
/// <c>phase-consensus-engine/stage-log-replication</c> at the third
/// stage of the third phase materialises as
/// <c>.worktree/stage-3.3-log-replication</c>.
///
/// <para>Rows without a <see cref="WorkstreamRow.PlanAnchor"/>
/// (hand-crafted via <c>POST /api/workstreams</c>, or pre-Phase-42
/// rows that never went through the reconciler) fall back to
/// <c>ws-{sanitizedId}</c>, matching the ad-hoc crew-queue
/// convention.</para>
///
/// <para>Phase/stage numbering matches the swarm-control phase-board UI
/// (<c>SwarmMasterPanel.stageNumOf</c>) exactly — group by
/// <see cref="WorkstreamRow.PhaseAnchor"/> preserving first-encounter
/// order, position-within-group is the stage number.</para>
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

        // Plan-anchored workstreams take the canonical short name.
        if (!string.IsNullOrWhiteSpace(workstream.PlanAnchor))
        {
            var (phaseNum, stageNum) = ComputePosition(workstream, sameSpawnWorkstreams);
            var stageTitle = StageTitleSlug(workstream.PlanAnchor!);
            return $"stage-{phaseNum}.{stageNum}-{stageTitle}";
        }

        // No plan anchor → use the crew-queue ad-hoc shape so the path
        // is still deterministic and unique per workstream id.
        return $"ws-{SanitizeBranchSlug(workstream.Branch ?? workstream.Id)}";
    }

    /// <summary>
    /// Convenience overload — loads the same-spawn siblings from
    /// <paramref name="db"/> and delegates to <see cref="Compute"/>. Callers
    /// that already have the sibling list cached should prefer the pure
    /// overload to avoid the extra query.
    /// </summary>
    public static async Task<string> ComputeAsync(
        WorkstreamRow workstream, ProductDbContext db, CancellationToken ct = default)
    {
        if (workstream is null) throw new ArgumentNullException(nameof(workstream));

        List<WorkstreamRow> siblings;
        if (string.IsNullOrWhiteSpace(workstream.StorySpawnId))
        {
            siblings = new List<WorkstreamRow> { workstream };
        }
        else
        {
            siblings = await db.Workstreams.AsNoTracking()
                .Where(w => w.StorySpawnId == workstream.StorySpawnId)
                .ToListAsync(ct);
            if (siblings.All(w => w.Id != workstream.Id)) siblings.Add(workstream);
        }
        return Compute(workstream, siblings);
    }

    /// <summary>
    /// Extract the stage title from a <see cref="WorkstreamRow.PlanAnchor"/>
    /// formatted as <c>phase-{phaseSlug}/stage-{stageSlug}</c> — returns
    /// just <c>{stageSlug}</c>. Anchors that don't follow the convention
    /// fall back to the full anchor with separators normalised, keeping
    /// the result filesystem-safe.
    /// </summary>
    private static string StageTitleSlug(string planAnchor)
    {
        // Drop the trailing <c>@{contentHash}</c> the reconciler appends
        // on stage scope-edits — the hash isn't useful in a directory
        // name and changes every plan rev.
        var s = planAnchor;
        var at = s.IndexOf('@');
        if (at > 0) s = s[..at];

        var slash = s.IndexOf('/');
        if (slash >= 0)
        {
            s = s[(slash + 1)..];
        }
        if (s.StartsWith("stage-", StringComparison.Ordinal))
        {
            s = s["stage-".Length..];
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
