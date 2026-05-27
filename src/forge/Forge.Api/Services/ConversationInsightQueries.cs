// -----------------------------------------------------------------------
// <copyright file="ConversationInsightQueries.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Shared query helpers for the Conversation-tab workstream-insight surfaces
/// (Insight I.2 — iter notes, I.3 — iter KPIs + sparkline, I.4 — agent
/// memory / context-usage bar). Originally inlined in
/// <see cref="Endpoints.WorkstreamsEndpoints"/>; extracted so the per-doc
/// plan-doc routes on <see cref="Endpoints.StoriesEndpoints"/> can render
/// the same insights without duplicating the join logic.
///
/// <para>All three helpers key on <c>workItemId</c> — for engineer
/// workstreams this is <c>WorkstreamRow.Id</c>; for plan-doc tasks it is
/// <c>plan-doc:{spawnId}:{fileName}</c>. The crew lookup for the memory
/// query walks the most recent <see cref="DeliverableRow"/> instead of
/// the workstreams table, so the same code path works for both
/// shapes.</para>
/// </summary>
public static class ConversationInsightQueries
{
    /// <summary>
    /// Build the iter-KPI payload (Insight I.3). Joins deliverables +
    /// agent_usage + task_transcript_event so the Conversation tab's KPI
    /// strip can render per-iter score / token / cost / files / duration
    /// chips and the matching sparklines.
    ///
    /// <para>Iter window semantics:</para>
    /// <list type="number">
    ///   <item>Anchor = deliverables.CreatedAt ordered ASC by Iter.</item>
    ///   <item>For iter N: usage rows with StartedAt &gt; iter[N-1].At
    ///   AND StartedAt &lt;= iter[N].At; iter 1's lower bound is the
    ///   FIRST agent_usage.StartedAt for this work item (or
    ///   DateTimeOffset.MinValue if none).</item>
    ///   <item>Duration = scored.At − started.At from the transcript
    ///   for matching iter number; null when either marker is missing.</item>
    ///   <item>filesChanged = count of distinct file paths in
    ///   <c>task.worktree-tick</c> events whose At falls in the iter window.
    ///   Empty when the work item has no worktree subscriber (plan-doc
    ///   spawns, or a coding step whose worktree never resolved).</item>
    /// </list>
    /// </summary>
    public static async Task<object> GetIterKpisAsync(
        ControlDbContext control, ProductDbContext db, string workItemId, CancellationToken ct)
    {
        // EF/SQLite refuses server-side ORDER BY on DateTimeOffset
        // (NotSupportedException), so we materialise then sort
        // client-side. Memory'd in [[feedback_sqlite_no_order_by_datetimeoffset]].
        //
        // 2026-05-26 — order chronologically (CreatedAt first, Iter as
        // tiebreaker) so the per-iter chip strip + sparkline track real
        // wall-clock progress. `Iter` resets per pair attempt — a Retry that
        // wipes pair-attempt accounting and lands a fresh pair (e.g. ws
        // 3.3-e2e: YS4 stuck at 86 for iters 1..65 → SoftReset on Retry →
        // FL passes at score 95 stamped as Iter=1 again) produces TWO rows
        // with `Iter=1`. Ordering by Iter parked the 95 at chip position 2
        // and pushed the 64 zero-token chips (whose agent_usage was wiped)
        // to positions 3..66, so the score sparkline spiked at position 2
        // instead of the rightmost. Matches the secondary sort used by
        // TaskExecutionEndpoints.GetExecution.deliverableRows so both views
        // agree on chip-to-slot mapping.
        var deliverables = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .ToListAsync(ct))
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Iter)
            .ToList();
        if (deliverables.Count == 0)
        {
            return new { workItemId, iters = Array.Empty<object>() };
        }

        var usageRows = await control.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == workItemId)
            .Select(u => new
            {
                u.StartedAt,
                u.InputTokens,
                u.OutputTokens,
                u.CostUsd,
                u.CacheReadTokens,
                u.CacheWriteTokens,
                u.ReasoningTokens,
                u.ConversationTokens,
            })
            .ToListAsync(ct);

        var transcript = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == workItemId
                && (e.Kind == "task.iteration.started"
                    || e.Kind == "task.iteration.scored"
                    || e.Kind == "task.worktree-tick"
                    || e.Kind == "task.test-gate.resolving"
                    || e.Kind == "task.test-gate.resolved"))
            .Select(e => new { e.Kind, e.At, e.PayloadJson })
            .ToListAsync(ct);

        var startedByIter = new Dictionary<int, DateTimeOffset>();
        var scoredByIter = new Dictionary<int, DateTimeOffset>();
        // Impacted-test resolution window per iter (the in-process phase between
        // the build gate passing and the test command running — where a hung
        // language server stranded stage-1.3 for hours, 2026-05-26). resolveMs
        // = resolved.At - resolving.At feeds the per-iter "resolve" sparkline so
        // a slow/runaway resolve is visible without a heap dump. Prefer the
        // emitted durationMs from the resolved event when present (authoritative).
        var resolvingByIter = new Dictionary<int, DateTimeOffset>();
        var resolveMsByIter = new Dictionary<int, long>();
        foreach (var e in transcript)
        {
            if (e.Kind == "task.iteration.started" || e.Kind == "task.iteration.scored")
            {
                int? iter = TryExtractIter(e.PayloadJson);
                if (iter is null) continue;
                if (e.Kind == "task.iteration.started") startedByIter[iter.Value] = e.At;
                else scoredByIter[iter.Value] = e.At;
            }
            else if (e.Kind == "task.test-gate.resolving")
            {
                int? iter = TryExtractIter(e.PayloadJson);
                if (iter is not null) resolvingByIter[iter.Value] = e.At;
            }
            else if (e.Kind == "task.test-gate.resolved")
            {
                int? iter = TryExtractIter(e.PayloadJson);
                if (iter is null) continue;
                // Authoritative duration from the event payload; else derive from
                // the resolving→resolved wall-clock gap.
                long? durMs = TryExtractLong(e.PayloadJson, "durationMs");
                if (durMs is null && resolvingByIter.TryGetValue(iter.Value, out var startedAt))
                {
                    var gap = (long)(e.At - startedAt).TotalMilliseconds;
                    if (gap >= 0) durMs = gap;
                }
                if (durMs is { } ms && ms >= 0) resolveMsByIter[iter.Value] = ms;
            }
        }
        var worktreeChanged = transcript
            .Where(e => e.Kind == "task.worktree-tick")
            .Select(e => (e.At, paths: ExtractChangedPaths(e.PayloadJson)))
            .ToList();

        var sortedUsage = usageRows.OrderBy(u => u.StartedAt).ToList();

        var iters = new List<object>(deliverables.Count);
        // Start from MinValue so the first iter's window captures ALL
        // usage rows up to the deliverable's CreatedAt. The previous
        // value (`earliestUsage`) excluded the first usage row because
        // the boundary check is strict `>` — a single-iter workstream
        // (e.g. generator-timeout on iter 1) showed 0 tokens / $0.00.
        DateTimeOffset prevAt = DateTimeOffset.MinValue;
        foreach (var d in deliverables)
        {
            var windowStart = prevAt;
            var windowEnd = d.CreatedAt;

            long inTok = 0, outTok = 0;
            decimal cost = 0m;
            // 2026-05-16 Phase 72 — accumulate cache / reasoning tokens
            // per iter window so the UI can show a "cache hit %" badge
            // and a reasoning-cost breakdown. ConversationTokens is a
            // snapshot, not a sum — latest wins inside the window.
            long cacheReadTok = 0, cacheWriteTok = 0, reasoningTok = 0;
            long? conversationTokLatest = null;
            foreach (var u in sortedUsage)
            {
                if (u.StartedAt > windowStart && u.StartedAt <= windowEnd)
                {
                    inTok += u.InputTokens;
                    outTok += u.OutputTokens;
                    cost += u.CostUsd;
                    if (u.CacheReadTokens is long crt) cacheReadTok += crt;
                    if (u.CacheWriteTokens is long cwt) cacheWriteTok += cwt;
                    if (u.ReasoningTokens is long rt) reasoningTok += rt;
                    if (u.ConversationTokens is long ct2) conversationTokLatest = ct2;
                }
            }

            var pathsThisIter = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (at, paths) in worktreeChanged)
            {
                if (at > windowStart && at <= windowEnd)
                {
                    foreach (var p in paths) pathsThisIter.Add(p);
                }
            }
            // Phase 65 — prefer the deliverable row's deterministic
            // FilesChanged column when present. The event-aggregation
            // path above is the fallback for legacy rows that pre-date
            // the OnIterationCommit hook (row column is null) and for
            // rows whose hook returned null (worktree path unresolvable
            // at snapshot time). The event aggregation loses fidelity
            // when worktree-tick events miss the iter window (worktree
            // removed mid-flight, commits in the last 1-second slice) —
            // exactly the failure mode Phase 65 fixes. Row column wins
            // because it was captured inside the loop's transaction
            // boundary and is immune to polling timing.
            int filesChanged = d.FilesChanged ?? pathsThisIter.Count;

            long? durationMs = null;
            if (startedByIter.TryGetValue(d.Iter, out var s))
            {
                // Prefer the scored timestamp; fall back to the
                // deliverable's CreatedAt so stalled/failed iters
                // (which never emit task.iteration.scored) still
                // show their real wall-clock duration in the sparkline.
                var end = scoredByIter.TryGetValue(d.Iter, out var e2) ? e2 : d.CreatedAt;
                durationMs = (long)(end - s).TotalMilliseconds;
                if (durationMs < 0) durationMs = null;
            }

            iters.Add(new
            {
                iter = d.Iter,
                // Phase 87 — null on pre-Phase-87 rows; UI treats null as
                // pair 1 so legacy workstreams render unchanged. Stamped
                // by DeliverableLoop from WorkItem.PairAttemptCount.
                pairAttempt = d.PairAttempt,
                score = d.Score,
                verdict = d.Verdict,
                inputTokens = inTok,
                outputTokens = outTok,
                costUsd = cost,
                durationMs,
                filesChanged,
                createdAt = d.CreatedAt,
                // 2026-05-16 Phase 72 — cache / reasoning / conversation
                // telemetry. Null fields mean the runner didn't report
                // them (CLI backends, pre-Phase-72 rows). UI renders
                // "no cache data" badge when all four are null.
                cacheReadTokens = cacheReadTok > 0 ? (long?)cacheReadTok : null,
                cacheWriteTokens = cacheWriteTok > 0 ? (long?)cacheWriteTok : null,
                reasoningTokens = reasoningTok > 0 ? (long?)reasoningTok : null,
                conversationTokens = conversationTokLatest,
                // 2026-05-26 — impacted-test resolution wall-clock for this iter
                // (build-gate-pass → test-command-start window). Null on iters that
                // didn't resolve (resolution disabled, or legacy rows pre-dating the
                // task.test-gate.resolving/resolved events). Feeds the "resolve"
                // sparkline so a hung/slow resolver is visible in iteration insights.
                resolveMs = resolveMsByIter.TryGetValue(d.Iter, out var rms) ? (long?)rms : null,
            });
            prevAt = windowEnd;
        }

        return new { workItemId, iters };
    }

    /// <summary>
    /// Build the iter-notes payload (Insight I.2). Read-only enumeration of
    /// the agent's per-iter working notes archive
    /// (<c>{worktreePath}/.forge/notes/iter-{N}.md</c>). Returns empty
    /// <c>iters</c> when <paramref name="worktreePath"/> is null or the
    /// directory doesn't exist (worktree not yet created, or post-merge
    /// cleanup).
    /// </summary>
    public static async Task<object> GetIterNotesAsync(
        string workItemId, string? worktreePath, CancellationToken ct)
    {
        const int PerIterCap = 16_000;
        var iters = new List<object>();

        if (!string.IsNullOrEmpty(worktreePath))
        {
            var notesDir = Path.Combine(worktreePath, ".forge", "notes");
            if (Directory.Exists(notesDir))
            {
                var parsed = new List<(int Iter, string Path)>();
                foreach (var file in Directory.GetFiles(notesDir, "iter-*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var dash = name.IndexOf('-');
                    if (dash >= 0
                        && int.TryParse(name[(dash + 1)..], out var n)
                        && n > 0)
                    {
                        parsed.Add((n, file));
                    }
                }
                parsed.Sort((a, b) => a.Iter.CompareTo(b.Iter));

                foreach (var (iter, path) in parsed)
                {
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(path, ct);
                    }
                    catch
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var truncated = false;
                    if (content.Length > PerIterCap)
                    {
                        content = content[..PerIterCap];
                        truncated = true;
                    }
                    iters.Add(new
                    {
                        iter,
                        content,
                        truncated,
                        lengthBytes = content.Length,
                    });
                }
            }
        }

        return new
        {
            workItemId,
            iters,
        };
    }

    /// <summary>
    /// Build the memory payload (Insight I.4 / Phase 55 / Phase G).
    /// Returns the per-role <see cref="AgentMemoryRow"/> rows + their
    /// effective compactor threshold so the UI can render the
    /// ContextUsageBar.
    ///
    /// <para>Crew lookup walks the most-recent <see cref="DeliverableRow"/>
    /// rather than the workstreams table — works for both engineer
    /// workstream ids and synthetic plan-doc workItemIds, since
    /// architect + engineer crews both write deliverables keyed by the
    /// workItemId.</para>
    /// </summary>
    public static async Task<object> GetMemoryAsync(
        ControlDbContext control,
        ProductDbContext db,
        CopilotSessionPool copilotPool,
        MemoryCompactor compactor,
        string workItemId,
        CancellationToken ct)
    {
        var rows = await db.AgentMemory.AsNoTracking()
            .Where(m => m.WorkItemId == workItemId)
            .ToListAsync(ct);

        // Resolve the crew for THIS work item by walking the most recent
        // deliverable row (same shape works for engineer workstreams + plan-doc
        // synthetic work items). The generator crew is on the row directly;
        // the paired evaluator crew (if any) supplies the eval-side override.
        CrewRow? wsCrew = null;
        CrewRow? evalCrew = null;
        var lastDel = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (lastDel is not null && !string.IsNullOrEmpty(lastDel.CrewId))
        {
            wsCrew = await control.Crews.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == lastDel.CrewId, ct);
            if (wsCrew is not null && !string.IsNullOrEmpty(wsCrew.PairedWithCrewId))
            {
                evalCrew = await control.Crews.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == wsCrew.PairedWithCrewId, ct);
            }
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            int? turnCount = null;
            if (!string.IsNullOrEmpty(row.MessagesJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(row.MessagesJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        turnCount = doc.RootElement.GetArrayLength();
                    }
                }
                catch
                {
                    // corrupt payload — leave turnCount null
                }
            }

            var agentRow = await control.Agents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == row.AgentId, ct);
            var roleCrew = string.Equals(row.Role, "evaluator", StringComparison.OrdinalIgnoreCase)
                ? (evalCrew ?? wsCrew)
                : wsCrew;
            string? modelId = string.Equals(row.Role, "evaluator", StringComparison.OrdinalIgnoreCase)
                ? (agentRow?.EvaluatorModel ?? roleCrew?.EvaluatorModel ?? roleCrew?.Model)
                : (agentRow?.GeneratorModel ?? roleCrew?.Model);
            var crewOverride = roleCrew?.CompactionTokenThreshold;
            var detail = !string.IsNullOrEmpty(modelId)
                ? compactor.ComputeThresholdDetail(modelId, crewOverride)
                : new MemoryCompactor.ThresholdDetail(
                    EffectiveThreshold: crewOverride ?? MemoryCompactor.DefaultThreshold,
                    ContextWindowTokens: null,
                    ContextWindowRatio: compactor.ContextWindowRatio,
                    DefaultThreshold: MemoryCompactor.DefaultThreshold,
                    CrewOverride: crewOverride,
                    Source: crewOverride.HasValue
                        ? MemoryCompactor.ThresholdSource.CrewOverride
                        : MemoryCompactor.ThresholdSource.GlobalDefault);

            result[row.Role] = new
            {
                agentId = row.AgentId,
                backend = row.Backend,
                invokeMode = row.InvokeMode,
                model = modelId,
                sessionId = string.IsNullOrEmpty(row.SessionId)
                    ? null
                    : (row.SessionId.Length <= 12 ? row.SessionId : row.SessionId[..12] + "…"),
                hasMessagesHistory = !string.IsNullOrEmpty(row.MessagesJson),
                turnCount,
                approxInputTokens = row.ApproxInputTokens,
                effectiveThreshold = detail.EffectiveThreshold,
                contextWindowTokens = detail.ContextWindowTokens,
                contextWindowRatio = detail.ContextWindowRatio,
                defaultThreshold = detail.DefaultThreshold,
                crewOverride = detail.CrewOverride,
                thresholdSource = detail.Source.ToString(),
                lastIter = row.LastIter,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt,
            };
        }

        return new
        {
            workItemId,
            roles = result,
        };
    }

    private static int? TryExtractIter(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("iter", out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number) return null;
            return v.GetInt32();
        }
        catch
        {
            return null;
        }
    }

    private static long? TryExtractLong(string payloadJson, string property)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty(property, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number) return null;
            return v.GetInt64();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractChangedPaths(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var list = new List<string>();

            // task.worktree-tick shape: `branchDiff` (committed diff vs
            // fork point) + `paths` (uncommitted dirty), each split into
            // {added, modified, deleted}. Union both so the count is
            // non-zero whether the agent has committed yet or not.
            AppendCategorizedPaths(root, "branchDiff", list);
            if (root.TryGetProperty("paths", out var pathsEl)
                && pathsEl.ValueKind == JsonValueKind.Object)
            {
                AppendCategorizedPaths(root, "paths", list);
            }
            else if (pathsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in pathsEl.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var p = el.GetString();
                        if (!string.IsNullOrEmpty(p)) list.Add(p);
                    }
                }
            }
            else if (root.TryGetProperty("path", out var single)
                && single.ValueKind == JsonValueKind.String)
            {
                var one = single.GetString();
                if (!string.IsNullOrEmpty(one)) list.Add(one);
            }
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AppendCategorizedPaths(
        JsonElement root, string propertyName, List<string> sink)
    {
        if (!root.TryGetProperty(propertyName, out var grp)
            || grp.ValueKind != JsonValueKind.Object) return;
        foreach (var category in new[] { "added", "modified", "deleted" })
        {
            if (!grp.TryGetProperty(category, out var arr)
                || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var p = el.GetString();
                    if (!string.IsNullOrEmpty(p)) sink.Add(p);
                }
            }
        }
    }
}
