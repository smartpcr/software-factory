// -----------------------------------------------------------------------
// <copyright file="Phase17Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent schema upgrade for Phase 17 — persistent task queue &amp;
/// team-to-crew routing. Adds the <c>task_queue</c> +
/// <c>task_queue_history</c> tables, plus the partial-unique idempotency
/// guard from Q17.6 that EF Core can't model portably. Runs after
/// <see cref="Phase16Migration"/>; SQLite-only.
/// </summary>
public static class Phase17Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS task_queue (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                WorkItemId TEXT NOT NULL,
                TargetKind TEXT NOT NULL DEFAULT 'team',
                TargetId TEXT NOT NULL DEFAULT '',
                State TEXT NOT NULL DEFAULT 'awaiting-ack',
                PriorityRank INTEGER NOT NULL DEFAULT 1,
                EnqueuedAt TEXT NOT NULL,
                AckRequiredBy TEXT NULL,
                AckedAt TEXT NULL,
                AckByUserId TEXT NULL,
                AutoAcked INTEGER NOT NULL DEFAULT 0,
                RoutedToCrewId TEXT NULL,
                PickedUpAt TEXT NULL,
                PickedUpByAgent TEXT NULL,
                CompletedAt TEXT NULL,
                LastUpdatedAt TEXT NOT NULL
            );
            """, ct);

        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_queue_target_state_order ON task_queue(TargetKind, TargetId, State, PriorityRank, EnqueuedAt);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_queue_WorkItemId ON task_queue(WorkItemId);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_queue_ack_required ON task_queue(State, AckRequiredBy);", ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_queue_RoutedToCrewId ON task_queue(RoutedToCrewId);", ct);

        // Q17.6 — at most one open queue row per work item, anywhere.
        // SQLite supports partial unique indexes via the WHERE clause.
        // Deduplicate existing data first — keep the row with the highest Id
        // (most recent enqueue) and delete older duplicates.
        await ExecAsync(conn, """
            DELETE FROM task_queue
            WHERE Id NOT IN (
                SELECT MAX(Id)
                FROM task_queue
                WHERE State IN ('awaiting-ack','awaiting-route','queued','picked-up')
                GROUP BY WorkItemId
            )
            AND State IN ('awaiting-ack','awaiting-route','queued','picked-up')
            AND WorkItemId IN (
                SELECT WorkItemId
                FROM task_queue
                WHERE State IN ('awaiting-ack','awaiting-route','queued','picked-up')
                GROUP BY WorkItemId
                HAVING COUNT(*) > 1
            );
            """, ct);
        await ExecAsync(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_task_queue_open_per_work_item
            ON task_queue(WorkItemId)
            WHERE State IN ('awaiting-ack','awaiting-route','queued','picked-up');
            """, ct);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS task_queue_history (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                QueueId INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                FromValue TEXT NULL,
                ToValue TEXT NULL,
                ByUserId TEXT NULL,
                AtDepth INTEGER NULL,
                EtaMinutes INTEGER NULL,
                PayloadJson TEXT NOT NULL DEFAULT '{}',
                At TEXT NOT NULL
            );
            """, ct);
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS IX_task_queue_history_QueueId_At ON task_queue_history(QueueId, At);", ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
