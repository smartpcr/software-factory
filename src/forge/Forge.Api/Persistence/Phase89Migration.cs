// -----------------------------------------------------------------------
// <copyright file="Phase89Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 89 — wire per-agent PO override actions (promote / demote / hold)
/// from the Performance view's <c>DecisionCard</c>. Adds a nullable
/// <c>Actor</c> column to <c>agent_level_history</c> so operator-initiated
/// level changes carry the caller identity, complementing the existing
/// nightly <c>PromotionJob</c> rows (which leave <c>Actor</c> NULL).
/// <para>
/// Idempotent: <c>pragma_table_info</c>-guarded <c>ALTER TABLE</c>, matching
/// <see cref="Phase88Migration"/> and the rest of the additive-column
/// migrations.
/// </para>
/// </summary>
public static class Phase89Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "agent_level_history", "Actor", "TEXT NULL", ct);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using (var tableProbe = conn.CreateCommand())
        {
            tableProbe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = @t";
            tableProbe.Parameters.AddWithValue("@t", table);
            if (await tableProbe.ExecuteScalarAsync(ct) is null) return;
        }

        await using (var columnProbe = conn.CreateCommand())
        {
            columnProbe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
            columnProbe.Parameters.AddWithValue("@name", column);
            if (await columnProbe.ExecuteScalarAsync(ct) is not null) return;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
