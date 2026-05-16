// -----------------------------------------------------------------------
// <copyright file="Phase64Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 64 — adds structured failure metadata to deliverables.
///
/// <list type="bullet">
/// <item><c>deliverables.FailureKind</c> — the
/// <c>GeneratorFailureKinds</c> classifier value when the generator
/// invocation aborted (e.g. <c>generator-timeout</c>,
/// <c>generator-max-turns</c>, <c>generator-inactive</c>).</item>
/// <item><c>deliverables.FailureReason</c> — the verbatim provider error
/// string (e.g. <c>"Copilot SDK hard timeout: exceeded 16m budget"</c>).</item>
/// </list>
///
/// <para>Both columns are nullable; only stalled rows produced by an
/// aborted generator carry values. Idempotent: the column-existence
/// check makes re-runs a no-op.</para>
///
/// <para>Backfills are deliberately skipped — the row history is large
/// and the only consumer (workstream <c>StallReason</c>) falls back to
/// parsing the <c>"GENERATOR FAILED (kind): reason"</c> prefix in
/// <c>Notes</c> when the columns are null. Going forward, new stalled
/// rows populate the columns directly.</para>
/// </summary>
public static class Phase64Migration
{
    public static async Task ApplyAsync(ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        await AddColumnIfMissingAsync(conn, "deliverables", "FailureKind", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, "deliverables", "FailureReason", "TEXT", ct);
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
