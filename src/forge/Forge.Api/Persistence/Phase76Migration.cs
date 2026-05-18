// -----------------------------------------------------------------------
// <copyright file="Phase76Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 76 — relocate the <c>repos</c> table from the control DB
/// (forge.db) to per-product DBs ({slug}.db). Operator intent
/// 2026-05-17: <i>"i want repo to move to productDb"</i>. The split
/// boundary (<see cref="Split.DbBoundary"/>) is updated to classify
/// <c>repos</c> as <see cref="Split.DbKind.Product"/> with
/// <c>PartitionStrategy.DirectProductId</c>; this migration moves the
/// existing data so the schema matches.
///
/// <para><b>Idempotent.</b> Gated by
/// <c>phase76-repos-moved-to-product</c> in <c>forge_seed_state</c>.
/// Re-running on an already-migrated DB returns immediately.</para>
///
/// <para><b>Algorithm</b>:
/// <list type="number">
///   <item>Bail when marker stamped, or when Phase 73 split marker
///         is NOT stamped (Phase 76 requires per-product DBs to exist).</item>
///   <item>Read product list + their slugs from <c>control.products</c>.</item>
///   <item>For each product slug:
///         <list type="bullet">
///           <item>Open the per-product DB.</item>
///           <item>Run the EF Core CREATE TABLE statement for repos
///                 (without the cross-DB FK to <c>products</c> — that
///                 constraint moves to app layer).</item>
///           <item>Create the per-product indexes:
///                 <c>IX_repos_CloneRoot UNIQUE</c>,
///                 <c>IX_repos_ProductId</c>,
///                 <c>IX_repos_RemoteUrl UNIQUE</c>.
///                 (CloneRoot + RemoteUrl uniqueness now scoped per
///                 product; cross-product uniqueness moves to app
///                 layer in <c>ProductsEndpoints.AddRepo</c>.)</item>
///           <item>ATTACH the control DB to this connection AS
///                 <c>control_main</c>.</item>
///           <item>INSERT INTO repos SELECT * FROM control_main.repos
///                 WHERE ProductId = '{productId}'.</item>
///           <item>DETACH.</item>
///         </list></item>
///   <item>Verify: sum of per-product row counts equals control row
///         count. On mismatch abort (leaves control.repos intact, no
///         marker stamped) so the operator can investigate.</item>
///   <item>Drop the <c>repos</c> table from the control DB.</item>
///   <item>Stamp <c>phase76-repos-moved-to-product</c>.</item>
///   <item>VACUUM control DB.</item>
/// </list>
/// </para>
///
/// <para><b>Why not a standard PhaseMigration?</b> Standard
/// PhaseMigrations operate on a single context per invocation (the
/// runner fans out per-product for Product-kind migrations). Phase 76
/// is fundamentally a cross-DB DATA MOVE: needs control DB open +
/// every per-product DB open + ATTACH chains. Cleanest is a
/// standalone service mirroring
/// <see cref="Split.SplitApplyService"/>, invoked from
/// <c>Program.cs</c> after the PhaseMigrationRunner. Constraints from
/// the application layer (DI, IForgeDbPaths, IConfiguration) are
/// available there.</para>
/// </summary>
public sealed class Phase76Migration
{
    public const string MarkerKey = "phase76-repos-moved-to-product";

    private readonly IForgeDbPaths paths;
    private readonly ILogger<Phase76Migration>? logger;

    public Phase76Migration(IForgeDbPaths paths, ILogger<Phase76Migration>? logger = null)
    {
        this.paths = paths;
        this.logger = logger;
    }

    public sealed record Result(
        bool AlreadyApplied,
        bool RequiresPhase73,
        int ProductsProcessed,
        int RowsMoved,
        IReadOnlyList<string> Errors);

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

