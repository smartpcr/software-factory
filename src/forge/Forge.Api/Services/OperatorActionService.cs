// -----------------------------------------------------------------------
// <copyright file="OperatorActionService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 stage 3.2 — the shared operator-action core, extracted verbatim from
/// the <c>WorkstreamsEndpoints</c> Accept / Cancel / Retry / Restart handlers so
/// both the HTTP endpoints AND the chat-bridge inbound path (Phase 5) drive one
/// implementation — no behaviour fork.
///
/// <para>HttpContext-free: the caller resolves the actor (paired session for
/// HTTP, verified provider link for chat) and passes it in. Each method returns an
/// <see cref="OperatorActionOutcome"/> that the endpoint maps to an
/// <c>IResult</c> and the chat dispatcher maps to a reply message.</para>
///
/// <para>Extracted incrementally (Accept → Cancel → Retry → Restart) so each
/// step is independently compilable/testable.</para>
/// </summary>
public interface IOperatorActionService
{
    /// <summary>
    /// Operator ACCEPT — override the latest deliverable verdict to <c>pass</c>
    /// so the orchestrator commits the worktree files and opens the PR. Preserves
    /// worktree / deliverables / transcript.
    /// </summary>
    Task<OperatorActionOutcome> AcceptAsync(string workstreamId, string actor, CancellationToken ct);

    /// <summary>
    /// Operator CANCEL — stop the running gen/eval loop: kill in-flight agent
    /// invocations, cancel open queue rows, mark the work item + workstream
    /// cancelled (terminal), and stall the last iterate deliverable.
    /// </summary>
    Task<OperatorActionOutcome> CancelAsync(string workstreamId, CancellationToken ct);

    /// <summary>
    /// Operator RETRY — soft-reset a stalled/failed workstream to resume the
    /// gen/eval loop (a continuation, not a clean slate): cancel in-flight work,
    /// short-circuit to <c>complete</c> when the PR is genuinely merged, else
    /// demote the terminal verdict via <c>WorkstreamRecovery</c> and re-activate.
    /// </summary>
    Task<OperatorActionOutcome> RetryAsync(string workstreamId, string actor, CancellationToken ct);

    /// <summary>
    /// Operator RESTART — full clean slate. Pre-flight-verifies the worktree is
    /// removable (fails loudly otherwise), then wipes deliverables / transcript /
    /// prompt audit / pair-attempt + cost ledger / unanswered questions (answered
    /// ones are preserved), closes prior PRs, nukes the git branch + worktree,
    /// resets the workstream + work item, force-reconciles the plan, and re-drives
    /// the orchestrator. <b>Destructive</b> — the chat path gates it behind a
    /// double-confirm.
    /// </summary>
    Task<OperatorActionOutcome> RestartAsync(string workstreamId, CancellationToken ct);
}

/// <summary>
/// Result of an operator action. On failure carries the problem shape the HTTP
/// endpoint used verbatim; on success carries the response payload.
/// </summary>
public sealed record OperatorActionOutcome(
    bool Ok,
    int StatusCode,
    string? ProblemType,
    string? Title,
    string? Detail,
    object? Payload)
{
    public static OperatorActionOutcome Success(object payload) => new(true, 200, null, null, null, payload);

    public static OperatorActionOutcome Problem(int statusCode, string? type, string title, string? detail) =>
        new(false, statusCode, type, title, detail, null);
}

/// <inheritdoc/>
public sealed partial class OperatorActionService : IOperatorActionService
{
    private readonly ForgeActionContext actionContext;
    private readonly Forge.Api.Services.Chat.ChatDomainActionRunner chatActions;
    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly IAgentRunner runner;
    private readonly IHubContext<ForgeHub> hub;
    private readonly AgentMemoryStore memStore;
    private readonly IForgeDbPaths paths;
    private readonly IEnumerable<IForgeProvider> providers;
    private readonly StorySpawnOrchestrator orchestrator;
    private readonly PlanReconciler reconciler;
    private readonly IterationPromptStore promptStore;
    private readonly CopilotSessionPool copilotPool;

