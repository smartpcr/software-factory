// -----------------------------------------------------------------------
// <copyright file="Phase14Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 14 — performance, scoring, and
/// promotion. Adds the level/hourly columns to <c>agents</c>, the schedule
/// columns to <c>crews</c>, and the new <c>deliverables</c> +
/// <c>agent_level_history</c> tables. Runs after <see cref="Phase12Migration"/>
/// so a fresh DB picks everything up from the EF model and an existing dev
/// DB only sees the additive changes. SQLite-only.
/// </summary>
public static class Phase14Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Phase 14.1 — agent level + payroll columns. The hourly_usd default
        // is the spec's level-0 hourly so existing rows get a sane value
        // without referencing the formula at migration time.
        await EnsureColumnAsync(conn, "agents", "Level",          "INTEGER NOT NULL DEFAULT 0",  ct);
        await EnsureColumnAsync(conn, "agents", "LevelUpdatedAt", "TEXT NULL",                    ct);
        await EnsureColumnAsync(conn, "agents", "HourlyUsd",      "REAL NOT NULL DEFAULT 4",      ct);

        // Phase 14.1 — crew schedule columns. Default timezone is the spec
        // default; ScheduleEnabled defaults true so behaviour matches the
        // gate's "9-9-6 unless explicitly disabled" semantic.
        await EnsureColumnAsync(conn, "crews", "Timezone",        "TEXT NOT NULL DEFAULT 'America/Los_Angeles'", ct);
        await EnsureColumnAsync(conn, "crews", "ScheduleEnabled", "INTEGER NOT NULL DEFAULT 1",                   ct);

        // Phase 14.1 — deliverables: one row per (work-item × iteration).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS deliverables (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                WorkItemId TEXT NOT NULL,
                CrewId TEXT NOT NULL,
                GeneratorAgentId TEXT NOT NULL,
                EvaluatorAgentId TEXT NOT NULL,
                Iter INTEGER NOT NULL DEFAULT 1,
                Score INTEGER NOT NULL DEFAULT 0,
                DimsJson TEXT NOT NULL DEFAULT '{}',
                Verdict TEXT NOT NULL DEFAULT 'iterate',
                Notes TEXT NOT NULL DEFAULT '',
                GenCostUsd REAL NOT NULL DEFAULT 0,
                EvalCostUsd REAL NOT NULL DEFAULT 0,
                JudgeAgentId TEXT NULL,
                JudgeScore INTEGER NULL,
                JudgeNotes TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_deliverables_WorkItemId ON deliverables(WorkItemId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_deliverables_CrewId_CreatedAt ON deliverables(CrewId, CreatedAt);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_deliverables_Generator_CreatedAt ON deliverables(GeneratorAgentId, CreatedAt);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_deliverables_Evaluator_CreatedAt ON deliverables(EvaluatorAgentId, CreatedAt);", ct);

        // Phase 14.1 — agent_level_history: one row per promotion/demotion.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS agent_level_history (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                AgentId TEXT NOT NULL,
                OldLevel INTEGER NOT NULL,
                NewLevel INTEGER NOT NULL,
                WindowAvg REAL NOT NULL DEFAULT 0,
                Reason TEXT NOT NULL DEFAULT 'promote',
                CreatedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_agent_level_history_AgentId_CreatedAt ON agent_level_history(AgentId, CreatedAt);", ct);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string typeDecl, CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct)) return;
        await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
