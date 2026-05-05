// -----------------------------------------------------------------------
// <copyright file="Phase43Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 43 — story attachments. Adds <c>AttachmentsJson</c> to
/// <c>story_spawns</c> so operator-uploaded reference docs are available
/// to architect agents during the plan phase.
/// </summary>
public static class Phase43Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "story_spawns", "AttachmentsJson", "TEXT", ct);
        await EnsureColumnAsync(conn, "product_stories", "AttachmentsJson", "TEXT", ct);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
        var exists = Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE [{table}] ADD COLUMN [{column}] {typeAffinity}";
        await alter.ExecuteNonQueryAsync(ct);
    }
}
