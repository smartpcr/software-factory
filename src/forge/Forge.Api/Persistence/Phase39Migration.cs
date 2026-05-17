// -----------------------------------------------------------------------
// <copyright file="Phase39Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 39 (Layer C) — create the <c>deliverable_questions</c> table that
/// holds open questions extracted from generator output. Operator answers
/// them via the conversation-tab wizard; answers are prepended to the next
/// iteration's user prompt and any unanswered question forces the
/// evaluator's verdict to <c>iterate</c> regardless of score.
/// </summary>
public static class Phase39Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // CREATE TABLE IF NOT EXISTS — idempotent, safe to re-run on every boot.
        await using var create = conn.CreateCommand();
        create.CommandText = @"
CREATE TABLE IF NOT EXISTS deliverable_questions (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    ExternalId   TEXT NOT NULL,
    WorkItemId   TEXT NOT NULL,
    Iter         INTEGER NOT NULL,
    Text         TEXT NOT NULL,
    Kind         TEXT NOT NULL,
    ChoicesJson  TEXT,
    Answer       TEXT,
    AnsweredAt   TEXT,
    AnsweredBy   TEXT,
    CreatedAt    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_dq_workitem ON deliverable_questions (WorkItemId);
CREATE INDEX IF NOT EXISTS ix_dq_workitem_extid_iter ON deliverable_questions (WorkItemId, ExternalId, Iter);
";
        await create.ExecuteNonQueryAsync(ct);
    }
}
