// -----------------------------------------------------------------------
// <copyright file="RepoMoveToProductService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;

/// <summary>
/// Phase 80.B — physically relocate the <c>repos</c> table from the control
/// DB (<c>forge.db</c>) into each per-product <c>{slug}.db</c>. Supersedes the
/// never-wired <c>Phase76Migration</c> (whose <c>INSERT … SELECT *</c> was
/// column-order-fragile and whose DDL pre-dated <c>repos.TestImpactedOnly</c>).
///
/// <para><b>Split-mode only.</b> Gated on the Phase 73 split marker — if the
/// DB hasn't been split, <c>repos</c> already lives in the single
/// <c>forge.db</c> as a product-model table (created by the product schema
/// script), so there is nothing to move. Idempotent via
/// <see cref="MarkerKey"/>.</para>
///
/// <para><b>Algorithm</b> (mirrors <see cref="Split.SplitApplyService"/>):
/// for each product with a <c>{slug}.db</c>, create the product-side
/// <c>repos</c> table, ATTACH the control DB, copy that product's rows with an
/// <i>explicit, dynamically-intersected</i> column list (robust to column
/// order / drift), DETACH. Verify the per-product totals equal the control
/// count (orphan rows whose product is unknown abort the drop), then drop the
/// control <c>repos</c> table and VACUUM. Registry population, <c>DefaultRepoId</c>
/// and the <c>RepoId</c> backfill are handled separately by
/// <see cref="RepoRegistrySync"/> (which runs in both split and single-file
/// modes).</para>
/// </summary>
public sealed class RepoMoveToProductService
{
    public const string MarkerKey = "phase80b-repos-physical-moved";

    private readonly IForgeDbPaths paths;
    private readonly ILogger<RepoMoveToProductService>? logger;

    public RepoMoveToProductService(IForgeDbPaths paths, ILogger<RepoMoveToProductService>? logger = null)
    {
        this.paths = paths;
        this.logger = logger;
    }

    public sealed record Result(bool AlreadyApplied, bool RequiresSplit, int ProductsProcessed, int RowsMoved, IReadOnlyList<string> Errors);

    public async Task<Result> ApplyAsync(CancellationToken ct = default)
    {
        var controlPath = this.paths.ControlDbPath();
        if (!File.Exists(controlPath))
        {
            return new Result(false, false, 0, 0, new[] { "Control DB does not exist." });
        }

        var cs = new SqliteConnectionStringBuilder { DataSource = controlPath, DefaultTimeout = 30 }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        await EnsureSeedStateTableAsync(conn, ct);
        if (await IsAppliedAsync(conn, MarkerKey, ct))
        {
            return new Result(true, false, 0, 0, Array.Empty<string>());
        }
        // Single-file (un-split) mode: `repos` is already a product table in
        // the one forge.db — nothing to move. Do NOT stamp the marker: if this
        // DB is later split, a subsequent boot MUST still run the move. The
        // probe (open + two marker reads) is cheap to repeat each boot.
        if (!await IsAppliedAsync(conn, "phase73-split-applied", ct))
        {
            return new Result(false, true, 0, 0, Array.Empty<string>());
        }

        // The control table may already be gone (a prior partial run). If so,
        // there is nothing to move — stamp and return.
        if (!await TableExistsAsync(conn, "repos", ct))
        {
            await StampAppliedAsync(conn, MarkerKey, ct);
            return new Result(false, false, 0, 0, Array.Empty<string>());
        }

        var products = new List<(string Id, string Slug)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Slug FROM products WHERE Slug IS NOT NULL AND Slug != '' ORDER BY Slug;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) products.Add((r.GetString(0), r.GetString(1).ToLowerInvariant()));
        }

        int controlCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM repos;";
            controlCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        // Source column list (control.repos), used to build the explicit
        // INSERT/SELECT column intersection per product.
        var sourceCols = await ColumnsAsync(conn, "repos", ct);

        var errors = new List<string>();
        var totalMoved = 0;

        foreach (var (productId, slug) in products)
        {
            var slugPath = this.paths.ProductDbPath(slug);
            if (!File.Exists(slugPath))
            {
                errors.Add($"Per-product DB '{slugPath}' missing for product '{slug}'.");
                continue;
            }

            await using var prodConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = slugPath,
                DefaultTimeout = 30,
            }.ToString());
            await prodConn.OpenAsync(ct);

            await using (var createCmd = prodConn.CreateCommand())
            {
                createCmd.CommandText = ReposCreateTableSql + ReposIndexSql;
                await createCmd.ExecuteNonQueryAsync(ct);
            }

            // Explicit, intersected column list — robust to column order /
            // drift between source and target (Phase76's `SELECT *` was not).
            var targetCols = await ColumnsAsync(prodConn, "repos", ct);
            var cols = sourceCols.Intersect(targetCols, StringComparer.OrdinalIgnoreCase).ToList();
            var colList = string.Join(", ", cols.Select(c => $"\"{c}\""));

            var controlEsc = controlPath.Replace("'", "''");
            await using (var attachCmd = prodConn.CreateCommand())
            {
                attachCmd.CommandText = $"ATTACH DATABASE '{controlEsc}' AS control_main;";
                await attachCmd.ExecuteNonQueryAsync(ct);
            }
            try
            {
                await using var insertCmd = prodConn.CreateCommand();
                insertCmd.CommandText =
                    $"INSERT OR IGNORE INTO repos ({colList}) " +
                    $"SELECT {colList} FROM control_main.repos WHERE ProductId = $pid;";
                var p = insertCmd.CreateParameter();
                p.ParameterName = "$pid";
                p.Value = productId;
                insertCmd.Parameters.Add(p);
                var moved = await insertCmd.ExecuteNonQueryAsync(ct);
                totalMoved += moved;
                this.logger?.LogInformation(
                    "[Phase80b] Product '{Slug}': copied {Moved} repo row(s) to {Path}.", slug, moved, slugPath);
            }
            catch (SqliteException ex)
            {
                errors.Add($"INSERT failed for product '{slug}': {ex.Message}");
            }
            finally
            {
                await using var detachCmd = prodConn.CreateCommand();
                detachCmd.CommandText = "DETACH DATABASE control_main;";
                try { await detachCmd.ExecuteNonQueryAsync(ct); }
                catch (SqliteException ex) { errors.Add($"DETACH failed for product '{slug}': {ex.Message}"); }
            }
        }

        if (errors.Count > 0)
        {
            return new Result(false, false, products.Count, totalMoved, errors);
        }

        var orphanCount = controlCount - totalMoved;
        if (orphanCount > 0)
        {
            errors.Add(
                $"Verification: {orphanCount} control repo row(s) belong to no known product "
                + $"(controlCount={controlCount}, moved={totalMoved}). Source table preserved; investigate "
                + "SELECT Id, ProductId FROM repos WHERE ProductId NOT IN (SELECT Id FROM products).");
            return new Result(false, false, products.Count, totalMoved, errors);
        }

        await using (var dropCmd = conn.CreateCommand())
        {
            dropCmd.CommandText = "DROP TABLE repos;";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }
        await StampAppliedAsync(conn, MarkerKey, ct);
        await using (var vacuumCmd = conn.CreateCommand())
        {
            vacuumCmd.CommandText = "VACUUM;";
            await vacuumCmd.ExecuteNonQueryAsync(ct);
        }

        this.logger?.LogInformation(
            "[Phase80b] Moved {Total} repo row(s) across {Products} product DB(s); control `repos` dropped.",
            totalMoved, products.Count);
        return new Result(false, false, products.Count, totalMoved, Array.Empty<string>());
    }

    // Full current RepoRow schema (incl Phase 59 TestImpactedOnly). Index
    // names match DbModelProduct so EnsureCreated on a fresh {slug}.db and this
    // raw DDL don't double-create.
    private const string ReposCreateTableSql = @"
