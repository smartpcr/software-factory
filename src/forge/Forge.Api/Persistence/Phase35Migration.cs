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
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE crews SET ProductId = NULL WHERE ProductId IS NOT NULL";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
