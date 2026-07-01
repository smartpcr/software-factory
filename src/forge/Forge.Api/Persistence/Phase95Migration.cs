// -----------------------------------------------------------------------
// <copyright file="Phase95Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 (chat bridge) — control-side schema for the operator chat bridge.
/// <b>Additive only; no behavior change.</b> Creates:
/// <list type="number">
///   <item>the <c>chat_notifications</c> outbox table — durable outbound
///         notifications + reply correlation (see <see cref="ChatNotificationRow"/>),
///         with a partial-UNIQUE index on <c>DedupeKey</c> filtered to the OPEN
///         states (<c>pending</c>/<c>sent</c>) so a duplicate alert can't be
///         enqueued while one is still outstanding;</item>
///   <item>the <c>chat_subscribers</c> allow-list table
///         (see <see cref="ChatSubscriberRow"/>) with a UNIQUE index on
///         <c>(Platform, ChatId)</c>.</item>
/// </list>
///
/// <para><b>Control-only.</b> All ops target the control DB (the
/// <see cref="ControlDbContext"/> connection); the <c>ProductDbContext</c>
/// parameter is unused but kept to satisfy the
/// <see cref="PhaseMigrationRunner"/> reflection signature. Mirrors
/// <see cref="Phase80Migration"/>.</para>
///
/// <para><b>Idempotent.</b> <c>CREATE TABLE / INDEX IF NOT EXISTS</c>. Index
/// names are pinned to match <see cref="DbModelControl"/>'s
/// <c>HasDatabaseName(...)</c> so that on a fresh DB — where
/// <c>EnsureCreated</c> lays down the model index first — the
/// <c>IF NOT EXISTS</c> here is a clean no-op rather than a second index.</para>
/// </summary>
public static class Phase95Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS chat_notifications (
                Id TEXT NOT NULL PRIMARY KEY,
                Kind TEXT NOT NULL DEFAULT '',
                ProductId TEXT NOT NULL DEFAULT '',
                RepoId TEXT NULL,
                WorkItemId TEXT NULL,
                WorkstreamId TEXT NULL,
                QueueId TEXT NULL,
                DedupeKey TEXT NOT NULL DEFAULT '',
                Title TEXT NOT NULL DEFAULT '',
                Body TEXT NOT NULL DEFAULT '',
                ActionsJson TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                Platform TEXT NOT NULL DEFAULT '',
                ChatId TEXT NULL,
                ChatMessageId TEXT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT NULL,
                CreatedAt TEXT NOT NULL DEFAULT '',
                SentAt TEXT NULL,
                RespondedAt TEXT NULL,
                ExpiresAt TEXT NULL
            );
            """, ct);

        await ExecAsync(conn,
            "CREATE INDEX IF NOT EXISTS IX_chat_notifications_Status ON chat_notifications (Status);", ct);
        // Idempotency — at most one OPEN (pending/sent) notification per dedupe key.
        await ExecAsync(conn,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_chat_notifications_DedupeKey_open ON chat_notifications (DedupeKey) WHERE Status IN ('pending','sent');", ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS chat_subscribers (
                Id TEXT NOT NULL PRIMARY KEY,
                Platform TEXT NOT NULL DEFAULT '',
                ChatId TEXT NOT NULL DEFAULT '',
                ForgeUserId TEXT NULL,
                LinkToken TEXT NULL,
                LinkedAt TEXT NULL,
                ProductFilterJson TEXT NULL,
                Muted INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT ''
            );
            """, ct);

        await ExecAsync(conn,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_chat_subscribers_platform_chatid ON chat_subscribers (Platform, ChatId);", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
