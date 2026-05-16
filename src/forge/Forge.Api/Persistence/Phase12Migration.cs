// -----------------------------------------------------------------------
// <copyright file="Phase12Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 12 — Work Item Tracking. Adds
/// <c>work_items</c> + <c>work_item_links</c> tables and their indexes.
/// Runs after <see cref="Phase11Migration"/> so a fresh DB picks everything
/// up from the EF model and an existing dev DB only sees the additive
/// changes. SQLite-only.
/// </summary>
public static class Phase12Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS work_items (
                Id TEXT NOT NULL PRIMARY KEY,
                RepoId TEXT NULL,
                Provider TEXT NOT NULL DEFAULT 'local',
                ProviderRef INTEGER NULL,
                Track TEXT NOT NULL DEFAULT 'dev',
                Type TEXT NOT NULL,
                State TEXT NOT NULL DEFAULT 'proposed',
                Title TEXT NOT NULL,
                DescriptionMd TEXT NOT NULL DEFAULT '',
                ParentId TEXT NULL,
                FeatureRef TEXT NULL,
                IterationRef TEXT NOT NULL DEFAULT '',
                AreaPath TEXT NOT NULL DEFAULT '',
                AssignedToJson TEXT NULL,
                Points INTEGER NOT NULL DEFAULT 0,
                Priority TEXT NOT NULL DEFAULT 'med',
                TagsJson TEXT NOT NULL DEFAULT '[]',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                RemoteRevision TEXT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_work_items_ParentId ON work_items(ParentId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_work_items_IterationRef ON work_items(IterationRef);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_work_items_RepoId_Type ON work_items(RepoId, Type);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_work_items_Provider_ProviderRef ON work_items(Provider, ProviderRef);", ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS work_item_links (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                FromId TEXT NOT NULL,
                ToId TEXT NOT NULL,
                Kind TEXT NOT NULL DEFAULT 'related',
                CreatedAt TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_work_item_links_FromId ON work_item_links(FromId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_work_item_links_ToId ON work_item_links(ToId);", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
