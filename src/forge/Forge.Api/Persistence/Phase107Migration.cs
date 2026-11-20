// -----------------------------------------------------------------------
// <copyright file="Phase107Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// Per-workstream chat conversations and "set up once" chat
/// (docs/chat/chat-per-workstream-conversations.md):
/// <c>chat_connections.AllProducts</c>, <c>chat_subscriptions.ConversationScope</c>,
/// <c>chat_conversation_bindings.ChildKey</c> / <c>Title</c>, and replacing the
/// one-live-binding-per-subscription index with one per (subscription, child).
/// <para>
/// Runs at the HEAD of <see cref="PhaseMigrationRunner.ExecutionOrder"/>: Phases 98
/// and 100 regenerate the chat model with <c>CREATE INDEX IF NOT EXISTS</c>, and the
/// new index references <c>ChildKey</c>. On an upgraded DB the column must exist
/// before those run. On a fresh DB the tables do not exist yet, so this is a no-op
/// and Phase 98 creates them (columns, new index, <c>chat_host_defaults</c>) from
/// the model.
/// </para>
/// </summary>
public static class Phase107Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        await control.Database.OpenConnectionAsync(ct);
        await using var tx = await control.Database.BeginTransactionAsync(ct);
        foreach (var (table, name, type) in new[]
        {
            ("chat_connections", "AllProducts", "INTEGER NOT NULL DEFAULT 0"),
            ("chat_subscriptions", "ConversationScope", "TEXT NOT NULL DEFAULT 'story'"),
            ("chat_conversation_bindings", "ChildKey", "TEXT NOT NULL DEFAULT ''"),
            ("chat_conversation_bindings", "Title", "TEXT NULL"),
        })
        {
            if (!await ScalarExistsAsync(control, tx, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $v", table, ct)) continue;
            if (await ScalarExistsAsync(control, tx, $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $v", name, ct)) continue;
            await ExecAsync(control, tx, $"ALTER TABLE {table} ADD COLUMN {name} {type}", ct);
        }

        // The old index allowed one live binding per subscription revision; the
        // replacement (created by the model in Phase 98/100) adds ChildKey.
        await ExecAsync(control, tx, "DROP INDEX IF EXISTS IX_chat_conversation_bindings_active_revision", ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<bool> ScalarExistsAsync(ControlDbContext control, IDbContextTransaction tx, string sql, string value, CancellationToken ct)
    {
        await using var command = control.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$v";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task ExecAsync(ControlDbContext control, IDbContextTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = control.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}