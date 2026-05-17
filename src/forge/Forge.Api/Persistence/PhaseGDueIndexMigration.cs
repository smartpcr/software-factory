// -----------------------------------------------------------------------
// <copyright file="PhaseGDueIndexMigration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase G — additive schema upgrade for existing dev DBs that pre-date the
/// control-side <c>product_runtime</c> "due" watermark. Creates the table
/// + a <c>LastPendingAt</c> index used by background workers to decide
/// which products to fan out to per tick. Idempotent.
///
/// <para>Not in the numbered <c>Phase{N}Migration</c> series because it's
/// an infrastructure addition rather than a feature phase, but invoked
/// through the same <see cref="PhaseMigrationRunner"/> pathway (registered
/// in <c>Program.cs</c> after the runner finishes the numbered sequence).</para>
/// </summary>
public static class PhaseGDueIndexMigration
{
    public static async Task ApplyAsync(ControlDbContext control, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS product_runtime (
                ProductId TEXT NOT NULL PRIMARY KEY,
                LastPendingAt TEXT NULL,
                ActivePrCount INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn,
            "CREATE INDEX IF NOT EXISTS IX_product_runtime_lastpending ON product_runtime (LastPendingAt);",
            ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
