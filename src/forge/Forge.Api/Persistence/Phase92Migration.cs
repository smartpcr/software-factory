// -----------------------------------------------------------------------
// <copyright file="Phase92Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 92 — per-workstream human operator steering prompts.
///
/// <para><b>Feature.</b> The conversation pane's Operator subtab lets a human
/// operator enter a free-text steering / follow-up directive for a single
/// workstream. Every ACTIVE (non-dismissed) prompt is injected into BOTH the
/// generator and the evaluator prompt on every iteration of
/// <see cref="Services.Stories.CrewStoryArtifactStrategy.RunGenEvalLoopAsync"/>
/// (persistent + cumulative) until the operator dismisses it.</para>
///
/// <para><b>Why its own table.</b> Unlike the one-shot resume
/// <c>task.operator-note</c> transcript event (bounded + janitor-pruned), an
/// operator prompt must persist across the whole loop and carry a mutable
/// dismissed flag, so it lives in a durable <c>operator_prompts</c> table that
/// transcript retention never touches.</para>
///
/// <para><b>Product-only — must fan out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to every per-product
/// <c>{slug}.db</c> as well as the default <c>forge.db</c>.</para>
///
/// <para><b>Idempotent.</b> <c>CREATE TABLE IF NOT EXISTS</c> +
/// <c>CREATE INDEX IF NOT EXISTS</c>; the index name matches
/// <see cref="DbModelProduct.Configure"/> so a fresh EnsureCreated DB and a
/// migrated DB carry the same schema fingerprint.</para>
/// </summary>
public static class Phase92Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS operator_prompts (
                    Id           TEXT NOT NULL PRIMARY KEY,
                    WorkstreamId TEXT NOT NULL,
                    Body         TEXT NOT NULL,
                    CreatedAt    TEXT NOT NULL,
                    CreatedBy    TEXT NULL,
                    DismissedAt  TEXT NULL
                )
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var idx = conn.CreateCommand())
        {
            idx.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_operator_prompts_ws_dismissed
                    ON operator_prompts (WorkstreamId, DismissedAt)
                """;
            await idx.ExecuteNonQueryAsync(ct);
        }
    }
}
