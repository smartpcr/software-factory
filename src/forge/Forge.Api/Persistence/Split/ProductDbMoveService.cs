// -----------------------------------------------------------------------
// <copyright file="ProductDbMoveService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// Moves one product that lives on the control-DB fallback (created after the
/// Phase 73 split, so it never got a file) into its own <c>{slug}.db</c>.
/// See docs/forge/post-split-product-dbs.md (D3, DEC-4..DEC-8).
/// <para>
/// Offline only: the caller guarantees Forge.Api is stopped, because live
/// writers would keep adding rows to the control DB during the copy and
/// routing switches to the new file as soon as it exists.
/// </para>
/// <para>
/// Order: backup control DB → build <c>{slug}.db.moving</c> from the template
/// → copy the product's rows (explicit column lists) → verify counts and
/// primary keys → rename to <c>{slug}.db</c> → delete exactly the copied rows
/// from the control DB in one transaction. Any failure returns the host to
/// its pre-run state: the control DB is only written by the final delete
/// transaction (rolled back on failure), and the only file removed is the one
/// this run created. Backups are never deleted.
/// </para>
/// </summary>
public sealed class ProductDbMoveService
{
    internal const string MovingSuffix = ".moving";

    private readonly IForgeDbPaths paths;
    private readonly ProductDbProvisioner provisioner;
    private readonly ILogger<ProductDbMoveService>? logger;

    public ProductDbMoveService(IForgeDbPaths paths, ProductDbProvisioner provisioner, ILogger<ProductDbMoveService>? logger = null)
    {
        this.paths = paths;
        this.provisioner = provisioner;
        this.logger = logger;
    }

    /// <summary>Test seam: invoked at named points ("after-copy", "before-delete-commit"); throwing simulates a crash there.</summary>
    internal Func<string, Task>? FaultHook { get; set; }

