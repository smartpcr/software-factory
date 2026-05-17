// -----------------------------------------------------------------------
// <copyright file="SplitApplyService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

using Microsoft.Data.Sqlite;

/// <summary>
/// Phase F — actually splits <c>forge.db</c> into a control DB
/// (<c>forge.db</c>) + one per-product DB (<c>{slug}.db</c>) per
/// <see cref="ProductRow"/> row. Reuses
/// <see cref="SplitDryRunService"/>'s partition catalog so the wet run
/// shares the boundary the operator already validated in Phase C.
///
/// <para>Algorithm (transactional, with backup):</para>
/// <list type="number">
///   <item><strong>Idempotency check</strong> — read <c>forge_seed_state</c>
///         for <see cref="AppliedMarkerKey"/>. If present, return
///         <see cref="SplitApplyResult.AlreadyApplied"/>.</item>
///   <item><strong>Pre-flight</strong> — run <see cref="SplitDryRunService"/>
///         to capture pre-counts + product list + warn on orphans /
///         unknown tables. Refuse to apply if any unknown table holds
///         data (operator must decide).</item>
///   <item><strong>Backup</strong> — copy <c>forge.db</c> to
///         <c>forge.db.pre-phase73.{timestamp}.bak</c>. Fail loud if
///         backup fails. Operator restores by replacing <c>forge.db</c>.</item>
///   <item><strong>Schema replay</strong> — read every product-table
///         CREATE statement from <c>sqlite_master</c>; replay each into
///         a fresh <c>{slug}.db</c> file. Indexes and constraints
///         (added by earlier Phase migrations) are preserved exactly.</item>
///   <item><strong>Partition copy</strong> — for each product, ATTACH
///         that product's <c>{slug}.db</c> to the control connection
///         and run <c>INSERT INTO {slug}.{table} SELECT ... FROM {table}
///         WHERE / JOIN ...</c> using the boundary catalog's
///         partition SQL.</item>
///   <item><strong>Verification</strong> — for each table, assert the
///         sum of per-product post-counts equals the pre-count minus
///         orphans. Abort + leave source intact if any mismatch.</item>
///   <item><strong>Drop source tables</strong> — only after every
///         product DB is verified, <c>DROP TABLE</c> for each
///         product-side table in the source <c>forge.db</c>. Control
///         tables remain.</item>
///   <item><strong>Stamp marker</strong> — INSERT into
///         <c>forge_seed_state</c> so subsequent boots return
///         <see cref="SplitApplyResult.AlreadyApplied"/>.</item>
/// </list>
///
/// <para>The whole sequence is single-threaded and serialized on the
/// control DB write lock. Concurrent writers from background workers
/// must be paused (operator runs this manually with services
/// disabled), but the apply still tolerates a few rows added between
/// the pre-count and the partition copy — the verification step
/// catches the mismatch and the backup is the recovery path.</para>
/// </summary>
public sealed class SplitApplyService
{
    public const string AppliedMarkerKey = "phase73-split-applied";

    private readonly IForgeDbPaths paths;
    private readonly SplitDryRunService analyzer;
    private readonly ILogger<SplitApplyService>? logger;

    public SplitApplyService(
        IForgeDbPaths paths,
        SplitDryRunService analyzer,
        ILogger<SplitApplyService>? logger = null)
    {
        this.paths = paths;
        this.analyzer = analyzer;
        this.logger = logger;
    }

    public async Task<SplitApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        var dbPath = this.paths.ControlDbPath();
        if (!File.Exists(dbPath))
        {
            return new SplitApplyResult(
                AlreadyApplied: false,
                BackupPath: null,
                ProductDbs: Array.Empty<string>(),
                RowsCopied: 0,
                Errors: new[] { "Control DB does not exist; nothing to split." });
        }

