// -----------------------------------------------------------------------
// <copyright file="Phase65Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 65 — adds per-iter commit metadata to deliverables.
///
/// <list type="bullet">
/// <item><c>deliverables.CommitSha</c> — the SHA the producer's
/// <c>OnIterationCommit</c> callback captured after the iter's commit
/// landed (plan-doc <c>CommitPathInWorktreeAsync</c>, engineer's
/// per-iter HEAD snapshot). Null when the iter produced no commit.</item>
/// <item><c>deliverables.CommitMessage</c> — truncated commit subject
/// for display in the iter strip tooltip.</item>
/// <item><c>deliverables.FilesAdded</c> / <c>FilesModified</c> /
/// <c>FilesDeleted</c> — counts from <c>git show --numstat</c> against
/// <c>CommitSha</c> (or <c>git diff</c> against the prior iter's
/// snapshot for engineer rows that accumulate uncommitted edits).</item>
/// <item><c>deliverables.FilesChanged</c> — denormalised total so
/// downstream queries don't have to deal with null arithmetic.</item>
/// </list>
///
/// <para><b>Why this is a real schema column rather than a derivation.</b>
/// The pre-Phase-65 iter-KPI endpoint computed <c>filesChanged</c> from
/// <c>task.worktree-tick</c> events that fall inside the iter's time
/// window. That signal is unreliable: the tick poller runs at 1 Hz, the
/// worktree is removed post-PR-merge so retrospective queries return
/// zero, and iter persist-time vs. tick-time edges miss commits made in
/// the last 1-second slice. The deterministic per-commit snapshot is
/// captured at iter persist time and survives worktree removal,
/// restart, and historical drill-downs. The endpoint prefers the row
/// value when present and falls back to event aggregation only for
/// pre-Phase-65 rows where the column is null.</para>
///
/// <para><b>Idempotent by construction.</b> Same column-existence
/// pattern as <see cref="Phase64Migration"/>: a re-run on an
/// already-migrated database is a no-op. No backfill — existing rows
/// keep their null column values and fall back to the event-based
/// computation in <c>ConversationInsightQueries.GetIterKpisAsync</c>.</para>
/// </summary>
public static class Phase65Migration
{
    public static async Task ApplyAsync(ForgeDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "deliverables", "CommitSha", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "deliverables", "CommitMessage", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "deliverables", "FilesAdded", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "deliverables", "FilesModified", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "deliverables", "FilesDeleted", "INTEGER", ct);
        await AddColumnIfMissingAsync(conn, "deliverables", "FilesChanged", "INTEGER", ct);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
        if (exists) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alter.ExecuteNonQueryAsync(ct);
    }
}