    public OperatorActionService(
        ControlDbContext control,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub,
        AgentMemoryStore memStore,
        IForgeDbPaths paths,
        IEnumerable<IForgeProvider> providers,
        StorySpawnOrchestrator orchestrator,
        PlanReconciler reconciler,
        IterationPromptStore promptStore,
        CopilotSessionPool copilotPool,
        ForgeActionContext actionContext, Forge.Api.Services.Chat.ChatDomainActionRunner chatActions)
    {
        this.actionContext = actionContext;
        this.chatActions = chatActions;
        this.control = control;
        this.db = db;
        this.runner = runner;
        this.hub = hub;
        this.memStore = memStore;
        this.paths = paths;
        this.providers = providers;
        this.orchestrator = orchestrator;
        this.reconciler = reconciler;
        this.promptStore = promptStore;
        this.copilotPool = copilotPool;
    }

    /// <inheritdoc/>
    private async Task<OperatorActionOutcome> AcceptCoreAsync(string id, string actor, CancellationToken ct)
    {
        var verifiedActor = await this.actionContext.RequireAsync("workstream.accept", new(WorkstreamId: id), ct);
        actor = verifiedActor.UserId;
        var ws = await this.db.Workstreams.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return OperatorActionOutcome.Problem(404, null, $"Workstream {id} not found", null);

        if (ws.Status is "cancelled" or "complete" or "merging")
        {
            return OperatorActionOutcome.Problem(
                409, "accept.terminal",
                $"Workstream is in terminal status '{ws.Status}'; cannot accept.",
                "Use Restart to dispatch fresh, or open a new workstream.");
        }

        var deliverables = await this.db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        var lastDel = deliverables
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        if (lastDel is null)
        {
            return OperatorActionOutcome.Problem(
                400, "accept.no-deliverables",
                "Nothing to accept — workstream has no prior deliverables.",
                "Wait for at least one iteration to land before ACCEPT.");
        }

        var priorVerdict = lastDel.Verdict;
        var priorScore = lastDel.Score;
        var alreadyPassed = string.Equals(lastDel.Verdict, "pass", StringComparison.OrdinalIgnoreCase);

        // (1) Cancel any in-flight runner work and open queue rows so the loop
        // doesn't keep iterating while the orchestrator transitions to the
        // commit+PR-open path. Mirrors Retry's first two steps.
        try { await this.runner.CancelByTaskAsync(id, ct); }
        catch { /* best-effort */ }

        var queueRows = await this.db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (2) Flip the latest deliverable verdict to `pass` + append an audit
        // note. The orchestrator's DriveOneWorkstreamImplAsync reads
        // `lastDel.Verdict == "pass"` to route into the commit-and-PR-open branch.
        if (!alreadyPassed)
        {
            lastDel.Verdict = "pass";
            var note = $"OPERATOR ACCEPT ({actor}) — verdict overridden from '{priorVerdict}' to 'pass' at score {priorScore}. Worktree, deliverables, transcript, agent_usage, branch, and any prior PR commits are preserved. Orchestrator's next tick will commit the worktree files and open the PR via the cached-files branch.";
            lastDel.Notes = string.IsNullOrEmpty(lastDel.Notes) ? note : lastDel.Notes + "\n\n" + note;
        }

        // (3) Reset workstream + work-item state so the orchestrator's claim
        // sweep / ExecBadge unwedges.
        ws.Status = "active";
        ws.StallReason = null;

        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await this.control.SaveChangesAsync(ct);
        await this.db.SaveChangesAsync(ct);

        // (4) Emit workstream-changed so the live UI refreshes. The
        // orchestrator's next tick does the commit + PR-open work.
        await this.hub.Clients.All.SendAsync("workstream.changed", new
        {
            workstreamId = id,
            status = ws.Status,
            kind = "accepted",
            priorVerdict,
            newVerdict = lastDel.Verdict,
            iter = lastDel.Iter,
            score = lastDel.Score,
            alreadyPassed,
        }, ct);

        return OperatorActionOutcome.Success(new
        {
            workstreamId = id,
            priorVerdict,
            newVerdict = lastDel.Verdict,
            alreadyPassed,
            iter = lastDel.Iter,
            score = lastDel.Score,
            status = ws.Status,
            queueRowsCancelled = queueRows.Count,
        });
    }