    public async Task<ProductDbMoveResult> MoveAsync(string productId, bool dryRun, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var controlPath = this.paths.ControlDbPath();
        if (!File.Exists(controlPath)) return Fail(productId, null, "Control DB does not exist.");
        if (!SplitMarkerCache.IsPhase73Applied(this.paths))
            return Fail(productId, null, "Host is not split (no phase73-split-applied marker); every product already lives in the control DB.");

        string? slug;
        await using (var probe = Open(controlPath, SqliteOpenMode.ReadOnly))
        {
            await probe.OpenAsync(ct);
            slug = await ScalarAsync<string?>(probe, "SELECT Slug FROM products WHERE Id = $pid;", productId, ct);
        }
        if (slug is null) return Fail(productId, null, $"Unknown product '{productId}'.");
        if (!ForgeDbPaths.TryValidateSlug(slug, out var slugError)) return Fail(productId, slug, slugError ?? "Invalid slug.");

        var target = this.paths.ProductDbPath(slug);
        if (File.Exists(target))
            return Fail(productId, slug, $"'{target}' already exists. This tool never adopts or overwrites a product DB; move it aside first.");

        // Plan: per-table source counts under the DbBoundary predicates.
        List<MovePlanEntry> plan;
        await using (var control = Open(controlPath, SqliteOpenMode.ReadOnly))
        {
            await control.OpenAsync(ct);
            plan = await this.PlanAsync(control, productId, ct);
        }
        var planned = plan.Select(p => new ProductDbTableMove(p.Spec.TableName, p.SourceRows, 0, 0)).ToList();
        if (dryRun) return new ProductDbMoveResult(productId, slug, true, null, null, planned, errors);

        // 1. Backup (online API: includes un-checkpointed WAL content).
        var backupPath = $"{controlPath}.pre-move-{slug}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        await using (var src = Open(controlPath, SqliteOpenMode.ReadWrite))
        await using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
        {
            await src.OpenAsync(ct);
            await dst.OpenAsync(ct);
            src.BackupDatabase(dst);
        }
        this.logger?.LogInformation("Product move '{Slug}': control DB backed up to {Backup}.", slug, backupPath);

        var moving = target + MovingSuffix;
        var createdTarget = false;
        var results = new List<ProductDbTableMove>();
        try
        {
            // 2. Build the empty product DB under a name nothing routes to.
            ProductDbProvisioner.DeleteDbFiles(moving);
            await this.provisioner.BuildAsync(slug, moving, ct);

            // 3 + 4. Copy, then verify, with the new file attached.
            await using (var control = Open(controlPath, SqliteOpenMode.ReadWrite))
            {
                await control.OpenAsync(ct);
                await ExecAsync(control, "PRAGMA foreign_keys=OFF;", ct);
                await AttachAsync(control, moving, ct);
                await using (var tx = (SqliteTransaction)await control.BeginTransactionAsync(ct))
                {
                    foreach (var entry in plan)
                    {
                        var cols = await SharedColumnsAsync(control, entry.Spec.TableName, ct);
                        var list = string.Join(", ", cols.Select(Quote));
                        var select = string.Join(", ", cols.Select(c => "t." + Quote(c)));
                        var copied = await ExecAsync(control,
                            $"INSERT INTO dst.{Quote(entry.Spec.TableName)} ({list}) SELECT {select} {entry.From};", ct, productId, tx);
                        results.Add(new ProductDbTableMove(entry.Spec.TableName, entry.SourceRows, copied, 0));
                    }
                    await tx.CommitAsync(ct);
                }

                foreach (var entry in plan)
                {
                    var table = Quote(entry.Spec.TableName);
                    var inDst = await ScalarAsync<long>(control, $"SELECT count(*) FROM dst.{table};", null, ct);
                    var inSrc = await ScalarAsync<long>(control, $"SELECT count(*) {entry.From};", productId, ct);
                    var pk = await PrimaryKeyAsync(control, entry.Spec.TableName, ct);
                    var match = string.Join(" AND ", pk.Select(c => $"s.{Quote(c)} = d.{Quote(c)}"));
                    var unmatched = await ScalarAsync<long>(control,
                        $"SELECT count(*) FROM dst.{table} d WHERE NOT EXISTS (SELECT 1 FROM main.{table} s WHERE {match});", null, ct);
                    if (inDst != inSrc || inDst != entry.SourceRows || unmatched != 0)
                    {
                        throw new InvalidOperationException(
                            $"Verification failed for {entry.Spec.TableName}: planned {entry.SourceRows}, source now {inSrc}, copied {inDst}, unmatched keys {unmatched}.");
                    }
                }
                if (this.FaultHook is not null) await this.FaultHook("after-copy");
                await ExecAsync(control, "DETACH DATABASE dst;", ct);
            }

            // 5. Publish: from here Forge routes the product to its own file.
            File.Move(moving, target, overwrite: false);
            createdTarget = true;

            // 6. Remove exactly the copied rows (matched by primary key) from the control DB.
            await using (var control = Open(controlPath, SqliteOpenMode.ReadWrite))
            {
                await control.OpenAsync(ct);
                await ExecAsync(control, "PRAGMA foreign_keys=OFF;", ct);
                await AttachAsync(control, target, ct);
                await using (var tx = (SqliteTransaction)await control.BeginTransactionAsync(ct))
                {
                    for (var i = 0; i < results.Count; i++)
                    {
                        var table = Quote(results[i].Table);
                        var pk = (await PrimaryKeyAsync(control, results[i].Table, ct)).Select(Quote).ToList();
                        var key = pk.Count == 1 ? pk[0] : "(" + string.Join(", ", pk) + ")";
                        var deleted = await ExecAsync(control,
                            $"DELETE FROM main.{table} WHERE {key} IN (SELECT {string.Join(", ", pk)} FROM dst.{table});", ct, null, tx);
                        if (deleted != results[i].CopiedRows)
                        {
                            throw new InvalidOperationException(
                                $"Delete mismatch for {results[i].Table}: copied {results[i].CopiedRows}, would delete {deleted}.");
                        }
                        results[i] = results[i] with { DeletedRows = deleted };
                    }
                    if (this.FaultHook is not null) await this.FaultHook("before-delete-commit");
                    await tx.CommitAsync(ct);
                }
                await ExecAsync(control, "DETACH DATABASE dst;", ct);
            }
        }
        catch (Exception ex)
        {
            // The control DB is unchanged (delete transaction rolled back or
            // never started); remove only the file this run created.
            SqliteConnection.ClearAllPools();
            ProductDbProvisioner.DeleteDbFiles(moving);
            if (createdTarget) ProductDbProvisioner.DeleteDbFiles(target);
            this.logger?.LogError(ex, "Product move '{Slug}' failed; control DB left unchanged, backup kept at {Backup}.", slug, backupPath);
            errors.Add(ex.Message);
            return new ProductDbMoveResult(productId, slug, false, backupPath, null, results.Count > 0 ? results : planned, errors);
        }

        this.logger?.LogInformation("Product move '{Slug}': {Rows} rows moved into {Target}.", slug, results.Sum(r => r.CopiedRows), target);
        return new ProductDbMoveResult(productId, slug, false, backupPath, target, results, errors);
    }

