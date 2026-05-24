// -----------------------------------------------------------------------
// <copyright file="Phase82Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 82 — durable per-iteration prompt audit table.
///
/// <para><b>Problem.</b> Generator/evaluator <c>task.prompt</c> emits land in
/// <c>task_transcript_event</c>, which is bounded by
/// <see cref="Realtime.TaskChannel"/> (10 000-row cap, oldest-first eviction)
/// AND pruned by <see cref="Services.TranscriptRetentionJanitor"/>
/// (last 10 iters open / 1 iter closed). On a 65-iter qa-e2e run the
/// first 55 iters' prompts are pruned before the operator can audit
/// whether each iter saw the right impl-plan / prior-notes /
/// evaluator-feedback / global-rules context.</para>
///
/// <para><b>Solution.</b> Mirror every <c>task.prompt</c> emit into
/// <c>iteration_prompt</c> via <see cref="Services.IterationPromptStore"/>.
/// This table is NOT touched by transcript retention; it is pruned only
/// on explicit lifecycle hooks (workstream delete / restart /
/// <c>ClearTranscript</c>).</para>
///
/// <para><b>Append-only.</b> A retry / replay for the same
/// (WorkItemId, Iter, Role) inserts a new row with the next
/// <c>Attempt</c> number rather than overwriting — the diff between
/// attempts is itself a forensic data point.</para>
///
/// <para><b>Product-only — must fan out.</b> Classified
/// <see cref="PhaseMigrationKind.Product"/> in <see cref="PhaseMigrationCatalog"/>
/// so <see cref="PhaseMigrationRunner"/> applies it to every
/// per-product <c>{slug}.db</c> as well as the default <c>forge.db</c>.</para>
///
/// <para><b>Idempotent.</b> <c>CREATE TABLE IF NOT EXISTS</c> guard.
/// Indexes use <c>CREATE INDEX IF NOT EXISTS</c>; their names match
/// <see cref="DbModelProduct.Configure"/> so a fresh EnsureCreated DB
/// and a migrated DB carry the same schema fingerprint.</para>
///
/// <para><b>Opportunistic backfill.</b> Where any <c>task.prompt</c>
/// rows still survive in <c>task_transcript_event</c>, copy them into
/// <c>iteration_prompt</c> at attempt=1 so the operator sees what's
/// currently visible the moment the migration completes. Bounded to a
/// reasonable budget so the migration stays fast on large transcripts.</para>
/// </summary>
public static class Phase82Migration
{
    /// <summary>Cap on how many transcript rows to scan during backfill.</summary>
    private const int BackfillScanLimit = 5_000;

    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS iteration_prompt (
                    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorkItemId    TEXT NOT NULL,
                    Iter          INTEGER NOT NULL,
                    Role          TEXT NOT NULL,
                    Attempt       INTEGER NOT NULL DEFAULT 1,
                    AgentId       TEXT NOT NULL,
                    CrewId        TEXT NOT NULL,
                    SystemPrompt  TEXT NULL,
                    UserPrompt    TEXT NOT NULL,
                    Model         TEXT NULL,
                    Backend       TEXT NULL,
                    InvokeMode    TEXT NULL,
                    CreatedAt     TEXT NOT NULL
                )
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var idx1 = conn.CreateCommand())
        {
            idx1.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_iteration_prompt_wi_iter_role_attempt
                    ON iteration_prompt (WorkItemId, Iter, Role, Attempt)
                """;
            await idx1.ExecuteNonQueryAsync(ct);
        }

        await using (var idx2 = conn.CreateCommand())
        {
            idx2.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_iteration_prompt_wi_iter
                    ON iteration_prompt (WorkItemId, Iter)
                """;
            await idx2.ExecuteNonQueryAsync(ct);
        }

        await BackfillFromTranscriptAsync(conn, ct);
    }

    /// <summary>
    /// Opportunistic backfill — read up to <see cref="BackfillScanLimit"/>
    /// <c>task.prompt</c> rows out of <c>task_transcript_event</c> and
    /// stamp them into <c>iteration_prompt</c> at attempt=1. Skips rows
    /// already present (idempotent re-run safe). Older transcript rows
    /// already pruned by the janitor / cap are gone — this only rescues
    /// the residue that's still resident at migration time.
    /// </summary>
    private static async Task BackfillFromTranscriptAsync(SqliteConnection conn, CancellationToken ct)
    {
        // The transcript table may not exist on fresh post-split per-product DBs
        // that have never received a transcript write yet — guard.
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_transcript_event' LIMIT 1";
            var exists = await probe.ExecuteScalarAsync(ct);
            if (exists is null) return;
        }

        var rows = new List<(string Wi, string PayloadJson, DateTimeOffset At, string? AgentId, string? CrewId)>();
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = $@"
                SELECT WorkItemId, PayloadJson, At, AgentId, CrewId
                FROM task_transcript_event
                WHERE Kind = 'task.prompt'
                ORDER BY Id ASC
                LIMIT {BackfillScanLimit}";
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    Wi: reader.GetString(0),
                    PayloadJson: reader.GetString(1),
                    At: DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                    AgentId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    CrewId: reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        if (rows.Count == 0) return;

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            string? role = null;
            int? iter = null;
            string? sys = null;
            string? user = null;
            string? agentId = row.AgentId;
            string? crewId = row.CrewId;
            string? model = null;
            string? backend = null;
            string? invokeMode = null;

            try
            {
                using var doc = JsonDocument.Parse(row.PayloadJson);
                var r = doc.RootElement;
                if (r.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                {
                    role = roleEl.GetString();
                }
                if (r.TryGetProperty("iter", out var iterEl) && iterEl.ValueKind == JsonValueKind.Number)
                {
                    iter = iterEl.GetInt32();
                }
                if (r.TryGetProperty("systemPrompt", out var spEl) && spEl.ValueKind == JsonValueKind.String)
                {
                    sys = spEl.GetString();
                }
                if (r.TryGetProperty("userPrompt", out var upEl) && upEl.ValueKind == JsonValueKind.String)
                {
                    user = upEl.GetString();
                }
                if (r.TryGetProperty("agentId", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                {
                    agentId = aEl.GetString();
                }
                if (r.TryGetProperty("crewId", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                {
                    crewId = cEl.GetString();
                }
                if (r.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                {
                    model = mEl.GetString();
                }
                if (r.TryGetProperty("backend", out var bEl) && bEl.ValueKind == JsonValueKind.String)
                {
                    backend = bEl.GetString();
                }
                if (r.TryGetProperty("invokeMode", out var imEl) && imEl.ValueKind == JsonValueKind.String)
                {
                    invokeMode = imEl.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            // Skip records that aren't gen/eval iteration prompts. The
            // CrewQueueWorker emit at line 1471 (build-fix / comment-response)
            // does NOT carry role/iter — those stay transcript-only.
            if (string.IsNullOrEmpty(role) || iter is null || string.IsNullOrEmpty(user)) continue;
            if (!string.Equals(role, "generator", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(role, "evaluator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Idempotent — skip if a row already exists for this key at any attempt.
            await using (var exists = conn.CreateCommand())
            {
                exists.Transaction = tx;
                exists.CommandText = "SELECT 1 FROM iteration_prompt WHERE WorkItemId = @wi AND Iter = @iter AND Role = @role LIMIT 1";
                exists.Parameters.AddWithValue("@wi", row.Wi);
                exists.Parameters.AddWithValue("@iter", iter.Value);
                exists.Parameters.AddWithValue("@role", role!.ToLowerInvariant());
                if (await exists.ExecuteScalarAsync(ct) is not null) continue;
            }

            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO iteration_prompt
                    (WorkItemId, Iter, Role, Attempt, AgentId, CrewId, SystemPrompt, UserPrompt, Model, Backend, InvokeMode, CreatedAt)
                VALUES
                    (@wi, @iter, @role, 1, @agentId, @crewId, @sys, @user, @model, @backend, @invokeMode, @createdAt)
                """;
            ins.Parameters.AddWithValue("@wi", row.Wi);
            ins.Parameters.AddWithValue("@iter", iter.Value);
            ins.Parameters.AddWithValue("@role", role!.ToLowerInvariant());
            ins.Parameters.AddWithValue("@agentId", (object?)agentId ?? string.Empty);
            ins.Parameters.AddWithValue("@crewId", (object?)crewId ?? string.Empty);
            ins.Parameters.AddWithValue("@sys", (object?)sys ?? DBNull.Value);
            ins.Parameters.AddWithValue("@user", user!);
            ins.Parameters.AddWithValue("@model", (object?)model ?? DBNull.Value);
            ins.Parameters.AddWithValue("@backend", (object?)backend ?? DBNull.Value);
            ins.Parameters.AddWithValue("@invokeMode", (object?)invokeMode ?? DBNull.Value);
            ins.Parameters.AddWithValue("@createdAt", row.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
