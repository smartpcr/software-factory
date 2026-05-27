// -----------------------------------------------------------------------
// <copyright file="PlanDocStateHelpers.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 53 — sequential plan-doc state. Helpers for reading and writing
/// the JSON column <see cref="StorySpawnRow.PlanDocPrsJson"/>, plus
/// reverse-lookup over the entire <c>story_spawns</c> table by PR id.
///
/// <para>The four plan docs run in strict sequence:
/// <c>architecture → tech-spec → implementation-plan → e2e-scenarios</c>.
/// Each doc has its own branch (<c>story/{id}/plan-{key}</c>), its own
/// worktree (<c>.worktree/plan-{key}</c>), and its own PR. Doc N+1's
/// gen↔eval loop only kicks off after doc N's PR merges and its latest
/// deliverable verdict is <c>pass</c>.</para>
///
/// <para>Legacy spawns (created before Phase 53 shipped) carry
/// <c>PlanDocPrsJson == null</c> and continue to drive the single-PR
/// <see cref="StorySpawnRow.PlanPrId"/> path. The discriminator is
/// strictly column-presence — no row ever flips between paths
/// mid-life.</para>
/// </summary>
public static class PlanDocStateHelpers
{
    /// <summary>
    /// Canonical plan-doc ordering. Single source of truth — the orchestrator,
    /// the strategy, the UI, and the gate helpers all read from this list.
    /// </summary>
    public static readonly IReadOnlyList<string> PlanDocOrder = new[]
    {
        "architecture",
        "tech-spec",
        "implementation-plan",
        "e2e-scenarios",
    };

