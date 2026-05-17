// -----------------------------------------------------------------------
// <copyright file="Phase60Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 60 — raise the plan-doc Points floor to 3 (90 turns / 15 min
/// wall-clock minimum) so the generator never runs out of budget on a
/// 2-point plan doc.
///
/// <para>Observed pattern (2026-05-13): on
/// <c>local:UPDATE-HISTORY-IN-CL</c> the <c>architecture.md</c> plan-doc
/// row carried <c>Points = 2</c>, giving <c>2 * 30 = 60</c> turns. The
/// Architect agent's mandatory grep-verify protocol + incremental edits
/// + sibling-doc reads exhausted the cap on iter 2 (3.79M cum input
/// tokens / $172) and iter 3 (1.64M / $79). Both rounds emitted Phase 58
/// synthetic <c>score=0 / verdict=stalled</c>, blocking further
/// progress until an operator bumped Points manually.</para>
///
/// <para>This migration bumps every existing <c>plan-doc:*</c> work
/// item that still carries <c>Points &lt; 3</c> up to 3, matching the
/// new floor applied at write-time in
/// <see cref="Forge.Api.Services.Stories.StorySpawnOrchestrator"/>'s
/// <c>EnsurePlanDocWorkItemsAsync</c> and in
/// <see cref="Phase38Migration"/> backfill. The <c>BudgetForPhase("plan")</c>
/// helper in <c>CrewStoryArtifactStrategy</c> was updated in the same
/// commit to scale wall-clock with points (3 pt → 15 min, 4 pt → 20 min,
/// 6+ pt → 30 min) so the new turn budget is actually reachable; without
/// that the fix would just convert <c>generator-max-turns</c> failures
/// into <c>generator-timeout</c> failures.</para>
///
/// <para><b>Idempotent by construction.</b> The <c>WHERE</c> clause
/// short-circuits the <c>UPDATE</c> to rows that still have
/// <c>Points &lt; 3</c>; re-running the migration on an already-bumped
/// database is a no-op. We intentionally do NOT use a "phase60 already
/// applied" guard row here so any future code path that accidentally
/// writes a sub-3-point plan-doc row gets corrected on the next boot.</para>
///
/// <para><b>Mid-run safety.</b> The Points value is read at the start of
/// each gen/eval round (<c>RunGenEvalLoopAsync</c> → <c>storyPoints * 30</c>)
/// and at queue dispatch time (<c>CrewQueueWorker.cs:1287-1289</c>);
/// changing it between rounds is observed but does NOT corrupt the
/// active SDK session's already-set <c>MaxTurns</c>. The new budget
/// takes effect on the next iter / resume.</para>
/// </summary>
public static class Phase60Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        // Filter on the synthetic id prefix so only plan-doc rows are
        // touched. Workstream tasks and decomp / qa / review rows have
        // their own (different) Points semantics and must not be
        // bumped here.
        const string Sql = "UPDATE work_items SET Points = 3 WHERE Id LIKE 'plan-doc:%' AND Points < 3";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
