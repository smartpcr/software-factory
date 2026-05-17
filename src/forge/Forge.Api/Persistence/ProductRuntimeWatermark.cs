// -----------------------------------------------------------------------
// <copyright file="ProductRuntimeWatermark.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase G — convenience helpers for maintaining the control-side
/// <c>product_runtime</c> watermark from background write paths.
///
/// <para>Use:</para>
/// <list type="bullet">
///   <item><see cref="StampPendingAsync"/> when a write path enqueues
///         work that a background worker should pick up (task_queue
///         INSERT, story_spawn create, build job submit). The watermark
///         flips <c>LastPendingAt = now</c> so the next worker tick
///         sees the product as due.</item>
///   <item><see cref="SetActivePrCountAsync"/> when a PR open/merge/close
///         happens. The watermark snapshots the open-PR count so
///         AutoMergeWatcher can skip products with 0 open PRs.</item>
///   <item><see cref="ListDueProductsAsync"/> from a worker tick to get
///         only the products with pending work, avoiding the cost of
///         opening every <c>{slug}.db</c>.</item>
/// </list>
///
/// <para>UPSERT semantics: SQLite's <c>INSERT … ON CONFLICT (ProductId) DO UPDATE</c>
/// is used so concurrent writers from parallel tick workers never miss an
/// update. Pre-Phase-F this is the same forge.db file as the product
/// tables, so the watermark is just a tiny optimization hint; post-Phase-F
/// it avoids the much more expensive per-product DB open.</para>
/// </summary>
public static class ProductRuntimeWatermark
{
    /// <summary>
    /// UPSERT <c>product_runtime.LastPendingAt = now</c> for the given
    /// product. Safe to call from any write path that produces pending work
    /// (task_queue enqueue, story_spawn create, build job submit).
    /// </summary>
    public static async Task StampPendingAsync(
        ControlDbContext control, string productId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(productId)) return;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO product_runtime (ProductId, LastPendingAt, ActivePrCount, UpdatedAt)
            VALUES (@id, @at, 0, @at)
            ON CONFLICT(ProductId) DO UPDATE SET
                LastPendingAt = excluded.LastPendingAt,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("@id", productId);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// UPSERT the active PR count for a product. Called by PR write paths
    /// (open / merge / close) so <c>AutoMergeWatcher</c> can skip
    /// zero-active-PR products without opening their per-product DB.
    /// </summary>
    public static async Task SetActivePrCountAsync(
        ControlDbContext control, string productId, int activePrCount, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(productId)) return;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO product_runtime (ProductId, LastPendingAt, ActivePrCount, UpdatedAt)
            VALUES (@id, NULL, @count, @at)
            ON CONFLICT(ProductId) DO UPDATE SET
                ActivePrCount = excluded.ActivePrCount,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("@id", productId);
        cmd.Parameters.AddWithValue("@count", activePrCount);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Return the product slugs whose <c>LastPendingAt</c> is non-null
    /// (i.e., they have pending work) OR <c>ActivePrCount &gt; 0</c>
    /// (open PRs that may need merge-watcher attention). Workers use this
    /// to filter <c>ProductFanout</c>'s product list.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListDueProductsAsync(
        ControlDbContext control, CancellationToken ct = default)
    {
        var dueProductIds = await control.ProductRuntime
            .AsNoTracking()
            .Where(r => r.LastPendingAt != null || r.ActivePrCount > 0)
            .Select(r => r.ProductId)
            .ToListAsync(ct);

        if (dueProductIds.Count == 0) return Array.Empty<string>();

        var slugs = await control.Products
            .AsNoTracking()
            .Where(p => dueProductIds.Contains(p.Id))
            .Select(p => p.Slug)
            .ToListAsync(ct);

        return slugs;
    }

    /// <summary>
    /// Clear <c>LastPendingAt</c> on a product (e.g., after a worker tick
    /// drains its task queue). The product becomes "not due" until the
    /// next write path stamps it again. Idempotent.
    /// </summary>
    public static async Task ClearPendingAsync(
        ControlDbContext control, string productId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(productId)) return;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE product_runtime
            SET LastPendingAt = NULL, UpdatedAt = @at
            WHERE ProductId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", productId);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