    /// <summary>
    /// Return the canonical position for <paramref name="docKey"/> in
    /// <see cref="PlanDocOrder"/>.
    /// </summary>
    public static int PlanDocIndexOf(string docKey)
    {
        for (var i = 0; i < PlanDocOrder.Count; i++)
        {
            if (string.Equals(PlanDocOrder[i], docKey, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Map plan-doc key → on-disk file name (matches
    /// <c>BuildPlanDocSpecs</c> + <c>PlanDocFileNames</c>). Both with the
    /// <c>.md</c> suffix because deliverables, transcripts, and queue rows
    /// all key on <c>plan-doc:{spawnId}:{fileName}</c> with the suffix.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PlanDocFileName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["architecture"]         = "architecture.md",
            ["tech-spec"]            = "tech-spec.md",
            ["implementation-plan"]  = "implementation-plan.md",
            ["e2e-scenarios"]        = "e2e-scenarios.md",
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// True when this spawn uses the Phase 53 sequential plan-doc path.
    /// Legacy spawns (<see cref="StorySpawnRow.PlanDocPrsJson"/> is null)
    /// return false — they drive the single-PR <see cref="StorySpawnRow.PlanPrId"/>
    /// state machine in <c>DrivePlanAsync</c>'s legacy branch.
    /// </summary>
    public static bool IsSequentialMode(StorySpawnRow spawn) =>
        spawn.PlanDocPrsJson is not null;

    /// <summary>
    /// Read the per-doc state list. Always returns four entries (one per
    /// canonical key); entries that haven't been started yet have null
    /// <c>Branch</c> and null <c>PrId</c>. Returns an empty list for
    /// legacy spawns so callers can branch on <c>Count == 0</c>.
    /// </summary>
    public static IReadOnlyList<PlanDocPrEntry> GetPlanDocStates(StorySpawnRow spawn)
    {
        if (spawn.PlanDocPrsJson is null) return Array.Empty<PlanDocPrEntry>();

        // Parse robustly. Malformed JSON => treat as empty (the orchestrator
        // will re-persist a clean array on its next drive).
        IReadOnlyList<PlanDocPrEntry> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<PlanDocPrEntry>>(spawn.PlanDocPrsJson, JsonOptions)
                ?? new List<PlanDocPrEntry>();
        }
        catch (JsonException)
        {
            parsed = Array.Empty<PlanDocPrEntry>();
        }

        // Always project to the canonical order so callers don't depend
        // on the JSON ordering (a hand-edit could shuffle entries).
        var byKey = parsed
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<PlanDocPrEntry>(PlanDocOrder.Count);
        foreach (var key in PlanDocOrder)
        {
            if (byKey.TryGetValue(key, out var entry))
            {
                result.Add(entry);
            }
            else
            {
                result.Add(new PlanDocPrEntry(key, Branch: null, PrId: null));
            }
        }
        return result;
    }

    /// <summary>
    /// Persist the per-doc state list onto the spawn row. Entries are
    /// serialised in canonical order regardless of the input order.
    /// Callers must <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// after this — the helper mutates the row but does not save.
    /// </summary>
    public static void SetPlanDocStates(StorySpawnRow spawn, IReadOnlyList<PlanDocPrEntry> entries)
    {
        var byKey = entries
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var ordered = new List<PlanDocPrEntry>(PlanDocOrder.Count);
        foreach (var key in PlanDocOrder)
        {
            if (byKey.TryGetValue(key, out var entry))
            {
                ordered.Add(entry);
            }
        }
        spawn.PlanDocPrsJson = JsonSerializer.Serialize(ordered, JsonOptions);
    }

    /// <summary>
    /// Look up the single entry for <paramref name="docKey"/>. Returns the
    /// canonical-ordered placeholder entry (with null branch + null PrId)
    /// when the doc hasn't been started yet. Throws
    /// <see cref="ArgumentException"/> for an unknown key — callers must
    /// pass a value from <see cref="PlanDocOrder"/>.
    /// </summary>
    public static PlanDocPrEntry GetEntry(StorySpawnRow spawn, string docKey)
    {
        if (!PlanDocFileName.ContainsKey(docKey))
        {
            throw new ArgumentException($"Unknown plan-doc key '{docKey}'.", nameof(docKey));
        }
        var states = GetPlanDocStates(spawn);
        var entry = states.FirstOrDefault(e => string.Equals(e.Key, docKey, StringComparison.OrdinalIgnoreCase));
        return entry ?? new PlanDocPrEntry(docKey, Branch: null, PrId: null);
    }

    /// <summary>
    /// Resolve the branch name for a given plan doc on this spawn:
    /// <list type="bullet">
    /// <item>For a sequential-mode spawn with a recorded entry, returns
    /// the persisted <c>Branch</c>.</item>
    /// <item>For a sequential-mode spawn without a recorded entry (the
    /// doc hasn't been started yet), returns the canonical branch name
    /// <c>story/{safeStoryId}/plan-{docKey}</c>.</item>
    /// <item>For a legacy spawn (no <c>PlanDocPrsJson</c>), returns
    /// <c>spawn.PlanBranch ?? "story/{safeStoryId}/plan"</c> — same
    /// behaviour as every legacy call site already has inlined.</item>
    /// </list>
    /// </summary>
    public static string FindDocBranch(StorySpawnRow spawn, string docKey)
    {
        if (!PlanDocFileName.ContainsKey(docKey))
        {
            throw new ArgumentException($"Unknown plan-doc key '{docKey}'.", nameof(docKey));
        }

        if (!IsSequentialMode(spawn))
        {
            return spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan";
        }

        var entry = GetEntry(spawn, docKey);
        return entry.Branch ?? CanonicalDocBranch(spawn.StoryId, docKey);
    }

    /// <summary>
    /// The canonical (deterministic) branch name for a plan doc on a
    /// sequential-mode spawn. Always <c>story/{safeStoryId}/plan-{docKey}</c>.
    /// </summary>
    public static string CanonicalDocBranch(string storyId, string docKey)
        => $"story/{StoryPaths.Safe(storyId)}/plan-{docKey}";

    /// <summary>
    /// The canonical worktree slug for a plan doc on a sequential-mode
    /// spawn. When <paramref name="storyId"/> is provided, the slug is
    /// <c>plan-{safeStoryId}-{docKey}</c> so two stories in the same
    /// repo don't collide. The single-arg overload returns the legacy
    /// <c>plan-{docKey}</c> slug for backward compatibility (read paths
    /// that probe both conventions).
    /// </summary>
    public static string CanonicalDocWorktreeSlug(string storyId, string docKey)
        => $"plan-{StoryPaths.Safe(storyId)}-{docKey}";

    /// <summary>Legacy slug (no story scoping). Used by read-path probes
    /// that need to find existing worktrees created before the
    /// story-scoped slug was introduced.</summary>
    public static string CanonicalDocWorktreeSlug(string docKey) => $"plan-{docKey}";

    /// <summary>
    /// The canonical worktree slug for a legacy parallel-mode plan branch.
    /// All four parallel architects share one worktree (the legacy single-PR
    /// flow checks out <c>story/{id}/plan</c> and runs every doc on it).
    /// Single bucket per repo — concurrent spawns on the same repo are
    /// outside the design space the parallel mode supports.
    /// </summary>
    public static string CanonicalParallelPlanWorktreeSlug() => "plan";

    /// <summary>
    /// The canonical worktree slug for the QA gate branch
    /// (<c>story/{storyId}/qa</c>). Includes the safe story id so QA
    /// runs for distinct stories on the same repo don't collide.
    /// </summary>
    public static string CanonicalQaWorktreeSlug(string storyId) => $"qa-{StoryPaths.Safe(storyId)}";

    /// <summary>
    /// The canonical worktree slug for a per-workstream QA-steps branch
    /// (<c>ws/{storyId}/{anchor}-steps</c>). Keyed on the workstream id
    /// so each step-def PR runs in its own worktree.
    /// </summary>
    public static string CanonicalQaStepsWorktreeSlug(string workstreamId) => $"qa-steps-{workstreamId}";

    /// <summary>
    /// Reverse-lookup: find the spawn + doc-key that owns a given PR id.
    /// Used by <c>PlanPrMergeReactor</c> when a PR-merge event fires and
    /// the reactor only knows the PR id.
    ///
    /// <para>Tries the legacy <see cref="StorySpawnRow.PlanPrId"/> column
    /// first (indexed, O(log n)). Falls back to a JSON scan of every
    /// sequential-mode spawn. The fallback is O(n × 4) where n is the
    /// number of sequential-mode spawns; acceptable for the operator's
    /// expected spawn count (~tens). A future indexed child table is the
    /// scale-out path.</para>
    /// </summary>
    public static async Task<(StorySpawnRow Spawn, string? DocKey)?> FindByPrIdAsync(
        ControlDbContext control, ProductDbContext db, string prId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(prId)) return null;

        // Legacy fast path.
        var legacy = await db.StorySpawns.FirstOrDefaultAsync(s => s.PlanPrId == prId, ct);
        if (legacy is not null)
        {
            return (legacy, DocKey: null);
        }

        // Sequential-mode scan.
        var sequentialSpawns = await db.StorySpawns
            .Where(s => s.PlanDocPrsJson != null)
            .ToListAsync(ct);
        foreach (var spawn in sequentialSpawns)
        {
            var states = GetPlanDocStates(spawn);
            var match = states.FirstOrDefault(e => string.Equals(e.PrId, prId, StringComparison.Ordinal));
            if (match is not null)
            {
                return (spawn, match.Key);
            }
        }
        return null;
    }
}

/// <summary>
/// One plan doc's state on a sequential-mode story spawn.
///
/// <para><c>Key</c> is one of <see cref="PlanDocStateHelpers.PlanDocOrder"/>.
/// <c>Branch</c> is the per-doc git branch (always
/// <c>story/{storyId}/plan-{key}</c> at v1, but kept on the row so a
/// future rename keeps the canonical mapping discoverable). <c>PrId</c>
/// is the per-doc PR id; null when the doc's branch has been created
/// but the PR hasn't been opened yet (mid-iter or pre-PR-open
/// crash-recovery anchor).</para>
/// </summary>
public sealed record PlanDocPrEntry(string Key, string? Branch, string? PrId);
