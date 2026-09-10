// -----------------------------------------------------------------------
// <copyright file="Phase99Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>Stage 1S retirement timestamp; no delivery tables or legacy data conversion.</summary>
public static class Phase99Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        var connection = control.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('spawn_plugins') WHERE name = 'RetiredAt'";
        if (await command.ExecuteScalarAsync(ct) is not null) return;
        command.CommandText = "ALTER TABLE spawn_plugins ADD COLUMN RetiredAt TEXT NULL";
        await command.ExecuteNonQueryAsync(ct);
    }
}