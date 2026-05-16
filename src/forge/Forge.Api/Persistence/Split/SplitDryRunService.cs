// -----------------------------------------------------------------------
// <copyright file="SplitDryRunService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

using Microsoft.Data.Sqlite;

/// <summary>
/// Phase B — analyzes the current <c>forge.db</c> against the
/// <see cref="DbBoundary"/> catalog and produces a
/// <see cref="SplitDryRunReport"/>. Read-only; never writes to the DB.
///
/// <para>Runs as a singleton service so it can be invoked from the admin
/// endpoint and from the Phase F migration step (which reuses the same
/// partition queries).</para>
/// </summary>
public sealed class SplitDryRunService
{
    private readonly IForgeDbPaths paths;

    public SplitDryRunService(IForgeDbPaths paths)
    {
        this.paths = paths;
    }

    /// <summary>
    /// Analyse the control DB and return the partition report. Pure read.
    /// </summary>
    public async Task<SplitDryRunReport> AnalyzeAsync(CancellationToken ct = default)
    {
        var dbPath = this.paths.ControlDbPath();
        var dbExists = File.Exists(dbPath);
        var dbSize = dbExists ? new FileInfo(dbPath).Length : 0L;

        if (!dbExists)
        {
            return new SplitDryRunReport(
                GeneratedAt: DateTimeOffset.UtcNow,
                ControlDbPath: dbPath,
                ControlDbSizeBytes: 0,
                TotalRows: 0,
                ControlRows: 0,
                ProductRows: 0,
                OrphanRows: 0,
                UnknownRows: 0,
                Tables: Array.Empty<TableSummary>(),
                Products: Array.Empty<ProductPartition>(),
                Orphans: Array.Empty<OrphanWarning>(),
                UnknownTables: Array.Empty<UnknownTable>());
        }

        // Open read-only so we never accidentally write back.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        var actualTables = await ListTablesAsync(conn, ct);
        var products = await ListProductsAsync(conn, ct); // id → slug
        var perProduct = products.ToDictionary(
            p => p.Key,
            _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var tables = new List<TableSummary>();
        var orphans = new List<OrphanWarning>();
        var unknown = new List<UnknownTable>();

        // 1) Walk every known table that exists in the DB.
        foreach (var spec in DbBoundary.AllTables)
        {
            if (!actualTables.Contains(spec.TableName)) continue;

            TableSummary summary;
            try
            {
                summary = await AnalyzeTableAsync(conn, spec, perProduct, orphans, ct);
            }
            catch (SqliteException ex)
            {
                // Schema drift: a column referenced by the partition SQL
                // doesn't exist in this DB. Surface as a per-table orphan
                // warning so the rest of the report still completes.
                var total = await CountRowsAsync(conn, spec.TableName, ct);
                orphans.Add(new OrphanWarning(
                    TableName: spec.TableName,
                    Kind: "schema-mismatch",
                    Count: total,
                    Detail: $"Could not partition '{spec.TableName}' using strategy '{spec.Strategy}' on column '{spec.PartitionColumn}'. SQLite says: {ex.Message}. Likely the boundary catalog is out of date with the actual schema."));
                summary = new TableSummary(
                    TableName: spec.TableName,
                    Kind: spec.Kind == DbKind.Control ? "control" : "product",
                    Strategy: spec.Strategy.ToString().ToLowerInvariant(),
                    PartitionColumn: spec.PartitionColumn,
                    TotalRows: total,
                    ClassifiedRows: 0,
                    OrphanRows: total);
            }
            tables.Add(summary);
            actualTables.Remove(spec.TableName);
        }

        // 2) Anything that exists in the DB but isn't in our catalog is
        //    surfaced as an Unknown row — operator either adds it to
        //    DbBoundary or drops it via a phase migration before Phase F.
        foreach (var name in actualTables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            // Skip SQLite-internal tables.
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip EF Core migration history.
            if (string.Equals(name, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase)) continue;

            var count = await CountRowsAsync(conn, name, ct);
            unknown.Add(new UnknownTable(name, count));
        }

        // 3) Build per-product rollup.
        var productPartitions = new List<ProductPartition>();
        foreach (var (productId, slug) in products.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var rows = perProduct[productId];
            var total = rows.Values.Sum();
            productPartitions.Add(new ProductPartition(
                ProductId: productId,
                ProductSlug: slug,
                TotalRows: total,
                RowsPerTable: rows));
        }

        var controlRows = tables.Where(t => t.Kind == "control").Sum(t => t.TotalRows);
        var productRows = tables.Where(t => t.Kind == "product").Sum(t => t.ClassifiedRows);
        var orphanRows = tables.Sum(t => t.OrphanRows);
        var unknownRows = unknown.Sum(t => t.RowCount);

        return new SplitDryRunReport(
            GeneratedAt: DateTimeOffset.UtcNow,
            ControlDbPath: dbPath,
            ControlDbSizeBytes: dbSize,
            TotalRows: controlRows + productRows + orphanRows + unknownRows,
            ControlRows: controlRows,
            ProductRows: productRows,
            OrphanRows: orphanRows,
            UnknownRows: unknownRows,
            Tables: tables,
            Products: productPartitions,
            Orphans: orphans,
            UnknownTables: unknown);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<HashSet<string>> ListTablesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private static async Task<Dictionary<string, string>> ListProductsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Slug FROM products";
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var slug = reader.IsDBNull(1) ? id : reader.GetString(1);
                map[id] = slug;
            }
        }
        catch (SqliteException)
        {
            // products table missing — fresh DB or pre-Phase-8 schema. Caller
            // sees an empty product list and every product table reports
            // every row as "unknown-product".
        }
        return map;
    }

    private static async Task<int> CountRowsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is null ? 0 : Convert.ToInt32(o);
    }

    private async Task<TableSummary> AnalyzeTableAsync(
        SqliteConnection conn,
        DbTableSpec spec,
        Dictionary<string, Dictionary<string, int>> perProduct,
        List<OrphanWarning> orphans,
        CancellationToken ct)
    {
        var total = await CountRowsAsync(conn, spec.TableName, ct);

        if (spec.Kind == DbKind.Control)
        {
            return new TableSummary(
                TableName: spec.TableName,
                Kind: "control",
                Strategy: PartitionStrategy.Global.ToString().ToLowerInvariant(),
                PartitionColumn: null,
                TotalRows: total,
                ClassifiedRows: total,
                OrphanRows: 0);
        }

        // Product table — partition by strategy.
        var (sqlPartition, sqlOrphan) = BuildPartitionSql(spec);

        var classified = 0;
        var orphanCount = 0;

        if (spec.Strategy == PartitionStrategy.NoPartitionKey)
        {
            // No way to partition; the whole table is flagged.
            orphanCount = total;
            orphans.Add(new OrphanWarning(
                TableName: spec.TableName,
                Kind: "no-partition-strategy",
                Count: total,
                Detail: $"Table '{spec.TableName}' has no partition column today; Phase F will route all rows to the default product unless a ProductId column is added first."));

            return new TableSummary(
                TableName: spec.TableName,
                Kind: "product",
                Strategy: spec.Strategy.ToString().ToLowerInvariant(),
                PartitionColumn: spec.PartitionColumn,
                TotalRows: total,
                ClassifiedRows: 0,
                OrphanRows: orphanCount);
        }

        // Run the partition query → group by product_id.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sqlPartition!;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var n = Convert.ToInt32(reader.GetValue(1));
                if (string.IsNullOrEmpty(productId))
                {
                    // SQL guarantees NULL only when the chain broke (LEFT JOIN
                    // didn't resolve) or the column itself was NULL. Treat
                    // as orphan; the orphan query below disambiguates.
                    continue;
                }
                if (!perProduct.TryGetValue(productId, out var bucket))
                {
                    // Unknown product id — emit warning and skip the bucket.
                    orphans.Add(new OrphanWarning(
                        TableName: spec.TableName,
                        Kind: "unknown-product",
                        Count: n,
                        Detail: $"{n} row(s) in '{spec.TableName}' resolve to product_id='{productId}', which is not in 'products'. Likely a stale FK from a deleted product."));
                    orphanCount += n;
                    continue;
                }
                bucket.TryGetValue(spec.TableName, out var existing);
                bucket[spec.TableName] = existing + n;
                classified += n;
            }
        }

        // Run the orphan query → rows whose chain didn't resolve at all.
        if (sqlOrphan is not null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sqlOrphan;
            var o = await cmd.ExecuteScalarAsync(ct);
            var brokenChain = o is null ? 0 : Convert.ToInt32(o);
            if (brokenChain > 0)
            {
                orphans.Add(new OrphanWarning(
                    TableName: spec.TableName,
                    Kind: "broken-foreign-key",
                    Count: brokenChain,
                    Detail: $"{brokenChain} row(s) in '{spec.TableName}' have a {spec.PartitionColumn} that doesn't chain back to a repo (and therefore to a product). Likely orphaned by a cascade-delete bug."));
                orphanCount += brokenChain;
            }
        }

        return new TableSummary(
            TableName: spec.TableName,
            Kind: "product",
            Strategy: spec.Strategy.ToString().ToLowerInvariant(),
            PartitionColumn: spec.PartitionColumn,
            TotalRows: total,
            ClassifiedRows: classified,
            OrphanRows: orphanCount);
    }

    /// <summary>
    /// Returns (partition-by-product SQL, orphan-count SQL) for a product
    /// table. The partition SQL groups rows by the resolved product id;
    /// rows whose chain broke (LEFT JOIN missed) are excluded from that
    /// result and counted separately by the orphan SQL.
    /// </summary>
    private static (string? Partition, string? Orphan) BuildPartitionSql(DbTableSpec spec)
    {
        var t = spec.TableName;
        switch (spec.Strategy)
        {
            case PartitionStrategy.DirectProductId:
                // Group by the table's own ProductId column directly.
                return (
                    $"SELECT \"{spec.PartitionColumn}\", COUNT(*) FROM \"{t}\" WHERE \"{spec.PartitionColumn}\" IS NOT NULL GROUP BY \"{spec.PartitionColumn}\"",
                    $"SELECT COUNT(*) FROM \"{t}\" WHERE \"{spec.PartitionColumn}\" IS NULL");

            case PartitionStrategy.ViaRepoId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN repos r ON r.Id = t.\"{spec.PartitionColumn}\" GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN repos r ON r.Id = t.\"{spec.PartitionColumn}\" WHERE r.Id IS NULL");

            case PartitionStrategy.ViaWorkItemId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN work_items wi ON wi.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = wi.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN work_items wi ON wi.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN repos r ON r.Id = wi.RepoId WHERE wi.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaWorkstreamId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN workstreams w ON w.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = w.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN workstreams w ON w.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN repos r ON r.Id = w.RepoId WHERE w.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaPrId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN prs p ON p.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = p.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN prs p ON p.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN repos r ON r.Id = p.RepoId WHERE p.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaPrCommentId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN pr_comments pc ON pc.Id = t.\"{spec.PartitionColumn}\" INNER JOIN prs p ON p.Id = pc.PrId INNER JOIN repos r ON r.Id = p.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN pr_comments pc ON pc.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN prs p ON p.Id = pc.PrId LEFT JOIN repos r ON r.Id = p.RepoId WHERE pc.Id IS NULL OR p.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaBuildId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN builds b ON b.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = b.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN builds b ON b.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN repos r ON r.Id = b.RepoId WHERE b.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaBuildStageId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN build_stages bs ON bs.Id = t.\"{spec.PartitionColumn}\" INNER JOIN builds b ON b.Id = bs.BuildId INNER JOIN repos r ON r.Id = b.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN build_stages bs ON bs.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN builds b ON b.Id = bs.BuildId LEFT JOIN repos r ON r.Id = b.RepoId WHERE bs.Id IS NULL OR b.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaBuildArtifactId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN build_artifacts ba ON ba.Id = t.\"{spec.PartitionColumn}\" INNER JOIN builds b ON b.Id = ba.BuildId INNER JOIN repos r ON r.Id = b.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN build_artifacts ba ON ba.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN builds b ON b.Id = ba.BuildId LEFT JOIN repos r ON r.Id = b.RepoId WHERE ba.Id IS NULL OR b.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaSyncRunId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN work_item_sync_runs s ON s.Id = t.\"{spec.PartitionColumn}\" INNER JOIN repos r ON r.Id = s.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN work_item_sync_runs s ON s.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN repos r ON r.Id = s.RepoId WHERE s.Id IS NULL OR r.Id IS NULL");

            case PartitionStrategy.ViaQueueId:
                return (
                    $"SELECT r.ProductId, COUNT(*) FROM \"{t}\" t INNER JOIN task_queue q ON q.Id = t.\"{spec.PartitionColumn}\" INNER JOIN work_items wi ON wi.Id = q.WorkItemId INNER JOIN repos r ON r.Id = wi.RepoId GROUP BY r.ProductId",
                    $"SELECT COUNT(*) FROM \"{t}\" t LEFT JOIN task_queue q ON q.Id = t.\"{spec.PartitionColumn}\" LEFT JOIN work_items wi ON wi.Id = q.WorkItemId LEFT JOIN repos r ON r.Id = wi.RepoId WHERE q.Id IS NULL OR wi.Id IS NULL OR r.Id IS NULL");

            default:
                return (null, null);
        }
    }
}