CREATE TABLE IF NOT EXISTS ""repos"" (
    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_repos"" PRIMARY KEY,
    ""Sort"" INTEGER NOT NULL DEFAULT 0,
    ""ProductId"" TEXT NOT NULL,
    ""Kind"" TEXT NOT NULL,
    ""RemoteUrl"" TEXT NOT NULL,
    ""DisplayName"" TEXT NOT NULL,
    ""DefaultBranch"" TEXT NOT NULL DEFAULT 'main',
    ""CloneRoot"" TEXT NOT NULL,
    ""AdoOrg"" TEXT NULL,
    ""AdoProject"" TEXT NULL,
    ""GhOwner"" TEXT NULL,
    ""GhRepo"" TEXT NULL,
    ""AreaPathsJson"" TEXT NULL,
    ""LastSyncAt"" TEXT NULL,
    ""LastSyncCursor"" TEXT NULL,
    ""LastSyncStatus"" TEXT NOT NULL DEFAULT 'idle',
    ""LastSyncError"" TEXT NULL,
    ""IsRunningSince"" TEXT NULL,
    ""AgentReadyDefaultTeam"" TEXT NULL,
    ""InitialSyncWindowDays"" INTEGER NULL,
    ""InitCommand"" TEXT NULL,
    ""RestoreCommand"" TEXT NULL,
    ""BuildCommand"" TEXT NULL,
    ""TestCommand"" TEXT NULL,
    ""BuildTimeoutSec"" INTEGER NULL,
    ""TestTimeoutSec"" INTEGER NULL,
    ""TestImpactedOnly"" INTEGER NULL,
    ""WiSyncAssignedTo"" TEXT NULL,
    ""WiSyncDateFromDays"" INTEGER NULL,
    ""WiSyncFromDate"" TEXT NULL,
    ""WiSyncToDate"" TEXT NULL,
    ""PrSyncCreatedBy"" TEXT NULL,
    ""PrSyncStatus"" TEXT NULL,
    ""BuildPipelineIdsJson"" TEXT NULL,
    ""WorkItemSyncIntervalSeconds"" INTEGER NULL,
    ""PrSyncIntervalSeconds"" INTEGER NULL,
    ""BuildSyncIntervalSeconds"" INTEGER NULL,
    ""LastUpstreamChangedDate"" TEXT NULL
);";

    private const string ReposIndexSql = @"
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_repos_CloneRoot"" ON ""repos"" (""CloneRoot"");
CREATE INDEX IF NOT EXISTS ""IX_repos_ProductId"" ON ""repos"" (""ProductId"");
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_repos_RemoteUrl"" ON ""repos"" (""RemoteUrl"");";

    private static async Task<List<string>> ColumnsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) cols.Add(r.GetString(1));
        return cols;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task EnsureSeedStateTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsAppliedAsync(SqliteConnection conn, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM forge_seed_state WHERE Key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task StampAppliedAsync(SqliteConnection conn, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES (@key, @at);";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
