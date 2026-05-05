// -----------------------------------------------------------------------
// <copyright file="Phase36Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Add Reaction column to pr_comments for thumbsup / thumbsdown rating.
/// </summary>
public static class Phase36Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('pr_comments') WHERE name = 'Reaction'";
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE pr_comments ADD COLUMN Reaction TEXT";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
