// -----------------------------------------------------------------------
// <copyright file="RepoRegistrySync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 80.B — reconciles the control-side <see cref="RepoRegistryRow"/>
/// index from the per-product <c>repos</c> rows, sets each
/// <see cref="ProductRow.DefaultRepoId"/>, and backfills null <c>RepoId</c>
/// values on the product-scoped tables to the product's default repo.
///
/// <para>Runs in BOTH split and single-file modes (it reads
/// <c>product.Repos</c> via <see cref="IProductDbContextFactory"/>, which
/// routes to <c>{slug}.db</c> or falls back to <c>forge.db</c>), AFTER
/// <see cref="RepoMoveToProductService"/> has relocated the rows in split
/// mode. Idempotent — registry rows are upserted by id, <c>DefaultRepoId</c>
/// is only set when null, and the backfill only touches <c>RepoId IS NULL</c>
/// rows — so it is safe to run every boot. The ongoing write-path
/// maintenance of the registry (repo create / update / delete) is Stage 80.C.</para>
///
/// <para>Products are processed sequentially to avoid concurrent writers on
/// the single control DB (SQLITE_BUSY).</para>
/// </summary>
public static class RepoRegistrySync
{
    /// <summary>Product tables carrying a nullable <c>RepoId</c> to backfill.</summary>
    private static readonly string[] BackfillTables =
    {
        "product_stories", "work_items", "workstreams", "prs", "cards", "story_spawns",
    };

    public static async Task ReconcileAsync(
        ControlDbContext control,
        IProductDbContextFactory factory,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        var products = await control.Products
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        // 80.C.2 — quarantine support: track every repo id actually present in
        // a product DB this pass. Registry rows not in this set are stale (a
        // torn delete left the mirror behind) and get soft-deleted below — but
        // ONLY when every product DB was read cleanly (a failed/skipped product
        // would otherwise look like its repos vanished).
        var seenRepoIds = new HashSet<string>(StringComparer.Ordinal);
        var fullScan = true;

        foreach (var product in products)
        {
            if (string.IsNullOrEmpty(product.Slug)) { fullScan = false; continue; }
            try
            {
                var db = factory.CreateFor(product.Slug);
                var repos = await db.Repos.AsNoTracking()
                    .Where(r => r.ProductId == product.Id)
                    .OrderBy(r => r.Sort)
                    .ToListAsync(ct);
                if (repos.Count == 0) continue;

                // 1. Upsert registry rows for this product.
                var ids = repos.Select(r => r.Id).ToList();
                seenRepoIds.UnionWith(ids);
                var existing = await control.RepoRegistry
                    .Where(r => ids.Contains(r.Id))
                    .ToDictionaryAsync(r => r.Id, ct);
                foreach (var repo in repos)
                {
                    if (!existing.TryGetValue(repo.Id, out var reg))
                    {
                        reg = new RepoRegistryRow { Id = repo.Id };
                        control.RepoRegistry.Add(reg);
                    }
                    reg.Sort = repo.Sort;
                    reg.ProductId = product.Id;
                    reg.ProductSlug = product.Slug;
                    reg.Kind = repo.Kind;
                    reg.RemoteUrl = repo.RemoteUrl;
                    reg.CloneRoot = repo.CloneRoot;
                    reg.DisplayName = repo.DisplayName;
                    reg.DeletedAt = null;
                    reg.LastSyncAt = repo.LastSyncAt;
                    reg.LastSyncCursor = repo.LastSyncCursor;
                    reg.LastSyncStatus = repo.LastSyncStatus;
                    reg.LastSyncError = repo.LastSyncError;
                    reg.IsRunningSince = repo.IsRunningSince;
                    reg.LastUpstreamChangedDate = repo.LastUpstreamChangedDate;
                    reg.WorkItemSyncIntervalSeconds = repo.WorkItemSyncIntervalSeconds;
                    reg.PrSyncIntervalSeconds = repo.PrSyncIntervalSeconds;
                    reg.BuildSyncIntervalSeconds = repo.BuildSyncIntervalSeconds;
                }

                // 2. Default repo = lowest-Sort repo, when not already set.
                var defaultRepoId = product.DefaultRepoId;
                if (string.IsNullOrEmpty(defaultRepoId))
                {
                    defaultRepoId = repos[0].Id;
                    var tracked = await control.Products.FirstAsync(p => p.Id == product.Id, ct);
                    tracked.DefaultRepoId = defaultRepoId;
                }

                await control.SaveChangesAsync(ct);

                // 3. Backfill null RepoId on product tables → default repo.
                await BackfillRepoIdAsync(db, defaultRepoId!, log, ct);
            }
            catch (Exception ex)
            {
                fullScan = false;
                log?.LogWarning(ex, "[Phase80b] Registry reconcile failed for product '{Slug}'.", product.Slug);
            }
        }

        // 80.C.2 — soft-delete (quarantine) registry rows whose repo no longer
        // exists in any product DB. Only runs after a clean full scan so a
        // transient product-DB read failure can't wrongly tombstone live repos.
        if (fullScan)
        {
            var stale = await control.RepoRegistry
                .Where(r => r.DeletedAt == null && !seenRepoIds.Contains(r.Id))
                .ToListAsync(ct);
            if (stale.Count > 0)
            {
                var nowIso = DateTimeOffset.UtcNow.ToString("o");
                foreach (var r in stale) r.DeletedAt = nowIso;
                await control.SaveChangesAsync(ct);
                log?.LogInformation(
                    "[Phase80b] Quarantined {Count} stale repo_registry row(s) with no product repo: {Ids}",
                    stale.Count, string.Join(", ", stale.Select(s => s.Id)));
            }
        }
    }

    private static async Task BackfillRepoIdAsync(ProductDbContext db, string defaultRepoId, ILogger? log, CancellationToken ct)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        foreach (var table in BackfillTables)
        {
            if (!await TableHasColumnAsync(conn, table, "RepoId", ct)) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE \"{table}\" SET RepoId = $d WHERE RepoId IS NULL;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$d";
            p.Value = defaultRepoId;
            cmd.Parameters.Add(p);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n > 0) log?.LogInformation("[Phase80b] Backfilled RepoId on {Count} {Table} row(s).", n, table);
        }
    }

    private static async Task<bool> TableHasColumnAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @c;";
        cmd.Parameters.AddWithValue("@c", column);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
