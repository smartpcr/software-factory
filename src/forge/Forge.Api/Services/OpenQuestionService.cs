// -----------------------------------------------------------------------
// <copyright file="OpenQuestionService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 stage 3.1 — the shared open-question answer/resume core, extracted
/// verbatim from <c>TaskExecutionEndpoints.AnswerQuestions</c> /
/// <c>DismissQuestions</c> / <c>TryUnparkAndResumeAsync</c> so both the HTTP
/// endpoints AND the chat-bridge inbound path (Phase 4) drive one implementation
/// — no behaviour fork. The HTTP endpoints are now thin wrappers over this.
///
/// <para>Deliberately free of <c>HttpContext</c>: the caller resolves the actor
/// (X-Forge-User header for HTTP, linked subscriber for chat) and passes it in.</para>
/// </summary>
public interface IOpenQuestionService
{
    /// <summary>
    /// Record operator answers for a work item, then — when the unanswered count
    /// hits 0 — fire the unpark-and-resume path. Returns the recomputed count +
    /// whether a resume was scheduled.
    /// </summary>
    Task<OpenQuestionAnswerResult> AnswerAsync(
        string workItemId, IReadOnlyList<OpenQuestionAnswer> answers, string actor, CancellationToken ct);

    /// <summary>
    /// Dismiss (sentinel-answer) the given questions, then unpark-and-resume when
    /// the unanswered count hits 0. Same gate/resume path as <see cref="AnswerAsync"/>.
    /// </summary>
    Task<OpenQuestionAnswerResult> DismissAsync(
        string workItemId, IReadOnlyList<string> externalIds, string actor, CancellationToken ct);
}

/// <summary>An operator answer to a single open question.</summary>
public sealed record OpenQuestionAnswer(string ExternalId, string? Answer);

/// <summary>Result of an answer/dismiss: the recomputed unanswered count + whether a resume fired.</summary>
public sealed record OpenQuestionAnswerResult(int UnansweredCount, bool ResumeScheduled);

/// <inheritdoc/>
public sealed class OpenQuestionService : IOpenQuestionService
{
    private const string DismissSentinel = "(dismissed by operator)";

    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly IHubContext<ForgeHub> hub;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IStoryArtifactStrategy strategy;

    public OpenQuestionService(
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IServiceScopeFactory scopeFactory,
        IStoryArtifactStrategy strategy)
    {
        this.control = control;
        this.db = db;
        this.hub = hub;
        this.scopeFactory = scopeFactory;
        this.strategy = strategy;
    }

    /// <inheritdoc/>
    public async Task<OpenQuestionAnswerResult> AnswerAsync(
        string workItemId, IReadOnlyList<OpenQuestionAnswer> answers, string actor, CancellationToken ct)
    {
        var byExtId = await this.LatestByExternalIdAsync(workItemId, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var a in answers)
        {
            if (string.IsNullOrEmpty(a.ExternalId)) continue;
            if (!byExtId.TryGetValue(a.ExternalId, out var latest)) continue;
            // Update the latest row in place so subsequent renders show the
            // answer next to the question that surfaced it.
            latest.Answer = a.Answer ?? string.Empty;
            latest.AnsweredAt = now;
            latest.AnsweredBy = actor;
        }

        return await this.FinalizeAsync(workItemId, ct);
    }

    /// <inheritdoc/>
    public async Task<OpenQuestionAnswerResult> DismissAsync(
        string workItemId, IReadOnlyList<string> externalIds, string actor, CancellationToken ct)
    {
        var byExtId = await this.LatestByExternalIdAsync(workItemId, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var extId in externalIds)
        {
            if (string.IsNullOrEmpty(extId)) continue;
            if (!byExtId.TryGetValue(extId, out var latest)) continue;
            latest.Answer = DismissSentinel;
            latest.AnsweredAt = now;
            latest.AnsweredBy = actor;
        }

        return await this.FinalizeAsync(workItemId, ct);
    }