        // 1. Idempotency + Phase 73 prerequisite checks.
        await EnsureSeedStateTableAsync(conn, ct);
        if (await IsAppliedAsync(conn, MarkerKey, ct))
        {
            return new Result(true, false, 0, 0, Array.Empty<string>());
        }
        if (!await IsAppliedAsync(conn, "phase73-split-applied", ct))
        {
            return new Result(false, true, 0, 0, new[]
            {
                "Phase 76 requires the Phase 73 split to be applied first "
                + "(`phase73-split-applied` marker missing from forge_seed_state). "
                + "Per-product DBs must exist before repos can be moved.",
            });
        }

        // 2. Read product list.
        var products = new List<(string Id, string Slug)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Slug FROM products WHERE Slug IS NOT NULL AND Slug != '' ORDER BY Slug;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                products.Add((r.GetString(0), r.GetString(1).ToLowerInvariant()));
            }
        }
        if (products.Count == 0)
        {
            // No products → nothing to move. Stamp marker so subsequent
            // boots skip and the schema swap is safe.
            await using (var dropCmd = conn.CreateCommand())
            {
                dropCmd.CommandText = "DROP TABLE IF EXISTS repos;";
                await dropCmd.ExecuteNonQueryAsync(ct);
            }
            await StampAppliedAsync(conn, MarkerKey, ct);
            return new Result(false, false, 0, 0, Array.Empty<string>());
        }

        // 3. Get the original control.repos count for verification.
        int controlCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM repos;";
            controlCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        this.logger?.LogInformation(
            "[Phase76] Moving {Count} repo rows from control DB to {Products} per-product DB(s).",
            controlCount, products.Count);

        var errors = new List<string>();
        var totalMoved = 0;

        // 4. For each product, open its DB, create the repos schema, ATTACH control, INSERT.
        foreach (var product in products)
        {
            var slugPath = this.paths.ProductDbPath(product.Slug);
            if (!File.Exists(slugPath))
            {
                errors.Add(
                    $"Per-product DB '{slugPath}' is missing for product '{product.Slug}'. "
                    + "Phase 73 split must have created it; re-run Phase 73 ResumeAsync if needed.");
                continue;
            }

            var moved = 0;
            await using var prodConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = slugPath,
                DefaultTimeout = 30,
            }.ToString());
            await prodConn.OpenAsync(ct);

            // 4a. Create the repos table on the per-product DB. We
            // deliberately omit the FK to products (cross-DB FK can't
            // be enforced anyway) and we keep CloneRoot + RemoteUrl
            // UNIQUE since within one product they MUST still be
            // unique. Cross-product uniqueness becomes app-layer in
            // ProductsEndpoints.AddRepo.
            await using (var createCmd = prodConn.CreateCommand())
            {
                createCmd.CommandText = ReposCreateTableSql + ReposIndexSql;
                await createCmd.ExecuteNonQueryAsync(ct);
            }

            // 4b. ATTACH control + copy.
            var controlEsc = controlPath.Replace("'", "''");
            await using (var attachCmd = prodConn.CreateCommand())
            {
                attachCmd.CommandText = $"ATTACH DATABASE '{controlEsc}' AS control_main;";
                await attachCmd.ExecuteNonQueryAsync(ct);
            }
            try
            {
                await using var insertCmd = prodConn.CreateCommand();
                insertCmd.CommandText = "INSERT OR IGNORE INTO repos SELECT * FROM control_main.repos WHERE ProductId = $pid;";
                var p = insertCmd.CreateParameter();
                p.ParameterName = "$pid";
                p.Value = product.Id;
                insertCmd.Parameters.Add(p);
                moved = await insertCmd.ExecuteNonQueryAsync(ct);
                totalMoved += moved;
                this.logger?.LogInformation(
                    "[Phase76] Product '{Slug}' (id={Id}): copied {Moved} repo row(s) to {Path}.",
                    product.Slug, product.Id, moved, slugPath);
            }
            catch (SqliteException ex)
            {
                errors.Add($"INSERT failed for product '{product.Slug}': {ex.Message}");
            }
            finally
            {
                await using var detachCmd = prodConn.CreateCommand();
                detachCmd.CommandText = "DETACH DATABASE control_main;";
                try { await detachCmd.ExecuteNonQueryAsync(ct); }
                catch (SqliteException ex)
                {
                    errors.Add($"DETACH failed for product '{product.Slug}': {ex.Message}");
                }
            }
        }

        if (errors.Count > 0)
        {
            return new Result(false, false, products.Count, totalMoved, errors);
        }

        // 5. Verify counts match (totalMoved over per-product DBs ==
        // controlCount). Account for orphan repos (ProductId pointing
        // at a product that no longer exists in products table) —
        // those stay in control and the migration refuses to drop
        // the source table.
        var orphanCount = controlCount - totalMoved;
        if (orphanCount > 0)
        {
            errors.Add(
                $"Verification: {orphanCount} repo row(s) in control DB do NOT belong to any known product "
                + $"(controlCount={controlCount}, moved={totalMoved}). Source table preserved. "
                + "Inspect: SELECT Id, ProductId FROM repos WHERE ProductId NOT IN (SELECT Id FROM products).");
            return new Result(false, false, products.Count, totalMoved, errors);
        }

        // 6. Drop source.
        await using (var dropCmd = conn.CreateCommand())
        {
            dropCmd.CommandText = "DROP TABLE repos;";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        // 7. Stamp marker.
        await StampAppliedAsync(conn, MarkerKey, ct);

        // 8. VACUUM control DB.
        await using (var vacuumCmd = conn.CreateCommand())
        {
            vacuumCmd.CommandText = "VACUUM;";
            await vacuumCmd.ExecuteNonQueryAsync(ct);
        }

        this.logger?.LogInformation(
            "[Phase76] Moved {Total} repo row(s) across {Products} product DB(s). Control `repos` table dropped + marker stamped.",
            totalMoved, products.Count);

        return new Result(false, false, products.Count, totalMoved, Array.Empty<string>());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private const string ReposCreateTableSql = @"
CREATE TABLE IF NOT EXISTS ""repos"" (
    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_repos"" PRIMARY KEY,
    ""Sort"" INTEGER NOT NULL,
    ""ProductId"" TEXT NOT NULL,
    ""Kind"" TEXT NOT NULL,
    ""RemoteUrl"" TEXT NOT NULL,
    ""DisplayName"" TEXT NOT NULL,
    ""DefaultBranch"" TEXT NOT NULL,
    ""CloneRoot"" TEXT NOT NULL,
    ""AdoOrg"" TEXT NULL,
    ""AdoProject"" TEXT NULL,
    ""GhOwner"" TEXT NULL,
    ""GhRepo"" TEXT NULL,
    AreaPathsJson TEXT,
    LastSyncAt TEXT,
    LastSyncCursor TEXT,
    LastSyncStatus TEXT NOT NULL DEFAULT 'idle',
    LastSyncError TEXT,
    IsRunningSince TEXT,
    AgentReadyDefaultTeam TEXT,
    InitialSyncWindowDays INTEGER,
    BuildCommand TEXT NULL,
    TestCommand TEXT NULL,
    BuildTimeoutSec INTEGER NULL,
    TestTimeoutSec INTEGER NULL,
    InitCommand TEXT NULL,
    RestoreCommand TEXT NULL,
    WiSyncAssignedTo TEXT,
    WiSyncDateFromDays INTEGER,
    PrSyncCreatedBy TEXT,
    PrSyncStatus TEXT,
    BuildPipelineIdsJson TEXT,
    WiSyncFromDate TEXT,
    WiSyncToDate TEXT,
    WorkItemSyncIntervalSeconds INTEGER,
    PrSyncIntervalSeconds INTEGER,
    BuildSyncIntervalSeconds INTEGER,
    LastUpstreamChangedDate TEXT
);";

    private const string ReposIndexSql = @"
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_repos_CloneRoot"" ON ""repos"" (""CloneRoot"");
CREATE INDEX IF NOT EXISTS ""IX_repos_ProductId"" ON ""repos"" (""ProductId"");
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_repos_RemoteUrl"" ON ""repos"" (""RemoteUrl"");";

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
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is not null && Convert.ToInt32(o) > 0;
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
