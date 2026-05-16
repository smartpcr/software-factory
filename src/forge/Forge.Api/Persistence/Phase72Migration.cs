// -----------------------------------------------------------------------
// <copyright file="Phase72Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 2026-05-16 — prompt-cache + reasoning + conversation token columns
/// on <c>agent_usage</c>. Captures fields the Copilot SDK already
/// exposes but Forge previously discarded:
///
/// <list type="bullet">
/// <item><c>CacheReadTokens INTEGER NULL</c> — prompt-cache hit tokens
/// (Anthropic <c>cache_read_input_tokens</c>; Copilot SDK
/// <c>AssistantUsageEvent.Data.CacheReadTokens</c>). Billed at the
/// cache-read discount (~10% of fresh input). Non-zero means the SDK's
/// session reuse + provider cache hit on the prompt prefix.</item>
/// <item><c>CacheWriteTokens INTEGER NULL</c> — cache write tokens.
/// First turn pays this; subsequent turns hit the read path.</item>
/// <item><c>ReasoningTokens INTEGER NULL</c> — model chain-of-thought
/// tokens (Anthropic <c>thinking</c>, OpenAI o1). Billed separately
/// from output tokens.</item>
/// <item><c>ConversationTokens INTEGER NULL</c> — full conversation
/// size at this turn, from
/// <c>SessionUsageInfoEvent.Data.ConversationTokens</c>. Reveals
/// context bloat when prompt+history balloons across iters.</item>
/// </list>
///
/// <para>All four columns are nullable so pre-existing rows keep their
/// schema-stable shape (no backfill — null === "unknown, pre-tracking").
/// The companion C# fields on <see cref="AgentUsageRow"/> are
/// <c>long?</c>.</para>
///
/// <para>Idempotent — each <c>ALTER</c> checks <c>pragma_table_info</c>
/// first so booting against a previously-migrated DB is a no-op.</para>
/// </summary>
public static class Phase72Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "agent_usage", "CacheReadTokens", "INTEGER NULL", ct);
        await EnsureColumnAsync(conn, "agent_usage", "CacheWriteTokens", "INTEGER NULL", ct);
        await EnsureColumnAsync(conn, "agent_usage", "ReasoningTokens", "INTEGER NULL", ct);
        await EnsureColumnAsync(conn, "agent_usage", "ConversationTokens", "INTEGER NULL", ct);
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
