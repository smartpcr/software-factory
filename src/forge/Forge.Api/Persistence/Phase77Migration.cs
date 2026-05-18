// -----------------------------------------------------------------------
// <copyright file="Phase77Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 77 — repair legacy <c>user_settings</c> schema drift.
///
/// <para><b>Observed pattern (2026-05-17):</b> on a control DB where the
/// canonical Phase 8 <c>user_settings</c> table was lost (mid-Phase-73
/// split recovery dropped it) and operator-restored from an even older
/// backup, the table came back with the pre-Phase-8 key/value style:
/// <c>(UserId TEXT, Key TEXT, Value TEXT, UpdatedAt TEXT, …)</c> with
/// composite PK <c>(UserId, Key)</c>. The EF model however expects the
/// canonical schema (single PK <c>UserId</c> plus columns
/// <c>DefaultAdoOrgUrl</c>, <c>DefaultAdoProject</c>,
/// <c>BrowserPreference</c>, etc).</para>
///
/// <para>Effect of the drift: every endpoint that touches
/// <c>UserSettingsRow</c> via EF crashes with
/// <c>SQLite Error 1: 'no such column: u.BrowserPreference'</c>.
/// In particular:
/// <list type="bullet">
///   <item><c>GET /api/providers/ado/test</c> →
///         <see cref="Forge.Api.Providers.Ado.AdoConfigResolver"/>
///         crashes resolving the default org URL.</item>
///   <item><c>GET /api/me/settings</c> →
///         <see cref="Forge.Api.Endpoints.MeSettingsEndpoints"/>
///         crashes reading the legacy row.</item>
///   <item>The Providers &amp; sync page shows
///         <c>NOT CONNECTED</c> for ADO even when the MSAL token
///         is fresh, because the resolver fails before
///         <c>IsConfigured</c> can return true.</item>
/// </list></para>
///
/// <para><b>Idempotent</b>. Two-pass:</para>
/// <list type="number">
///   <item>If the canonical column <c>BrowserPreference</c> is present
///         AND the legacy <c>Key</c> column is NOT present, return
///         immediately — the table is already in good shape.</item>
///   <item>Otherwise, snapshot the rows (we expect 0 in practice; abort
///         on &gt; 0 to surface the issue to an operator instead of
///         silently dropping data), drop the legacy table, and re-create
///         it with the Phase 8 + Phase 11 schema.</item>
/// </list>
///
/// <para><b>Phase 8 was a no-op for this DB.</b> Phase 8 uses
/// <c>CREATE TABLE IF NOT EXISTS user_settings (…)</c> — which is a
/// silent no-op when the table already exists with the wrong schema.
/// Phase 77's diagnostic is "if BrowserPreference column is missing"
/// because no ALTER TABLE in any subsequent Phase migration adds it
/// (Phase 11 only adds <c>PreferredAdoAccount</c> + 
/// <c>PreferredGitHubAccount</c>).</para>
/// </summary>
public static class Phase77Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)control.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // 1. Diagnose schema drift.
        var hasBrowserPreference = await ColumnExistsAsync(conn, "user_settings", "BrowserPreference", ct);
        var hasLegacyKey = await ColumnExistsAsync(conn, "user_settings", "Key", ct);
        if (hasBrowserPreference && !hasLegacyKey)
        {
            // Canonical schema present, legacy column absent — nothing to do.
            return;
        }

        // 2. Safety check — never silently drop rows. If the table exists
        //    AND has any rows AND looks legacy, dump them and abort. An
        //    operator can inspect + decide. In practice the legacy
        //    user_settings rows are session-only (key/value cache for the
        //    Web UI), so an empty table is the overwhelmingly likely case.
        var tableExists = await TableExistsAsync(conn, "user_settings", ct);
        if (tableExists)
        {
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM user_settings;";
            var rowCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
            if (rowCount > 0)
            {
                throw new InvalidOperationException(
                    $"Phase 77: legacy `user_settings` table has {rowCount} rows but "
                    + "schema is incompatible with the canonical Phase 8 EF model. "
                    + "Refusing to drop. Operator: dump the rows and re-create "
                    + "manually, or empty the table if it's safe to discard.");
            }

            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TABLE user_settings;";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Recreate with canonical schema (Phase 8 base + Phase 11
        //    additions). Future Phase migrations that touch
        //    user_settings should chain via EnsureColumnAsync as usual;
        //    this re-create covers only the Phase 8 + Phase 11 surface.
        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE user_settings (
                UserId TEXT NOT NULL PRIMARY KEY,
                DefaultAdoOrgUrl TEXT NULL,
                DefaultAdoProject TEXT NULL,
                DefaultGitHubOwner TEXT NULL,
                DefaultProductId TEXT NULL,
                DefaultRepoId TEXT NULL,
                BrowserPreference TEXT NULL,
                Theme TEXT NULL,
                NotificationsEnabled INTEGER NOT NULL DEFAULT 1,
                UpdatedAt TEXT NOT NULL,
                PreferredAdoAccount TEXT NULL,
                PreferredGitHubAccount TEXT NULL
            );";
        await createCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
        cmd.Parameters.AddWithValue("@n", name);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is not null && Convert.ToInt32(o) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // PRAGMA returns one row per column; check by name.
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // Columns: cid, name, type, notnull, dflt_value, pk
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
