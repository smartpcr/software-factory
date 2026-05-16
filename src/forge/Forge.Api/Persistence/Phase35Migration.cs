// -----------------------------------------------------------------------
// <copyright file="Phase35Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Decouple crews from products — NULL out the now-unused ProductId column.
/// The column is left in place (SQLite doesn't support DROP COLUMN cleanly)
/// but EF no longer maps it so new rows won't populate it.
/// </summary>
public static class Phase35Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // ProductId only exists on DBs created before EF stopped mapping it.
        // Fresh DBs (test factory's per-test temp file) build the schema from
        // the current model and skip this column entirely, so the UPDATE
        // would fail with "no such column".
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('crews') WHERE name = 'ProductId'";
            if (await probe.ExecuteScalarAsync(ct) is null) return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE crews SET ProductId = NULL WHERE ProductId IS NOT NULL";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
