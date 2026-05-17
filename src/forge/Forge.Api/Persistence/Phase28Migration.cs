// -----------------------------------------------------------------------
// <copyright file="Phase28Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 28 — PR diff statistics enrichment.
/// Adds the <c>Updates</c> column (ADO "Updates" tab count = iteration count)
/// to the <c>prs</c> table so the PR card and detail panel can display it
/// alongside commits / files.
/// </summary>
public static class Phase28Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "prs", "Updates", "INTEGER NOT NULL DEFAULT 0", ct);

        // Repair: an earlier PrSyncJob bug stored ADO canonical states ("open" /
        // "abandoned" / "completed") into PrRow.Col, which doesn't match the
        // UI's PrColumn enum (implementation | build | review | merged) — those
        // rows vanished from the board. Map them back to valid UI columns.
        // Idempotent: re-running on a clean DB is a no-op.
        await using var fixOpen = conn.CreateCommand();
        fixOpen.CommandText = "UPDATE prs SET Col = 'review' WHERE Col = 'open'";
        await fixOpen.ExecuteNonQueryAsync(ct);

        await using var fixCompleted = conn.CreateCommand();
        fixCompleted.CommandText = "UPDATE prs SET Col = 'merged' WHERE Col IN ('completed', 'abandoned')";
        await fixCompleted.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alterCmd.ExecuteNonQueryAsync(ct);
    }
}
