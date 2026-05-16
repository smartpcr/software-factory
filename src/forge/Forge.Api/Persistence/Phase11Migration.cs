// -----------------------------------------------------------------------
// <copyright file="Phase11Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 11 — the agent_usage cost ledger.
/// Runs after <see cref="Phase8Migration"/> so a fresh DB picks everything up
/// from the EF model and an existing dev DB only sees the additive change.
/// SQLite-only.
/// </summary>
public static class Phase11Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS agent_usage (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                AgentId TEXT NOT NULL,
                CrewId TEXT NOT NULL,
                TaskId TEXT NOT NULL DEFAULT '',
                Model TEXT NOT NULL,
                InputTokens INTEGER NOT NULL DEFAULT 0,
                OutputTokens INTEGER NOT NULL DEFAULT 0,
                CostUsd REAL NOT NULL DEFAULT 0,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'running'
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_agent_usage_CrewId_StartedAt ON agent_usage(CrewId, StartedAt);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_agent_usage_TaskId ON agent_usage(TaskId);", ct);

        // Phase 11.3 — first-class agent records.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS agents (
                Id TEXT NOT NULL PRIMARY KEY,
                Sort INTEGER NOT NULL DEFAULT 0,
                Name TEXT NOT NULL,
                Role TEXT NOT NULL DEFAULT '',
                CrewId TEXT NULL,
                SoulPath TEXT NULL,
                CapabilitiesJson TEXT NOT NULL DEFAULT '[]',
                State TEXT NOT NULL DEFAULT 'idle'
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_agents_CrewId ON agents(CrewId);", ct);

        // Phase 11.8 — agent_history audit log.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS agent_history (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                At TEXT NOT NULL,
                AgentId TEXT NOT NULL,
                FromCrewId TEXT NULL,
                ToCrewId TEXT NULL,
                FromModel TEXT NULL,
                ToModel TEXT NULL,
                Actor TEXT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_agent_history_AgentId_At ON agent_history(AgentId, At);", ct);

        // Phase 11.10 — denormalised crew budgets.
        await EnsureColumnAsync(conn, "crews", "DailyBudgetUsd", "REAL NULL", ct);
        await EnsureColumnAsync(conn, "crews", "MaxTaskBudgetUsd", "REAL NULL", ct);

        // Phase 9.11 — per-user preferred sign-in accounts.
        await EnsureColumnAsync(conn, "user_settings", "PreferredAdoAccount", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "user_settings", "PreferredGitHubAccount", "TEXT NULL", ct);

        // Phase 9.12 — per-user / per-product role override.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS user_product_roles (
                UserId TEXT NOT NULL,
                ProductId TEXT NOT NULL,
                Role TEXT NOT NULL DEFAULT 'human',
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (UserId, ProductId)
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_user_product_roles_UserId ON user_product_roles(UserId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_user_product_roles_ProductId ON user_product_roles(ProductId);", ct);

        // Phase 13.1 — crew hierarchy + capabilities + quotas (additive on `crews`).
        await EnsureColumnAsync(conn, "crews", "ParentCrewId",     "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "crews", "Depth",            "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(conn, "crews", "Backend",          "TEXT NOT NULL DEFAULT 'copilot'", ct);
        await EnsureColumnAsync(conn, "crews", "InvokeMode",       "TEXT NOT NULL DEFAULT 'sdk'", ct);
        await EnsureColumnAsync(conn, "crews", "CapabilitiesJson", "TEXT NOT NULL DEFAULT '[]'", ct);
        await EnsureColumnAsync(conn, "crews", "MaxConcurrent",    "INTEGER NOT NULL DEFAULT 2", ct);
        await EnsureColumnAsync(conn, "crews", "WeeklyCap",        "INTEGER NOT NULL DEFAULT 20", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_crews_ParentCrewId ON crews(ParentCrewId);", ct);

        // Phase 13.1 — crew membership join table.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS crew_members (
                CrewId TEXT NOT NULL,
                AgentId TEXT NOT NULL,
                AddedAt TEXT NOT NULL,
                AddedBy TEXT NULL,
                PRIMARY KEY (CrewId, AgentId)
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_crew_members_CrewId  ON crew_members(CrewId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_crew_members_AgentId ON crew_members(AgentId);", ct);

        // Phase 13.1 — crew_history audit log.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS crew_history (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                At TEXT NOT NULL,
                CrewId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                FromValue TEXT NULL,
                ToValue TEXT NULL,
                ByUserId TEXT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_crew_history_CrewId_At ON crew_history(CrewId, At);", ct);

        // Phase 13b — per-member role + crew-level default pair.
        await EnsureColumnAsync(conn, "crew_members", "Role",                    "TEXT NOT NULL DEFAULT 'coder'", ct);
        await EnsureColumnAsync(conn, "crews",        "DefaultGeneratorAgentId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "crews",        "DefaultEvaluatorAgentId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "crews",        "EvaluatorModel",          "TEXT NULL", ct);

        // Phase 13e — per-agent runtime overrides. Null = inherit crew default.
        await EnsureColumnAsync(conn, "agents", "Backend",        "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "agents", "InvokeMode",     "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "agents", "GeneratorModel", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "agents", "EvaluatorModel", "TEXT NULL", ct);

        // Phase 13.7 — domain-expert index.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS domain_experts (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ContentId TEXT NOT NULL,
                RepoId TEXT NOT NULL,
                Path TEXT NOT NULL,
                Topic TEXT NOT NULL,
                Summary TEXT NOT NULL DEFAULT '',
                IndexedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS UX_domain_experts_ContentId ON domain_experts(ContentId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_domain_experts_RepoId ON domain_experts(RepoId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_domain_experts_Topic  ON domain_experts(Topic);", ct);
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