    /// <inheritdoc/>
    private async Task<OperatorActionOutcome> CancelCoreAsync(string id, CancellationToken ct)
    {
        var verifiedActor = await this.actionContext.RequireAsync("workstream.cancel", new(WorkstreamId: id), ct);
        var ws = await this.db.Workstreams.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return OperatorActionOutcome.Problem(404, null, $"Workstream {id} not found", null);

        // (1) Kill in-flight agent invocations FIRST so no stray events land
        // after we flip the DB state below. Best-effort — the runner is tolerant
        // of unknown taskIds (returns 0).
        var invocationsCancelled = 0;
        try
        {
            invocationsCancelled = await this.runner.CancelByTaskAsync(id, ct);
        }
        catch
        {
            // Don't let runner failures prevent DB cleanup — the queue row is the
            // source of truth for whether the workstream is "running".
        }

        // (2) Cancel any queued/active task_queue rows for this workstream.
        var queueRows = await this.db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (3) Mark the work item execution cancelled — TERMINAL state (an "idle"
        // here would be re-claimed by the orchestrator tick).
        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Cancelled;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Mark the workstream cancelled so DriveImplementAsync skips it.
        ws.Status = "cancelled";

        // (5) Mark the last deliverable as stalled if it's in iterate state.
        var lastDel = (await this.db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct))
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (lastDel is not null && lastDel.Verdict is "iterate")
        {
            lastDel.Verdict = "stalled";
        }

        await this.control.SaveChangesAsync(ct);
        await this.db.SaveChangesAsync(ct);
        await this.hub.Clients.All.SendAsync("workstream.changed", new { kind = "cancelled", id }, ct);

        return OperatorActionOutcome.Success(new
        {
            id,
            status = "cancelled",
            queueRowsCancelled = queueRows.Count,
            invocationsCancelled,
        });
    }

    /// <inheritdoc/>
    private async Task<OperatorActionOutcome> RetryCoreAsync(string id, string actor, CancellationToken ct)
    {
        var verifiedActor = await this.actionContext.RequireAsync("workstream.retry", new(WorkstreamId: id), ct);
        actor = verifiedActor.UserId;
        var ws = await this.db.Workstreams.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return OperatorActionOutcome.Problem(404, null, $"Workstream {id} not found", null);

        var deliverables = await this.db.Deliverables
            .Where(d => d.WorkItemId == id)
            .ToListAsync(ct);
        var lastDel = deliverables
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        var latestPassClass = lastDel is null
            ? StorySpawnOrchestrator.PassGateClassification.NotPass
            : StorySpawnOrchestrator.ClassifyPassGate(
                lastDel.Verdict, lastDel.Score, lastDel.FailureKind, lastDel.Notes);
        var latestCanCompleteMergedPr = latestPassClass is StorySpawnOrchestrator.PassGateClassification.NaturalPass
            or StorySpawnOrchestrator.PassGateClassification.OperatorAccepted;
        var isRecoverableFalseComplete = string.Equals(ws.Status, "complete", StringComparison.OrdinalIgnoreCase)
            && lastDel is not null
            && !latestCanCompleteMergedPr;

        if (ws.Status is "cancelled" or "merging"
            || (string.Equals(ws.Status, "complete", StringComparison.OrdinalIgnoreCase) && !isRecoverableFalseComplete))
        {
            return OperatorActionOutcome.Problem(
                409, "retry.terminal",
                $"Workstream is in terminal status '{ws.Status}'; cannot retry.",
                "Use the Cancel/Restart actions instead, or open a new workstream.");
        }

        if (lastDel is null)
        {
            return OperatorActionOutcome.Problem(
                400, "retry.no-deliverables",
                "Nothing to retry -- workstream has no prior deliverables.",
                "Use Restart to dispatch fresh, or wait for the first iteration to land.");
        }

        // (1) Cancel any in-flight runner work and open queue rows. Mirrors the
        // first two steps of Restart, but preserves everything else.
        try { await this.runner.CancelByTaskAsync(id, ct); }
        catch { /* best-effort */ }

        var queueRows = await this.db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (2) Load work-item row early — needed by both the merged-PR
        // short-circuit and the normal active-reset path below.
        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);