    private async Task<Dictionary<string, DeliverableQuestionRow>> LatestByExternalIdAsync(string workItemId, CancellationToken ct)
    {
        var existing = await this.db.DeliverableQuestions
            .Where(q => q.WorkItemId == workItemId)
            .ToListAsync(ct);
        return existing
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<OpenQuestionAnswerResult> FinalizeAsync(string workItemId, CancellationToken ct)
    {
        await this.db.SaveChangesAsync(ct);

        var unansweredCount = await DeliverableLoop.CountUnansweredQuestionsAsync(this.control, this.db, workItemId, ct);

        var resumeScheduled = false;
        if (unansweredCount == 0)
        {
            resumeScheduled = await this.TryUnparkAndResumeAsync(workItemId, ct);
        }

        return new OpenQuestionAnswerResult(unansweredCount, resumeScheduled);
    }

    /// <summary>
    /// Phase 66 — flip a work item out of <see cref="ExecutionStatuses.AwaitingAnswers"/>
    /// and kick the appropriate resume path (plan-doc / workstream / bare work-item).
    /// Moved verbatim from <c>TaskExecutionEndpoints.TryUnparkAndResumeAsync</c>.
    /// </summary>
    private async Task<bool> TryUnparkAndResumeAsync(string workItemId, CancellationToken ct)
    {
        // Plan-doc path: id format `plan-doc:{spawnId}:{filename}`.
        if (workItemId.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            var parts = workItemId.Split(':', 3);
            if (parts.Length != 3) return false;
            var spawnId = parts[1];
            var fileName = parts[2];
            var docKey = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

            var spawn = await this.db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            if (spawn is null) return false;

            if (!string.IsNullOrEmpty(spawn.LastError)
                && spawn.LastError.StartsWith(StorySpawnOrchestrator.AwaitingAnswersErrorPrefix, StringComparison.Ordinal))
            {
                spawn.LastError = null;
                spawn.LastErrorAt = null;
                spawn.UpdatedAt = DateTimeOffset.UtcNow;
            }

            StoriesEndpoints.AddResumePending(spawn, docKey);
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
            await this.db.SaveChangesAsync(ct);

            var storyId = spawn.StoryId;
            _ = Task.Run(() => StoriesEndpoints.RunResumeWithCleanupAsync(
                this.scopeFactory, this.strategy, spawn.Id, storyId, docKey, CancellationToken.None));

            await this.hub.Clients.All.SendAsync("story-spawn.changed",
                new { kind = "questions-answered", storyId, key = docKey, workItemId }, ct);
            return true;
        }

        // Workstream path: id is the workstream's row id.
        var ws = await this.db.Workstreams.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (ws is not null)
        {
            var parentSpawn = await this.db.StorySpawns
                .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (parentSpawn is not null
                && !string.IsNullOrEmpty(parentSpawn.LastError)
                && parentSpawn.LastError.StartsWith(StorySpawnOrchestrator.AwaitingAnswersErrorPrefix, StringComparison.Ordinal))
            {
                parentSpawn.LastError = null;
                parentSpawn.LastErrorAt = null;
                parentSpawn.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // 2026-05-15 — unwedge bogus Sloppy-3-attempts / stalled-questions
            // stall so the operator's answer/dismiss actually resumes the row.
            const string BogusSloppyPrefix = "Sloppy-3-attempts:";
            const string StalledQuestionsVerdictPrefix = "Evaluator terminal verdict 'stalled-questions'";
            if (string.Equals(ws.Status, "stalled", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(ws.StallReason)
                && (ws.StallReason.StartsWith(BogusSloppyPrefix, StringComparison.Ordinal)
                    || ws.StallReason.StartsWith(StalledQuestionsVerdictPrefix, StringComparison.Ordinal)))
            {
                ws.Status = "active";
                ws.StallReason = null;
                ws.LastActive = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var wiForReset = await this.db.WorkItems.FirstOrDefaultAsync(x => x.Id == workItemId, ct);
                if (wiForReset is not null)
                {
                    wiForReset.FailureReason = null;
                    if (string.Equals(wiForReset.ExecutionStatus, ExecutionStatuses.Failed, StringComparison.Ordinal)
                        || string.Equals(wiForReset.ExecutionStatus, ExecutionStatuses.PendingReassign, StringComparison.Ordinal)
                        || string.Equals(wiForReset.ExecutionStatus, ExecutionStatuses.AwaitingAnswers, StringComparison.Ordinal))
                    {
                        wiForReset.ExecutionStatus = ExecutionStatuses.Queued;
                    }
                    wiForReset.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            await this.db.SaveChangesAsync(ct);
            await this.hub.Clients.All.SendAsync("ws-changed",
                new { workstreamId = ws.Id, storyId = parentSpawn?.StoryId, kind = "questions-answered" }, ct);
            return true;
        }

        // Generic work-item path: bare WorkItem row (e.g. crew-queue items).
        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is not null
            && string.Equals(wi.ExecutionStatus, ExecutionStatuses.AwaitingAnswers, StringComparison.Ordinal))
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await this.db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }
}
