// -----------------------------------------------------------------------
// <copyright file="Phase96Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 (chat bridge, Phase 4) — control-side <c>chat_bridge_state</c>
/// single-row table persisting the inbound long-poll cursor
/// (<see cref="ChatBridgeStateRow"/>). <b>Additive only; new table.</b>
///
/// <para><b>Control-only.</b> The <c>ProductDbContext</c> parameter is unused
/// (kept for the <see cref="PhaseMigrationRunner"/> reflection signature).
/// Idempotent — <c>CREATE TABLE IF NOT EXISTS</c>.</para>
/// </summary>
public static class Phase96Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_bridge_state (
                Id TEXT NOT NULL PRIMARY KEY,
                LastUpdateId INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL DEFAULT ''
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
