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
        ControlDbContext control,
        ProductDbContext db,
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
            await TryImportQuestionsFromDocAsync(control, db, id, ct);
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
        ControlDbContext control, ProductDbContext db, string workItemId, CancellationToken ct)
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
    ///
    /// <para>Phase 66 — when the dismissal drops the unanswered count to 0,
    /// fire the same unpark-and-resume path as
    /// <see cref="AnswerQuestions"/> so the operator's dismiss decision
    /// also unblocks the gen-eval loop.</para>
    /// </summary>
    private static async Task<IResult> DismissQuestions(
        HttpContext ctx,
        string id,
        [FromBody] DismissQuestionsRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IServiceScopeFactory scopeFactory,
        IStoryArtifactStrategy strategy,
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

        var unansweredCount = await DeliverableLoop.CountUnansweredQuestionsAsync(control, db, id, ct);

        // Phase 66 — dismissals also clear the gate; fire the resume.
        var resumeScheduled = false;
        if (unansweredCount == 0)
        {
            resumeScheduled = await TryUnparkAndResumeAsync(
                control, db, hub, scopeFactory, strategy, id, ct);
        }
        return Results.Ok(new { workItemId = id, unansweredCount, resumeScheduled });
    }

    /// <summary>
    /// Phase 39 (Layer C) — record operator answers. We always insert a
    /// fresh row (matching the iter of the most-recent emission for each
    /// ExternalId) so the history reads "asked at iter N, answered at iter
    /// N" cleanly. Returns the updated unanswered count so the UI can
    /// re-render the wizard without an extra GET.
    ///
    /// <para>Phase 66 — when the recomputed unanswered count drops to 0,
    /// clear <see cref="ExecutionStatuses.AwaitingAnswers"/> on the work
    /// item and fire-and-forget the resume path:</para>
    /// <list type="bullet">
    ///   <item>For plan-doc work items (<c>plan-doc:{spawnId}:{filename}</c>)
    ///   call <see cref="StoriesEndpoints.AddResumePending"/> +
    ///   <see cref="StoriesEndpoints.RunResumeWithCleanupAsync"/> with
    ///   <c>restartFromScratch=false</c> so the deliverable chain seeds
    ///   the next gen↔eval iter (resumes at iter N+1 with the operator
    ///   answers folded into the generator's <c>## Operator answers</c>
    ///   prelude per Phase 39 Layer C).</item>
    ///   <item>For workstream work items, set
    ///   <c>ExecutionStatus = queued</c> and emit <c>ws-changed</c>
    ///   on the SignalR hub; the next orchestrator tick re-enters
    ///   <c>DriveOneWorkstreamImplAsync</c>, sees
    ///   <c>verdict=stalled-questions</c> with
    ///   <c>unansweredCount=0</c>, and falls through to
    ///   <c>ProduceWorkstreamChangesAsync</c>.</item>
    /// </list>
    /// <para>Also clears <c>spawn.LastError</c> when it references this
    /// work item (the soft "Awaiting operator answers" note set by
    /// <see cref="StorySpawnOrchestrator"/>).</para>
    /// </summary>
    private static async Task<IResult> AnswerQuestions(
        HttpContext ctx,
        string id,
        [FromBody] AnswerQuestionsRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IServiceScopeFactory scopeFactory,
        IStoryArtifactStrategy strategy,
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
        var unansweredCount = await DeliverableLoop.CountUnansweredQuestionsAsync(control, db, id, ct);

        // Phase 66 — when all questions are answered, fire the resume.
        var resumeScheduled = false;
        if (unansweredCount == 0)
        {
            resumeScheduled = await TryUnparkAndResumeAsync(
                control, db, hub, scopeFactory, strategy, id, ct);
        }

        return Results.Ok(new { workItemId = id, unansweredCount, resumeScheduled });
    }

    /// <summary>
    /// Phase 66 — flip a work item out of
    /// <see cref="ExecutionStatuses.AwaitingAnswers"/> and kick the
    /// appropriate resume path:
    /// <list type="bullet">
    ///   <item>Plan-doc ids (<c>plan-doc:{spawnId}:{filename}</c>) → resolve
    ///   spawn + story, call <see cref="StoriesEndpoints.AddResumePending"/>,
    ///   then fire-and-forget <see cref="StoriesEndpoints.RunResumeWithCleanupAsync"/>.
    ///   The resume marker is required so a crash mid-resume is recovered
    ///   by <c>StoryResumeRecoveryService</c>.</item>
    ///   <item>Workstream ids → clear any bogus
    ///   <c>"Sloppy-3-attempts:"</c> stall on the row (2026-05-15 fix),
    ///   demote the workstream back to <c>active</c>, reset WI failure
    ///   fields, and emit <c>ws-changed</c>; the orchestrator's next
    ///   tick re-enters <c>DriveOneWorkstreamImplAsync</c>. Operator-set
    ///   stall reasons (build failures, no-progress exceptions,
    ///   cancelled/paused/complete/merging/review/historic) are left
    ///   untouched so unrelated stalls aren't reanimated.</item>
    ///   <item>Bare work-item rows (legacy / non-workstream) → flip
    ///   <c>ExecutionStatus=queued</c>.</item>
    /// </list>
    /// Also clears any <c>spawn.LastError</c> that references this work
    /// item via the <see cref="StorySpawnOrchestrator.AwaitingAnswersErrorPrefix"/>
    /// sentinel so the UI banner flips from blue "awaiting answers" back
    /// to clean.
    /// </summary>
    /// <returns>True when a resume was scheduled (caller can surface this
    /// to the operator); false when no further action was needed.</returns>
    private static async Task<bool> TryUnparkAndResumeAsync(
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IServiceScopeFactory scopeFactory,
        IStoryArtifactStrategy strategy,
        string workItemId,
        CancellationToken ct)
    {
        // Plan-doc path: id format `plan-doc:{spawnId}:{filename}`.
        if (workItemId.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            var parts = workItemId.Split(':', 3);
            if (parts.Length != 3) return false;
            var spawnId = parts[1];
            var fileName = parts[2];
            // Strip the `.md` suffix to get the canonical doc key
            // (matches PlanDocStateHelpers.PlanDocFileName values).
            var docKey = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

            var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            if (spawn is null) return false;

            // Clear soft "Awaiting operator answers" LastError when it
            // references this doc; the banner flips back to clean.
            if (!string.IsNullOrEmpty(spawn.LastError)
                && spawn.LastError.StartsWith(StorySpawnOrchestrator.AwaitingAnswersErrorPrefix, StringComparison.Ordinal))
            {
                spawn.LastError = null;
                spawn.LastErrorAt = null;
                spawn.UpdatedAt = DateTimeOffset.UtcNow;
            }

            StoriesEndpoints.AddResumePending(spawn, docKey);
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Fire-and-forget the resume; the strategy's deliverable loop
            // seeds the prior chain (now including the stalled-questions
            // row, which the loop's resume branch treats as iterate-like)
            // and continues at iter N+1.
            var storyId = spawn.StoryId;
            _ = Task.Run(() => StoriesEndpoints.RunResumeWithCleanupAsync(
                scopeFactory, strategy, spawn.Id, storyId, docKey, CancellationToken.None));

            await hub.Clients.All.SendAsync("story-spawn.changed",
                new { kind = "questions-answered", storyId, key = docKey, workItemId }, ct);
            return true;
        }

        // Workstream path: id is the workstream's row id.
        var ws = await db.Workstreams.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (ws is not null)
        {
            // Clear soft LastError on the parent spawn if it references
            // this workstream.
            var parentSpawn = await db.StorySpawns
                .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (parentSpawn is not null
                && !string.IsNullOrEmpty(parentSpawn.LastError)
                && parentSpawn.LastError.StartsWith(StorySpawnOrchestrator.AwaitingAnswersErrorPrefix, StringComparison.Ordinal))
            {
                parentSpawn.LastError = null;
                parentSpawn.LastErrorAt = null;
                parentSpawn.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // 2026-05-15 — unwedge bogus Sloppy-3-attempts stall. The
            // operator's answer/dismiss action signals their intent to
            // resume; if the workstream was wrongly demoted to `stalled`
            // by SloppyReassignmentReconciler.MarkTerminalFailedAsync
            // while it was actually parked on `stalled-questions` (bug
            // path: ImplSpawnGuard mis-counted no-progress during
            // question parking → routed to sloppy reassignment → at-cap
            // → terminal failure → ws.Status=stalled with bogus
            // "Sloppy-3-attempts: ..." reason), we MUST clear that stall
            // here. Otherwise the orchestrator's claim sweep at
            // StorySpawnOrchestrator.cs:1801 keeps skipping the row and
            // even a successful post-answer iter never gets the PR
            // opened.
            //
            // Scope: ONLY recover when ws.Status=="stalled" AND
            // ws.StallReason starts with "Sloppy-3-attempts:" — that's
            // the known-bogus class. Build failures (`Gate failed`),
            // no-progress exceptions (`No progress: ...`), and
            // operator-set states (cancelled/paused/complete/merging
            // /review/historic) are all left untouched.
            //
            // Also clear stale WI terminal-failure fields so a later
            // path can't re-interpret the WI as still-failed and
            // re-stall the row. WorkstreamRecovery owns the
            // canonical reset shape, but this path only needs to
            // erase the failure indicators — pair-attempt accounting
            // and PriorPairsJson are retained so the next attempt
            // still excludes already-tried pairs.
            const string BogusSloppyPrefix = "Sloppy-3-attempts:";
            if (string.Equals(ws.Status, "stalled", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(ws.StallReason)
                && ws.StallReason.StartsWith(BogusSloppyPrefix, StringComparison.Ordinal))
            {
                ws.Status = "active";
                ws.StallReason = null;
                ws.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var wiForReset = await db.WorkItems.FirstOrDefaultAsync(x => x.Id == workItemId, ct);
                if (wiForReset is not null)
                {
                    wiForReset.FailureReason = null;
                    // Only reset ExecutionStatus when it's still in a
                    // terminal-failed shape. If a queue-worker path
                    // already advanced it to Queued/Live/Completed,
                    // leave it alone so we don't stomp valid state.
                    if (string.Equals(wiForReset.ExecutionStatus, ExecutionStatuses.Failed, StringComparison.Ordinal)
                        || string.Equals(wiForReset.ExecutionStatus, ExecutionStatuses.PendingReassign, StringComparison.Ordinal))
                    {
                        wiForReset.ExecutionStatus = ExecutionStatuses.Queued;
                    }
                    wiForReset.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("ws-changed",
                new { workstreamId = ws.Id, storyId = parentSpawn?.StoryId, kind = "questions-answered" }, ct);
            return true;
        }

        // Generic work-item path: bare WorkItem row (e.g. crew-queue items).
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is not null
            && string.Equals(wi.ExecutionStatus, ExecutionStatuses.AwaitingAnswers, StringComparison.Ordinal))
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }

    private static async Task<IResult> GetExecution(
        string id,
        ControlDbContext control,
        ProductDbContext db,
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
            var evaluatorAgent = await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == current.EvaluatorAgentId, ct);
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
                var usageRows = await control.AgentUsage.AsNoTracking()
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
        var usage = await control.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == id)
            .ToListAsync(ct);
        decimal genCost  = usage.Where(u => u.CostCategory == "generator").Sum(u => u.CostUsd);
        decimal evalCost = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.CostUsd);
        long genIn  = usage.Where(u => u.CostCategory == "generator").Sum(u => u.InputTokens);
        long genOut = usage.Where(u => u.CostCategory == "generator").Sum(u => u.OutputTokens);
        long evalIn  = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.InputTokens);
        long evalOut = usage.Where(u => u.CostCategory == "evaluator").Sum(u => u.OutputTokens);

        // Phase 54 — per-iteration prompt-size samples. Pair generator+
        // evaluator usage rows by their ordinal position within each
        // CostCategory (ordered by StartedAt) — the loop writes them in
        // strict gen→eval sequence per iteration, so positions align with
        // iterations 1..N. Missing-side rows (e.g. eval skipped on first
        // run) get a zero-token placeholder so the UI sparkline still has
        // a stable x-axis.
        var genUsage  = usage.Where(u => u.CostCategory == "generator").OrderBy(u => u.StartedAt).ThenBy(u => u.Id).ToList();
        var evalUsage = usage.Where(u => u.CostCategory == "evaluator").OrderBy(u => u.StartedAt).ThenBy(u => u.Id).ToList();
        var iterCount = Math.Max(genUsage.Count, evalUsage.Count);
        var promptGrowth = new List<IterationPromptGrowth>(iterCount);
        for (int i = 0; i < iterCount; i++)
        {
            var g = i < genUsage.Count ? genUsage[i] : null;
            var e = i < evalUsage.Count ? evalUsage[i] : null;
            promptGrowth.Add(new IterationPromptGrowth(
                Iter: i + 1,
                GeneratorInputTokens: g?.InputTokens ?? 0,
                GeneratorOutputTokens: g?.OutputTokens ?? 0,
                EvaluatorInputTokens: e?.InputTokens ?? 0,
                EvaluatorOutputTokens: e?.OutputTokens ?? 0,
                GeneratorTurnSamplesJson: g?.TurnSamplesJson,
                EvaluatorTurnSamplesJson: e?.TurnSamplesJson));
        }

        // Phase 52 — windowed generator tokens for the sloppy-cap meter the
        // UI displays. Uses the SAME (agentId + BudgetWindowStartUtc) filter
        // as SloppyDetector.CheckTokenCapAsync so the displayed
        // tokens-used/cap ratio matches the actual gate. Cumulative genIn/
        // genOut (above) include prior reassigned pairs and are kept for
        // backwards compatibility.
        long generatorTokensSinceWindow = 0;
        if (!string.IsNullOrEmpty(generatorAgentId))
        {
            (_, generatorTokensSinceWindow) = await SloppyDetector.CheckTokenCapAsync(
                control, db, id, generatorAgentId, wi?.BudgetWindowStartUtc, ct);
        }

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
            var crew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == generatorCrewId, ct);
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
                var evalCrew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evaluatorCrewId, ct);
                evaluatorAgentId = crew?.DefaultEvaluatorAgentId ?? evalCrew?.Lead;
                evaluatorCrewName = evalCrew?.Name;
            }
            if (crew is not null)
            {
                var since = DateTimeOffset.UtcNow.AddHours(-24);
                var crewUsage = (await control.AgentUsage.AsNoTracking()
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
        // after a server restart that lost in-memory feed subscriptions),
        // OR when the cached tick is from before the Phase 49 follow-up
        // (no branchDiff field → fall through so the disk sampler
        // attaches the correct cumulative branch totals).
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

        // Phase 49 follow-up — when the cached tick predates the
        // branchDiff fix the UI's pre-/commits-loaded fallback can't
        // get accurate counts. Treat a tick with no BranchDiff as
        // "stale shape" and force a fresh disk sample below so the
        // operator sees correct numbers on first open.
        var latestActivityAt = transcriptRows
            .Where(e => !string.Equals(e.Kind, "task.worktree-tick", StringComparison.Ordinal))
            .Select(e => (DateTimeOffset?)e.At)
            .Concat(deliverableRows.Select(d => (DateTimeOffset?)d.CreatedAt))
            .Concat(usage.Select(u => (DateTimeOffset?)(u.EndedAt ?? u.StartedAt)))
            .Where(t => t is not null)
            .Max();

        var cachedTickIsStale = lastTick is not null
            && latestActivityAt is not null
            && latestActivityAt > lastTick.At;

        var diskSampleNeeded = worktree is null || worktree.BranchDiff is null || cachedTickIsStale;

        // Fallback: read worktree directly from disk using the path from
        // the started event or derived from the work item's repo + branch.
        if (diskSampleNeeded)
        {
            string? wtPath = worktree?.WorktreePath;
            if (string.IsNullOrEmpty(wtPath))
            {
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
            }

            // Convention fallback: resolve via WorktreePathResolver so the
            // snapshot endpoint and the commits endpoint agree on which
            // path actually holds the worktree (Phase 49 short name vs.
            // legacy `spawn-` vs. legacy `ws-`).
            if (string.IsNullOrEmpty(wtPath))
            {
                var resolved = await Forge.Api.Services.Stories.WorktreePathResolver
                    .ResolveForWorkstreamAsync(control, db, id, ct);
                if (!string.IsNullOrEmpty(resolved.WorktreePath))
                {
                    wtPath = resolved.WorktreePath;
                }
            }

            if (!string.IsNullOrEmpty(wtPath) && Directory.Exists(wtPath))
            {
                try
                {
                    // Phase 49 follow-up — resolve the workstream's
                    // real base branch (spawn.BaseBranch when present)
                    // so the disk-sample's branchDiff / ahead / behind
                    // agree with what /commits computes.
                    var execBase = await Forge.Api.Services.Stories.WorkItemBaseBranchResolver
                        .ResolveAsync(control, db, id, ct);
                    var diskSnap = await worktreeFeed.SampleAsync(wtPath, execBase, ct);
                    if (diskSnap is not null)
                    {
                        worktree = new WorktreeSnapshot(
                            string.Empty,
                            diskSnap.Added, diskSnap.Modified, diskSnap.Deleted,
                            diskSnap.Ahead, diskSnap.Behind, diskSnap.Dirty,
                            diskSnap.HeadSha, diskSnap.HeadMessage, diskSnap.HeadAuthor, diskSnap.HeadAt,
                            diskSnap.Truncated, DateTimeOffset.UtcNow,
                            wtPath,
                            BranchDiff: new WorktreeBranchDiff(
                                diskSnap.BranchDiffAdded ?? Array.Empty<string>(),
                                diskSnap.BranchDiffModified ?? Array.Empty<string>(),
                                diskSnap.BranchDiffDeleted ?? Array.Empty<string>()));

                        // Re-subscribe the feed so future ticks are emitted.
                        worktreeFeed.Subscribe(id, string.Empty, wtPath, execBase);
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
            var agentRows = await control.Agents.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .ToListAsync(ct);
            var genAgent = agentRows.FirstOrDefault(a => a.Id == generatorAgentId);
            var evalAgent = agentRows.FirstOrDefault(a => a.Id == evaluatorAgentId);
            generatorAgentName = genAgent?.Name;
            evaluatorAgentName = evalAgent?.Name;

            CrewRow? genCrew = null;
            CrewRow? evalCrew = null;
            if (!string.IsNullOrEmpty(generatorCrewId))
                genCrew = await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == generatorCrewId, ct);
            if (!string.IsNullOrEmpty(evaluatorCrewId))
                evalCrew = evaluatorCrewId == generatorCrewId
                    ? genCrew
                    : await control.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == evaluatorCrewId, ct);

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
            BudgetSeconds: budgetSeconds,
            PairAttemptCount: wi?.PairAttemptCount ?? 1,
            MaxPairAttempts: ScoringConstants.MaxPairAttempts,
            GeneratorTokensSinceWindow: generatorTokensSinceWindow,
            GeneratorTokenCap: ScoringConstants.SloppyGeneratorTokenCap,
            PromptGrowth: promptGrowth,
            PromptBloatGrowthThreshold: ScoringConstants.PromptBloatGrowthThreshold);

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
        ControlDbContext control,
        ProductDbContext db,
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
        ControlDbContext control,
        ProductDbContext db,
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
        ControlDbContext control,
        ProductDbContext db,
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
            r.GeneratorSummary ?? string.Empty,
            r.CommitSha,
            r.CommitMessage,
            r.FilesAdded,
            r.FilesModified,
            r.FilesDeleted,
            r.FilesChanged);
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

        // Phase 49 follow-up — surface the branchDiff arrays so the
        // Worktree-tab pills agree with the PR file list during the
        // pre-/commits-loaded window. Older ticks (pre-fix) didn't
        // emit branchDiff at all, OR emitted it anchored at `main` —
        // both surface as `null` here so the UI falls back gracefully.
        WorktreeBranchDiff? branchDiff = null;
        if (el.TryGetProperty("branchDiff", out var bd) && bd.ValueKind == JsonValueKind.Object)
        {
            branchDiff = new WorktreeBranchDiff(
                Added: ExtractStrings(bd, "added"),
                Modified: ExtractStrings(bd, "modified"),
                Deleted: ExtractStrings(bd, "deleted"));
        }

        return new WorktreeSnapshot(
            wsId ?? string.Empty,
            added, modified, deleted,
            ahead, behind, dirty,
            sha, msg, author, headAt,
            truncated, at,
            wtPath,
            BranchDiff: branchDiff);
    }

    private static IReadOnlyList<string> ExtractStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)
                    && !s.StartsWith(".forge/", StringComparison.Ordinal)
                    && s != ".forge")
                    list.Add(s);
            }
        }
        return list;
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
        ControlDbContext control,
        ProductDbContext db,
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

    private sealed record CommitsResponse(
        string? WorktreePath,
        string Branch,
        string BaseBranch,
        int Ahead,
        int Behind,
        string? HeadSha,
        IReadOnlyList<Forge.Api.Services.WorktreeCommitsScanner.CommitEntry> Commits,
        Forge.Api.Services.WorktreeCommitsScanner.UncommittedChanges Uncommitted,
        Forge.Api.Services.WorktreeCommitsScanner.BranchTotals BranchTotals);

    private static async Task<IResult> GetCommits(
        string id,
        ControlDbContext control,
        ProductDbContext db,
        HttpContext httpCtx,
        CancellationToken ct)
    {
        var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is null)
            return Results.Problem(title: $"Work item {id} not found", statusCode: 404);

        // Phase 49 follow-up — operator-driven refresh. The default
        // (refresh=false) returns fast based on the locally-cached
        // origin/{baseBranch} ref + fires a background fetch with a
        // 15s cooldown so the NEXT poll sees fresh upstream data.
        // ?refresh=true makes the fetch blocking — used by the UI's
        // explicit refresh button when the operator wants up-to-the-
        // second ahead/behind counts. Cold load drops from ~4s to
        // ~600ms with the default; refresh stays at ~2-4s.
        var refreshParam = httpCtx.Request.Query["refresh"].FirstOrDefault();
        var fetchUpstream = string.Equals(refreshParam, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(refreshParam, "1", StringComparison.Ordinal);

        // Resolve worktree path with the same convention CrewQueueWorker uses:
        // {repo.CloneRoot}/.worktree/ws-{sanitizedId}.
        string? cloneRoot = null;
        if (!string.IsNullOrEmpty(wi.RepoId))
        {
            var repo = await db.Set<Forge.Api.Persistence.RepoRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
            if (!string.IsNullOrEmpty(repo?.CloneRoot)) cloneRoot = repo.CloneRoot;
        }

        // Phase 49 follow-up — base branch comes from the shared
        // resolver so /commits, /execution disk-sample, and the
        // CrewQueueWorker subscribe path all anchor at the same ref.
        // For story-spawn workstreams that's spawn.BaseBranch (e.g.
        // `feature/xraft`); plain crew-queue items get repo default.
        var baseBranch = await Forge.Api.Services.Stories.WorkItemBaseBranchResolver
            .ResolveAsync(control, db, id, ct);

        // Plan-doc work-items (id pattern `plan-doc:{spawnId}:{file}`)
        // share the spawn's plan-branch worktree at
        // `{cloneRoot}/.worktree/plan-{docKey}` (sequential) or
        // `{cloneRoot}/.worktree/plan` (legacy parallel). Engineer
        // workstreams use the canonical `stage-{n.n}-{title}` slug or
        // the ad-hoc `ws-{id}` slug, whichever WorktreePathResolver
        // finds on disk.
        string branch;
        string? worktreePath;
        if (id.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            // Extract `spawn-{storyId}` and the file name from
            // `plan-doc:{spawnId}:{file}`.
            var afterPrefix = id.Substring("plan-doc:".Length);
            var lastColon = afterPrefix.LastIndexOf(':');
            string spawnId;
            string? planDocKey = null;
            if (lastColon > 0)
            {
                spawnId = afterPrefix.Substring(0, lastColon);
                var fileName = afterPrefix.Substring(lastColon + 1);
                // Strip ".md" to derive the canonical doc key.
                if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    planDocKey = fileName.Substring(0, fileName.Length - 3).ToLowerInvariant();
                }
            }
            else
            {
                spawnId = afterPrefix;
            }
            // Look up the spawn so we use its real PlanBranch (preserves
            // any custom branch name the orchestrator picked at spawn time).
            var spawn = await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            // Sequential-mode spawns put each doc on its own branch and
            // worktree; legacy parallel-mode shares one bucket.
            if (spawn is not null && planDocKey is not null
                && Forge.Api.Services.Stories.PlanDocStateHelpers.PlanDocFileName.ContainsKey(planDocKey))
            {
                branch = Forge.Api.Services.Stories.PlanDocStateHelpers.FindDocBranch(spawn, planDocKey);
                var slug = Forge.Api.Services.Stories.PlanDocStateHelpers.IsSequentialMode(spawn)
                    ? Forge.Api.Services.Stories.PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, planDocKey)
                    : Forge.Api.Services.Stories.PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug();
                worktreePath = !string.IsNullOrEmpty(cloneRoot)
                    ? Path.Combine(cloneRoot, ".worktree", slug)
                    : null;
                // Legacy fallback: check the old slug (no story prefix)
                if (worktreePath is not null && !Directory.Exists(worktreePath))
                {
                    var legacySlug = Forge.Api.Services.Stories.PlanDocStateHelpers.CanonicalDocWorktreeSlug(planDocKey);
                    var legacyPath = Path.Combine(cloneRoot!, ".worktree", legacySlug);
                    if (Directory.Exists(legacyPath)) worktreePath = legacyPath;
                }
            }
            else
            {
                branch = spawn?.PlanBranch ?? $"story/{Forge.Api.Services.Stories.StoryPaths.Safe(spawn?.StoryId ?? string.Empty)}/plan";
                worktreePath = !string.IsNullOrEmpty(cloneRoot)
                    ? Path.Combine(cloneRoot, ".worktree",
                        Forge.Api.Services.Stories.PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug())
                    : null;
            }
        }
        else
        {
            // Engineer workstream — let WorktreePathResolver pick the
            // right convention (new short name vs. legacy long names) by
            // looking at what's actually on disk.
            var resolved = await Forge.Api.Services.Stories.WorktreePathResolver
                .ResolveForWorkstreamAsync(control, db, id, ct);
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
                Commits: Array.Empty<Forge.Api.Services.WorktreeCommitsScanner.CommitEntry>(),
                Uncommitted: new Forge.Api.Services.WorktreeCommitsScanner.UncommittedChanges(
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                BranchTotals: new Forge.Api.Services.WorktreeCommitsScanner.BranchTotals(
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));
        }

        // All git work (fetch/log/status/diff/merge-base) lives in the
        // scanner so it can be unit-tested directly against a temp
        // repo. See Phase49WorktreeCommitsScannerTests.
        var scan = await Forge.Api.Services.WorktreeCommitsScanner.ScanAsync(worktreePath, baseBranch, fetchUpstream, ct);
        return Results.Ok(new CommitsResponse(
            WorktreePath: worktreePath,
            Branch: branch,
            BaseBranch: baseBranch,
            Ahead: scan.Ahead, Behind: scan.Behind,
            HeadSha: scan.HeadSha,
            Commits: scan.Commits,
            Uncommitted: scan.Uncommitted,
            BranchTotals: scan.BranchTotals));
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
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
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
        ControlDbContext control,
        ProductDbContext db,
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
