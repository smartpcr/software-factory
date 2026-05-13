// -----------------------------------------------------------------------
// <copyright file="WorkstreamMergeAdvancer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Advances workstream rows when their PR reaches Col="merged" through any
/// path that bypasses <see cref="StorySpawnOrchestrator.DriveReviewAsync"/> —
/// human-driven merge, ADO auto-merge, or PrSyncJob detecting the upstream
/// transition. The orchestrator's existing "merging → complete" promotion
/// only fires when the orchestrator was the one that flipped "review →
/// merging" via ApproveAndMergeAsync; without this helper, every other
/// merge path leaves the workstream stuck on "review" forever and the
/// phase-board chip never advances.
/// </summary>
public static class WorkstreamMergeAdvancer
{
    /// <summary>
    /// Advance any workstream whose <c>PrsJson</c> contains <paramref name="prId"/>
    /// and whose Status is "review" or "merging" → "complete". Returns the
    /// IDs of the workstreams that were advanced (empty when no match).
    /// Caller is responsible for SaveChangesAsync on the supplied context.
    /// <para>
    /// 2026-05-13 — active-comments guard. A PR with <c>PrComments.State == "active"</c>
    /// is NOT closed: the workstream is left on review/merging so the
    /// orchestrator's <c>DriveReviewAsync</c> fix loop can address the
    /// comments first. Without this guard, an externally-merged PR (human
    /// clicked Merge on GitHub UI, ADO auto-merge, etc.) would strand
    /// reviewer comments by flipping the workstream straight to "complete",
    /// taking it out of <c>DriveReviewAsync</c>'s scan window. Pairs with
    /// the equivalent guard on the orchestrator's own external-merge fast
    /// path so every "advance to complete" decision honors active comments.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<string>> AdvanceForMergedPrAsync(
        ForgeDbContext db, string prId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(prId)) return Array.Empty<string>();

        // Active-comments guard — never close a PR with unresolved review comments.
        var hasActiveComments = await db.PrComments.AsNoTracking()
            .AnyAsync(c => c.PrId == prId && c.State == "active", ct);
        if (hasActiveComments) return Array.Empty<string>();

        var advanced = new List<string>();
        var candidates = await db.Workstreams
            .Where(w => w.PrsJson.Contains(prId) && (w.Status == "review" || w.Status == "merging"))
            .ToListAsync(ct);
        foreach (var w in candidates)
        {
            if (!ReferencesPr(w.PrsJson, prId)) continue;
            w.Status = "complete";
            advanced.Add(w.Id);
        }
        return advanced;
    }

    /// <summary>
    /// Backfill pass: walk every <c>PrRow</c> with <c>Col="merged"</c> and
    /// advance any matching workstream that's still on review/merging. Run
    /// once on startup to repair rows merged before the per-path advances
    /// were wired in. PRs with active comments are deferred (the
    /// orchestrator's fix loop picks them up on its next tick).
    /// </summary>
    public static async Task<int> BackfillAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var mergedPrIds = await db.Prs
            .Where(p => p.Col == "merged")
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (mergedPrIds.Count == 0) return 0;

        var totalAdvanced = 0;
        foreach (var prId in mergedPrIds)
        {
            var advanced = await AdvanceForMergedPrAsync(db, prId, ct);
            totalAdvanced += advanced.Count;
        }
        if (totalAdvanced > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return totalAdvanced;
    }

    /// <summary>
    /// Exact-match a PR id inside a <c>PrsJson</c> array. The LIKE prefilter
    /// in <see cref="AdvanceForMergedPrAsync"/> can match substrings of
    /// larger ids (e.g. <c>"PR #42"</c> appearing inside <c>"PR #423"</c>);
    /// this confirms an actual array element match.
    /// </summary>
    public static bool ReferencesPr(string prsJson, string prId)
    {
        if (string.IsNullOrEmpty(prsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(prsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String
                    && string.Equals(el.GetString(), prId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch { /* malformed JSON — skip */ }
        return false;
    }
}
