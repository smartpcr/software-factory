// -----------------------------------------------------------------------
// <copyright file="Phase59Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 59 Stage A — adds <c>TestImpactedOnly INTEGER NULL</c> to the
/// <c>repos</c> table so an operator can override the global
/// <c>Forge:Test:ImpactedOnly</c> default per repo (null = inherit
/// global; 0 = off; 1 = on). Drives
/// <see cref="Forge.Api.Services.Stories.ImpactedTestResolver"/> in the
/// test-gate path inside
/// <c>CrewStoryArtifactStrategy.RunGateChainAsync</c>.
/// </summary>
public static class Phase59Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "repos", "TestImpactedOnly", "INTEGER NULL", ct);
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
