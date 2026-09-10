// -----------------------------------------------------------------------
// <copyright file="OpenQuestionService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Endpoints;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Forge.Api.Services.Chat;
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
/// (paired browser session or verified provider link) and passes it in.</para>
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
public sealed record OpenQuestionAnswer(string ExternalId, string? Answer, long? ExpectedRevision = null,
    bool EditExisting = false, string? ExpectedAnswerRevision = null);

/// <summary>Result of an answer/dismiss: the recomputed unanswered count + whether a resume fired.</summary>
public sealed record OpenQuestionAnswerResult(int UnansweredCount, bool ResumeScheduled, int AcceptedCount = 0, bool RevisionConflict = false);

/// <inheritdoc/>
public sealed class OpenQuestionService : IOpenQuestionService
{
    private const string DismissSentinel = "(dismissed by operator)";

    private readonly ForgeActionContext actionContext;
    private readonly ChatRuntimeState chatRuntime;
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
        IStoryArtifactStrategy strategy,
        ForgeActionContext actionContext,
        ChatRuntimeState chatRuntime)
    {
        this.actionContext = actionContext;
        this.chatRuntime = chatRuntime;
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
        var verifiedActor = await this.actionContext.RequireAsync(ForgeCapabilities.QuestionAnswer, new(WorkItemId: workItemId), ct);
        actor = verifiedActor.UserId;
        await this.chatRuntime.DomainGate.WaitAsync(ct);
        try
        {
            await this.actionContext.RequireAsync("question.answer", new(WorkItemId: workItemId), ct);
            using var limits = ChatDbBudget.Limit(this.db);
            var now = DateTimeOffset.UtcNow;
            var productId = await this.control.Products.Where(p => p.Slug == this.db.ProductSlug).Select(p => p.Id).SingleAsync(ct);
            var accepted = 0;
            var newAnswers = 0;
            var conflict = false;
            ChatActionReceiptRow? resume = null;
            int unanswered;
            await using (var tx = await this.db.Database.BeginTransactionAsync(ct))
            {
                var byExtId = await this.LatestByExternalIdAsync(workItemId, ct);
                foreach (var answer in answers)
                {
                    if (string.IsNullOrEmpty(answer.ExternalId) || string.IsNullOrWhiteSpace(answer.Answer)
                        || !byExtId.TryGetValue(answer.ExternalId, out var latest)) continue;
                    if (answer.ExpectedRevision is { } revision && revision != latest.Id) { conflict = true; continue; }
                    var editing = !string.IsNullOrEmpty(latest.Answer);
                    if (editing && (!answer.EditExisting || verifiedActor.Source != "browser")) continue;
                    if (editing && (answer.ExpectedAnswerRevision is null || answer.ExpectedAnswerRevision != latest.AnsweredAt?.ToString("O"))) { conflict = true; continue; }
                    var actionKey = editing ? $"question-edit:{workItemId}:{latest.Id}:{answer.ExpectedAnswerRevision}" : $"question:{workItemId}:{latest.Id}";
                    latest.Answer = answer.Answer ?? string.Empty; latest.AnsweredAt = now; latest.AnsweredBy = actor;
                    this.db.ChatActionReceipts.Add(new() { ProductId = productId, ActionKey = ChatEventCatalog.Hash(actionKey),
                        Action = editing ? "question.edit" : "question.answer", TargetId = workItemId, ExpectedRevision = latest.Id, ActorId = actor,
                        CreatedAt = now.ToUnixTimeMilliseconds(), TerminalAt = now.ToUnixTimeMilliseconds() });
                    accepted++;
                    if (!editing) newAnswers++;
                }
                unanswered = byExtId.Values.Count(q => string.IsNullOrEmpty(q.Answer));
                if (newAnswers > 0 && unanswered == 0)
                {
                    resume = new() { ProductId = productId, ActionKey = "resume:" + Guid.NewGuid().ToString("N"), Action = "questions.resume",
                        TargetId = workItemId, ActorId = actor, State = "executing", Result = "pending", OperationId = Guid.NewGuid().ToString("N"),
                        OwnerProcessInstanceId = this.chatRuntime.ProcessId, CreatedAt = now.ToUnixTimeMilliseconds() };
                    this.db.ChatActionReceipts.Add(resume);
                }
                await this.db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            var scheduled = false;
            if (resume is not null)
            {
                try { scheduled = await this.TryUnparkAndResumeAsync(workItemId, ct); resume.State = "completed"; resume.Result = scheduled ? "scheduled" : "no-resume-needed"; resume.TerminalAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
                catch { resume.State = "uncertain"; resume.Result = "resume-outcome-unknown"; }
                await this.db.SaveChangesAsync(ct);
            }
            this.chatRuntime.Signal();
            return new(unanswered, scheduled, accepted, conflict);
        }
        finally { this.chatRuntime.DomainGate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<OpenQuestionAnswerResult> DismissAsync(
        string workItemId, IReadOnlyList<string> externalIds, string actor, CancellationToken ct)
    {
        return await this.AnswerAsync(workItemId, externalIds.Select(id => new OpenQuestionAnswer(id, DismissSentinel)).ToArray(), actor, ct);
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
