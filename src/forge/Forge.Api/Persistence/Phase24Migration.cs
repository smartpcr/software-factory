// -----------------------------------------------------------------------
// <copyright file="Phase24Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 24 — PR state &amp; comment-lifecycle
/// sync. Adds sync-tracking columns to <c>prs</c>, creates
/// <c>pr_comments</c> and <c>pr_comment_history</c> tables with indexes.
/// </summary>
public static class Phase24Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // -- prs table: add Phase 24 columns --
        await EnsureColumnAsync(conn, "prs", "WorkItemId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "IsActive", "INTEGER NOT NULL DEFAULT 1", ct);
        await EnsureColumnAsync(conn, "prs", "LastSyncAt", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "LastSyncStatus", "TEXT NOT NULL DEFAULT 'idle'", ct);
        await EnsureColumnAsync(conn, "prs", "LastSyncError", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "IsRunningSince", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "CommentSyncCursor", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "LastUpstreamUpdatedAt", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "Provider", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "prs", "ProviderRef", "TEXT NULL", ct);

        // -- pr_comments table --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS pr_comments (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                PrId            TEXT    NOT NULL,
                ProviderCommentId TEXT  NOT NULL,
                ThreadId        TEXT    NOT NULL,
                ParentCommentId INTEGER NULL,
                AuthorId        TEXT    NULL,
                AuthorKind      TEXT    NOT NULL DEFAULT 'human',
                Body            TEXT    NOT NULL DEFAULT '',
                FilePath        TEXT    NULL,
                Line            INTEGER NULL,
                State           TEXT    NOT NULL DEFAULT 'active',
                CreatedAt       TEXT    NOT NULL,
                UpdatedAt       TEXT    NOT NULL,
                ResolvedByUserId TEXT   NULL,
                ResolvedAt      TEXT    NULL,
                LinkedCommitSha TEXT    NULL,
                LinkedAgentResponseId INTEGER NULL,
                RequiresAttention INTEGER NOT NULL DEFAULT 0
            );", ct);

        await ExecAsync(conn, @"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_pr_comments_PrId_ProviderCommentId
            ON pr_comments (PrId, ProviderCommentId);", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_pr_comments_PrId_RequiresAttention
            ON pr_comments (PrId, RequiresAttention);", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_pr_comments_ThreadId
            ON pr_comments (ThreadId);", ct);

        // -- pr_comment_history table --
        await ExecAsync(conn, @"
            CREATE TABLE IF NOT EXISTS pr_comment_history (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                CommentId   INTEGER NOT NULL,
                Kind        TEXT    NOT NULL DEFAULT '',
                FromValue   TEXT    NULL,
                ToValue     TEXT    NULL,
                ByUserId    TEXT    NULL,
                At          TEXT    NOT NULL,
                PayloadJson TEXT    NULL
            );", ct);

        await ExecAsync(conn, @"
            CREATE INDEX IF NOT EXISTS IX_pr_comment_history_CommentId_At
            ON pr_comment_history (CommentId, At);", ct);

        // -- task_queue: add Kind discriminator (Q24.8) + extend unique
        // index to allow a 'work' row and a 'comment-response' row to
        // coexist for the same work item.
        await EnsureColumnAsync(conn, "task_queue", "Kind", "TEXT NOT NULL DEFAULT 'work'", ct);
        await ExecAsync(conn, "DROP INDEX IF EXISTS UX_task_queue_open_per_work_item;", ct);
        await ExecAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_task_queue_open_per_work_item_kind
            ON task_queue(WorkItemId, Kind)
            WHERE State IN ('awaiting-ack','awaiting-route','queued','picked-up');
            """, ct);

        // -- prs: track linker cursor for incremental commit-link sweeps --
        await EnsureColumnAsync(conn, "prs", "LastLinkerCommitSha", "TEXT NULL", ct);
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