    /// <summary>Tables to move: DbBoundary product tables with an owner predicate, plus <c>repos</c> (moved per product in Phase 80.B).</summary>
    internal static IReadOnlyList<DbTableSpec> MoveTables() =>
        DbBoundary.AllTables
            .Where(t => t.Kind == DbKind.Product && t.Strategy is not (PartitionStrategy.NoPartitionKey or PartitionStrategy.Global))
            .Append(new DbTableSpec("repos", DbKind.Product, PartitionStrategy.DirectProductId, "ProductId"))
            .ToList();

    /// <summary>The FROM/JOIN/WHERE that selects a product's rows of <paramref name="spec"/> (alias <c>t</c>, parameter <c>$pid</c>).</summary>
    internal static string SourceFrom(DbTableSpec spec)
    {
        var t = $"FROM main.{Quote(spec.TableName)} t";
        var c = "t." + Quote(spec.PartitionColumn ?? throw new InvalidOperationException($"{spec.TableName} has no partition column."));
        const string Repo = "INNER JOIN main.repos r ON r.Id = {0} WHERE r.ProductId = $pid";
        return spec.Strategy switch
        {
            PartitionStrategy.DirectProductId => $"{t} WHERE {c} = $pid",
            PartitionStrategy.ViaRepoId => $"{t} " + string.Format(Repo, c),
            PartitionStrategy.ViaWorkItemId => $"{t} INNER JOIN main.work_items wi ON wi.Id = {c} " + string.Format(Repo, "wi.RepoId"),
            PartitionStrategy.ViaWorkstreamId => $"{t} INNER JOIN main.workstreams w ON w.Id = {c} " + string.Format(Repo, "w.RepoId"),
            PartitionStrategy.ViaPrId => $"{t} INNER JOIN main.prs p ON p.Id = {c} " + string.Format(Repo, "p.RepoId"),
            PartitionStrategy.ViaPrCommentId => $"{t} INNER JOIN main.pr_comments pc ON pc.Id = {c} INNER JOIN main.prs p ON p.Id = pc.PrId " + string.Format(Repo, "p.RepoId"),
            PartitionStrategy.ViaBuildId => $"{t} INNER JOIN main.builds b ON b.Id = {c} " + string.Format(Repo, "b.RepoId"),
            PartitionStrategy.ViaBuildStageId => $"{t} INNER JOIN main.build_stages bs ON bs.Id = {c} INNER JOIN main.builds b ON b.Id = bs.BuildId " + string.Format(Repo, "b.RepoId"),
            PartitionStrategy.ViaBuildArtifactId => $"{t} INNER JOIN main.build_artifacts ba ON ba.Id = {c} INNER JOIN main.builds b ON b.Id = ba.BuildId " + string.Format(Repo, "b.RepoId"),
            PartitionStrategy.ViaSyncRunId => $"{t} INNER JOIN main.work_item_sync_runs s ON s.Id = {c} " + string.Format(Repo, "s.RepoId"),
            PartitionStrategy.ViaQueueId => $"{t} INNER JOIN main.task_queue q ON q.Id = {c} INNER JOIN main.work_items wi ON wi.Id = q.WorkItemId " + string.Format(Repo, "wi.RepoId"),
            _ => throw new InvalidOperationException($"No row predicate for {spec.TableName} ({spec.Strategy})."),
        };
    }

