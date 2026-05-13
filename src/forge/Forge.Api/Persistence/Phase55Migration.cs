// -----------------------------------------------------------------------
// <copyright file="Phase55Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 55 (docs/forge/sloppy_guard.md) — agent memory persistence layer.
///
/// <para>Creates one new table <c>agent_memory</c> — per
/// <c>(WorkItemId, AgentId, Role)</c> tuple. Layer 2 of the gen-eval memory
/// plan (Claude/CLI <c>--resume</c>, Claude/SDK history replay,
/// Copilot/SDK long-lived session) consumes the row to reconnect to a
/// prior iteration's provider session; Layer 1 (worktree scratch notes
/// from Phase A) is independent and continues to work without this row.</para>
///
/// <para>Indexes:</para>
/// <list type="bullet">
/// <item><c>ux_agent_memory_wi_agent_role</c> — UNIQUE on
/// <c>(WorkItemId, AgentId, Role)</c>. Enforces the "one continuity stream
/// per (work item, agent, role)" rule from the plan; doubles as the
/// lookup path for <c>AgentMemoryStore.GetAsync</c>.</item>
/// <item><c>ix_agent_memory_wi</c> — covers
/// <c>DeleteForWorkItemAsync</c> (called on terminal verdicts so memory
/// for a completed workstream gets reaped).</item>
/// </list>
///
/// <para>Idempotent — checks <c>sqlite_master</c> before
/// <c>CREATE TABLE</c> / <c>CREATE INDEX</c> so this migration is safe to
/// re-run on every boot.</para>
/// </summary>
public static class Phase55Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Both payload columns nullable so a single row shape works for
        // every (Backend, InvokeMode) cell. SessionId carries the
        // Claude/CLI --resume id or the Copilot/SDK pool handle;
        // MessagesJson carries the Claude/SDK conversation array.
        // ApproxInputTokens is the meter Phase F's compactor reads to
        // decide whether to summarise older turns.
        await EnsureTableAsync(conn, "agent_memory", """
            CREATE TABLE agent_memory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkItemId TEXT NOT NULL,
                AgentId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Backend TEXT NOT NULL,
                InvokeMode TEXT NOT NULL,
                SessionId TEXT NULL,
                MessagesJson TEXT NULL,
                LastIter INTEGER NOT NULL DEFAULT 0,
                ApproxInputTokens INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )
            """, ct);

        await EnsureIndexAsync(conn, "ux_agent_memory_wi_agent_role", "agent_memory",
            "WorkItemId, AgentId, Role", unique: true, ct);
        await EnsureIndexAsync(conn, "ix_agent_memory_wi", "agent_memory",
            "WorkItemId", unique: false, ct);

        // Phase 55 toggle on crews. Operator decision (2026-05-13)
        // flipped this from opt-in (DEFAULT 0) to opt-out (DEFAULT 1):
        // every supported crew gets memory unless explicitly disabled.
        // Fresh installs land with 1; existing installs that picked up
        // the original DEFAULT 0 get the one-time backfill below so
        // their crew rows match the new default. The runner cells gate
        // their session-resume / history-replay code paths on this
        // column being 1.
        var columnAdded = await EnsureColumnAsync(
            conn, "crews", "EnableAgentMemory", "INTEGER NOT NULL DEFAULT 1", ct);

        // Phase G follow-up backfill. Only runs once -- guarded by an
        // idempotency row -- so operator opt-outs (SET EnableAgentMemory
        // = 0) made AFTER this backfill stick across reboots. Skip the
        // backfill entirely on fresh installs where the column was just
        // added (every row inherits DEFAULT 1 already).
        if (!columnAdded)
        {
            const string BackfillKey = "phase55-backfill-enable-agent-memory-default-on";
            await using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT 1 FROM idempotency WHERE Key = @key";
            probe.Parameters.AddWithValue("@key", BackfillKey);
            var alreadyDone = await probe.ExecuteScalarAsync(ct) is not null;
            if (!alreadyDone)
            {
                await using var update = conn.CreateCommand();
                update.CommandText = "UPDATE crews SET EnableAgentMemory = 1 WHERE EnableAgentMemory = 0";
                var rows = await update.ExecuteNonQueryAsync(ct);

                await using var record = conn.CreateCommand();
                record.CommandText = """
                    INSERT INTO idempotency (Key, At, Status, ContentType, Body)
                    VALUES (@key, @at, 200, 'text/plain', @body)
                    """;
                record.Parameters.AddWithValue("@key", BackfillKey);
                record.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
                record.Parameters.AddWithValue("@body", $"backfilled {rows} crew row(s) to EnableAgentMemory=1");
                await record.ExecuteNonQueryAsync(ct);
            }
        }

        // Phase 55 / Phase F compaction-trigger override. Null = use the
        // 300K default in MemoryCompactor; the compactor still clamps the
        // effective trigger down to model.ContextWindow * 0.3 so a
        // smaller-window evaluator model can never overrun its context.
        await EnsureColumnAsync(conn, "crews", "CompactionTokenThreshold", "INTEGER NULL", ct);

        // Phase 55 / Phase G per-agent override (NULL = inherit crew). Lets
        // an operator flip one agent in a crew on/off without touching the
        // rest. SQLite has no native nullable bool; we map NULL/0/1.
        await EnsureColumnAsync(conn, "agents", "AgentMemoryOverride", "INTEGER NULL", ct);
    }

    private static async Task<bool> EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeAffinity, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name";
        probe.Parameters.AddWithValue("@name", column);
        if (await probe.ExecuteScalarAsync(ct) is not null) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAffinity}";
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static async Task<bool> EnsureTableAsync(
        SqliteConnection conn, string table, string createSql, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        probe.Parameters.AddWithValue("@name", table);
        if (await probe.ExecuteScalarAsync(ct) is not null) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = createSql;
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static async Task<bool> EnsureIndexAsync(
        SqliteConnection conn, string indexName, string table, string columns, bool unique, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name";
        probe.Parameters.AddWithValue("@name", indexName);
        if (await probe.ExecuteScalarAsync(ct) is not null) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE {(unique ? "UNIQUE" : string.Empty)} INDEX {indexName} ON {table}({columns})";
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }
}
