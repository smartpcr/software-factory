// -----------------------------------------------------------------------
// <copyright file="Phase105Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>Adds bounded app reception diagnostics and recoverable Slack root status updates.</summary>
public static class Phase105Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        await control.Database.OpenConnectionAsync(ct);
        await using var tx = await control.Database.BeginTransactionAsync(ct);
        foreach (var (table, name, type) in new[]
        {
            ("chat_receiver_state", "DiagnosticCode", "TEXT NULL"),
            ("chat_receiver_state", "DiagnosticTenantId", "TEXT NULL"),
            ("chat_receiver_state", "DiagnosticAt", "INTEGER NULL"),
            ("chat_receiver_state", "DiagnosticCount", "INTEGER NOT NULL DEFAULT 0"),
            ("chat_conversation_bindings", "RootDesiredState", "TEXT NOT NULL DEFAULT 'active'"),
            ("chat_conversation_bindings", "RootRenderedState", "TEXT NOT NULL DEFAULT 'active'"),
            ("chat_conversation_bindings", "RootNextAttemptAt", "INTEGER NOT NULL DEFAULT 0"),
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