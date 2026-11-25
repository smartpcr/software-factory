// -----------------------------------------------------------------------
// <copyright file="Phase108Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 108 — the product rule catalog: <c>rule_tags</c>,
/// <c>catalog_rules</c>, <c>workstream_tag_overrides</c> and
/// <c>work_item_rules</c> (<see cref="RuleTagRow"/>, <see cref="CatalogRuleRow"/>,
/// <see cref="WorkstreamTagOverrideRow"/>, <see cref="WorkItemRuleRow"/>).
///
/// <para>Product-only, so <see cref="PhaseMigrationRunner"/> applies it to
/// every per-product <c>{slug}.db</c>. New tables only; no migration queries
/// them, so it has no ordering dependency. Idempotent: <c>CREATE ... IF NOT
/// EXISTS</c>, with index names matching <see cref="DbModelProduct.Configure"/>.</para>
/// </summary>
public static class Phase108Migration
{
    private static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS rule_tags (
            Id             TEXT NOT NULL PRIMARY KEY,
            ProductId      TEXT NOT NULL,
            Name           TEXT NOT NULL,
            Description    TEXT NOT NULL,
            RepoIdsJson    TEXT NOT NULL,
            CategoriesJson TEXT NOT NULL,
            PhrasesJson    TEXT NOT NULL,
            PatternsJson   TEXT NOT NULL,
            CreatedAt      TEXT NOT NULL,
            CreatedBy      TEXT NULL,
            UpdatedAt      TEXT NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_rule_tags_product_name ON rule_tags (ProductId, Name)",
        """
        CREATE TABLE IF NOT EXISTS catalog_rules (
            Id         TEXT NOT NULL PRIMARY KEY,
            ProductId  TEXT NOT NULL,
            Title      TEXT NOT NULL,
            Text       TEXT NOT NULL,
            TagIdsJson TEXT NOT NULL,
            Enabled    INTEGER NOT NULL,
            CreatedAt  TEXT NOT NULL,
            CreatedBy  TEXT NULL,
            UpdatedAt  TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_catalog_rules_product ON catalog_rules (ProductId)",
        """
        CREATE TABLE IF NOT EXISTS workstream_tag_overrides (
            Id           TEXT NOT NULL PRIMARY KEY,
            WorkstreamId TEXT NOT NULL,
            TagId        TEXT NOT NULL,
            "On"         INTEGER NOT NULL,
            CreatedAt    TEXT NOT NULL,
            CreatedBy    TEXT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_workstream_tag_overrides_ws_tag ON workstream_tag_overrides (WorkstreamId, TagId)",
        """
        CREATE TABLE IF NOT EXISTS work_item_rules (
            Id         TEXT NOT NULL PRIMARY KEY,
            WorkItemId TEXT NOT NULL,
            RuleId     TEXT NOT NULL,
            "On"       INTEGER NOT NULL,
            CreatedAt  TEXT NOT NULL,
            CreatedBy  TEXT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_work_item_rules_item_rule ON work_item_rules (WorkItemId, RuleId)",
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