        // Open the source DB read-write — we'll mutate it (DROP TABLE) at the end.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
        }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        // 1. Idempotency check.
        await EnsureSeedStateTableAsync(conn, ct);
        if (await IsAppliedAsync(conn, ct))
        {
            return new SplitApplyResult(
                AlreadyApplied: true,
                BackupPath: null,
                ProductDbs: Array.Empty<string>(),
                RowsCopied: 0,
                Errors: Array.Empty<string>());
        }

        // 2. Pre-flight dry-run for counts + warnings.
        var preReport = await this.analyzer.AnalyzeAsync(ct);
        if (preReport.UnknownTables.Count > 0)
        {
            var names = string.Join(", ", preReport.UnknownTables.Select(t => $"{t.TableName} ({t.RowCount})"));
            return new SplitApplyResult(
                AlreadyApplied: false,
                BackupPath: null,
                ProductDbs: Array.Empty<string>(),
                RowsCopied: 0,
                Errors: new[]
                {
                    $"Refusing to apply: {preReport.UnknownTables.Count} unknown table(s) hold data: {names}. " +
                    $"Add them to DbBoundary.AllTables (or drop them) before applying.",
                });
        }

        // 3. Backup using SQLite's online backup API. The source conn
        // is still open with a write lock; File.Copy would race against
        // any in-flight WAL transactions. BackupDatabase performs a
        // page-level copy under SQLite's own locking, producing a
        // crash-consistent snapshot we can replay from.
        var backupPath = $"{dbPath}.pre-phase73.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        await using (var backupConn = new SqliteConnection($"Data Source={backupPath}"))
        {
            await backupConn.OpenAsync(ct);
            conn.BackupDatabase(backupConn);
        }
        this.logger?.LogInformation("[Phase73] Backed up forge.db to {Backup}", backupPath);

        var errors = new List<string>();
        var productDbPaths = new List<string>();
        var rowsCopied = 0;

        try
        {
            // 4. For each product, create {slug}.db with the same schema as forge.db.
            var schemaStmts = await ExtractProductSchemaStatementsAsync(conn, ct);
            this.logger?.LogInformation("[Phase73] Extracted {Count} schema statements for product tables.", schemaStmts.Count);

            foreach (var product in preReport.Products)
            {
                var slug = product.ProductSlug;
                var slugDbPath = this.paths.ProductDbPath(slug);

                if (File.Exists(slugDbPath))
                {
                    errors.Add($"Per-product DB '{slugDbPath}' already exists; refusing to overwrite. Move it aside and retry.");
                    continue;
                }

                await CreateProductDbAsync(slugDbPath, schemaStmts, ct);
                productDbPaths.Add(slugDbPath);
                this.logger?.LogInformation("[Phase73] Created product DB at {Path}", slugDbPath);
            }

            if (errors.Count > 0)
            {
                return new SplitApplyResult(false, backupPath, productDbPaths, 0, errors);
            }

            // 5. ATTACH each product DB and copy partitioned rows.
            //    The default SQLITE_LIMIT_ATTACHED is 10, so >10 products
            //    would need a re-attach pass; we error early in that case.
            if (preReport.Products.Count > 9)
            {
                errors.Add($"Too many products ({preReport.Products.Count}); SQLITE_LIMIT_ATTACHED is 10 (1 main + 9 attached). " +
                           "Phase F's current implementation attaches all product DBs at once; raise the limit or split the apply.");
                return new SplitApplyResult(false, backupPath, productDbPaths, 0, errors);
            }

            // Attach all product DBs, and disable FK enforcement during
            // the copy so cross-context FK declarations on product
            // tables (e.g. product_iterations.ProductId →
            // products.Id) don't trip while the rows are being moved.
            // The FKs are conceptually broken post-split anyway (the
            // referenced control rows live in a different DB file);
            // app-layer code enforces them instead.
            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                await pragmaCmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var product in preReport.Products)
            {
                var slug = product.ProductSlug;
                var slugDbPath = this.paths.ProductDbPath(slug);
                var attachAlias = SqliteAlias(slug);
                await using var attachCmd = conn.CreateCommand();
                attachCmd.CommandText = $"ATTACH DATABASE @path AS {attachAlias};";
                attachCmd.Parameters.AddWithValue("@path", slugDbPath);
                await attachCmd.ExecuteNonQueryAsync(ct);
            }

            // For each product table in the boundary, partition + copy.
            foreach (var spec in DbBoundary.AllTables.Where(t => t.Kind == DbKind.Product))
            {
                var tableExists = await TableExistsAsync(conn, spec.TableName, ct);
                if (!tableExists) continue;

                foreach (var product in preReport.Products)
                {
                    var slug = product.ProductSlug;
                    var attachAlias = SqliteAlias(slug);
                    var partitionSql = BuildInsertSql(spec, attachAlias, product.ProductId);
                    if (partitionSql is null)
                    {
                        // NoPartitionKey: route all rows to the default
                        // product (first in slug order — typically 'atlas').
                        if (product.ProductSlug == preReport.Products[0].ProductSlug)
                        {
                            await using var fallbackCmd = conn.CreateCommand();
                            fallbackCmd.CommandText = $"INSERT INTO {attachAlias}.\"{spec.TableName}\" SELECT * FROM \"{spec.TableName}\";";
                            try
                            {
                                var n = await fallbackCmd.ExecuteNonQueryAsync(ct);
                                rowsCopied += n;
                                this.logger?.LogInformation(
                                    "[Phase73] {Table}: {Count} no-partition row(s) routed to default product {Slug}.",
                                    spec.TableName, n, slug);
                            }
                            catch (SqliteException ex)
                            {
                                errors.Add($"Failed to copy {spec.TableName} to {slug}: {ex.Message}");
                            }
                        }
                        continue;
                    }

                    await using var copyCmd = conn.CreateCommand();
                    copyCmd.CommandText = partitionSql;
                    try
                    {
                        var n = await copyCmd.ExecuteNonQueryAsync(ct);
                        rowsCopied += n;
                        if (n > 0)
                        {
                            this.logger?.LogInformation(
                                "[Phase73] {Table} → {Slug}: {Count} row(s)", spec.TableName, slug, n);
                        }
                    }
                    catch (SqliteException ex)
                    {
                        errors.Add($"Failed partition copy {spec.TableName}→{slug}: {ex.Message}");
                    }
                }

                if (errors.Count > 0) break;
            }

            // 6. Verify counts.
            if (errors.Count == 0)
            {
                var postReport = await this.analyzer.AnalyzeAsync(ct);
                foreach (var pre in preReport.Tables.Where(t => t.Kind == "product"))
                {
                    // For partitioned tables, expected post-count in source
                    // is still pre.TotalRows (we haven't dropped yet). The
                    // sum across product DBs should be pre.ClassifiedRows
                    // (orphans don't move).
                    //
                    // For NoPartitionKey tables, all rows are routed to the
                    // default product (see line ~219). The dry-run reports
                    // ClassifiedRows=0 and OrphanRows=total for them (treating
                    // every row as orphan from a partitioning standpoint), but
                    // the apply step routes them all anyway — so the verifier
                    // must compare against TotalRows, not ClassifiedRows, or
                    // else NoPartitionKey tables would always trip the
                    // mismatch and block the DROP.
                    var expectedAttached = string.Equals(pre.Strategy, "nopartitionkey", StringComparison.OrdinalIgnoreCase)
                        ? pre.TotalRows
                        : pre.ClassifiedRows;
                    var attachedTotal = await SumAttachedTableAsync(conn, pre.TableName, preReport.Products, ct);
                    if (attachedTotal != expectedAttached)
                    {
                        errors.Add(
                            $"Row-count mismatch for {pre.TableName} (strategy={pre.Strategy}): " +
                            $"expected-attached={expectedAttached} actual-attached={attachedTotal} " +
                            $"total={pre.TotalRows} classified={pre.ClassifiedRows} orphans={pre.OrphanRows}. " +
                            $"Refusing to drop source. Restore from {backupPath}.");
                    }
                }
            }

            // 7. Detach all product DBs (must detach before DROP TABLE on source).
            foreach (var product in preReport.Products)
            {
                var attachAlias = SqliteAlias(product.ProductSlug);
                try
                {
                    await using var detachCmd = conn.CreateCommand();
                    detachCmd.CommandText = $"DETACH DATABASE {attachAlias};";
                    await detachCmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException ex)
                {
                    errors.Add($"Failed to detach {attachAlias}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                return new SplitApplyResult(false, backupPath, productDbPaths, rowsCopied, errors);
            }

            // 8. DROP product tables from source.
            foreach (var spec in DbBoundary.AllTables.Where(t => t.Kind == DbKind.Product))
            {
                if (!await TableExistsAsync(conn, spec.TableName, ct)) continue;
                await using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = $"DROP TABLE \"{spec.TableName}\";";
                await dropCmd.ExecuteNonQueryAsync(ct);
            }

            // 9. Stamp marker.
            await MarkAppliedAsync(conn, ct);

            // 10. VACUUM the source to reclaim space.
            await using (var vacuumCmd = conn.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync(ct);
            }

            this.logger?.LogInformation(
                "[Phase73] Split applied: {Products} product DB(s), {Rows} rows copied. forge.db is now control-only.",
                productDbPaths.Count, rowsCopied);

            return new SplitApplyResult(
                AlreadyApplied: false,
                BackupPath: backupPath,
                ProductDbs: productDbPaths,
                RowsCopied: rowsCopied,
                Errors: Array.Empty<string>());
        }
        catch (Exception ex)
        {
            errors.Add($"Unhandled exception: {ex.Message}");
            this.logger?.LogError(ex, "[Phase73] Split apply failed. Source forge.db left intact; backup at {Backup}", backupPath);
            return new SplitApplyResult(false, backupPath, productDbPaths, rowsCopied, errors);
        }
    }

    /// <summary>
    /// Phase 73 Stage 2C — idempotent retry for a HALF-APPLIED split.
    /// Distinct from <see cref="ApplyAsync"/> which refuses to run when
    /// per-product files already exist (treating their presence as a
    /// "didn't fully wipe last time, ambiguous" signal). Resume
    /// explicitly handles the case the operator now needs: backup +
    /// schema replay + partition copy ran on a prior boot, but
    /// verification or the source-DROP step failed and left the
    /// marker un-stamped. Algorithm:
    ///
    /// <list type="number">
    ///   <item>Bail with <see cref="SplitApplyResult.AlreadyApplied"/>
    ///         when the marker is already stamped.</item>
    ///   <item>Pre-flight dry-run for counts + unknown-table check
    ///         (identical to <see cref="ApplyAsync"/>).</item>
    ///   <item>Refuse with an actionable error if any product is
    ///         missing its per-product DB file — operator should run
    ///         <see cref="ApplyAsync"/> from scratch instead (it
    ///         creates the schemas).</item>
    ///   <item>ATTACH every per-product DB.</item>
    ///   <item>For each product table: <c>DELETE FROM
    ///         {alias}.{table}</c> then replay the partition INSERT
    ///         from the source. Truncate-then-fill is safe because
    ///         the source rows are the live ones (writers have been
    ///         landing them in <c>forge.db</c> while the per-product
    ///         file was stale).</item>
    ///   <item>Verify counts + DROP source tables + stamp marker +
    ///         VACUUM (identical to <see cref="ApplyAsync"/> from
    ///         step 6 onward).</item>
    /// </list>
    ///
    /// <para>Writers MUST be paused for the duration (typically a
    /// maintenance-window restart with Forge.Api offline) so the
    /// truncate-then-fill is consistent against the live source.</para>
    /// </summary>
    public async Task<SplitApplyResult> ResumeAsync(CancellationToken ct = default)
    {
        var dbPath = this.paths.ControlDbPath();
        if (!File.Exists(dbPath))
        {
            return new SplitApplyResult(
                AlreadyApplied: false,
                BackupPath: null,
                ProductDbs: Array.Empty<string>(),
                RowsCopied: 0,
                Errors: new[] { "Control DB does not exist; nothing to resume." });
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
        }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        // 1. Idempotency check.
        await EnsureSeedStateTableAsync(conn, ct);
        if (await IsAppliedAsync(conn, ct))
        {
            return new SplitApplyResult(
                AlreadyApplied: true,
                BackupPath: null,
                ProductDbs: Array.Empty<string>(),
                RowsCopied: 0,
                Errors: Array.Empty<string>());
        }

        // 2. Pre-flight dry-run.
        var preReport = await this.analyzer.AnalyzeAsync(ct);
        if (preReport.UnknownTables.Count > 0)
        {
            var names = string.Join(", ", preReport.UnknownTables.Select(t => $"{t.TableName} ({t.RowCount})"));
            return new SplitApplyResult(
                AlreadyApplied: false,
                BackupPath: null,
                ProductDbs: Array.Empty<string>(),
                RowsCopied: 0,
                Errors: new[]
                {
                    $"Refusing to resume: {preReport.UnknownTables.Count} unknown table(s) hold data: {names}. "
                    + "Add them to DbBoundary.AllTables (or drop them) before resuming.",
                });
        }

        // 3. All per-product files must exist (Resume's defining
        // precondition — distinguishes it from a fresh Apply).
        var errors = new List<string>();
        var productDbPaths = new List<string>();
        foreach (var product in preReport.Products)
        {
            var slugDbPath = this.paths.ProductDbPath(product.ProductSlug);
            if (!File.Exists(slugDbPath))
            {
                errors.Add(
                    $"Per-product DB '{slugDbPath}' is missing for product '{product.ProductSlug}'. "
                    + "Resume expects every product to have its DB file from the prior partial apply. "
                    + "Run ApplyAsync from scratch instead, or restore the missing file from the backup.");
            }
            else
            {
                productDbPaths.Add(slugDbPath);
            }
        }
        if (errors.Count > 0)
        {
            return new SplitApplyResult(false, null, productDbPaths, 0, errors);
        }

        if (preReport.Products.Count > 9)
        {
            errors.Add(
                $"Too many products ({preReport.Products.Count}); SQLITE_LIMIT_ATTACHED is 10.");
            return new SplitApplyResult(false, null, productDbPaths, 0, errors);
        }

        var rowsCopied = 0;
        try
        {
            // 4. ATTACH every product DB.
            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                await pragmaCmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var product in preReport.Products)
            {
                var slugDbPath = this.paths.ProductDbPath(product.ProductSlug);
                var attachAlias = SqliteAlias(product.ProductSlug);
                await using var attachCmd = conn.CreateCommand();
                attachCmd.CommandText = $"ATTACH DATABASE @path AS {attachAlias};";
                attachCmd.Parameters.AddWithValue("@path", slugDbPath);
                await attachCmd.ExecuteNonQueryAsync(ct);
            }

            // 5. For each product table: truncate, then replay the
            // partition copy from the (live) source.
            foreach (var spec in DbBoundary.AllTables.Where(t => t.Kind == DbKind.Product))
            {
                if (!await TableExistsAsync(conn, spec.TableName, ct)) continue;

                foreach (var product in preReport.Products)
                {
                    var slug = product.ProductSlug;
                    var attachAlias = SqliteAlias(slug);

                    // Truncate the per-product table first. Skip when
                    // the table doesn't exist in this product DB (a
                    // schema gap from the prior partial apply — we
                    // continue best-effort and let the verify step
                    // catch any genuine row-count mismatch).
                    try
                    {
                        await using var truncCmd = conn.CreateCommand();
                        truncCmd.CommandText = $"DELETE FROM {attachAlias}.\"{spec.TableName}\";";
                        await truncCmd.ExecuteNonQueryAsync(ct);
                    }
                    catch (SqliteException)
                    {
                        // Table missing in product DB — log and skip;
                        // the operator should re-apply from scratch.
                        this.logger?.LogWarning(
                            "[Phase73 Resume] {Table} missing in per-product DB for {Slug}; skipping truncate.",
                            spec.TableName, slug);
                    }

                    var partitionSql = BuildInsertSql(spec, attachAlias, product.ProductId);
                    if (partitionSql is null)
                    {
                        if (product.ProductSlug == preReport.Products[0].ProductSlug)
                        {
                            await using var fallbackCmd = conn.CreateCommand();
                            fallbackCmd.CommandText = $"INSERT INTO {attachAlias}.\"{spec.TableName}\" SELECT * FROM \"{spec.TableName}\";";
                            try
                            {
                                var n = await fallbackCmd.ExecuteNonQueryAsync(ct);
                                rowsCopied += n;
                            }
                            catch (SqliteException ex)
                            {
                                errors.Add($"Resume failed to copy {spec.TableName} to {slug}: {ex.Message}");
                            }
                        }
                        continue;
                    }

                    await using var copyCmd = conn.CreateCommand();
                    copyCmd.CommandText = partitionSql;
                    try
                    {
                        var n = await copyCmd.ExecuteNonQueryAsync(ct);
                        rowsCopied += n;
                    }
                    catch (SqliteException ex)
                    {
                        errors.Add($"Resume failed partition copy {spec.TableName}→{slug}: {ex.Message}");
                    }
                }

                if (errors.Count > 0) break;
            }

            // 6. Verify counts.
            if (errors.Count == 0)
            {
                foreach (var pre in preReport.Tables.Where(t => t.Kind == "product"))
                {
                    var expectedAttached = string.Equals(pre.Strategy, "nopartitionkey", StringComparison.OrdinalIgnoreCase)
                        ? pre.TotalRows
                        : pre.ClassifiedRows;
                    var attachedTotal = await SumAttachedTableAsync(conn, pre.TableName, preReport.Products, ct);
                    if (attachedTotal != expectedAttached)
                    {
                        errors.Add(
                            $"Resume row-count mismatch for {pre.TableName} (strategy={pre.Strategy}): "
                            + $"expected-attached={expectedAttached} actual-attached={attachedTotal} "
                            + $"total={pre.TotalRows} classified={pre.ClassifiedRows} orphans={pre.OrphanRows}.");
                    }
                }
            }

            // 7. DETACH product DBs (must detach before DROP TABLE).
            foreach (var product in preReport.Products)
            {
                var attachAlias = SqliteAlias(product.ProductSlug);
                try
                {
                    await using var detachCmd = conn.CreateCommand();
                    detachCmd.CommandText = $"DETACH DATABASE {attachAlias};";
                    await detachCmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException ex)
                {
                    errors.Add($"Resume failed to detach {attachAlias}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                return new SplitApplyResult(false, null, productDbPaths, rowsCopied, errors);
            }

            // 8. DROP source product tables.
            foreach (var spec in DbBoundary.AllTables.Where(t => t.Kind == DbKind.Product))
            {
                if (!await TableExistsAsync(conn, spec.TableName, ct)) continue;
                await using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = $"DROP TABLE \"{spec.TableName}\";";
                await dropCmd.ExecuteNonQueryAsync(ct);
            }

            // 9. Stamp marker.
            await MarkAppliedAsync(conn, ct);

            // 10. VACUUM source.
            await using (var vacuumCmd = conn.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync(ct);
            }

            this.logger?.LogInformation(
                "[Phase73 Resume] Split resumed: {Products} product DB(s), {Rows} rows re-copied. forge.db is now control-only.",
                productDbPaths.Count, rowsCopied);

            // Clear the marker cache so subsequent factory resolves
            // see the just-stamped state without a process restart.
            SplitMarkerCache.ResetForTesting();

            return new SplitApplyResult(
                AlreadyApplied: false,
                BackupPath: null,
                ProductDbs: productDbPaths,
                RowsCopied: rowsCopied,
                Errors: Array.Empty<string>());
        }
        catch (Exception ex)
        {
            errors.Add($"Unhandled exception during resume: {ex.Message}");
            this.logger?.LogError(ex, "[Phase73 Resume] Split resume failed. Source forge.db left intact.");
            return new SplitApplyResult(false, null, productDbPaths, rowsCopied, errors);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task EnsureSeedStateTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS forge_seed_state (Key TEXT PRIMARY KEY, DoneAt TEXT NOT NULL);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsAppliedAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM forge_seed_state WHERE Key = @key;";
        cmd.Parameters.AddWithValue("@key", AppliedMarkerKey);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is not null && Convert.ToInt32(o) > 0;
    }

    private static async Task MarkAppliedAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO forge_seed_state (Key, DoneAt) VALUES (@key, @at);";
        cmd.Parameters.AddWithValue("@key", AppliedMarkerKey);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> ExtractProductSchemaStatementsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var productTables = DbBoundary.AllTables
            .Where(t => t.Kind == DbKind.Product)
            .Select(t => t.TableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stmts = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(0);
            var name = reader.GetString(1);
            var tblName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var sql = reader.GetString(3);

            // Include CREATE TABLE for product tables + every CREATE INDEX
            // whose tbl_name is a product table. Auto-generated indexes
            // (sqlite_autoindex_*) are skipped — SQLite recreates them
            // from PK / UNIQUE clauses on the CREATE TABLE.
            if (name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase)) continue;
            if (type == "table" && productTables.Contains(name))
            {
                stmts.Add(sql);
            }
            else if (type == "index" && tblName is not null && productTables.Contains(tblName))
            {
                stmts.Add(sql);
            }
        }
        return stmts;
    }

    private static async Task CreateProductDbAsync(string dbPath, IReadOnlyList<string> schemaStmts, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        // WAL bootstrap.
        await using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragmaCmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var stmt in schemaStmts)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = stmt;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
        cmd.Parameters.AddWithValue("@n", tableName);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is not null && Convert.ToInt32(o) > 0;
    }

    private static async Task<int> SumAttachedTableAsync(
        SqliteConnection conn, string tableName, IReadOnlyList<ProductPartition> products, CancellationToken ct)
    {
        var sum = 0;
        foreach (var p in products)
        {
            var alias = SqliteAlias(p.ProductSlug);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {alias}.\"{tableName}\";";
            try
            {
                var o = await cmd.ExecuteScalarAsync(ct);
                if (o is not null) sum += Convert.ToInt32(o);
            }
            catch (SqliteException)
            {
                // Table doesn't exist in the per-product DB — treat as 0.
            }
        }
        return sum;
    }

    /// <summary>
    /// Build the INSERT-with-partition-filter SQL for one (table, product)
    /// pair. Mirrors <see cref="SplitDryRunService.BuildPartitionSql"/> but
    /// produces an <c>INSERT INTO {alias}.{table} SELECT ...</c> shape
    /// instead of <c>GROUP BY</c>.
    /// </summary>
    private static string? BuildInsertSql(DbTableSpec spec, string attachAlias, string productId)
    {
        var t = spec.TableName;
        var pidEsc = productId.Replace("'", "''");
        var dst = $"{attachAlias}.\"{t}\"";
        switch (spec.Strategy)
        {
            case PartitionStrategy.DirectProductId:
                return $"INSERT INTO {dst} SELECT * FROM \"{t}\" WHERE \"{spec.PartitionColumn}\" = '{pidEsc}';";

            case PartitionStrategy.ViaRepoId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN repos r ON r.Id = t.\"{spec.PartitionColumn}\" WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaWorkItemId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN work_items wi ON wi.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = wi.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaWorkstreamId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN workstreams w ON w.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = w.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaPrId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN prs p ON p.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = p.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaPrCommentId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN pr_comments pc ON pc.Id = t.\"{spec.PartitionColumn}\" INNER JOIN prs p ON p.Id = pc.PrId INNER JOIN repos r ON r.Id = p.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaBuildId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN builds b ON b.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = b.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaBuildStageId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN build_stages bs ON bs.Id = t.\"{spec.PartitionColumn}\" INNER JOIN builds b ON b.Id = bs.BuildId INNER JOIN repos r ON r.Id = b.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaBuildArtifactId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN build_artifacts ba ON ba.Id = t.\"{spec.PartitionColumn}\" INNER JOIN builds b ON b.Id = ba.BuildId INNER JOIN repos r ON r.Id = b.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaSyncRunId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN work_item_sync_runs s ON s.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = s.RepoId WHERE r.ProductId = '{pidEsc}';";

            case PartitionStrategy.ViaQueueId:
                return $"INSERT INTO {dst} SELECT t.* FROM \"{t}\" t INNER JOIN task_queue q ON q.Id = t.\"{spec.PartitionColumn}\" INNER JOIN work_items wi ON wi.Id = q.WorkItemId INNER JOIN repos r ON r.Id = wi.RepoId WHERE r.ProductId = '{pidEsc}';";

            default:
                return null;
        }
    }

    /// <summary>
    /// Sanitize a product slug into a SQLite-safe ATTACH alias. Slugs
    /// are already <c>[a-z0-9-]</c>; hyphens are illegal in identifiers
    /// so we convert to underscores. Prepend <c>p_</c> to avoid
    /// collisions with SQLite reserved words.
    /// </summary>
    private static string SqliteAlias(string slug) =>
        "p_" + slug.Replace('-', '_');
}

/// <summary>Result of <see cref="SplitApplyService.ApplyAsync"/>.</summary>
public sealed record SplitApplyResult(
    bool AlreadyApplied,
    string? BackupPath,
    IReadOnlyList<string> ProductDbs,
    int RowsCopied,
    IReadOnlyList<string> Errors)
{
    public bool Success => this.Errors.Count == 0;
}
