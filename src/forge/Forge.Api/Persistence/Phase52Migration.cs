// -----------------------------------------------------------------------
// <copyright file="Phase52Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 52 — sloppy-iteration guard &amp; pair reassignment.
///
/// <para>Adds three nullable / default-init columns to <c>work_items</c>:</para>
/// <list type="bullet">
/// <item><c>PairAttemptCount INTEGER NOT NULL DEFAULT 1</c> — total
/// pair-attempts so far. Starts at 1 on first claim; the reassignment
/// reconciler increments to 2 then 3. When the row is judged sloppy at
/// attempt 3, the work item terminates with a structured failure.</item>
/// <item><c>PriorPairsJson TEXT NULL</c> — JSON array of <c>[generatorId,
/// evaluatorId]</c> tuples captured at each reassignment. The claim
/// picker excludes ALL prior pairs (not just the most recent) so a
/// rotating reassignment never re-circulates the same pair.</item>
/// <item><c>FailureReason TEXT NULL</c> — free-form failure reason; today
/// set only on the sloppy-3-attempts terminal path
/// (<c>ScoringConstants.SloppyTerminalFailureReason</c>) but reserved for
/// future terminal-failure paths so the UI has one column to read.</item>
/// </list>
///
/// <para>Creates one new table <c>agent_pair_assignment</c> — one row per
/// (work_item, pair) attempt, capturing the audit-trail-of-record for
/// "what did each pair cost and produce". Phase 14's KPI service (Phase
/// 14.16) reads it for the <c>SloppyRate</c> KPI.</para>
///
/// <para>Idempotent — checks <c>pragma_table_info</c> / <c>sqlite_master</c>
/// before each ALTER / CREATE.</para>
/// </summary>
public static class Phase52Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Q52.5 — PairAttemptCount defaults to 1. Existing rows mid-flight at
        // migration time get the default; the first sloppy trigger after the
        // boot will increment to 2, so backfill semantics are: "this is the
        // first pair we know about". Worst case for a mid-flight row that
        // had already been reassigned pre-Phase-52: it gets one fewer
        // attempt than the spec implies. Acceptable because the only way
        // a pre-Phase-52 reassignment happened is via Phase 51's cooldown
        // path (which doesn't fully reset state) so the rows look like
        // first-attempt rows at migration time anyway.
        await EnsureColumnAsync(conn, "work_items", "PairAttemptCount", "INTEGER NOT NULL DEFAULT 1", ct);
        await EnsureColumnAsync(conn, "work_items", "PriorPairsJson", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "work_items", "FailureReason", "TEXT NULL", ct);

        // Q52.6 — agent_pair_assignment. One row per pair-attempt. Columns
        // mirror DeliverableRow's pricing columns so per-pair totals can be
        // computed without joining the full deliverables chain.
        //
        // Indexes:
        //   (WorkItemId, SequenceNo) UNIQUE — at-most-3 attempts per work
        //     item; the index doubles as a quick "what's the latest seq"
        //     ordering for the reconciler.
        //   (GeneratorAgentId, CreatedAt) — covers the
        //     Phase 14.16 SloppyRate query: agent's rows in a time window.
        //   (EvaluatorAgentId, CreatedAt) — same, for evaluator-side rate.
        await EnsureTableAsync(conn, "agent_pair_assignment", """
            CREATE TABLE agent_pair_assignment (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkItemId TEXT NOT NULL,
                SequenceNo INTEGER NOT NULL,
                GeneratorAgentId TEXT NOT NULL,
                EvaluatorAgentId TEXT NOT NULL,
                CrewId TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                TotalIters INTEGER NOT NULL DEFAULT 0,
                FinalScore INTEGER NULL,
                TotalInputTokens INTEGER NOT NULL DEFAULT 0,
                TotalOutputTokens INTEGER NOT NULL DEFAULT 0,
                TotalCostUsd TEXT NOT NULL DEFAULT '0',
                Outcome TEXT NOT NULL DEFAULT 'in-progress',
                SloppyReason TEXT NULL,
                CreatedAt TEXT NOT NULL
            )
            """, ct);

        await EnsureIndexAsync(conn, "ux_agent_pair_assignment_wi_seq", "agent_pair_assignment",
            "WorkItemId, SequenceNo", unique: true, ct);
        await EnsureIndexAsync(conn, "ix_agent_pair_assignment_gen_created", "agent_pair_assignment",
            "GeneratorAgentId, CreatedAt", unique: false, ct);
        await EnsureIndexAsync(conn, "ix_agent_pair_assignment_eval_created", "agent_pair_assignment",
            "EvaluatorAgentId, CreatedAt", unique: false, ct);
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
