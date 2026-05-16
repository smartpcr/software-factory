// -----------------------------------------------------------------------
// <copyright file="Phase54Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 54 — per-turn prompt-size growth metric.
///
/// <para>Adds one nullable column to <c>agent_usage</c>:</para>
/// <list type="bullet">
/// <item><c>TurnSamplesJson TEXT NULL</c> — JSON array of per-turn input/
/// output token snapshots for the invocation that wrote the row. Shape is
/// <c>[{ "turnIndex": int, "inputTokens": long, "outputTokens": long,
/// "atUtc": "iso8601" }, ...]</c>. Under the Phase 54 "1 invocation = 1
/// turn" definition the array is either null (pre-Phase-54 rows, CLI
/// runners with no per-event usage) or a single sample whose values match
/// the row's <c>InputTokens / OutputTokens</c> columns. The column is
/// designed to absorb richer sub-invocation samples (one entry per
/// <c>AssistantUsageEvent</c> from the Copilot SDK) when we later relax
/// the turn definition — no further schema change required.</item>
/// </list>
///
/// <para>The per-iteration growth metric is computed by joining
/// <c>agent_usage</c> rows for the same work item ordered by
/// <c>StartedAt</c> within each <c>CostCategory</c>. The samples column is
/// supplementary — the existing <c>InputTokens / OutputTokens</c> columns
/// remain the authoritative per-invocation totals.</para>
///
/// <para>Idempotent — checks <c>pragma_table_info</c> before the ALTER.</para>
/// </summary>
public static class Phase54Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "agent_usage", "TurnSamplesJson", "TEXT NULL", ct);
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
}
