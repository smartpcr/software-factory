// -----------------------------------------------------------------------
// <copyright file="SplitMarkerCache.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>
/// Phase 73 follow-up — process-wide cache of the
/// <c>phase73-split-applied</c> marker row in
/// <c>forge_seed_state</c> on the control DB. Used by
/// <see cref="ProductDbContextFactory.CreateFor"/> as a hard gate: a
/// per-product DB file is only opened when the marker is stamped.
/// Without the marker, the factory falls back to the control DB even
/// if <c>{slug}.db</c> exists on disk. This prevents the
/// <em>half-applied split</em> failure mode where
/// <see cref="Forge.Api.Persistence.Split.SplitApplyService"/> created
/// the per-product files but never finished cutover, leaving the UI
/// reading a frozen snapshot while writers continued landing rows in
/// the control DB.
///
/// <para>The marker only flips during a Phase 73 cutover, which itself
/// requires a Forge.Api restart in the maintenance window. So a
/// process-lifetime cache is safe — we never see the marker change
/// underneath us in production. Tests that mutate the marker between
/// cases call <see cref="ResetForTesting"/>.</para>
/// </summary>
public static class SplitMarkerCache
{
    /// <summary>
    /// The <c>forge_seed_state.Key</c> value stamped by
    /// <c>SplitApplyService.ApplyAsync</c> step 8 when the wet-run
    /// completes successfully. Mirrors
    /// <c>SplitApplyService.AppliedMarkerKey</c>.
    /// </summary>
    public const string MarkerKey = "phase73-split-applied";

    private static readonly object Lock = new();

    /// <summary>
    /// Cache slot keyed by control-DB path. Null = uncached.
    /// </summary>
    private static (string ControlPath, bool Applied)? cached;

    /// <summary>
    /// Reports whether the Phase 73 split is fully applied on the
    /// control DB at <paramref name="paths"/>. Safe to call from any
    /// thread; first caller performs the probe under
    /// <see cref="Lock"/>, subsequent callers hit the cache.
    /// </summary>
    /// <param name="paths">Resolver for the control-DB path.</param>
    /// <returns><c>true</c> when the marker row exists; <c>false</c>
    /// otherwise (no control DB, no <c>forge_seed_state</c> table, or
    /// row missing).</returns>
    public static bool IsPhase73Applied(IForgeDbPaths paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        var controlPath = paths.ControlDbPath();

        lock (Lock)
        {
            if (cached is { } c
                && string.Equals(c.ControlPath, controlPath, StringComparison.OrdinalIgnoreCase))
            {
                return c.Applied;
            }

            var applied = ProbeMarker(controlPath);
            cached = (controlPath, applied);
            return applied;
        }
    }

    /// <summary>
    /// Drop the cached result. Production code never calls this; tests
    /// that flip the marker mid-test do.
    /// </summary>
    public static void ResetForTesting()
    {
        lock (Lock)
        {
            cached = null;
        }
    }

    private static bool ProbeMarker(string controlPath)
    {
        if (string.IsNullOrEmpty(controlPath) || !File.Exists(controlPath))
        {
            return false;
        }

        try
        {
            // Read-only + immutable=0 — we want to see fresh writes if a
            // splitter is running concurrently (cache invalidation will
            // miss the live flip, but Phase 73 cutover is a maintenance-
            // window operation with a restart so this is fine).
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = controlPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 5,
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            // Table may not exist on a brand-new DB that pre-dates Phase 22.
            using (var check = conn.CreateCommand())
            {
                check.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name='forge_seed_state' LIMIT 1;";
                if (check.ExecuteScalar() is null) return false;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM forge_seed_state WHERE Key = $k;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$k";
            p.Value = MarkerKey;
            cmd.Parameters.Add(p);
            var raw = cmd.ExecuteScalar();
            return raw is not null && Convert.ToInt32(raw) > 0;
        }
        catch
        {
            // Fail-safe: any probe error => treat as not applied so the
            // factory falls back to control DB. Loud failure is the
            // caller's job, not ours.
            return false;
        }
    }
}