        // Merged-PR short-circuit: promote straight to "complete" only when ALL
        // PRs are truly merged AND the latest deliverable genuinely passed.
        var allPrs = await this.db.Prs
            .Where(p => p.Workstream == id)
            .OrderBy(p => p.Sort)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
        var shortCircuitToMerged = allPrs.Count > 0
            && allPrs.All(p => string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase))
            && latestCanCompleteMergedPr;
        if (shortCircuitToMerged)
        {
            ws.Status = "complete";
            ws.StallReason = null;

            if (wi is not null)
            {
                wi.ExecutionStatus = ExecutionStatuses.Completed;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await this.control.SaveChangesAsync(ct);
            await this.db.SaveChangesAsync(ct);

            await this.hub.Clients.All.SendAsync("workstream.changed", new
            {
                workstreamId = id,
                status = ws.Status,
                kind = "retry-promoted-complete",
                priorVerdict = lastDel.Verdict,
                prMerged = true,
                passClass = latestPassClass.ToString(),
            }, ct);

            return OperatorActionOutcome.Success(new
            {
                workstreamId = id,
                priorVerdict = lastDel.Verdict,
                newVerdict = lastDel.Verdict,
                demoted = false,
                priorIter = lastDel.Iter,
                priorScore = lastDel.Score,
                status = ws.Status,
            });
        }

        // (3) Phase 67 — operator-recovery reset. WorkstreamRecovery centralizes
        // the correct shape: demote any terminal verdict, and when stalled-or-at-
        // cap, also wipe the pair-attempt accounting + agent_pair_assignment +
        // agent_usage rows so the orchestrator gets a clean slate.
        var report = await WorkstreamRecovery.SoftResetForResumeAsync(
            this.control, this.db, ws, wi, lastDel,
            operatorActor: actor,
            noteSuffix: "operator clicked Retry",
            ct: ct);
        var priorVerdict = report.PriorVerdict;
        var demoted = report.VerdictDemoted;
        var memoryDeleted = 0;
        if (WorkstreamsEndpoints.HasPoisonedProviderSessionFailure(lastDel))
        {
            // A poisoned Copilot SDK provider session must be cleared or the next
            // iteration reattaches to the broken session and stalls again.
            memoryDeleted = await this.memStore.DeleteForWorkItemAsync(id, ct);
        }

        ws.Status = "active";
        ws.StallReason = null;

        // (4) Reset work-item ExecutionStatus (continuation, not a clean slate —
        // no PairAttemptCount/BudgetWindow/PriorPairs wipe; that's Restart).
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await this.control.SaveChangesAsync(ct);
        await this.db.SaveChangesAsync(ct);

        await this.hub.Clients.All.SendAsync("workstream.changed", new
        {
            workstreamId = id,
            status = ws.Status,
            kind = "retry",
            priorVerdict,
            newVerdict = lastDel.Verdict,
            iter = lastDel.Iter,
        }, ct);

        return OperatorActionOutcome.Success(new
        {
            workstreamId = id,
            priorVerdict,
            newVerdict = lastDel.Verdict,
            demoted,
            priorIter = lastDel.Iter,
            priorScore = lastDel.Score,
            memoryRowsCleaned = memoryDeleted,
            status = ws.Status,
        });
    }

    /// <inheritdoc/>
    private async Task<OperatorActionOutcome> RestartCoreAsync(string id, CancellationToken ct)
    {
        var verifiedActor = await this.actionContext.RequireAsync("workstream.restart", new(WorkstreamId: id), ct);
        var ws = await this.db.Workstreams.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return OperatorActionOutcome.Problem(404, null, $"Workstream {id} not found", null);

        var prIdsForRestart = WorkstreamsEndpoints.ParseStringList(ws.PrsJson);
        var mergedLinkedPr = await this.db.Prs.AsNoTracking()
            .Where(p => p.Col == "merged"
                && (p.Workstream == id || prIdsForRestart.Contains(p.Id)))
            .OrderBy(p => p.Sort)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(ct);
        if (mergedLinkedPr is not null)
        {
            return OperatorActionOutcome.Problem(
                409, null,
                "Cannot restart a workstream with a merged PR",
                $"Workstream {id} is linked to merged PR {mergedLinkedPr.Id}; restart would abandon the local PR row and rerun against an already-merged base.");
        }

        var resolvedWorktreeBeforeRestart = await WorktreePathResolver.ResolveForWorkstreamAsync(this.control, this.db, id, ct);

        // ── 0. Cancel in-flight work (mirrors Cancel) ──
        try { await this.runner.CancelByTaskAsync(id, ct); }
        catch { /* best-effort — don't block restart if runner is down */ }

        // ── 0b. Force-dispose any cached Copilot SDK session BEFORE wiping memory. ──
        try { await this.copilotPool.DisposeForWorkItemForceAsync(id, ct); }
        catch { /* best-effort — pool may be empty or already disposed */ }

        var queueRows = await this.db.TaskQueue
            .Where(q => q.WorkItemId == id && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }
        await this.control.SaveChangesAsync(ct);
        await this.db.SaveChangesAsync(ct);

        // ── 0d. Pre-flight: the worktree MUST be fully removable before we wipe
        // any state (fail LOUDLY — before deleting deliverables/transcript/branch). ──
        var worktreeClearError = await WorkstreamsEndpoints.EnsureWorktreeRemovedForRestartAsync(this.db, ws, ct);
        if (worktreeClearError is not null)
        {
            return OperatorActionOutcome.Problem(
                409, null,
                "Restart blocked — worktree could not be cleared",
                worktreeClearError);
        }

        // ── 1. Delete all deliverables ──
        var deliverables = await this.db.Deliverables.Where(d => d.WorkItemId == id).ToListAsync(ct);
        this.db.Deliverables.RemoveRange(deliverables);

        // ── 2. Delete transcript events ──
        var events = await this.db.TaskTranscriptEvents.Where(e => e.WorkItemId == id).ToListAsync(ct);
        this.db.TaskTranscriptEvents.RemoveRange(events);

        // ── 2b. Delete durable prompt audit rows ──
        var iterationPromptsDeleted = await this.promptStore.DeleteForWorkItemAsync(id, ct);

        // ── 3. Delete UNANSWERED open questions; PRESERVE answered ones. An
        // operator's ANSWER is a durable decision the restarted run must honour
        // ("change the answer, then Restart"); only stale unanswered rows are
        // purged. (Absorbed 2026-06-30 change.) ──
        var questions = await this.db.DeliverableQuestions
            .Where(q => q.WorkItemId == id && (q.Answer == null || q.Answer == ""))
            .ToListAsync(ct);
        this.db.DeliverableQuestions.RemoveRange(questions);

        // ── 3b. Wipe Phase 52 pair-attempt history + cost ledger ──
        var pairRows = await this.db.AgentPairAssignments.Where(p => p.WorkItemId == id).ToListAsync(ct);
        this.db.AgentPairAssignments.RemoveRange(pairRows);

        var usageRows = await this.control.AgentUsage.Where(u => u.TaskId == id).ToListAsync(ct);
        this.control.AgentUsage.RemoveRange(usageRows);

        // ── 4. Close upstream PRs and deactivate local rows ──
        foreach (var prId in prIdsForRestart)
        {
            var prRow = await this.db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (prRow is null) continue;

            if (!string.IsNullOrEmpty(prRow.Provider) && !string.IsNullOrEmpty(prRow.WebUrl))
            {
                try
                {
                    var provider = this.providers.FirstOrDefault(p =>
                        string.Equals(p.Kind, prRow.Provider, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
                    if (provider is not null)
                    {
                        await provider.ClosePullRequestAsync(prId, ProviderCallContext.Anonymous, ct);
                    }
                }
                catch { /* best-effort — PR may already be closed or deleted */ }
            }

            prRow.IsActive = false;
            prRow.Col = "abandoned";
            prRow.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }

        // ── 5. Reset workstream status ──
        ws.Status = "active";
        ws.PrsJson = "[]";
        ws.Commits = 0;
        ws.Ahead = 0;
        ws.Behind = 0;
        ws.Conflicts = 0;
        ws.WorktreeJson = CardMapper.Serialize(new[] { "(not yet created)" });
        ws.AgentsJson = "[]";
        ws.StallReason = null;

        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wi is not null)
        {
            wi.ExecutionStatus = ExecutionStatuses.Queued;
            wi.TrialCount = 0;
            wi.AssignedAgentJson = null;

            // Phase 52 reset — clean slate for the new attempt.
            wi.PairAttemptCount = 1;
            wi.PriorPairsJson = null;
            wi.FailureReason = null;
            wi.LastPairAgentIdsJson = null;
            wi.BudgetWindowStartUtc = DateTimeOffset.UtcNow;

            wi.UpdatedAt = DateTimeOffset.UtcNow;

            // Refresh DescriptionMd from the story — except qa-e2e rows, whose
            // DescriptionMd carries the per-stage `## Test Scenarios` block.
            if (!string.IsNullOrEmpty(ws.StorySpawnId)
                && !string.Equals(ws.Category, WorkstreamCategories.QaE2e, StringComparison.Ordinal))
            {
                var spawnForDesc = await this.db.StorySpawns.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
                if (spawnForDesc is not null)
                {
                    var story = await this.db.ProductStories.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == spawnForDesc.StoryId, ct);
                    if (story is not null && !string.IsNullOrEmpty(story.Description))
                    {
                        wi.DescriptionMd = story.Description;
                    }
                }
            }
        }

        await this.control.SaveChangesAsync(ct);
        await this.db.SaveChangesAsync(ct);

        // ── 5b. Prune gate logs even if the git cleanup below can't remove the
        // whole worktree. ──
        var gateLogsPruned = false;
        if (!string.IsNullOrEmpty(resolvedWorktreeBeforeRestart.WorktreePath))
        {
            gateLogsPruned = GateLogPaths.PruneAll(resolvedWorktreeBeforeRestart.WorktreePath);
        }

        // ── 6. Clear the cached workspace(s) ──
        var stateDir = this.paths.Folder;
        var spawnSlug = ws.StorySpawnId ?? "";
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') })
        {
            if (string.IsNullOrEmpty(slug)) continue;
            var spawnRoot = System.IO.Path.Combine(stateDir, "storyworkspaces", slug);
            if (!System.IO.Directory.Exists(spawnRoot)) continue;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(spawnRoot))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (name == $"impl-{id}"
                    || name == $"qa-e2e-{id}"
                    || name.StartsWith($"review-{id}-", StringComparison.Ordinal)
                    || name.StartsWith($"fix-{id}-", StringComparison.Ordinal))
                {
                    try { System.IO.Directory.Delete(dir, recursive: true); }
                    catch { /* best effort */ }
                }
            }
        }

        // ── 7. Nuke git branch — worktree, local ref, AND remote ──
        if (!string.IsNullOrEmpty(ws.Branch))
        {
            var repoRow = await this.db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ws.RepoId, ct);
            if (repoRow is not null && !string.IsNullOrEmpty(repoRow.CloneRoot)
                && System.IO.Directory.Exists(repoRow.CloneRoot))
            {
                var cloneRoot = repoRow.CloneRoot;

                string? canonicalWtPath = null;
                if (!string.IsNullOrEmpty(ws.StorySpawnId))
                {
                    var siblings = await this.db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == ws.StorySpawnId)
                        .ToListAsync(ct);
                    if (siblings.All(w => w.Id != ws.Id)) siblings.Add(ws);
                    var slug = WorkstreamWorktreeSlug.Compute(ws, siblings);
                    canonicalWtPath = System.IO.Path.Combine(cloneRoot, ".worktree", slug);
                }

                var registeredPath = await WorkstreamsEndpoints.TryFindWorktreeForBranchAsync(cloneRoot, ws.Branch);
                foreach (var wtPath in new[] { canonicalWtPath, registeredPath }
                    .Where(p => !string.IsNullOrEmpty(p)).Distinct())
                {
                    await WorkstreamsEndpoints.RunGitQuietAsync(cloneRoot, "worktree", "remove", "--force", wtPath!);

                    if (System.IO.Directory.Exists(wtPath!))
                    {
                        try { System.IO.Directory.Delete(wtPath!, recursive: true); }
                        catch { /* locked files — will be pruned on next tick */ }
                    }
                }

                await WorkstreamsEndpoints.RunGitQuietAsync(cloneRoot, "worktree", "prune");
                await WorkstreamsEndpoints.RunGitQuietAsync(cloneRoot, "branch", "-D", ws.Branch);
                await WorkstreamsEndpoints.RunGitQuietAsync(cloneRoot, "push", "origin", "--delete", ws.Branch);
            }
        }

        await this.hub.Clients.All.SendAsync("workstream.changed", new { kind = "restarted", id }, ct);

        // ── 7b. Wipe Phase 55 agent_memory rows (late, after teardown). ──
        int memoryDeleted;
        try
        {
            memoryDeleted = await this.memStore.DeleteForWorkItemAsync(id, ct);
        }
        catch
        {
            memoryDeleted = 0;
            /* best-effort — never block restart on memory cleanup */
        }

        // ── 8. Force a plan reconcile, THEN re-drive the orchestrator ──
        var planReconciled = 0;
        if (!string.IsNullOrEmpty(ws.StorySpawnId))
        {
            try
            {
                var rec = await this.reconciler.ReconcileAsync(ws.StorySpawnId, ct);
                planReconciled = rec?.Updated ?? 0;
            }
            catch
            {
                /* best-effort — the scheduler's periodic pass remains the backstop. */
            }

            var spawn = await this.db.StorySpawns.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (spawn is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await this.orchestrator.DriveAsync(spawn.StoryId, forceResume: false, CancellationToken.None); }
                    catch { /* failures persisted via PersistLastErrorAsync */ }
                });
            }
        }

        return OperatorActionOutcome.Success(new
        {
            id,
            status = "restarted",
            deliverablesCleaned = deliverables.Count,
            eventsCleaned = events.Count,
            iterationPromptsCleaned = iterationPromptsDeleted,
            memoryRowsCleaned = memoryDeleted,
            gateLogsPruned,
            planReconciled,
        });
    }
}
