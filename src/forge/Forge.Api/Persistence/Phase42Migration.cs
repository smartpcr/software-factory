// -----------------------------------------------------------------------
// <copyright file="Phase42Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 42 — plan/workstream reconciliation columns. Adds:
/// <list type="bullet">
/// <item><c>workstreams.PlanAnchor</c> + <c>workstreams.PlanContentHash</c> — stable Stage-slug join key
///   from <c>implementation-plan.md</c> to a persisted workstream. Lets the reconciler match
///   "same stage, scope changed" vs "stage moved/renamed" without fuzzy text matching.</item>
/// <item><c>work_items.PlanAnchor</c> + <c>work_items.PlanContentHash</c> — same join key for child
///   coding tasks (one work item per Implementation Step).</item>
/// </list>
/// All four columns are nullable so legacy rows that predate the reconciler don't break;
/// the reconciler treats null-anchor rows as "unmapped" and leaves them alone.
/// </summary>
public static class Phase42Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "workstreams", "PlanAnchor", "TEXT", ct);
        await EnsureColumnAsync(conn, "workstreams", "PlanContentHash", "TEXT", ct);
        await EnsureColumnAsync(conn, "work_items", "PlanAnchor", "TEXT", ct);
        await EnsureColumnAsync(conn, "work_items", "PlanContentHash", "TEXT", ct);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
