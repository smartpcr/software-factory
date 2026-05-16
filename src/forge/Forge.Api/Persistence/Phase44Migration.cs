// -----------------------------------------------------------------------
// <copyright file="Phase44Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 44 — agent timeslot tracking. Creates the <c>agent_claims</c> table
/// and adds <c>MaxConcurrentTasks</c> to <c>agents</c> so the scheduler can
/// enforce single-task-per-agent and track busy/idle state durably.
/// </summary>
public static class Phase44Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureTableAsync(conn, ct);
        await EnsureColumnAsync(conn, "agents", "MaxConcurrentTasks", "INTEGER NOT NULL DEFAULT 1", ct);
    }

    private static async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_claims'";
        var exists = Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE agent_claims (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                AgentId      TEXT    NOT NULL,
                WorkItemId   TEXT    NOT NULL,
                Role         TEXT    NOT NULL DEFAULT 'generator',
                ClaimedAt    TEXT    NOT NULL,
                ReleasedAt   TEXT,
                InputTokens  INTEGER NOT NULL DEFAULT 0,
                OutputTokens INTEGER NOT NULL DEFAULT 0,
                CostUsd      REAL    NOT NULL DEFAULT 0
            )
            """;
        await create.ExecuteNonQueryAsync(ct);

        await using var idx1 = conn.CreateCommand();
        idx1.CommandText = "CREATE INDEX IF NOT EXISTS IX_agent_claims_agent_released ON agent_claims (AgentId, ReleasedAt)";
        await idx1.ExecuteNonQueryAsync(ct);

        await using var idx2 = conn.CreateCommand();
        idx2.CommandText = "CREATE INDEX IF NOT EXISTS IX_agent_claims_workitem ON agent_claims (WorkItemId)";
        await idx2.ExecuteNonQueryAsync(ct);
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
