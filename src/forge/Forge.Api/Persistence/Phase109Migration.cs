// -----------------------------------------------------------------------
// <copyright file="Phase109Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 109 — policies applied to a whole product or a whole story:
/// <c>rule_policy_scopes</c> (<see cref="RulePolicyScopeRow"/>).
///
/// <para>Product-only, so <see cref="PhaseMigrationRunner"/> applies it to
/// every per-product <c>{slug}.db</c>. One new table; no migration queries it,
/// so it has no ordering dependency. Rule tags reuse the existing
/// <c>catalog_rules.TagIdsJson</c> column, so no column is added. Idempotent:
/// <c>CREATE ... IF NOT EXISTS</c>, with the index name matching
/// <see cref="DbModelProduct.Configure"/>.</para>
/// </summary>
public static class Phase109Migration
{
    private static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS rule_policy_scopes (
            Id        TEXT NOT NULL PRIMARY KEY,
            ProductId TEXT NOT NULL,
            PolicyId  TEXT NOT NULL,
            ScopeKind TEXT NOT NULL,
            ScopeId   TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            CreatedBy TEXT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_rule_policy_scopes_policy_scope ON rule_policy_scopes (PolicyId, ScopeKind, ScopeId)",
    };

    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = control;
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        foreach (var sql in Statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}