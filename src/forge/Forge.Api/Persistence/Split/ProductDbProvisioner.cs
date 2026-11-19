// -----------------------------------------------------------------------
// <copyright file="ProductDbProvisioner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>
/// Creates the per-product <c>{slug}.db</c> for a product added after the
/// Phase 73 split. Without it the product's rows fall back into the control
/// DB (<see cref="ProductDbContextFactory.CreateFor"/>), mixing products in
/// one file.
/// <para>
/// The schema is copied from an existing product DB (see
/// <see cref="TemplatePathAsync"/>), so the new file matches what the split
/// produced and what the per-product migration fan-out maintains. The file is
/// built under a
/// temporary name and moved into place only when complete, so a half-built
/// file is never routed to.
/// </para>
/// </summary>
public sealed class ProductDbProvisioner
{
    private const string TempSuffix = ".provisioning";

    private readonly IForgeDbPaths paths;
    private readonly ILogger<ProductDbProvisioner>? logger;

    public ProductDbProvisioner(IForgeDbPaths paths, ILogger<ProductDbProvisioner>? logger = null)
    {
        this.paths = paths;
        this.logger = logger;
    }

    /// <summary>
    /// Create <c>{slug}.db</c> when the host has been split. Never overwrites
    /// or adopts an existing file: its contents are unknown, so the caller
    /// must refuse rather than route a new product onto them.
    /// </summary>
    public async Task<ProductDbProvisionResult> ProvisionAsync(string slug, CancellationToken ct = default)
    {
        if (!SplitMarkerCache.IsPhase73Applied(this.paths))
        {
            // Pre-split hosts keep every product in the control DB.
            return ProductDbProvisionResult.NotNeeded;
        }

        var target = this.paths.ProductDbPath(slug);
        if (File.Exists(target))
        {
            return ProductDbProvisionResult.Conflict(target);
        }

        // A leftover temp file can only come from an earlier attempt for this
        // slug that crashed before the move; nothing routes to it.
        var temp = target + TempSuffix;
        DeleteDbFiles(temp);
        int statements;
        try
        {
            statements = await this.BuildAsync(slug, temp, ct);

            // No overwrite: a file that appeared meanwhile is someone else's.
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            DeleteDbFiles(temp);
            throw;
        }

        this.logger?.LogInformation(
            "Provisioned per-product DB {Path} for product slug '{Slug}' ({Count} schema statements).",
            target, slug, statements);
        return ProductDbProvisionResult.Created(target);
    }

    /// <summary>
    /// Build an empty product DB at <paramref name="path"/>: the template's
    /// product tables and <c>repos</c>, plus the canonical board columns that
    /// every product DB carries (boot only backfills them on the next start).
    /// One transaction; the caller owns <paramref name="path"/> and moves it
    /// into place. Returns the number of schema statements applied.
    /// </summary>
    internal async Task<int> BuildAsync(string slug, string path, CancellationToken ct)
    {
        var templatePath = await this.TemplatePathAsync(slug, ct);
        List<string> schema;
        await using (var template = OpenReadOnly(templatePath))
        {
            await template.OpenAsync(ct);
            schema = await SplitApplyService.ExtractProductSchemaStatementsAsync(template, ct);
            if (schema.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Template DB '{templatePath}' has no product tables to copy a schema from.");
            }

            // `repos` moved into every {slug}.db in Phase 80.B
            // (RepoMoveToProductService) but DbBoundary still classifies it
            // as Control, so the split's extractor skips it.
            var repos = await ReposSchemaAsync(template, ct);
            if (repos.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Template DB '{templatePath}' has no repos table to copy a schema from.");
            }
            schema.AddRange(repos);
        }

        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await conn.OpenAsync(ct);
        await using (var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct))
        {
            foreach (var stmt in schema)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var column in ForgeSeed.CanonicalBoardColumns())
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO \"columns\" (\"Id\", \"Name\", \"Sort\", \"WipLimit\") VALUES ($id, $name, $sort, $wip);";
                cmd.Parameters.AddWithValue("$id", column.Id);
                cmd.Parameters.AddWithValue("$name", column.Name);
                cmd.Parameters.AddWithValue("$sort", column.Sort);
                cmd.Parameters.AddWithValue("$wip", (object?)column.WipLimit ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }
        return schema.Count;
    }

    /// <summary>
    /// Remove a file this provisioner created in the current operation, e.g.
    /// when the product row it was made for fails to save. Only call it with a
    /// path returned by <see cref="ProvisionAsync"/> in the same operation.
    /// </summary>
    public void Discard(string createdPath)
    {
        SqliteConnection.ClearAllPools();
        DeleteDbFiles(createdPath);
    }

    /// <summary>
    /// Schema source: an existing product's {slug}.db when there is one. Those
    /// files carry the split's raw DDL (column DEFAULTs that raw-SQL writers
    /// rely on) and are kept current by the per-product migration fan-out.
    /// The control DB's product tables come from EF's generated script, which
    /// omits those defaults, so they are only the fallback when no product DB
    /// exists yet.
    /// </summary>
    private async Task<string> TemplatePathAsync(string newSlug, CancellationToken ct)
    {
        var controlPath = this.paths.ControlDbPath();
        var slugs = new List<string>();
        await using (var control = OpenReadOnly(controlPath))
        {
            await control.OpenAsync(ct);
            await using var cmd = control.CreateCommand();
            cmd.CommandText = "SELECT Slug FROM products ORDER BY Sort, Id;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(0)) slugs.Add(reader.GetString(0));
        }
        foreach (var slug in slugs)
        {
            if (string.Equals(slug, newSlug, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ForgeDbPaths.TryValidateSlug(slug, out _)) continue;
            if (this.paths.ProductDbExists(slug)) return this.paths.ProductDbPath(slug);
        }
        return controlPath;
    }

    private static SqliteConnection OpenReadOnly(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString());

    private static async Task<List<string>> ReposSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        var stmts = new List<string>();
        await using var cmd = conn.CreateCommand();
        // Table first (rowid order), then its explicit indexes.
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name = 'repos' AND sql IS NOT NULL "
            + "AND type IN ('table', 'index') ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, rowid;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) stmts.Add(reader.GetString(0));
        return stmts;
    }

    internal static void DeleteDbFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

/// <summary>Outcome of <see cref="ProductDbProvisioner.ProvisionAsync"/>.</summary>
public sealed record ProductDbProvisionResult(string? CreatedPath, string? ConflictPath)
{
    public static readonly ProductDbProvisionResult NotNeeded = new(null, null);

    public static ProductDbProvisionResult Created(string path) => new(path, null);

    public static ProductDbProvisionResult Conflict(string path) => new(null, path);
}