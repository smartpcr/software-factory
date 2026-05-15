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
        ForgeDbContext db, string workItemId, CancellationToken ct)
    {
        // EF/SQLite refuses server-side ORDER BY on DateTimeOffset
        // (NotSupportedException), so we materialise then sort
        // client-side. Memory'd in [[feedback_sqlite_no_order_by_datetimeoffset]].
        var deliverables = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .ToListAsync(ct))
            .OrderBy(d => d.Iter)
            .ThenBy(d => d.CreatedAt)
            .ToList();
        if (deliverables.Count == 0)
        {
            return new { workItemId, iters = Array.Empty<object>() };
        }

        var usageRows = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == workItemId)
            .Select(u => new { u.StartedAt, u.InputTokens, u.OutputTokens, u.CostUsd })
            .ToListAsync(ct);

        var transcript = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == workItemId
                && (e.Kind == "task.iteration.started"
                    || e.Kind == "task.iteration.scored"
                    || e.Kind == "task.worktree-tick"))
            .Select(e => new { e.Kind, e.At, e.PayloadJson })
            .ToListAsync(ct);

        var startedByIter = new Dictionary<int, DateTimeOffset>();
        var scoredByIter = new Dictionary<int, DateTimeOffset>();
        foreach (var e in transcript)
        {
            if (e.Kind == "task.iteration.started" || e.Kind == "task.iteration.scored")
            {
                int? iter = TryExtractIter(e.PayloadJson);
                if (iter is null) continue;
                if (e.Kind == "task.iteration.started") startedByIter[iter.Value] = e.At;
                else scoredByIter[iter.Value] = e.At;
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
            foreach (var u in sortedUsage)
            {
                if (u.StartedAt > windowStart && u.StartedAt <= windowEnd)
                {
                    inTok += u.InputTokens;
                    outTok += u.OutputTokens;
                    cost += u.CostUsd;
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
            int filesChanged = pathsThisIter.Count;

            long? durationMs = null;
            if (startedByIter.TryGetValue(d.Iter, out var s)
                && scoredByIter.TryGetValue(d.Iter, out var e2))
            {
                durationMs = (long)(e2 - s).TotalMilliseconds;
                if (durationMs < 0) durationMs = null;
            }

            iters.Add(new
            {
                iter = d.Iter,
                score = d.Score,
                verdict = d.Verdict,
                inputTokens = inTok,
                outputTokens = outTok,
                costUsd = cost,
                durationMs,
                filesChanged,
                createdAt = d.CreatedAt,
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
        ForgeDbContext db,
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
            wsCrew = await db.Crews.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == lastDel.CrewId, ct);
            if (wsCrew is not null && !string.IsNullOrEmpty(wsCrew.PairedWithCrewId))
            {
                evalCrew = await db.Crews.AsNoTracking()
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

            var agentRow = await db.Agents.AsNoTracking()
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