    private async Task<List<MovePlanEntry>> PlanAsync(SqliteConnection control, string productId, CancellationToken ct)
    {
        var plan = new List<MovePlanEntry>();
        foreach (var spec in MoveTables())
        {
            var exists = await ScalarAsync<long>(control,
                $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{spec.TableName}';", null, ct) > 0;
            if (!exists) continue;
            var from = SourceFrom(spec);
            plan.Add(new MovePlanEntry(spec, from, await ScalarAsync<long>(control, $"SELECT count(*) {from};", productId, ct)));
        }
        return plan;
    }

    /// <summary>Source columns, by name; every one must exist in the destination (column order may differ, DEC-5).</summary>
    private static async Task<List<string>> SharedColumnsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        var src = await ColumnsAsync(conn, "main", table, ct);
        var dst = (await ColumnsAsync(conn, "dst", table, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = src.Where(c => !dst.Contains(c)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"{table}: destination lacks column(s) {string.Join(", ", missing)}; refusing to drop data.");
        return src;
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection conn, string schema, string table, CancellationToken ct)
    {
        var cols = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({Quote(table)});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) cols.Add(reader.GetString(1));
        return cols;
    }

    private static async Task<List<string>> PrimaryKeyAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        var pk = new List<(int Order, string Name)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA main.table_info({Quote(table)});";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (reader.GetInt32(5) > 0) pk.Add((reader.GetInt32(5), reader.GetString(1)));
        }
        if (pk.Count == 0) throw new InvalidOperationException($"{table} has no primary key; rows cannot be matched for verification.");
        return pk.OrderBy(p => p.Order).Select(p => p.Name).ToList();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
        DefaultTimeout = 30,
    }.ToString());

    private static async Task AttachAsync(SqliteConnection conn, string path, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "ATTACH DATABASE $path AS dst;";
        cmd.Parameters.AddWithValue("$path", path);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ExecAsync(SqliteConnection conn, string sql, CancellationToken ct, string? productId = null, SqliteTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (productId is not null) cmd.Parameters.AddWithValue("$pid", productId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection conn, string sql, string? productId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (productId is not null) cmd.Parameters.AddWithValue("$pid", productId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static ProductDbMoveResult Fail(string productId, string? slug, string error) =>
        new(productId, slug, false, null, null, Array.Empty<ProductDbTableMove>(), new[] { error });

    private sealed record MovePlanEntry(DbTableSpec Spec, string From, long SourceRows);
}

/// <summary>Per-table outcome of a product move.</summary>
public sealed record ProductDbTableMove(string Table, long SourceRows, long CopiedRows, long DeletedRows);

/// <summary>Outcome of <see cref="ProductDbMoveService.MoveAsync"/>.</summary>
public sealed record ProductDbMoveResult(
    string ProductId,
    string? Slug,
    bool DryRun,
    string? BackupPath,
    string? ProductDbPath,
    IReadOnlyList<ProductDbTableMove> Tables,
    IReadOnlyList<string> Errors)
{
    public bool Success => this.Errors.Count == 0;
}