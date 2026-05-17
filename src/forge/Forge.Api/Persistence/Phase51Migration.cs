// -----------------------------------------------------------------------
// <copyright file="Phase51Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 51 — auto-recovery for per-task budget-cap stalls.
///
/// Adds two nullable columns to <c>work_items</c>:
/// <list type="bullet">
/// <item><c>BudgetCooldownUntilUtc TEXT NULL</c> — when set, the row is
/// parked at <c>ExecutionStatus="cooldown-budget"</c> until either (a)
/// the operator raises <c>MaxTaskBudgetUsd</c> and cumulative cost drops
/// back under cap (immediate resume, same pair), or (b) this timestamp
/// passes (resume with rotated pair).</item>
/// <item><c>LastPairAgentIdsJson TEXT NULL</c> — JSON
/// <c>[generatorAgentId, evaluatorAgentId]</c> captured at the moment
/// the row entered cooldown. The claim picker excludes these on the
/// cooldown-elapsed resume path.</item>
/// </list>
///
/// Idempotent — checks <c>pragma_table_info</c> before each ALTER.
/// Indexes <c>ExecutionStatus</c> so the reconciler's 15-second scan
/// can hit the parked rows without a full table walk; <c>cooldown-budget</c>
/// rows are rare relative to the table size so a partial index is
/// overkill — full index is fine.
/// </summary>
public static class Phase51Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "work_items", "BudgetCooldownUntilUtc", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "work_items", "LastPairAgentIdsJson", "TEXT NULL", ct);
        // Phase 51 follow-up — budget window reset boundary. Cap checks
        // sum agent_usage from this timestamp forward, so a cooldown-
        // elapsed recovery grants a fresh $0 starting point.
        await EnsureColumnAsync(conn, "work_items", "BudgetWindowStartUtc", "TEXT NULL", ct);
        await EnsureIndexAsync(conn, "ix_work_items_execstatus", "work_items", "ExecutionStatus", ct);
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

    private static async Task<bool> EnsureIndexAsync(
        SqliteConnection conn, string indexName, string table, string columns, CancellationToken ct)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name";
        probe.Parameters.AddWithValue("@name", indexName);
        if (await probe.ExecuteScalarAsync(ct) is not null) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE INDEX {indexName} ON {table}({columns})";
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }
}
