// -----------------------------------------------------------------------
// <copyright file="Phase78Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 2026-05-18 — adds <c>Avatar</c> column to <c>agents</c> table.
/// Stores the PNG filename from <c>src/assets/crews/</c> chosen by the
/// operator via the agent-edit modal. Null = use allocation-manifest default.
/// Idempotent — checks <c>pragma_table_info</c> before altering.
/// </summary>
public static class Phase78Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('agents') WHERE name = 'Avatar'";
        if (await probe.ExecuteScalarAsync(ct) is not null) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER TABLE agents ADD COLUMN Avatar TEXT NULL";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
