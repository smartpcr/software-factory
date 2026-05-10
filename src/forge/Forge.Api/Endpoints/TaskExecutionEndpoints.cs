// -----------------------------------------------------------------------
// <copyright file="TaskExecutionEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using System.IO;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
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
        grp.MapGet("/{id}/transcript/{eventId:long}", GetTranscriptEvent).WithName("GetWorkItemTranscriptEvent");
        grp.MapDelete("/{id}/transcript", ClearTranscript).WithName("ClearWorkItemTranscript");
        grp.MapPatch("/{id}/execution", PatchExecution).WithName("PatchWorkItemExecution");
        grp.MapPost("/{id}/resume", Resume).WithName("ResumeWorkItem");
        grp.MapGet("/{id}/commits", GetCommits).WithName("GetWorkItemCommits");
        // Phase 39 (Layer C) — open-questions wizard for the conversation tab.
        grp.MapGet("/{id}/questions", GetQuestions).WithName("GetWorkItemQuestions");
        grp.MapPost("/{id}/questions/answer", AnswerQuestions).WithName("AnswerWorkItemQuestions");
        grp.MapPost("/{id}/questions/dismiss", DismissQuestions).WithName("DismissWorkItemQuestions");
        return app;
    }

    /// <summary>
    /// Phase 39 (Layer C) — list every question ever emitted for a work
    /// item, deduped by ExternalId taking the most-recent row. Powers the
    /// conversation-tab wizard: unanswered rows become editable, answered
    /// rows show the operator's prior answer for context.
    ///
    /// Self-healing: when no rows exist but this is a plan-doc workItemId
    /// AND the doc has a `## Open Questions` markdown section in its
    /// committed content, the endpoint parses it, persists the questions
    /// (so the verdict gate sees them too), and returns them. This covers
    /// the historical iterations that emitted questions in markdown
    /// rather than the structured JSON block format Layer C now requires.
    /// </summary>
    private static async Task<IResult> GetQuestions(
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var rows = await db.DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == id)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            // Self-heal: attempt to import questions from the doc's body
            // markdown. Only fires for plan-doc:* ids (the synthetic id
            // format we know how to resolve to a doc on disk / branch).
            await TryImportQuestionsFromDocAsync(db, id, ct);
            rows = await db.DeliverableQuestions.AsNoTracking()
                .Where(q => q.WorkItemId == id)
                .ToListAsync(ct);
        }

        var latestPerId = rows
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First())
            .OrderBy(q => q.Iter).ThenBy(q => q.ExternalId)
            .ToList();
        var items = latestPerId.Select(q => new
        {
            id = q.Id,
            externalId = q.ExternalId,
            iter = q.Iter,
            text = q.Text,
            kind = q.Kind,
            choices = string.IsNullOrEmpty(q.ChoicesJson)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(q.ChoicesJson) ?? Array.Empty<string>(),
            answer = q.Answer,
            answeredAt = q.AnsweredAt,
            answeredBy = q.AnsweredBy,
        }).ToList();
        var unansweredCount = items.Count(i => string.IsNullOrEmpty(i.answer));
        return Results.Ok(new { workItemId = id, items, unansweredCount });
    }

    /// <summary>
    /// Phase 39.1 — for plan-doc workItemIds (format
    /// <c>plan-doc:{spawnId}:{filename}</c>), read the doc's committed
    /// markdown content and parse any <c>## Open Questions</c> section into
    /// <see cref="DeliverableQuestionRow"/> rows. Idempotent: only fires
    /// when the table has zero rows for this work item.
    /// </summary>
    private static async Task TryImportQuestionsFromDocAsync(
        ForgeDbContext db, string workItemId, CancellationToken ct)
    {
        if (!workItemId.StartsWith("plan-doc:", StringComparison.Ordinal)) return;

        // Format: plan-doc:{spawnId}:{filename}
        var parts = workItemId.Split(':', 3);
        if (parts.Length != 3) return;
        var spawnId = parts[1];
        var fileName = parts[2];

        var spawn = await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spawnId, ct);
        if (spawn is null) return;

        // Path under the repo: docs/stories/{safeStoryId}/{file}.
        var safeStoryId = StoryPaths.Safe(spawn.StoryId);
        var relPath = $"docs/stories/{safeStoryId}/{fileName.ToLowerInvariant()}";
        var repoRoot = Forge.Api.Persistence.ForgeSeed.FindRepoRoot();
        var fullPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

        string? content = null;
        if (File.Exists(fullPath))
        {
            try { content = await File.ReadAllTextAsync(fullPath, ct); } catch { /* best-effort */ }
        }
        if (string.IsNullOrWhiteSpace(content)) return;

        var parsed = OpenQuestionsExtractor.Extract(content);
        if (parsed.Count == 0) return;

        // Use iter = 0 to mark these as imported-from-doc (vs emitted live).
        // The deliverable_questions schema allows it; the wizard renders
        // them the same as iter=N rows.
        foreach (var q in parsed)
        {
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = workItemId,
                ExternalId = q.ExternalId,
                Iter = 0,
                Text = q.Text,
                Kind = q.Kind,
                ChoicesJson = q.Choices is { Count: > 0 } ? JsonSerializer.Serialize(q.Choices) : null,
                Answer = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public sealed record AnswerQuestionsRequest(IReadOnlyList<AnswerEntry> Answers);
    public sealed record AnswerEntry(string ExternalId, string Answer);
    public sealed record DismissQuestionsRequest(IReadOnlyList<string> ExternalIds);

    /// <summary>
    /// Phase 39 (Layer C) — operator-dismiss path for legacy / unanswerable
    /// questions. Sets Answer to a sentinel (`"(dismissed by operator)"`)
    /// and AnsweredBy so the row stops blocking the verdict gate but the
    /// audit trail still shows who decided to skip it. UI surfaces a small
    /// "✕ dismiss" button next to each unanswered question.
    /// </summary>
    private static async Task<IResult> DismissQuestions(
        HttpContext ctx,
        string id,
        [FromBody] DismissQuestionsRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (req?.ExternalIds is null || req.ExternalIds.Count == 0)
            return Results.Problem(title: "externalIds[] is required", statusCode: 400);

        var actor = ctx.Request.Headers.TryGetValue("X-Forge-User", out var u) ? u.ToString() : "operator";
        var existing = await db.DeliverableQuestions
            .Where(q => q.WorkItemId == id)
            .ToListAsync(ct);
        var byExtId = existing
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(), StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        foreach (var extId in req.ExternalIds)
        {
            if (string.IsNullOrEmpty(extId)) continue;
            if (!byExtId.TryGetValue(extId, out var latest)) continue;
            latest.Answer = "(dismissed by operator)";
            latest.AnsweredAt = now;
            latest.AnsweredBy = actor;
        }
        await db.SaveChangesAsync(ct);

        var unansweredCount = await DeliverableLoop.CountUnansweredQuestionsAsync(db, id, ct);
        return Results.Ok(new { workItemId = id, unansweredCount });
    }

    /// <summary>
    /// Phase 39 (Layer C) — record operator answers. We always insert a
    /// fresh row (matching the iter of the most-recent emission for each
    /// ExternalId) so the history reads "asked at iter N, answered at iter
    /// N" cleanly. Returns the updated unanswered count so the UI can
    /// re-render the wizard without an extra GET.
    /// </summary>
    private static async Task<IResult> AnswerQuestions(
        HttpContext ctx,
        string id,
        [FromBody] AnswerQuestionsRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (req?.Answers is null || req.Answers.Count == 0)
            return Results.Problem(title: "answers[] is required", statusCode: 400);

        var actor = ctx.Request.Headers.TryGetValue("X-Forge-User", out var u) ? u.ToString() : "operator";
        var existing = await db.DeliverableQuestions
            .Where(q => q.WorkItemId == id)
            .ToListAsync(ct);
        var byExtId = existing
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(), StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        foreach (var a in req.Answers)
        {
            if (string.IsNullOrEmpty(a.ExternalId)) continue;
            if (!byExtId.TryGetValue(a.ExternalId, out var latest)) continue;
            // Update the latest row in place so subsequent renders show the
            // answer next to the question that surfaced it. If a future iter
            // re-emits the same ExternalId with a fresh row, the operator
            // will see the new (unanswered) row alongside the historical
            // answer via the per-iter dedupe.
            latest.Answer = a.Answer ?? string.Empty;
            latest.AnsweredAt = now;
            latest.AnsweredBy = actor;
        }
        await db.SaveChangesAsync(ct);

        // Re-compute and return the updated unanswered count for the UI.
        var unansweredCount = await DeliverableLoop.CountUnansweredQuestionsAsync(db, id, ct);
        return Results.Ok(new { workItemId = id, unansweredCount });
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

        // Synthetic workItemIds (e.g. `plan-doc:{spawn}:{file}.md` from
        // CrewStoryArtifactStrategy) bypass TaskQueue entirely — they invoke
        // IDeliverableLoop directly. Treat the existence of any transcript or
        // deliverable as proof the workItemId is real, so the live-exec /
        // doc-conv view can still subscribe + page history.
        if (wi is null && latestQueue is null)
        {
            var hasTranscript = await db.TaskTranscriptEvents.AsNoTracking()
                .AnyAsync(e => e.WorkItemId == id, ct);
            var hasDeliverable = await db.Deliverables.AsNoTracking()
                .AnyAsync(d => d.WorkItemId == id, ct);
            if (!hasTranscript && !hasDeliverable)
                return Results.Problem(title: $"Work item {id} not found", statusCode: 404);
        }

        // Deliverables for this work item, oldest → newest.
        var deliverableRows = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        deliverableRows = deliverableRows.OrderBy(d => d.CreatedAt).ThenBy(d => d.Iter).ToList();
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
            // Also try the work item's AssignedAgentJson (Phase 22) or
            // AssignedToJson (legacy) for any still-missing fields.
            // The queue row may set generatorCrewId but leave generatorAgentId null
            // (e.g. restart before claim), so check each field independently.
            var assignmentJson = wi?.AssignedAgentJson ?? wi?.AssignedToJson;
            if (!string.IsNullOrEmpty(assignmentJson)
                && (string.IsNullOrEmpty(generatorCrewId) || string.IsNullOrEmpty(generatorAgentId)
                    || string.IsNullOrEmpty(evaluatorAgentId) || string.IsNullOrEmpty(evaluatorCrewId)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(assignmentJson);
                    if (doc.RootElement.TryGetProperty("crewId", out var c))
                        generatorCrewId ??= c.GetString();
                    if (doc.RootElement.TryGetProperty("generatorAgentId", out var g))
                        generatorAgentId ??= g.GetString();
                    // Phase 22: agents[] array in AssignedAgentJson.
                    if (string.IsNullOrEmpty(generatorAgentId)
                        && doc.RootElement.TryGetProperty("agents", out var agArr) && agArr.ValueKind == JsonValueKind.Array && agArr.GetArrayLength() > 0)
                        generatorAgentId ??= agArr[0].GetString();
                    if (doc.RootElement.TryGetProperty("evaluatorAgentId", out var e) && e.ValueKind == JsonValueKind.String)
                        evaluatorAgentId ??= e.GetString();
                    // Phase 22: agents[1] is the evaluator in paired runs.
                    if (string.IsNullOrEmpty(evaluatorAgentId)
                        && doc.RootElement.TryGetProperty("agents", out var agArr2) && agArr2.ValueKind == JsonValueKind.Array && agArr2.GetArrayLength() > 1)
                        evaluatorAgentId ??= agArr2[1].GetString();
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

        // Total transcript count for paging UI.
        var transcriptTotalCount = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id)
            .CountAsync(ct);

        // Recent transcript (last 500 events, oldest→newest for natural rendering).
        // Always include prompt + iteration boundary events so the UI can
        // show the prompt section and iteration pager even when the window
        // has scrolled past them.
        var transcriptRows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id)
            .OrderByDescending(e => e.Id)
            .Take(500)
            .ToListAsync(ct);
        var pinnedKinds = new HashSet<string>
        {
            "task.prompt", "task.delegated",
            "task.iteration.started", "task.iteration.scored",
            "task.iteration.passed", "task.iteration.rejected",
            "task.iteration.iterated", "task.iteration.stalled",
        };
        var pinnedIds = new HashSet<long>(transcriptRows.Select(r => r.Id));
        var pinnedRows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id && pinnedKinds.Contains(e.Kind) && !pinnedIds.Contains(e.Id))
            .ToListAsync(ct);
        transcriptRows.AddRange(pinnedRows);
        transcriptRows = transcriptRows.OrderBy(e => e.Id).ToList();
        var transcript = transcriptRows.Select(MapTranscript).ToList();

        // Worktree (best-effort): most recent task.worktree-tick payload.
        // Falls back to a live disk read when no tick events exist (e.g.
        // after a server restart that lost in-memory feed subscriptions).
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

        // Fallback: read worktree directly from disk using the path from
        // the started event or derived from the work item's repo + branch.
        if (worktree is null)
        {
            string? wtPath = null;
            var startedEvt = transcriptRows.FirstOrDefault(e => e.Kind == "task.started" || e.Kind == "task.prompts");
            if (startedEvt is not null)
            {
                try
                {
                    using var startDoc = JsonDocument.Parse(startedEvt.PayloadJson);
                    if (startDoc.RootElement.TryGetProperty("worktreePath", out var wp))
                        wtPath = wp.GetString();
                }
                catch { /* ignore */ }
            }

            // Convention fallback: resolve via WorktreePathResolver so the
            // snapshot endpoint and the commits endpoint agree on which
            // path actually holds the worktree (Phase 49 short name vs.
            // legacy `spawn-` vs. legacy `ws-`).
            if (string.IsNullOrEmpty(wtPath))
            {
                var resolved = await Forge.Api.Services.Stories.WorktreePathResolver
                    .ResolveForWorkstreamAsync(db, id, ct);
                if (!string.IsNullOrEmpty(resolved.WorktreePath))
                {
                    wtPath = resolved.WorktreePath;
                }
            }

            if (!string.IsNullOrEmpty(wtPath) && Directory.Exists(wtPath))
            {
                try
                {
                    var diskSnap = await worktreeFeed.SampleAsync(wtPath, ct);
                    if (diskSnap is not null)
                    {
                        worktree = new WorktreeSnapshot(
                            string.Empty,
                            diskSnap.Added, diskSnap.Modified, diskSnap.Deleted,
                            diskSnap.Ahead, diskSnap.Behind, diskSnap.Dirty,
                            diskSnap.HeadSha, diskSnap.HeadMessage, diskSnap.HeadAuthor, diskSnap.HeadAt,
                            diskSnap.Truncated, DateTimeOffset.UtcNow,
                            wtPath);

                        // Re-subscribe the feed so future ticks are emitted.
                        worktreeFeed.Subscribe(id, string.Empty, wtPath);
                    }
                }
                catch { /* best-effort */ }
            }
        }

        // Time budget: derive from work-item points + phase tag. Coding tasks
        // use "impl"; plan-doc tasks supply their own budgetSeconds via the
        // per-doc iterations endpoint, but we surface the same field here so
        // the workstream conversation tab can render "timeout Xm" consistently.
        var phaseTag = "impl";
        var budgetSeconds = (int)CrewStoryArtifactStrategy.BudgetForPhase(wi?.Points ?? 0, phaseTag).TotalSeconds;

        // Iteration counter + score: derived from the chronologically latest deliverable.
        var currentIter = current?.Iter ?? 0;
        int? currentScore = current?.Score;
        string? currentVerdict = current?.Verdict;

        // Resolve agent full names + the model each role is invoking with.
        // Mirrors CrewQueueWorker's resolution order (agent override → crew
        // default) so the UI shows the same model the runtime will dispatch.
        string? generatorAgentName = null;
        string? evaluatorAgentName = null;
        string? generatorModel = null;
        string? evaluatorModel = null;
        var agentIds = new HashSet<string>();
        if (!string.IsNullOrEmpty(generatorAgentId)) agentIds.Add(generatorAgentId);
        if (!string.IsNullOrEmpty(evaluatorAgentId)) agentIds.Add(evaluatorAgentId);
        if (agentIds.Count > 0)
        {
            var agentRows = await db.Agents.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
            var genAgent = agentRows.FirstOrDefault(a => a.Id == generatorAgentId);
            var evalAgent = agentRows.FirstOrDefault(a => a.Id == evaluatorAgentId);
            generatorAgentName = genAgent?.Name;
            evaluatorAgentName = evalAgent?.Name;

            CrewRow? genCrew = null;
            CrewRow? evalCrew = null;
            if (!string.IsNullOrEmpty(generatorCrewId))
                genCrew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == generatorCrewId, ct);
            if (!string.IsNullOrEmpty(evaluatorCrewId))
                evalCrew = evaluatorCrewId == generatorCrewId
                    ? genCrew
                    : await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evaluatorCrewId, ct);

            generatorModel = NonEmpty(genAgent?.GeneratorModel) ?? NonEmpty(genCrew?.Model);
            evaluatorModel = NonEmpty(evalAgent?.EvaluatorModel)
                ?? NonEmpty(evalCrew?.EvaluatorModel)
                ?? NonEmpty(evalCrew?.Model);
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
            GeneratorModel: generatorModel,
            EvaluatorModel: evaluatorModel,
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
            TrialCount: wi?.TrialCount ?? 0,
            // Default to the points-derived cap (5 per pt) so the live-exec
            // header reads "iter N of {pts*5}" instead of always "of 5".
            // The runtime PATCH override on the queue row still wins.
            MaxIterations: latestQueue?.MaxIterOverride ?? ScoringConstants.MaxIterationsForPoints(wi?.Points ?? 0),
            TranscriptTotalCount: transcriptTotalCount,
            BudgetSeconds: budgetSeconds);

        return Results.Ok(snap);
    }

    /// <summary>
    /// Per-event lookup that returns the FULL untruncated payload. The bulk
    /// transcript stream (<see cref="GetTranscript"/>) trims systemPrompt /
    /// userPrompt / large tool results to 320 chars per field so a long
    /// session doesn't ship multi-MB responses; this endpoint backs the
    /// "Show full prompt" affordance the operator clicks when a marker
    /// appears in the live-exec / Overview view.
    /// </summary>
    private static async Task<IResult> GetTranscriptEvent(
        string id,
        long eventId,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var row = await db.TaskTranscriptEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkItemId == id && e.Id == eventId, ct);
        if (row is null) return Results.NotFound();

        // Bypass TruncateBulkyFields — the caller asked for the full
        // payload explicitly. Parse the raw JSON so the response shape
        // matches MapTranscript's contract; only the bulk fields differ.
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(row.PayloadJson) ? "{}" : row.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }
        return Results.Ok(new TaskTranscriptEvent(row.Id, row.WorkItemId, row.Kind, row.AgentId, row.CrewId, payload, row.At));
    }

    private static async Task<IResult> GetTranscript(
        string id,
        ForgeDbContext db,
        long? from,
        long? before,
        int? limit,
        string? order,
        CancellationToken ct)
    {
        var size = Math.Clamp(limit ?? 500, 1, 2000);
        var totalCount = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == id).CountAsync(ct);
        var query = db.TaskTranscriptEvents.AsNoTracking().Where(e => e.WorkItemId == id);

        var desc = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);
        if (desc)
        {
            if (before is long cur) query = query.Where(e => e.Id < cur);
            var rows = await query.OrderByDescending(e => e.Id).Take(size).ToListAsync(ct);
            rows.Reverse();
            return Results.Ok(new { events = rows.Select(MapTranscript).ToList(), totalCount });
        }
        else
        {
            if (from is long cursor) query = query.Where(e => e.Id > cursor);
            var rows = await query.OrderBy(e => e.Id).Take(size).ToListAsync(ct);
            return Results.Ok(new { events = rows.Select(MapTranscript).ToList(), totalCount });
        }
    }

    private static async Task<IResult> ClearTranscript(
        HttpContext ctx,
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

        // Phase 21 — also delete deliverable rows so the iter counter
        // resets to 0 when the live-exec view reloads after a restart.
        var deliverables = await db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        if (deliverables.Count > 0)
        {
            db.Deliverables.RemoveRange(deliverables);
            await db.SaveChangesAsync(ct);
        }

        // Phase 22 — clear AssignedAgent and record cleared-restart.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            var agentIds = Forge.Api.Endpoints.WorkItemsEndpoints.ParseAgentIds(wi.AssignedAgentJson);
            var userId = ctx.Request.Headers.TryGetValue("X-Forge-User", out var uv) ? uv.FirstOrDefault() : null;
            db.AgentAssignments.Add(new AgentAssignmentRow
            {
                WorkItemId = id,
                Kind = AgentAssignmentKinds.ClearedRestart,
                AgentIdsJson = System.Text.Json.JsonSerializer.Serialize(agentIds),
                FromExecutionStatus = wi.ExecutionStatus,
                ToExecutionStatus = ExecutionStatuses.Queued,
                AssignedBy = userId,
                At = DateTimeOffset.UtcNow,
            });
            wi.AssignedAgentJson = null;
            // Reset execution state so the item can be re-queued and re-claimed.
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.TrialCount = 0;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { deleted = rows.Count, deletedDeliverables = deliverables.Count });
    }

    private static Deliverable MapDeliverable(DeliverableRow r)
    {
        Dictionary<string, int> dims = new();
        try { dims = JsonSerializer.Deserialize<Dictionary<string, int>>(r.DimsJson) ?? new(); }
        catch { /* ignore malformed dims */ }
        return new Deliverable(
            r.Id, r.WorkItemId, r.CrewId, r.GeneratorAgentId, r.EvaluatorAgentId,
            r.Iter, r.Score, dims, r.Verdict, r.Notes, r.GenCostUsd, r.EvalCostUsd,
            r.JudgeAgentId, r.JudgeScore, r.JudgeNotes, r.CreatedAt,
            r.GeneratorSummary ?? string.Empty);
    }

    /// <summary>
    /// Per-event payload cap shipped via <c>/execution</c>. The full payload
    /// stays in <c>task_transcript_events</c> and is fetchable per-event via
    /// <c>/transcript</c>; the snapshot only ships a trimmed view so a long
    /// run with many <c>task.prompt</c> events (each carrying a multi-KB
    /// system prompt) doesn't balloon a single response into hundreds of KB.
    /// </summary>
    private const int SnapshotPayloadByteCap = 4096;

    private static TaskTranscriptEvent MapTranscript(TaskTranscriptEventRow r)
    {
        JsonElement payload;
        var raw = string.IsNullOrEmpty(r.PayloadJson) ? "{}" : r.PayloadJson;
        if (raw.Length > SnapshotPayloadByteCap && r.Kind is "task.prompt" or "task.tool-call" or "task.tool-result" or "task.iteration.scored")
        {
            // Replace bulky text fields with truncation markers so the UI
            // still renders the row but doesn't tax the wire.
            raw = TruncateBulkyFields(raw);
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            payload = doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse("{}");
            payload = doc.RootElement.Clone();
        }
        return new TaskTranscriptEvent(r.Id, r.WorkItemId, r.Kind, r.AgentId, r.CrewId, payload, r.At);
    }

    /// <summary>
    /// Strip / abbreviate the bulky string fields the live-exec view never
    /// renders inline (system prompts, full tool results). The full text is
    /// still available via <c>/api/work-items/{id}/transcript?from=…</c>.
    /// </summary>
    private static string TruncateBulkyFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && (prop.NameEquals("systemPrompt") || prop.NameEquals("userPrompt")
                            || prop.NameEquals("text") || prop.NameEquals("result")
                            || prop.NameEquals("notes") || prop.NameEquals("toolPayload")
                            || prop.NameEquals("content") || prop.NameEquals("body")))
                    {
                        var s = prop.Value.GetString() ?? string.Empty;
                        if (s.Length > 320)
                        {
                            w.WriteString(prop.Name, s[..320] + $"…[truncated {s.Length} → 320 — GET /api/work-items/{{wi}}/transcript/{{eventId}} for full]");
                            continue;
                        }
                    }
                    prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }

    private static WorktreeSnapshot? ParseWorktreeTick(JsonElement el, DateTimeOffset at)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        string? wsId = el.TryGetProperty("workstreamId", out var w) ? w.GetString() : null;
        string? wtPath = el.TryGetProperty("worktreePath", out var wp) ? wp.GetString() : null;
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
            truncated, at,
            wtPath);
    }

    private static int TryGetInt(JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return fallback;
    }

    // ── Phase 20 — PATCH /api/work-items/{id}/execution ─────────────────

    private sealed record PatchExecutionBody(int? MaxIterations);

    private static async Task<IResult> PatchExecution(
        string id,
        [FromBody] PatchExecutionBody body,
        ForgeDbContext db,
        CancellationToken ct)
    {
        if (body.MaxIterations is int val && (val < 1 || val > 50))
            return Results.Problem(title: "maxIterations must be between 1 and 50", statusCode: 400);

        // Prefer the active (non-completed) queue row so overrides land on the running task.
        var queueRow = await db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed")
            .OrderByDescending(q => q.Id)
            .FirstOrDefaultAsync(ct)
            ?? await db.TaskQueue
                .Where(q => q.WorkItemId == id)
                .OrderByDescending(q => q.Id)
                .FirstOrDefaultAsync(ct);

        if (queueRow is null)
            return Results.Problem(title: $"No queue entry for work item {id}", statusCode: 404);

        queueRow.MaxIterOverride = body.MaxIterations;
        queueRow.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { workItemId = id, maxIterations = body.MaxIterations });
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ── Commits — git log against the work item's worktree branch ──────
    //
    // GET /api/work-items/{id}/commits
    //
    // Authoritative source for "what did the agent change on this branch":
    // runs `git log --name-status origin/{base}..HEAD` against the worktree
    // and returns commits with their actual per-file deltas, plus any
    // currently uncommitted (`git status --porcelain`) paths.
    //
    // Replaces the transcript-tick reconstruction the Worktree tab used
    // to do — that approach attributed dirty paths observed *during the
    // prior SHA* to the next commit, which silently dropped commits
    // landed faster than the tick interval.
    //
    // 404 — work item not found, or repo / clone root unresolvable.
    // 200 with empty `commits` — branch has no commits ahead of base, or
    //                            the worktree directory doesn't exist.

    private sealed record CommitFileChange(string Status, string Path, string? OldPath = null);

    private sealed record CommitEntry(
        string Sha,
        string Message,
        string Author,
        string At,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted,
        IReadOnlyList<CommitFileChange> Other);

    private sealed record UncommittedChanges(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted);

    /// <summary>
    /// Phase 49 follow-up — the cumulative diff between
    /// <c>origin/{BaseBranch}</c> and the worktree's working tree
    /// (committed + uncommitted, the same scope a PR against
    /// <c>BaseBranch</c> would show). Different from
    /// <see cref="UncommittedChanges"/>, which is ONLY the dirty/staged
    /// changes from <c>git status</c>: for a workstream where the agent
    /// committed everything, <c>Uncommitted</c> is all-empty and the
    /// Worktree-tab counters underreport. Branch totals deduplicate
    /// per-path across commits (Add-then-Modify in two commits collapses
    /// to one Added entry) so the count matches what reviewers see in
    /// the PR's file list.
    /// </summary>
    private sealed record BranchTotals(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Deleted);

    private sealed record CommitsResponse(
        string? WorktreePath,
        string Branch,
        string BaseBranch,
        int Ahead,
        int Behind,
        string? HeadSha,
        IReadOnlyList<CommitEntry> Commits,
        UncommittedChanges Uncommitted,
        BranchTotals BranchTotals);

    private static async Task<IResult> GetCommits(
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is null)
            return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Resolve worktree path with the same convention CrewQueueWorker uses:
        // {repo.CloneRoot}/.worktree/ws-{sanitizedId}.
        string? cloneRoot = null;
        var baseBranch = "main";
        if (!string.IsNullOrEmpty(wi.RepoId))
        {
            var repo = await db.Set<Forge.Api.Persistence.RepoRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
            if (!string.IsNullOrEmpty(repo?.CloneRoot)) cloneRoot = repo.CloneRoot;
            if (!string.IsNullOrEmpty(repo?.DefaultBranch)) baseBranch = repo.DefaultBranch;
        }

        // Phase 49 follow-up — engineer workstreams branch off the SPAWN's
        // chosen base, not the repo's default branch. For
        // failover-cluster:XRAFT the spawn's BaseBranch is
        // `feature/xraft` (the plan PR merge target) while repo default
        // is `main`. Diffing against `main` includes the plan PR's own
        // commit (`[plan] xraft (#1)` with 9 doc files) as if it were
        // part of the workstream's contribution. Use the workstream's
        // owning spawn.BaseBranch when available so the commit list
        // shows only what THIS workstream produced.
        var wsRow = await db.Workstreams.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wsRow is not null && !string.IsNullOrEmpty(wsRow.StorySpawnId))
        {
            var spawnRow = await db.StorySpawns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == wsRow.StorySpawnId, ct);
            if (spawnRow is not null && !string.IsNullOrEmpty(spawnRow.BaseBranch))
            {
                baseBranch = spawnRow.BaseBranch;
            }
        }

        // Phase 42 — plan-doc work-items (id pattern `plan-doc:{spawnId}:{file}`)
        // share the spawn's plan-branch worktree at `{cloneRoot}/.worktree/spawn-{slug}`,
        // not the per-workstream `.worktree/ws-{id}` convention. Route to the
        // right worktree based on the id prefix; engineer workstreams take
        // the legacy path.
        //
        // Phase 49 — engineer workstreams switched to
        // `stage-{phaseNum}.{stageNum}-{anchorSlug}` to dodge Windows
        // MAX_PATH. Use WorktreePathResolver so the displayed worktree
        // path agrees with whatever's actually on disk (new short name,
        // legacy `spawn-` prefix, or legacy `ws-` prefix). Observed
        // 2026-05-10: PR #2 had 35 files but the Worktree tab read +0
        // changes because the old code hard-coded the `ws-{id}` path
        // while the orchestrator had written to `stage-1.1-…`.
        string branch;
        string? worktreePath;
        if (id.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            // Extract `spawn-{storyId}` from `plan-doc:{spawnId}:{file}`.
            var spawnId = id.Substring("plan-doc:".Length);
            var lastColon = spawnId.LastIndexOf(':');
            if (lastColon > 0) spawnId = spawnId.Substring(0, lastColon);
            // Look up the spawn so we use its real PlanBranch (preserves
            // any custom branch name the orchestrator picked at spawn time).
            var spawn = await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            branch = spawn?.PlanBranch ?? $"story/{Forge.Api.Services.Stories.StoryPaths.Safe(spawn?.StoryId ?? string.Empty)}/plan";
            // Plan-doc paths still use the legacy `spawn-{slug}` shape
            // (Phase 49 only renamed engineer workstream paths; plan-doc
            // names were already short).
            var sanitizedBranch = SanitizeBranchSlug(branch);
            worktreePath = !string.IsNullOrEmpty(cloneRoot)
                ? Path.Combine(cloneRoot, ".worktree", $"spawn-{sanitizedBranch}")
                : null;
        }
        else
        {
            // Engineer workstream — let WorktreePathResolver pick the
            // right convention (new short name vs. legacy long names) by
            // looking at what's actually on disk.
            var resolved = await Forge.Api.Services.Stories.WorktreePathResolver
                .ResolveForWorkstreamAsync(db, id, ct);
            branch = !string.IsNullOrEmpty(resolved.Branch) ? resolved.Branch : $"ws/{id}";
            worktreePath = resolved.WorktreePath;
        }

        // Empty response when the worktree directory isn't on disk yet.
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
        {
            return Results.Ok(new CommitsResponse(
                WorktreePath: worktreePath,
                Branch: branch,
                BaseBranch: baseBranch,
                Ahead: 0, Behind: 0,
                HeadSha: null,
                Commits: Array.Empty<CommitEntry>(),
                Uncommitted: new UncommittedChanges(
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                BranchTotals: new BranchTotals(
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));
        }

        // Refresh tracking refs so ahead/behind reflects what the remote knows.
        // Best-effort — a network failure here just means staler counts; the
        // commit listing is still local-only.
        await RunGitAsync(worktreePath, ct, "fetch", "origin", "--quiet");

        // Ahead / behind vs the remote base branch.
        var (acCode, acOut, _) = await RunGitAsync(worktreePath, ct,
            "rev-list", "--left-right", "--count", $"origin/{baseBranch}...HEAD");
        int ahead = 0, behind = 0;
        if (acCode == 0)
        {
            var parts = acOut.Trim().Split('\t');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out behind);
                int.TryParse(parts[1], out ahead);
            }
        }

        // HEAD sha for the convenience field.
        var (headCode, headSha, _) = await RunGitAsync(worktreePath, ct, "rev-parse", "HEAD");
        var headShaStr = headCode == 0 ? headSha.Trim() : null;

        // Commit list with per-file changes. The __COMMIT__ prefix lets us
        // distinguish header lines from --name-status rows in one pass.
        var (logCode, logOut, _) = await RunGitAsync(worktreePath, ct,
            "log",
            $"origin/{baseBranch}..HEAD",
            "--pretty=format:__COMMIT__%H%x09%aI%x09%aN%x09%s",
            "--name-status");

        var commits = new List<CommitEntry>();
        if (logCode == 0 && !string.IsNullOrWhiteSpace(logOut))
        {
            CommitEntry? cur = null;
            var added = new List<string>();
            var modified = new List<string>();
            var deleted = new List<string>();
            var other = new List<CommitFileChange>();
            void Flush()
            {
                if (cur is null) return;
                commits.Add(cur with
                {
                    Added = added.ToArray(),
                    Modified = modified.ToArray(),
                    Deleted = deleted.ToArray(),
                    Other = other.ToArray(),
                });
                added.Clear(); modified.Clear(); deleted.Clear(); other.Clear();
            }
            foreach (var rawLine in logOut.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("__COMMIT__", StringComparison.Ordinal))
                {
                    Flush();
                    var fields = line.Substring("__COMMIT__".Length).Split('\t');
                    if (fields.Length >= 4)
                    {
                        cur = new CommitEntry(
                            Sha: fields[0],
                            Message: fields[3],
                            Author: fields[2],
                            At: fields[1],
                            Added: Array.Empty<string>(),
                            Modified: Array.Empty<string>(),
                            Deleted: Array.Empty<string>(),
                            Other: Array.Empty<CommitFileChange>());
                    }
                }
                else if (cur is not null)
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    var status = parts[0];
                    // Renames/copies show 3 cols: R100 oldpath newpath. Track
                    // the rename in `other` and the new path under modified
                    // so the summary still surfaces it.
                    if (status.StartsWith("R", StringComparison.Ordinal) || status.StartsWith("C", StringComparison.Ordinal))
                    {
                        if (parts.Length >= 3)
                        {
                            other.Add(new CommitFileChange(status, parts[2], parts[1]));
                            modified.Add(parts[2]);
                        }
                        continue;
                    }
                    var path = parts[1];
                    switch (status)
                    {
                        case "A": added.Add(path); break;
                        case "M": modified.Add(path); break;
                        case "D": deleted.Add(path); break;
                        default: other.Add(new CommitFileChange(status, path)); break;
                    }
                }
            }
            Flush();
        }

        // Currently uncommitted (working-tree + staged, excluding untracked
        // dirs that the worktree-feed already skips).
        var (stCode, stOut, _) = await RunGitAsync(worktreePath, ct, "status", "--porcelain");
        var uAdded = new List<string>();
        var uMod = new List<string>();
        var uDel = new List<string>();
        if (stCode == 0)
        {
            foreach (var rawLine in stOut.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length < 4) continue;
                var status = line.Substring(0, 2);
                var path = line.Substring(3);
                // The two columns are <staged><unstaged>. We classify by the
                // first non-space column, treating ?? (untracked) as added.
                if (status[0] == 'A' || status[0] == '?' || status == "??") uAdded.Add(path);
                else if (status[0] == 'D' || status[1] == 'D') uDel.Add(path);
                else uMod.Add(path);
            }
        }

        // Phase 49 follow-up — cumulative branch totals (committed +
        // uncommitted) vs the merge target. Same scope a PR review would
        // see. Computed via `git diff --name-status origin/{baseBranch}`
        // which compares the WORKING TREE (HEAD + dirty) against the
        // upstream tip, so committed-and-uncommitted both fold into one
        // status per path. Without this, the Worktree tab pills
        // (`+0 ~0 -0`) underreported every workstream where the agent
        // self-committed (observed 2026-05-10 on failover-cluster:XRAFT
        // stage 1.1 — agent had 4 commits / 35 files, pills read +0/~0/-0).
        var tAdded = new List<string>();
        var tMod = new List<string>();
        var tDel = new List<string>();
        try
        {
            var (totalsCode, totalsOut, _) = await RunGitAsync(
                worktreePath, ct, "diff", "--name-status", $"origin/{baseBranch}");
            if (totalsCode == 0)
            {
                foreach (var rawLine in totalsOut.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    var status = parts[0];
                    // Renames / copies: R100\t<old>\t<new> — bucket as Modified.
                    if (status.StartsWith("R", StringComparison.Ordinal) || status.StartsWith("C", StringComparison.Ordinal))
                    {
                        if (parts.Length >= 3) tMod.Add(parts[2]);
                        continue;
                    }
                    var path = parts[1];
                    switch (status)
                    {
                        case "A": tAdded.Add(path); break;
                        case "D": tDel.Add(path); break;
                        case "M":
                        case "T": tMod.Add(path); break;
                        default: tMod.Add(path); break;
                    }
                }
            }
        }
        catch
        {
            // Best-effort — if origin/{baseBranch} doesn't exist locally
            // (never fetched / detached worktree), the totals stay empty
            // and the UI falls back to the Uncommitted counters.
        }

        return Results.Ok(new CommitsResponse(
            WorktreePath: worktreePath,
            Branch: branch,
            BaseBranch: baseBranch,
            Ahead: ahead, Behind: behind,
            HeadSha: headShaStr,
            Commits: commits,
            Uncommitted: new UncommittedChanges(uAdded, uMod, uDel),
            BranchTotals: new BranchTotals(tAdded, tMod, tDel)));
    }

    /// <summary>
    /// Phase 42 — keep this in sync with <c>RealStoryGitOps.SanitizeBranchSlug</c>
    /// so the commits endpoint computes the same worktree path the architect
    /// strategy creates. Branches like <c>story/foo/plan</c> become
    /// <c>story-foo-plan</c>; the worktree mounts at
    /// <c>{cloneRoot}/.worktree/spawn-{slug}</c>.
    /// </summary>
    private static string SanitizeBranchSlug(string branch)
    {
        var sb = new System.Text.StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }

    private static async Task<(int code, string stdout, string stderr)> RunGitAsync(
        string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    // ── Resume — single entry point that branches on last verdict ──────
    //
    // POST /api/work-items/{id}/resume   body: { note?: string }
    //
    // - last verdict = "pass"
    //     The iter loop already finished cleanly; only the post-loop
    //     delivery (build gate, push, PR) flopped. Enqueue
    //     kind=resume-delivery so CrewQueueWorker.RunAgentAsync skips the
    //     gen+eval loop and replays only the delivery phase. The optional
    //     `note` is ignored (no agent invocation to read it).
    //
    // - last verdict in { "iterate", "stalled", "stalled-budget" }
    //     The iter loop ended without a pass — the agent needs another
    //     turn. Enqueue kind=work without restart=true so the existing
    //     DeliverableLoop seeding (DeliverableLoop.cs:175) picks up at
    //     iter+1 with the full prior chain. If `note` is non-empty, emit
    //     a `task.operator-note` transcript event; CrewQueueWorker reads
    //     the latest one before building the user prompt and prepends it
    //     so the agent's next iteration is steered.
    //
    // - last verdict = "reject" or no deliverables → 400.
    // - already-open queue row → 409.

    private sealed record ResumeBody(string? Note);

    private static async Task<IResult> Resume(
        string id,
        [FromBody] ResumeBody? body,
        ForgeDbContext db,
        Forge.Api.Services.ITaskQueueService taskQueue,
        Microsoft.AspNetCore.SignalR.IHubContext<ForgeHub> hub,
        Forge.Api.Realtime.TaskChannel taskChannel,
        CancellationToken ct)
    {
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is null)
            return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        var allDeliv = await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        var lastDeliv = allDeliv
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();

        if (lastDeliv is null)
        {
            return Results.Problem(
                type: "resume.no-deliverables",
                title: "Nothing to resume — work item has no prior deliverables.",
                detail: "Dispatch the work item first, then resume after a partial run.",
                statusCode: 400);
        }
        if (lastDeliv.Verdict == "reject")
        {
            return Results.Problem(
                type: "resume.rejected",
                title: "Cannot resume a rejected work item.",
                detail: "The last evaluator verdict was 'reject'. Use Restart to re-plan from scratch.",
                statusCode: 400);
        }

        // Resolve target crew — prefer the last deliverable's crew so the
        // same agent picks it up; fall back to the most recent queue row's
        // routedToCrewId for cases where the deliverable row predates the
        // crew column being populated.
        var crewId = lastDeliv.CrewId;
        if (string.IsNullOrEmpty(crewId))
        {
            var lastQueue = (await db.TaskQueue.AsNoTracking()
                .Where(r => r.WorkItemId == id)
                .ToListAsync(ct))
                .OrderByDescending(r => r.LastUpdatedAt)
                .FirstOrDefault();
            crewId = lastQueue?.RoutedToCrewId;
        }
        if (string.IsNullOrEmpty(crewId))
            return Results.Problem(title: "Cannot determine crew to resume into.", statusCode: 400);

        var openStates = Forge.Api.Persistence.TaskQueueStates.OpenStates.ToList();
        var hasOpen = await db.TaskQueue.AsNoTracking()
            .AnyAsync(r => r.WorkItemId == id && openStates.Contains(r.State), ct);
        if (hasOpen)
            return Results.Problem(
                type: "resume.queue-busy",
                title: "Work item already has an open queue row.",
                detail: "Cancel the in-flight run before resuming.",
                statusCode: 409);

        var note = body?.Note;
        var isPassResume = lastDeliv.Verdict == "pass";
        var queueKind = isPassResume
            ? Forge.Api.Persistence.TaskQueueKinds.ResumeDelivery
            : Forge.Api.Persistence.TaskQueueKinds.Work;

        // Stash the operator note as a transcript event so CrewQueueWorker
        // can pick it up when it builds the next user prompt. Skipped for
        // the pass-resume path (agent doesn't run, the note has nowhere to
        // land). Empty / whitespace notes are also skipped.
        if (!isPassResume && !string.IsNullOrWhiteSpace(note))
        {
            await taskChannel.EmitAsync(id, "task.operator-note", new
            {
                note = note!.Trim(),
                priorIter = lastDeliv.Iter,
                priorVerdict = lastDeliv.Verdict,
            }, crewId: crewId, ct: ct);
        }

        var row = await taskQueue.EnqueueAsync(
            id, "crew", crewId, ct,
            kind: queueKind);

        var liveWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (liveWi is not null)
        {
            liveWi.ExecutionStatus = Forge.Api.Persistence.ExecutionStatuses.Queued;
            liveWi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        await hub.Clients.All.SendAsync("queue.changed", new { kind = "enqueued", item = row }, ct);
        return Results.Ok(new
        {
            workItemId = id,
            queueId = row.Id,
            crewId,
            mode = isPassResume ? "delivery-replay" : "continue-iter",
            priorIter = lastDeliv.Iter,
            priorScore = lastDeliv.Score,
            priorVerdict = lastDeliv.Verdict,
            noteApplied = !isPassResume && !string.IsNullOrWhiteSpace(note),
        });
    }
}
