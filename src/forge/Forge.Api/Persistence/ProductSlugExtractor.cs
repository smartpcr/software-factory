// -----------------------------------------------------------------------
// <copyright file="ProductSlugExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 73 follow-up — derives a product slug from common Forge
/// entity IDs by convention, with a <see cref="ControlDbContext"/>
/// fallback. Used by writer entry points (<c>StorySpawnOrchestrator</c>,
/// <c>StoryResumeRecoveryService</c>, ...) to push the right
/// <c>AsyncLocal</c> slug onto <see cref="ProductDbContextFactory"/>
/// before opening any <see cref="ProductDbContext"/>.
///
/// <para>Conventions parsed (case-insensitive, slug returned
/// lowercased):</para>
/// <list type="bullet">
///   <item>Story id: <c>{slug}:{STORY_KEY}</c> e.g.
///         <c>code-intelligence:AGENT-MEMORY</c>.</item>
///   <item>Spawn id: <c>spawn-{safeStoryId}</c> where
///         <c>safeStoryId = slug-STORY_KEY</c> (colon replaced by
///         hyphen by <c>StoryPaths.Safe</c>). Reverse the safe
///         transform via the known product slug list.</item>
///   <item>Workstream id: <c>ws-{slug}-{phase}-{stage}</c>. Prefix
///         match against the known product slug list because
///         <c>slug</c> itself can contain hyphens.</item>
///   <item>Plan-doc work-item id:
///         <c>plan-doc:spawn-{safeStoryId}:{file}.md</c>. Same
///         extraction as spawn id from the middle segment.</item>
/// </list>
/// </summary>
public static class ProductSlugExtractor
{
    /// <summary>
    /// Convention-based slug extraction from a story id of the form
    /// <c>{slug}:{STORY_KEY}</c>. Returns <c>null</c> when no colon
    /// is found or the slug is empty / invalid.
    /// </summary>
    public static string? FromStoryId(string? storyId)
    {
        if (string.IsNullOrWhiteSpace(storyId)) return null;
        var colon = storyId.IndexOf(':');
        if (colon <= 0 || colon == storyId.Length - 1) return null;
        var raw = storyId.Substring(0, colon).Trim().ToLowerInvariant();
        return ForgeDbPaths.TryValidateSlug(raw, out _) ? raw : null;
    }

    /// <summary>
    /// Convention-based slug extraction from a spawn id of the form
    /// <c>spawn-{safeStoryId}</c>. <paramref name="knownSlugs"/>
    /// supplies the candidate product slugs (typically from
    /// <see cref="ControlDbContext.Products"/>) because the leading
    /// slug-portion of <c>safeStoryId</c> is itself hyphen-bearing
    /// and indistinguishable from the story-key separator without
    /// the candidate set.
    /// </summary>
    public static string? FromSpawnId(string? spawnId, IEnumerable<string>? knownSlugs)
    {
        if (string.IsNullOrWhiteSpace(spawnId)) return null;
        const string prefix = "spawn-";
        if (!spawnId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = spawnId.Substring(prefix.Length);
        return LongestPrefixMatch(rest, knownSlugs);
    }

    /// <summary>
    /// Convention-based slug extraction from a workstream id of the
    /// form <c>ws-{slug}-{phase-...}-stage-...</c>. Same caveat as
    /// <see cref="FromSpawnId"/> — needs the candidate slug set.
    /// </summary>
    public static string? FromWorkstreamId(string? wsId, IEnumerable<string>? knownSlugs)
    {
        if (string.IsNullOrWhiteSpace(wsId)) return null;
        const string prefix = "ws-";
        if (!wsId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = wsId.Substring(prefix.Length);
        return LongestPrefixMatch(rest, knownSlugs);
    }

    /// <summary>
    /// Convention-based slug extraction from a plan-doc work-item id
    /// of the form <c>plan-doc:spawn-{safeStoryId}:{file}.md</c>.
    /// </summary>
    public static string? FromPlanDocWorkItemId(string? workItemId, IEnumerable<string>? knownSlugs)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) return null;
        const string prefix = "plan-doc:";
        if (!workItemId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = workItemId.Substring(prefix.Length);
        var nextColon = rest.IndexOf(':');
        var spawnId = nextColon > 0 ? rest.Substring(0, nextColon) : rest;
        return FromSpawnId(spawnId, knownSlugs);
    }

    /// <summary>
    /// Full pipeline: try the conventions in order against
    /// <paramref name="anyId"/>; if no convention matches, returns
    /// <c>null</c>. Caller should treat <c>null</c> as "unscoped" and
    /// fall through to control DB (legacy behaviour); after Phase 73
    /// completion the fail-loud guard in
    /// <see cref="ProductDbContextFactory"/> will surface the missing
    /// scope as an exception.
    ///
    /// <para>No DB-lookup fallback by design: <c>WorkItemRow</c> lives
    /// on <see cref="ProductDbContext"/> post-split, so a Control-only
    /// lookup can't resolve a raw <c>WI-12345</c> back to a product.
    /// In practice every writer entry point receives an ID that
    /// embeds the slug by convention (<c>storyId</c>, <c>spawnId</c>,
    /// <c>workstreamId</c>, plan-doc work-item id), so the conventions
    /// cover all observed call sites.</para>
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string? anyId,
        ControlDbContext control,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(anyId) || control is null) return null;

        // Heuristic 1: story-id convention.
        var slug = FromStoryId(anyId);
        if (slug is not null) return slug;

        // Load known product slugs once. The control table is small
        // (~10s of products in worst case) so a single query covers
        // the rest of the heuristics.
        var knownSlugs = await control.Products
            .AsNoTracking()
            .Where(p => p.Slug != null && p.Slug != string.Empty)
            .Select(p => p.Slug.ToLower())
            .ToListAsync(ct);

        // Heuristic 2: plan-doc work-item id.
        slug = FromPlanDocWorkItemId(anyId, knownSlugs);
        if (slug is not null) return slug;

        // Heuristic 3: workstream id.
        slug = FromWorkstreamId(anyId, knownSlugs);
        if (slug is not null) return slug;

        // Heuristic 4: spawn id.
        slug = FromSpawnId(anyId, knownSlugs);
        if (slug is not null) return slug;

        // No convention matched. Caller falls through to control DB
        // (legacy behaviour). For raw work-item ids (WI-12345) this
        // is the expected path; the sync workers already push slugs
        // via ProductFanout before invoking any per-product code.
        return null;
    }

    /// <summary>
    /// Returns the longest known slug that is a prefix of
    /// <paramref name="rest"/> followed by either end-of-string or a
    /// hyphen separator. Longest-match deliberately so a product
    /// named <c>code</c> doesn't shadow <c>code-intelligence</c>.
    /// </summary>
    private static string? LongestPrefixMatch(string rest, IEnumerable<string>? knownSlugs)
    {
        if (knownSlugs is null) return null;
        string? best = null;
        foreach (var slug in knownSlugs)
        {
            if (string.IsNullOrWhiteSpace(slug)) continue;
            if (!rest.StartsWith(slug, StringComparison.OrdinalIgnoreCase)) continue;
            if (rest.Length != slug.Length && rest[slug.Length] != '-') continue;
            if (best is null || slug.Length > best.Length)
            {
                best = slug.ToLowerInvariant();
            }
        }
        return best;
    }
}
