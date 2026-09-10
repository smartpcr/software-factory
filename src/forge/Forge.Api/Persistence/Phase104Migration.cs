// -----------------------------------------------------------------------
// <copyright file="Phase104Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>Adds interaction response claims, choice lookup metadata and topic retirement recovery.</summary>
public static class Phase104Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        await control.Database.OpenConnectionAsync(ct);
        await using var tx = await control.Database.BeginTransactionAsync(ct);
        foreach (var (table, name, type) in new[]
        {
            ("chat_inbox", "ResponseText", "TEXT NULL"), ("chat_inbox", "ResponseState", "TEXT NOT NULL DEFAULT 'none'"),
            ("chat_inbox", "ContentScopesJson", "TEXT NULL"), ("chat_inbox", "CommandRevision", "INTEGER NULL"),
            ("chat_inbox", "ResponseMessageId", "TEXT NULL"), ("chat_inbox", "ResponseOwner", "TEXT NULL"),
            ("chat_inbox", "ResponseDeadline", "INTEGER NULL"), ("chat_inbox", "NextResponseAt", "INTEGER NOT NULL DEFAULT 0"),
            ("chat_inbox", "ResponseAttempts", "INTEGER NOT NULL DEFAULT 0"), ("chat_action_tokens", "ChoiceIndex", "INTEGER NULL"),
            ("chat_conversation_bindings", "RetirementState", "TEXT NOT NULL DEFAULT 'none'"),
            ("chat_conversation_bindings", "RetirementNextAttemptAt", "INTEGER NOT NULL DEFAULT 0"),
        })
        {
            await using var command = control.Database.GetDbConnection().CreateCommand();
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = name; command.Parameters.Add(parameter);
            if (await command.ExecuteScalarAsync(ct) is not null) continue;
            command.Parameters.Clear(); command.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {type}";
            await command.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}