// -----------------------------------------------------------------------
// <copyright file="TaskExecutionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 16.9 — read-side surfaces for the Live Execution view.
///
/// <list type="bullet">
///   <item><c>GET /api/work-items/{id}/execution</c> — single snapshot:
///         current iteration, generator + evaluator crew/agent ids,
///         deliverables history, cumulative tokens, budget remaining,
///         last-N transcript events, last-known worktree state.</item>
///   <item><c>GET /api/work-items/{id}/transcript?from=...&amp;limit=...</c> —
///         paginated <c>task_transcript_event</c> rows for replay-on-open
///         when the SignalR live tail is attached.</item>
/// </list>
/// </summary>
public static class TaskExecutionEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static WebApplication MapTaskExecutionEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/work-items").WithTags("WorkItems");
        grp.MapGet("/{id}/execution", GetExecution).WithName("GetWorkItemExecution");
        grp.MapGet("/{id}/transcript", GetTranscript).WithName("GetWorkItemTranscript");
        grp.MapDelete("/{id}/transcript", ClearTranscript).WithName("ClearWorkItemTranscript");
        return app;
    }

    private static async Task<IResult> GetExecution(
        string id,
        ForgeDbContext db,
        WorktreeChangeFeed worktreeFeed,
        CancellationToken ct)
    {
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

        // When the work item row doesn't exist (e.g. plan/code task enqueued
        // before the WIT row is materialized), build a minimal snapshot from
        // the queue + transcript data instead of returning 404.
        var queueRows = await db.TaskQueue.AsNoTracking()
            .Where(r => r.WorkItemId == id)
            .ToListAsync(ct);
        var latestQueue = queueRows.OrderByDescending(r => r.EnqueuedAt).FirstOrDefault();

        if (wi is null && latestQueue is null)
            return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Deliverables for this work item, oldest → newest.
        var deliverableRows = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        deliverableRows = deliverableRows.OrderBy(d => d.Iter).ThenBy(d => d.CreatedAt).ToList();
        var deliverables = deliverableRows.Select(MapDeliverable).ToList();

        // Generator + evaluator from the most recent deliverable, falling
        // back to the queue row or work item assignment when no deliverables
        // exist yet (task is queued/claimed but hasn't produced output).
        string? generatorCrewId = null, evaluatorCrewId = null;
        string? generatorAgentId = null, evaluatorAgentId = null;
        var current = deliverableRows.LastOrDefault();
        if (current is not null)
        {
            generatorCrewId = current.CrewId;
            generatorAgentId = current.GeneratorAgentId;
            evaluatorAgentId = current.EvaluatorAgentId;
            var evaluatorAgent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == current.EvaluatorAgentId, ct);
            evaluatorCrewId = evaluatorAgent?.CrewId;
        }
        else
        {
            // Fallback: read from the queue row or work item assignment JSON.
            if (latestQueue is not null)
            {
                generatorCrewId = latestQueue.RoutedToCrewId;
                generatorAgentId = latestQueue.PickedUpByAgent;
            }
            // Also try the work item's AssignedToJson for crew/agent info.
            if (string.IsNullOrEmpty(generatorCrewId) && !string.IsNullOrEmpty(wi?.AssignedToJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(wi.AssignedToJson);
                    if (doc.RootElement.TryGetProperty("crewId", out var c))
                        generatorCrewId ??= c.GetString();
                    if (doc.RootElement.TryGetProperty("generatorAgentId", out var g))
                        generatorAgentId ??= g.GetString();
                    if (doc.RootElement.TryGetProperty("evaluatorAgentId", out var e) && e.ValueKind == JsonValueKind.String)
                        evaluatorAgentId ??= e.GetString();
                    if (doc.RootElement.TryGetProperty("evaluatorCrewId", out var ec) && ec.ValueKind == JsonValueKind.String)
                        evaluatorCrewId ??= ec.GetString();
                }
                catch { /* best effort */ }
            }
            // Last resort: check agent_usage ledger (covers direct invocations).
            if (string.IsNullOrEmpty(generatorCrewId))
            {
                var usageRows = await db.AgentUsage.AsNoTracking()
                    .Where(u => u.TaskId == id && u.CrewId != null && u.CrewId != "")
                    .ToListAsync(ct);
                var latestUsage = usageRows.OrderByDescending(u => u.StartedAt).FirstOrDefault();
                if (latestUsage is not null)
                {
                    generatorCrewId = latestUsage.CrewId;
                    generatorAgentId ??= latestUsage.AgentId;
                }
            }
        }

        // Cost: split by category from the agent_usage ledger.
        var usage = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == id)
            .ToListAsync(ct);
        decimal genCost  = usage.Where(u => u.CostCategory == "generator").Sum(u => u.CostUsd);
        decimal evalCost = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.CostUsd);
        long genIn  = usage.Where(u => u.CostCategory == "generator").Sum(u => u.InputTokens);
        long genOut = usage.Where(u => u.CostCategory == "generator").Sum(u => u.OutputTokens);
        long evalIn  = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.InputTokens);
        long evalOut = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.OutputTokens);

        // Crew budget (today's spend vs. daily cap, if any).
        // Also resolve evaluatorCrewId from the generator crew's pair when not
        // yet known from deliverables (pre-first-iteration state).
        decimal? dailyBudget = null;
        decimal? spendToday = null;
        decimal? maxTaskBudget = null;
        string? generatorCrewName = null;
        string? evaluatorCrewName = null;
        if (!string.IsNullOrEmpty(generatorCrewId))
        {
            var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == generatorCrewId, ct);
            dailyBudget = crew?.DailyBudgetUsd;
            maxTaskBudget = crew?.MaxTaskBudgetUsd;
            generatorCrewName = crew?.Name;
            if (string.IsNullOrEmpty(evaluatorCrewId) && !string.IsNullOrEmpty(crew?.PairedWithCrewId))
            {
                evaluatorCrewId = crew.PairedWithCrewId;
            }
            // Resolve evaluator agent from the evaluator crew's lead when
            // not yet known (pre-first-iteration or AssignedToJson cleared).
            if (string.IsNullOrEmpty(evaluatorAgentId) && !string.IsNullOrEmpty(evaluatorCrewId))
            {
                var evalCrew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evaluatorCrewId, ct);
                evaluatorAgentId = crew?.DefaultEvaluatorAgentId ?? evalCrew?.Lead;
                evaluatorCrewName = evalCrew?.Name;
            }
            if (crew is not null)
            {
                var since = DateTimeOffset.UtcNow.AddHours(-24);
                var crewUsage = (await db.AgentUsage.AsNoTracking()
                    .Where(u => u.CrewId == crew.Id)
                    .ToListAsync(ct))
                    .Where(u => u.StartedAt >= since);
                spendToday = crewUsage.Sum(u => u.CostUsd);
            }
        }

        // Recent transcript (last 200 events, oldest→newest for natural rendering).
        var transcriptRows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id)
            .OrderByDescending(e => e.Id)
            .Take(200)
            .ToListAsync(ct);
        transcriptRows.Reverse();
        var transcript = transcriptRows.Select(MapTranscript).ToList();

        // Worktree (best-effort): most recent task.worktree-tick payload.
        WorktreeSnapshot? worktree = null;
        var lastTick = transcriptRows.LastOrDefault(e => e.Kind == "task.worktree-tick");
        if (lastTick is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(lastTick.PayloadJson);
                worktree = ParseWorktreeTick(doc.RootElement, lastTick.At);
            }
            catch { /* malformed payload — fall through to null */ }
        }

        // Iteration counter + score: derived from deliverables.
        var currentIter = deliverableRows.Count;
        int? currentScore = current?.Score;
        string? currentVerdict = current?.Verdict;

        // Resolve agent full names.
        string? generatorAgentName = null;
        string? evaluatorAgentName = null;
        var agentIds = new HashSet<string>();
        if (!string.IsNullOrEmpty(generatorAgentId)) agentIds.Add(generatorAgentId);
        if (!string.IsNullOrEmpty(evaluatorAgentId)) agentIds.Add(evaluatorAgentId);
        if (agentIds.Count > 0)
        {
            var agentRows = await db.Agents.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
            generatorAgentName = agentRows.FirstOrDefault(a => a.Id == generatorAgentId)?.Name;
            evaluatorAgentName = agentRows.FirstOrDefault(a => a.Id == evaluatorAgentId)?.Name;
        }

        var snap = new TaskExecutionSnapshot(
            WorkItemId: id,
            GeneratorCrewId: generatorCrewId,
            GeneratorCrewName: generatorCrewName,
            EvaluatorCrewId: evaluatorCrewId,
            EvaluatorCrewName: evaluatorCrewName,
            GeneratorAgentId: generatorAgentId,
            GeneratorAgentName: generatorAgentName,
            EvaluatorAgentId: evaluatorAgentId,
            EvaluatorAgentName: evaluatorAgentName,
            CurrentIteration: currentIter,
            CurrentScore: currentScore,
            CurrentVerdict: currentVerdict,
            GeneratorCostUsd: genCost,
            EvaluatorCostUsd: evalCost,
            TotalCostUsd: genCost + evalCost,
            GeneratorInputTokens: genIn,
            GeneratorOutputTokens: genOut,
            EvaluatorInputTokens: evalIn,
            EvaluatorOutputTokens: evalOut,
            CrewDailyBudgetUsd: dailyBudget,
            CrewSpendTodayUsd: spendToday,
            MaxTaskBudgetUsd: maxTaskBudget,
            AutoDispatchedAt: wi?.AutoDispatchedAt,
            ExpectedFileChanges: wi?.ExpectedFileChanges,
            Deliverables: deliverables,
            RecentTranscript: transcript,
            Worktree: worktree,
            ExecutionStatus: wi?.ExecutionStatus ?? ExecutionStatuses.Idle,
            TrialCount: wi?.TrialCount ?? 0);

        return Results.Ok(snap);
    }

    private static async Task<IResult> GetTranscript(
        string id,
        ForgeDbContext db,
        long? from,
        int? limit,
        CancellationToken ct)
    {
        var size = Math.Clamp(limit ?? 500, 1, 2000);
        var query = db.TaskTranscriptEvents.AsNoTracking().Where(e => e.WorkItemId == id);
        if (from is long cursor) query = query.Where(e => e.Id > cursor);
        var rows = await query.OrderBy(e => e.Id).Take(size).ToListAsync(ct);
        return Results.Ok(rows.Select(MapTranscript).ToList());
    }

    private static async Task<IResult> ClearTranscript(
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var rows = await db.TaskTranscriptEvents
            .Where(e => e.WorkItemId == id)
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            db.TaskTranscriptEvents.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { deleted = rows.Count });
    }

    private static Deliverable MapDeliverable(DeliverableRow r)
    {
        Dictionary<string, int> dims = new();
        try { dims = JsonSerializer.Deserialize<Dictionary<string, int>>(r.DimsJson) ?? new(); }
        catch { /* ignore malformed dims */ }
        return new Deliverable(
            r.Id, r.WorkItemId, r.CrewId, r.GeneratorAgentId, r.EvaluatorAgentId,
            r.Iter, r.Score, dims, r.Verdict, r.Notes, r.GenCostUsd, r.EvalCostUsd,
            r.JudgeAgentId, r.JudgeScore, r.JudgeNotes, r.CreatedAt);
    }

    private static TaskTranscriptEvent MapTranscript(TaskTranscriptEventRow r)
    {
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(r.PayloadJson) ? "{}" : r.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }
        return new TaskTranscriptEvent(r.Id, r.WorkItemId, r.Kind, r.AgentId, r.CrewId, payload, r.At);
    }

    private static WorktreeSnapshot? ParseWorktreeTick(JsonElement el, DateTimeOffset at)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        string? wsId = el.TryGetProperty("workstreamId", out var w) ? w.GetString() : null;
        int added    = TryGetInt(el, "added", 0);
        int modified = TryGetInt(el, "modified", 0);
        int deleted  = TryGetInt(el, "deleted", 0);
        int ahead  = TryGetInt(el, "ahead", 0);
        int behind = TryGetInt(el, "behind", 0);
        int dirty  = TryGetInt(el, "dirty", added + modified + deleted);
        bool truncated = el.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;

        string? sha = null, msg = null, author = null;
        DateTimeOffset? headAt = null;
        if (el.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
        {
            sha    = head.TryGetProperty("sha", out var s) ? s.GetString() : null;
            msg    = head.TryGetProperty("message", out var m) ? m.GetString() : null;
            author = head.TryGetProperty("author", out var a) ? a.GetString() : null;
            if (head.TryGetProperty("at", out var aa) && aa.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(aa.GetString(), out var dt))
                headAt = dt;
        }

        return new WorktreeSnapshot(
            wsId ?? string.Empty,
            added, modified, deleted,
            ahead, behind, dirty,
            sha, msg, author, headAt,
            truncated, at);
    }

    private static int TryGetInt(JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return fallback;
    }
}
