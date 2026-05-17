// -----------------------------------------------------------------------
// <copyright file="DeliverableLoop.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 14.4 — generator ↔ evaluator iteration orchestrator. Runs a
/// deliverable through up to <see cref="ScoringConstants.MaxIterations"/>
/// rounds, persisting one <see cref="DeliverableRow"/> per attempt and
/// flipping the linked work-item state on terminal verdicts.
/// <para>
/// The loop is deliberately decoupled from <c>AgentRunnerDispatcher</c>:
/// callers pass in two delegates (one for the generator run, one for the
/// evaluator) so the orchestrator is fully unit-testable without a live
/// runner. Production wiring (Phase 13.4 / 13.5 stubs become real) will
/// supply delegates that invoke the dispatcher.
/// </para>
/// <para>
/// Verdict semantics (locked by <see cref="ScoringConstants"/>):
/// <list type="bullet">
///   <item>score ≥ 90 → <c>pass</c>; work item → <c>closed</c>; loop ends.</item>
///   <item>70 ≤ score &lt; 90 → <c>iterate</c>; loop re-enters with the
///         evaluator's notes appended to the next generator run.</item>
///   <item>score &lt; 70 → <c>reject</c>; work item → <c>proposed</c>;
///         loop ends. The lead reopens manually.</item>
///   <item>5 iterations exhausted with no pass → <c>stalled</c>; the work
///         item is left at its current state for the lead to triage.</item>
///   <item>budget cap hit → <c>stalled-budget</c>; same triage flow.</item>
/// </list>
/// </para>
/// </summary>
public interface IDeliverableLoop
{
    Task<DeliverableLoopResult> RunAsync(DeliverableLoopRequest request, CancellationToken ct);
}

/// <summary>
/// Inputs to one full deliverable run.
/// <para><c>Generator</c> — per-iteration delegate; receives the iter
/// number (1-based) and the evaluator's previous notes (empty on iter 1).</para>
/// <para><c>Evaluator</c> — per-iteration delegate; receives the
/// generator's raw output text and returns the evaluator's raw assistant
/// text, which the loop parses via <see cref="EvaluatorScoreParser"/>.</para>
/// <para><c>MaxTaskBudgetUsd</c> — optional hard cap on cumulative spend
/// across all iterations.</para>
/// </summary>
public sealed record DeliverableLoopRequest(
    string WorkItemId,
    string CrewId,
    string GeneratorAgentId,
    string EvaluatorAgentId,
    Func<int, string, CancellationToken, Task<GeneratorRunOutput>> Generator,
    Func<int, string, CancellationToken, Task<EvaluatorRunOutput>> Evaluator,
    decimal? MaxTaskBudgetUsd = null,
    /// <summary>
    /// Phase 16.4 — when set, evaluator-side cost ledger writes are tagged
    /// to this crew (the paired evaluator crew). When null, the existing
    /// Phase 14 in-crew evaluator path is used and both costs land on
    /// <see cref="CrewId"/>.
    /// </summary>
    string? EvaluatorCrewId = null,
    /// <summary>
    /// Phase 20 — optional override for <see cref="ScoringConstants.MaxIterations"/>.
    /// When null, the compile-time constant is used.
    /// </summary>
    int? MaxIterations = null,
    /// <summary>
    /// Phase 24 Q24.12 — task kind discriminator. Defaults to <c>'coding'</c>
    /// for backward compatibility; <c>'comment-response'</c> selects the
    /// scope-is-union-of-comments rubric note for the evaluator (Q24.13). The
    /// loop's flow control is identical across kinds — only the evaluator
    /// preamble differs, and that's the caller's responsibility (the loop
    /// surfaces this string on the deliverable row + transcript events so the
    /// downstream evaluator delegate can branch).
    /// </summary>
    string Kind = DeliverableKinds.Coding,
    /// <summary>
    /// Work-item story points — used to adjust the reject/iterate threshold.
    /// Higher-point items get a lower reject bar so more iterations are attempted.
    /// </summary>
    int WorkItemPoints = 1,
    /// <summary>
    /// Phase 56 — pass-validator hook. Invoked when the evaluator returns
    /// <c>verdict="pass"</c>, BEFORE the loop stamps pair-assignment as
    /// <c>Passed</c>, sets the work item state to <c>active</c>, or emits
    /// <c>task.iteration.passed</c>. When the callback returns null or
    /// whitespace, the pass is accepted and the loop terminates normally.
    /// When the callback returns a non-empty string, the verdict is
    /// demoted to <c>iterate</c>, the returned string is appended to the
    /// deliverable row's <c>Notes</c> (and surfaces in the next iter's
    /// evaluator chain via <c>FormatPriorChain</c>), and the loop
    /// continues to iter N+1. Same demotion mechanism as the unanswered-
    /// questions gate and the checkbox-attention gate elsewhere in this
    /// file. The callback may also throw <see cref="Stories.BuildFailedException"/>
    /// (or any other exception) to terminate the loop with that
    /// exception — used by the post-pass gate-recovery exhaustion path.
    /// Null = no validator (today's behavior; pass means pass).
    /// </summary>
    Func<int, CancellationToken, Task<string?>>? OnPassValidate = null,
    /// <summary>
    /// Phase 65 — per-iter commit-snapshot hook. Invoked AFTER the
    /// iter's <see cref="DeliverableRow"/> has been persisted (so the
    /// callback can reference the row's <see cref="DeliverableRow.Id"/>
    /// if it wants to cross-link telemetry) and BEFORE the unanswered-
    /// questions / checkbox-attention / <see cref="OnPassValidate"/>
    /// gates run. The producer captures the workstream's branch state
    /// (HEAD SHA, message, numstat against the iter's start point) and
    /// returns an <see cref="IterCommitInfo"/>; the loop UPDATEs the
    /// just-persisted row with the snapshot so downstream queries
    /// (iter-KPI strip, deliverables endpoint) don't have to re-derive
    /// from <c>task.worktree-tick</c> events that may have been missed
    /// when the worktree was removed mid-flight. Null callback = no
    /// snapshot recorded; existing aggregation paths still work.
    /// Throwing from the callback is non-fatal — the loop logs and
    /// continues; the iter row stays without commit metadata.
    /// </summary>
    Func<int, long, CancellationToken, Task<IterCommitInfo?>>? OnIterationCommit = null);

/// <summary>
/// Phase 24 Q24.12 — well-known values for <see cref="DeliverableLoopRequest.Kind"/>.
/// </summary>
public static class DeliverableKinds
{
    public const string Coding = "coding";
    public const string CommentResponse = "comment-response";
    public const string BuildFix = "build-fix";
    public const string Plan = "plan";
}

/// <summary>One generator turn's raw output + cost.</summary>
/// <remarks>
/// Phase 54 — <paramref name="TurnSamplesJson"/> carries an optional JSON
/// array of per-<c>AssistantUsageEvent</c> token snapshots from the
/// underlying SDK. Shape mirrors
/// <see cref="AgentUsageRow.TurnSamplesJson"/>. Null on CLI runners that
/// only emit a single cumulative tally. Default null preserves source
/// compatibility with existing call sites that construct
/// <c>new GeneratorRunOutput(text, cost, in, out)</c>.
/// <para>
/// 2026-05-13 root-cause fix (max-turns cap) — when the generator's
/// underlying invocation aborted with an <c>AgentEventKind.Failed</c>
/// event (per-task max-turns cap, hard timeout, transport silent,
/// non-zero CLI exit), <paramref name="FailureReason"/> carries the
/// dispatcher's error message and <paramref name="FailureKind"/> a
/// machine-classifier (see <see cref="GeneratorFailureKinds"/>). The
/// loop short-circuits on non-null <paramref name="FailureReason"/>:
/// no evaluator runs, the deliverable lands as <c>verdict="stalled"</c>
/// with the reason in <see cref="DeliverableRow.Notes"/>, and the
/// already-accumulated tokens/cost are still persisted to the agent
/// usage ledger via <c>WriteUsageAsync</c>. Without this, the loop
/// would feed the partial/corrupt generator output to the evaluator
/// and queue another iter against the same broken cap (operator-paid
/// $7.31 evaluator on a $0 cap-aborted generator on
/// qq:TELEGRAM-MESSENGER-S, 2026-05-13).
/// </para>
/// </remarks>
public sealed record GeneratorRunOutput(
    string OutputText,
    decimal CostUsd,
    long InputTokens = 0,
    long OutputTokens = 0,
    string? TurnSamplesJson = null,
    string? FailureReason = null,
    string? FailureKind = null,
    /// <summary>
    /// 2026-05-16 Phase 72 — prompt-cache hit tokens accumulated by
    /// the runner (Copilot SDK <c>AssistantUsageEvent.CacheReadTokens</c>).
    /// Null when the runner / backend doesn't surface cache metrics.
    /// </summary>
    long? CacheReadTokens = null,
    /// <summary>2026-05-16 Phase 72 — prompt-cache write tokens.</summary>
    long? CacheWriteTokens = null,
    /// <summary>2026-05-16 Phase 72 — model reasoning tokens.</summary>
    long? ReasoningTokens = null,
    /// <summary>
    /// 2026-05-16 Phase 72 — total conversation context size after this
    /// turn, from the SDK's <c>SessionUsageInfoEvent</c>. Reveals
    /// context bloat when prompt+history balloons across iters.
    /// </summary>
    long? ConversationTokens = null,
    /// <summary>
    /// 2026-05-16 Phase 72 — provider-native session id captured after
    /// <c>CreateSessionAsync</c>. <see cref="AgentMemoryStore"/>
    /// persists this so the operator can verify session reuse via
    /// <c>/api/workstreams/{id}/memory</c>.
    /// </summary>
    string? SessionId = null);

/// <summary>
/// Phase 65 — per-iter commit snapshot captured by the producer right
/// after the iter's evaluator scored. Wired through
/// <see cref="DeliverableLoopRequest.OnIterationCommit"/>; the loop
/// invokes the callback after persisting the iter row and (when the
/// callback returns non-null) UPDATEs the just-persisted
/// <see cref="DeliverableRow"/> with these values.
///
/// <para>All fields are nullable so the producer can return partial
/// information (e.g. commit happened but numstat parse failed, or
/// counts are known but the SHA wasn't captured). The Conversation
/// tab's iter-KPI strip prefers these row-level values over the
/// pre-existing <c>task.worktree-tick</c> event aggregation because
/// the event signal misses iter windows when the worktree is removed
/// mid-flight or a commit lands in the last 1-second tick slice (the
/// failure mode that triggered Phase 65 — see
/// PR smartpcr/failover-cluster#19 Log Replication, 2026-05-14, where
/// 3 real commits + 6 modified paths showed as "0f" for all four
/// iters because no <c>worktree-tick</c> event fell inside any iter's
/// time window).</para>
/// </summary>
public sealed record IterCommitInfo(
    string? CommitSha,
    string? CommitMessage,
    int? FilesAdded,
    int? FilesModified,
    int? FilesDeleted)
{
    /// <summary>Convenience denormalisation — null when every input is null, else sum-of-non-nulls.</summary>
    public int? FilesChanged =>
        (this.FilesAdded ?? 0) + (this.FilesModified ?? 0) + (this.FilesDeleted ?? 0) is var sum
            && (this.FilesAdded.HasValue || this.FilesModified.HasValue || this.FilesDeleted.HasValue)
                ? sum
                : null;
}

/// <summary>
/// 2026-05-13 root-cause fix — machine-classifier strings for
/// <see cref="GeneratorRunOutput.FailureKind"/>. The DeliverableLoop
/// short-circuit, the live-channel <c>task.iteration.stalled-generator</c>
/// event, and the Phase 52 pair-assignment outcome ledger all read these
/// constants so a UI/operator can distinguish "bumped against the per-task
/// max-turns cap (raise points and Resume)" from a transport hiccup or a
/// CLI crash without parsing the free-form provider error text.
/// </summary>
public static class GeneratorFailureKinds
{
    /// <summary>Per-task max-turns cap (Phase 21.5: 30 turns / point).
    /// Recovery: bump <c>WorkItem.Points</c> or
    /// <c>TaskQueue.MaxIterOverride</c> and Resume.</summary>
    public const string MaxTurns = "generator-max-turns";

    /// <summary>Hard wall-clock budget exceeded (per-task wall-clock
    /// budget defaults to 30 min / point).</summary>
    public const string Timeout = "generator-timeout";

    /// <summary>Underlying transport went silent for longer than the
    /// inactivity watchdog (Copilot SDK transport drop / server crash).
    /// </summary>
    public const string Inactive = "generator-inactive";

    /// <summary>CLI runner exited with a non-zero code (claude / copilot
    /// CLI crash, malformed args, mid-stream OOM).</summary>
    public const string CliExit = "generator-cli-exit";

    /// <summary>Provider HTTP error (Anthropic 4xx/5xx after retries
    /// exhausted, etc).</summary>
    public const string ProviderError = "generator-provider-error";

    /// <summary>Catch-all when the dispatcher emitted Failed but the
    /// error string didn't match any of the more specific patterns.
    /// </summary>
    public const string Other = "generator-failed";

    /// <summary>
    /// Phase 64 — a single tool call (one ToolExecutionStartEvent without
    /// a matching ToolExecutionCompleteEvent) ran longer than
    /// <c>AgentRunnerOptions.PerTurnTimeout</c>. Distinct from
    /// <see cref="Inactive"/> (which fires when the whole transport
    /// goes silent including between tools) and from <see cref="Timeout"/>
    /// (per-invocation wall clock). Recovery: investigate the named
    /// tool (e.g. a hung build / test subprocess) rather than bumping
    /// budgets.
    /// </summary>
    public const string StuckToolCall = "generator-stuck-tool-call";

    /// <summary>
    /// Classify a free-form Failed-event error message into one of the
    /// kinds above. Pattern-matching is conservative: we look for the
    /// exact phrases the dispatcher emits (see
    /// <c>AgentRunnerDispatcher.BuildFailedWithUsage</c> call sites).
    /// Unknown errors fall through to <see cref="Other"/>.
    /// </summary>
    public static string Classify(string? error)
    {
        if (string.IsNullOrEmpty(error)) return Other;
        // Copilot SDK / CrewQueueWorker max-turn loop emits
        // "Max turns ({n}) reached" verbatim (CrewQueueWorker.cs ~1450,
        // mirrored in the Copilot/SDK loop helpers). Match either
        // wording to be robust against minor phrasing tweaks.
        if (error.Contains("Max turns", StringComparison.OrdinalIgnoreCase)
            || error.Contains("max-turns", StringComparison.OrdinalIgnoreCase))
        {
            return MaxTurns;
        }
        // Phase 64 — stuck-tool-call must be checked BEFORE the generic
        // "exceeded budget" pattern, because the dispatcher's stuck-tool
        // message also contains "exceeded" (per-turn budget).
        if (error.Contains("stuck on tool", StringComparison.OrdinalIgnoreCase)
            || error.Contains("per-turn budget", StringComparison.OrdinalIgnoreCase))
        {
            return StuckToolCall;
        }
        if (error.Contains("hard timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("exceeded", StringComparison.OrdinalIgnoreCase) && error.Contains("budget", StringComparison.OrdinalIgnoreCase))
        {
            return Timeout;
        }
        if (error.Contains("transport silent", StringComparison.OrdinalIgnoreCase))
        {
            return Inactive;
        }
        if (error.Contains(" exit=", StringComparison.OrdinalIgnoreCase))
        {
            return CliExit;
        }
        if (error.StartsWith("Anthropic ", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP", StringComparison.OrdinalIgnoreCase) && error.Contains("status", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderError;
        }
        return Other;
    }
}

/// <summary>One evaluator turn's raw output + cost.</summary>
/// <remarks>
/// Phase 54 — see <see cref="GeneratorRunOutput.TurnSamplesJson"/>.
/// 2026-05-16 Phase 72 — same Cache* / Reasoning / Conversation /
/// SessionId fields the generator carries; evaluator side often runs
/// against an independent session (different agent), so the
/// conversationTokens it reports describe the evaluator's session,
/// not the generator's. Persist into the same agent_usage columns
/// keyed on <c>CostCategory="evaluator"</c>.
/// </remarks>
public sealed record EvaluatorRunOutput(
    string AssistantText,
    decimal CostUsd,
    long InputTokens = 0,
    long OutputTokens = 0,
    string? TurnSamplesJson = null,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    long? ReasoningTokens = null,
    long? ConversationTokens = null,
    string? SessionId = null);

/// <summary>Final outcome of <see cref="IDeliverableLoop.RunAsync"/>.</summary>
public sealed record DeliverableLoopResult(
    string FinalVerdict,
    int Iterations,
    int FinalScore,
    IReadOnlyList<long> DeliverableRowIds,
    decimal TotalCostUsd,
    /// <summary>
    /// Phase 52 — when <see cref="FinalVerdict"/> is <c>"sloppy"</c>,
    /// carries the structured trigger reason
    /// (<see cref="SloppyReasons.ScorePlateau"/> /
    /// <see cref="SloppyReasons.TokenCap"/> /
    /// <see cref="SloppyReasons.IterCap"/>). Null on any other verdict.
    /// </summary>
    string? SloppyReason = null);

/// <inheritdoc cref="IDeliverableLoop"/>
public sealed class DeliverableLoop : IDeliverableLoop
{
    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly TaskChannel? channel;

    /// <summary>
    /// Phase 38 (Layer A.3) — map a work item's story points to a per-loop
    /// iteration cap. Bigger work earns more attempts before the loop
    /// stalls; tiny work still gets a 5-iter floor so the gen↔eval pair has
    /// breathing room to converge on subtle docs. Falls back to
    /// <see cref="ScoringConstants.MaxIterations"/> for points &lt;= 0 (e.g.
    /// legacy synthetic workItemIds with no work-item row). Exposed as
    /// <c>public static</c> so call sites + tests can reference the same
    /// mapping without duplicating the table.
    /// </summary>
    /// <summary>5 iterations per story point. 1pt=5, 8pt=40, 13pt=65.</summary>
    public const int IterationsPerPoint = 5;

    public static int PointsToMaxIter(int points) =>
        points <= 0 ? ScoringConstants.MaxIterations : points * IterationsPerPoint;

    public DeliverableLoop(ControlDbContext control, ProductDbContext db) : this(control, db, channel: null) { }

    /// <summary>
    /// Phase 16.7 — when <paramref name="channel"/> is non-null, the loop
    /// emits <c>task.iteration.started | scored | iterated | passed |
    /// rejected | stalled</c> events on the per-work-item live channel.
    /// </summary>
    public DeliverableLoop(ControlDbContext control, ProductDbContext db, TaskChannel? channel)
    {
        this.control = control;
        this.db = db;
        this.channel = channel;
    }

    public async Task<DeliverableLoopResult> RunAsync(DeliverableLoopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Generator);
        ArgumentNullException.ThrowIfNull(request.Evaluator);

        var rowIds = new List<long>();
        var totalCost = 0m;
        // Running chain of (iter, score, verdict, notes) from every prior
        // attempt — fed to the next generator turn so it sees the full
        // feedback trajectory, not just the last evaluator's note. Without
        // this the generator forgets what it's already been told and can
        // ship the same flawed diff three rounds in a row (WI-1035 stalled
        // at score 10 by ignoring the same root issue across iters 3-5).
        var priorChain = new List<(int Iter, int Score, string Verdict, string Notes)>();
        var notesForNext = string.Empty;
        EvaluatorScore? lastScore = null;
        var verdict = "iterate";
        var iter = 0;

        // Phase 54 — track prior-iteration cumulative input/output tokens
        // per role so we can compute deltas and emit
        // `task.iteration.prompt-growth` + `task.prompt-bloat` events.
        long priorGenInputTokens = 0;
        long priorGenOutputTokens = 0;
        long priorEvalInputTokens = 0;
        long priorEvalOutputTokens = 0;

        // Phase 52 — load WorkItemRow.BudgetWindowStartUtc once (used by
        // the per-iter sloppy token-cap check). Phase 51 sets this on a
        // cooldown-elapsed resume to reset the cost meter; Phase 52
        // honors the same window for the token meter so a reassignment
        // grants the new pair a fresh $0 / 0-token start.
        var workItemRow = await this.db.WorkItems
            .FirstOrDefaultAsync(w => w.Id == request.WorkItemId, ct);
        var budgetWindowStartUtc = workItemRow?.BudgetWindowStartUtc;

        // Phase 52 — ensure an in-progress AgentPairAssignmentRow exists for
        // (workItem, current pair). Created lazily when missing (legacy
        // rows mid-flight at migration time, plan-doc synthetic IDs, etc.).
        // The reconciler relies on this row being present to stamp
        // outcome=reassigned-sloppy / terminal-failed on the trigger.
        var pairAssignmentId = await this.EnsureActivePairAssignmentAsync(
            request.WorkItemId,
            request.GeneratorAgentId,
            request.EvaluatorAgentId,
            request.CrewId,
            ct);

        // Phase 14.3 / 16.4 — pin the evaluator agent on iter 1 so subsequent
        // rounds reuse the same grader (rubric reproducibility). Generator
        // agent is also pinned for symmetry.
        var pinnedEvaluator = request.EvaluatorAgentId;
        var evalCrewId = request.EvaluatorCrewId ?? request.CrewId;

        // Phase 38 (Layer A.3): when the caller didn't pin MaxIterations,
        // derive the cap from the work item's points so a 1-point doc gets a
        // tight 3-iter budget while a 13-point delivery gets up to 15. This
        // matches the operator intuition that bigger work earns more
        // attempts before stalling. PATCH /api/work-items/{id}/execution
        // (Q20.2) still wins via MaxIterOverride on the queue row, which
        // the loop reads each iteration below.
        var maxIter = request.MaxIterations ?? PointsToMaxIter(request.WorkItemPoints);

        // Phase 47 — apply the queue's MaxIterOverride BEFORE the terminal
        // short-circuit below so an operator who bumped the cap (Resume on
        // a stalled WI) gets a chance to run additional iters instead of
        // being short-circuited on the OLD cap. The original per-iter
        // override read inside the while loop still wins for mid-loop
        // updates; this is just to seed the right baseline for the
        // short-circuit check.
        var initialQueueRows = await this.db.TaskQueue.AsNoTracking()
            .Where(q => q.WorkItemId == request.WorkItemId)
            .ToListAsync(ct);
        var initialOverride = initialQueueRows
            .Where(q => q.State != "completed" && q.MaxIterOverride != null)
            .OrderByDescending(q => q.Id)
            .Select(q => q.MaxIterOverride)
            .FirstOrDefault()
            ?? initialQueueRows
                .Where(q => q.MaxIterOverride != null)
                .OrderByDescending(q => q.Id)
                .Select(q => q.MaxIterOverride)
                .FirstOrDefault();
        if (initialOverride is int iov && iov > 0)
        {
            maxIter = iov;
        }

        // Resume support — when boot-recovery puts a half-finished work item
        // back on the queue, the agent invocation re-enters this loop. Seed
        // `iter` and `notesForNext` from the most recent persisted deliverable
        // so the next round picks up at iter+1 with the prior evaluator's
        // feedback, instead of starting iteration 1 over from scratch.
        // Skip the seeding for terminal verdicts ("pass" / "reject") — those
        // mean the loop reached its end naturally; re-running would either
        // duplicate completed work or fight a verdict the lead must triage.
        var existingDeliverables = await this.db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == request.WorkItemId)
            .ToListAsync(ct);
        var lastPersisted = existingDeliverables.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter).FirstOrDefault();

        // Phase 47 — terminal-verdict short-circuit. Before this, a workstream
        // that hit `iter == maxIter` with `stalled` verdict would re-enter the
        // loop with `iter` reset to 0 (the resume guard below requires
        // `lastPersisted.Iter < maxIter`) and run a full fresh cycle of
        // iterations 1→maxIter. Confirmed in production 2026-05-09: a single
        // workstream had 139 deliverable rows for maxIter=50 — the iter
        // histogram showed each iter 1-36 occurring 3 times, costing $9,825
        // across 4 days. Now we return the persisted verdict immediately on
        // any of these terminal cases:
        //   pass            — terminal regardless of iter (orchestrator-side
        //                     short-circuits this too, but defense-in-depth
        //                     in case a caller bypasses the orchestrator).
        //   reject          — terminal regardless of iter (lead must triage).
        //   stalled-budget  — terminal regardless of iter (operator must
        //                     raise budget via Restart; just re-running with
        //                     the same cap burns more cost).
        //   stalled         — terminal only when at-or-past cap. If the
        //                     operator bumped MaxIterOverride (handled
        //                     above), the new cap exceeds the persisted
        //                     iter and the resume path takes over instead.
        // Mirror the original side effects (TrySetWorkItemStateAsync) so a
        // replay of pass/reject still leaves WorkItem state consistent if a
        // crash had skipped them last round.
        if (lastPersisted is not null)
        {
            var persistedTerminal =
                lastPersisted.Verdict is "pass" or "reject" or "stalled-budget"
                || ((lastPersisted.Verdict == "stalled" || lastPersisted.Verdict == "sloppy")
                    && lastPersisted.Iter >= maxIter);
            if (persistedTerminal)
            {
                if (lastPersisted.Verdict == "pass")
                {
                    await TrySetWorkItemStateAsync(this.control, this.db, request.WorkItemId, "active", ct);
                }
                else if (lastPersisted.Verdict == "reject")
                {
                    await TrySetWorkItemStateAsync(this.control, this.db, request.WorkItemId, "proposed", ct);
                }

                await this.EmitAsync(request.WorkItemId, "task.iteration.terminal-replay", new
                {
                    iter = lastPersisted.Iter,
                    verdict = lastPersisted.Verdict,
                    score = lastPersisted.Score,
                    maxIter,
                }, ct);

                return new DeliverableLoopResult(
                    lastPersisted.Verdict,
                    lastPersisted.Iter,
                    lastPersisted.Score,
                    new List<long>(),
                    0m);
            }
        }

        if (lastPersisted is not null
            && lastPersisted.Verdict is "iterate" or "stalled" or "stalled-budget" or "sloppy" or "stalled-questions"
            && lastPersisted.Iter < maxIter)
        {
            iter = lastPersisted.Iter;
            // Seed the chain from every prior non-terminal row so the
            // resumed generator turn gets the same history a fresh run
            // would have built up in-memory.
            var seedRows = existingDeliverables
                .Where(d => d.Verdict is "iterate" or "stalled" or "stalled-budget" or "sloppy" or "stalled-questions")
                .OrderBy(d => d.Iter)
                .ThenBy(d => d.CreatedAt);
            foreach (var d in seedRows)
            {
                priorChain.Add((d.Iter, d.Score, d.Verdict, d.Notes ?? string.Empty));
            }
            notesForNext = FormatPriorChain(priorChain);
            await this.EmitAsync(request.WorkItemId, "task.iteration.resumed", new
            {
                resumeFromIter = iter,
                nextIter = iter + 1,
                priorVerdict = lastPersisted.Verdict,
                priorScore = lastPersisted.Score,
                priorAgent = lastPersisted.GeneratorAgentId,
            }, ct);
        }

        while (iter < maxIter)
        {
            // Phase 20 — re-read per-work-item override each iteration so
            // the lead can raise/lower the cap from the Live Execution view.
            // Prefer the active (non-completed) row, fall back to any row with an override.
            var queueRows = await this.db.TaskQueue.AsNoTracking()
                .Where(q => q.WorkItemId == request.WorkItemId)
                .ToListAsync(ct);
            var queueOverride = queueRows
                .Where(q => q.State != "completed" && q.MaxIterOverride != null)
                .OrderByDescending(q => q.Id)
                .Select(q => q.MaxIterOverride)
                .FirstOrDefault()
                ?? queueRows
                    .Where(q => q.MaxIterOverride != null)
                    .OrderByDescending(q => q.Id)
                    .Select(q => q.MaxIterOverride)
                    .FirstOrDefault();
            if (queueOverride is int ov && ov > 0)
            {
                maxIter = ov;
            }

            iter++;

            await this.EmitAsync(request.WorkItemId, "task.iteration.started", new
            {
                iter,
                generatorCrewId = request.CrewId,
                evaluatorCrewId = evalCrewId,
                generatorAgentId = request.GeneratorAgentId,
                evaluatorAgentId = pinnedEvaluator,
            }, ct);

            // Phase 39 (Layer C) — prepend operator-answered questions to
            // the notes-for-next so the generator sees the resolved answers
            // before any iteration-chain feedback. The block is appended as
            // a `## Operator answers` section the generator can quote
            // directly into the doc/code.
            var answersBlock = await BuildOperatorAnswersBlockAsync(this.control, this.db, request.WorkItemId, ct);
            var iterPrelude = string.IsNullOrEmpty(answersBlock)
                ? notesForNext
                : answersBlock + (string.IsNullOrEmpty(notesForNext) ? string.Empty : "\n\n" + notesForNext);

            // (a) generator
            var gen = await request.Generator(iter, iterPrelude, ct);
            await this.WriteUsageAsync(request.WorkItemId, request.GeneratorAgentId, request.CrewId, "generator", gen.CostUsd, gen.InputTokens, gen.OutputTokens, gen.TurnSamplesJson, gen.CacheReadTokens, gen.CacheWriteTokens, gen.ReasoningTokens, gen.ConversationTokens, ct);

            // 2026-05-13 root-cause fix — when the underlying agent
            // invocation aborted (per-task max-turns cap, hard timeout,
            // transport silent, CLI crash), the generator delegate now
            // surfaces the failure on
            // `GeneratorRunOutput.FailureReason/FailureKind`. Short-
            // circuit the evaluator (no point grading broken output) and
            // persist a deliverable row carrying the failure metadata.
            //
            // Transient failures (timeout, inactive transport) use
            // verdict="iterate" so the loop auto-retries with a fresh
            // budget on the next iter — the sloppy detectors (Phase 61
            // ConsecutiveScoreZero, Phase 67 NoProgressScoreZero) catch
            // persistent failures after ≤3 retries.
            //
            // Non-transient failures (max-turns cap, CLI crash, provider
            // error, stuck tool) use verdict="stalled" — these need
            // operator action (raise points, investigate crash, wait for
            // upstream recovery).
            if (!string.IsNullOrEmpty(gen.FailureReason))
            {
                var failKind = !string.IsNullOrEmpty(gen.FailureKind)
                    ? gen.FailureKind!
                    : GeneratorFailureKinds.Classify(gen.FailureReason);
                totalCost += gen.CostUsd;

                // Transient failures auto-retry; non-transient stall.
                var isTransient = failKind is GeneratorFailureKinds.Timeout
                    or GeneratorFailureKinds.Inactive;
                var failVerdict = isTransient ? "iterate" : "stalled";

                var failNote = $"GENERATOR FAILED ({failKind}): {gen.FailureReason}\n\n"
                    + "The evaluator was skipped to avoid burning tokens against a "
                    + "known-broken generator output. "
                    + (isTransient
                        ? "Auto-retrying on next iteration (transient failure)."
                        : "Resume after fixing root cause "
                          + "(e.g., bump WorkItem.Points or TaskQueue.MaxIterOverride for "
                          + $"`{GeneratorFailureKinds.MaxTurns}`).");
                if (!string.IsNullOrEmpty(notesForNext))
                {
                    failNote += "\n\n---\n\n" + notesForNext;
                }
                var failRow = new DeliverableRow
                {
                    WorkItemId = request.WorkItemId,
                    CrewId = request.CrewId,
                    GeneratorAgentId = request.GeneratorAgentId,
                    EvaluatorAgentId = pinnedEvaluator,
                    Iter = iter,
                    Score = 0,
                    DimsJson = "{}",
                    Verdict = failVerdict,
                    Notes = failNote,
                    GeneratorSummary = GeneratorSummaryExtractor.Extract(gen.OutputText ?? string.Empty),
                    GenCostUsd = gen.CostUsd,
                    EvalCostUsd = 0m,
                    FailureKind = failKind,
                    FailureReason = gen.FailureReason,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                this.db.Deliverables.Add(failRow);
                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                rowIds.Add(failRow.Id);

                await this.UpdatePairAssignmentTotalsAsync(
                    pairAssignmentId,
                    iter,
                    gen.InputTokens,
                    gen.OutputTokens,
                    gen.CostUsd,
                    latestScore: 0,
                    ct);

                await this.EmitAsync(request.WorkItemId, "task.iteration.stalled-generator", new
                {
                    iter,
                    failureKind = failKind,
                    failureReason = gen.FailureReason,
                    generatorAgentId = request.GeneratorAgentId,
                    evaluatorAgentId = pinnedEvaluator,
                    genCostUsd = gen.CostUsd,
                    genInputTokens = gen.InputTokens,
                    genOutputTokens = gen.OutputTokens,
                    totalCostUsd = totalCost,
                    autoRetry = isTransient,
                }, ct);

                if (!isTransient)
                {
                    await this.StampPairAssignmentAsync(
                        pairAssignmentId,
                        AgentPairAssignmentOutcomes.Cancelled,
                        sloppyReason: failKind,
                        finalScore: 0,
                        ct);

                    return new DeliverableLoopResult(
                        "stalled",
                        iter,
                        FinalScore: 0,
                        rowIds,
                        totalCost);
                }

                // Transient — feed the failure note into the next iter's
                // prelude so the agent knows what happened and can adjust.
                lastScore = new EvaluatorScore(
                    Score: 0, Dims: new Dictionary<string, int>(),
                    Verdict: "iterate", ModelVerdict: null,
                    VerdictMismatch: false, Notes: failNote,
                    ParseFailed: true, ParseError: failKind,
                    ParseRecovered: false);

                await this.EmitAsync(request.WorkItemId, "task.iteration.iterated", new { iter, score = 0, notes = failNote }, ct);
                continue;
            }

            // (b) evaluator
            var eval = await request.Evaluator(iter, gen.OutputText, ct);
            lastScore = EvaluatorScoreParser.Parse(eval.AssistantText, request.WorkItemPoints);
            await this.WriteUsageAsync(request.WorkItemId, pinnedEvaluator, evalCrewId, "evaluator", eval.CostUsd, eval.InputTokens, eval.OutputTokens, eval.TurnSamplesJson, eval.CacheReadTokens, eval.CacheWriteTokens, eval.ReasoningTokens, eval.ConversationTokens, ct);

            // Phase 54 — compute per-iteration prompt-size growth deltas
            // (iteration-over-iteration). Emit one summary event per iter
            // and an amber warning when generator/evaluator inputTokens
            // jumps more than ScoringConstants.PromptBloatGrowthThreshold.
            // Iter 1 has no prior, so deltaInputTokens == inputTokens
            // (operator can read that as "the first sample of the curve").
            var genDeltaIn = gen.InputTokens - priorGenInputTokens;
            var genDeltaOut = gen.OutputTokens - priorGenOutputTokens;
            var evalDeltaIn = eval.InputTokens - priorEvalInputTokens;
            var evalDeltaOut = eval.OutputTokens - priorEvalOutputTokens;
            await this.EmitAsync(request.WorkItemId, "task.iteration.prompt-growth", new
            {
                iter,
                generatorAgentId = request.GeneratorAgentId,
                evaluatorAgentId = pinnedEvaluator,
                generator = new
                {
                    inputTokens = gen.InputTokens,
                    outputTokens = gen.OutputTokens,
                    deltaInputTokens = genDeltaIn,
                    deltaOutputTokens = genDeltaOut,
                    turnSamplesJson = gen.TurnSamplesJson,
                },
                evaluator = new
                {
                    inputTokens = eval.InputTokens,
                    outputTokens = eval.OutputTokens,
                    deltaInputTokens = evalDeltaIn,
                    deltaOutputTokens = evalDeltaOut,
                    turnSamplesJson = eval.TurnSamplesJson,
                },
                threshold = ScoringConstants.PromptBloatGrowthThreshold,
            }, ct);

            // Only fire prompt-bloat for iter >= 2 — the delta on iter 1
            // is the absolute first-call size, which can be legitimately
            // large (decision pins + brief + prior PR diff context).
            // Bloat is iteration-over-iteration GROWTH, not absolute size.
            if (iter >= 2)
            {
                if (genDeltaIn > ScoringConstants.PromptBloatGrowthThreshold)
                {
                    await this.EmitAsync(request.WorkItemId, "task.prompt-bloat", new
                    {
                        iter,
                        role = "generator",
                        agentId = request.GeneratorAgentId,
                        delta = genDeltaIn,
                        threshold = ScoringConstants.PromptBloatGrowthThreshold,
                        inputTokens = gen.InputTokens,
                        priorInputTokens = priorGenInputTokens,
                    }, ct);
                }
                if (evalDeltaIn > ScoringConstants.PromptBloatGrowthThreshold)
                {
                    await this.EmitAsync(request.WorkItemId, "task.prompt-bloat", new
                    {
                        iter,
                        role = "evaluator",
                        agentId = pinnedEvaluator,
                        delta = evalDeltaIn,
                        threshold = ScoringConstants.PromptBloatGrowthThreshold,
                        inputTokens = eval.InputTokens,
                        priorInputTokens = priorEvalInputTokens,
                    }, ct);
                }
            }

            priorGenInputTokens = gen.InputTokens;
            priorGenOutputTokens = gen.OutputTokens;
            priorEvalInputTokens = eval.InputTokens;
            priorEvalOutputTokens = eval.OutputTokens;

            // 2026-05-07 — when the parser falls back to score 0 / iterate,
            // emit a transcript event carrying the exact AssistantText that
            // was parsed (capped at 8 KB). The fallback Notes already names
            // the parse error, but the operator can't reproduce a fix from
            // the row alone — they need to see the literal bytes the parser
            // saw. This event closes the gap between "parse failed" and
            // "what did the model emit" without spamming the row.
            if (lastScore.ParseFailed)
            {
                const int CaptureLimit = 8192;
                var captured = eval.AssistantText ?? string.Empty;
                var truncated = captured.Length > CaptureLimit;
                if (truncated) captured = captured[..CaptureLimit];
                await this.EmitAsync(request.WorkItemId, "task.eval.parse-failed", new
                {
                    iter,
                    parseError = lastScore.ParseError,
                    assistantTextLength = eval.AssistantText?.Length ?? 0,
                    assistantText = captured,
                    truncated,
                    evaluatorAgentId = pinnedEvaluator,
                }, ct);
            }

            totalCost += gen.CostUsd + eval.CostUsd;

            // (c) persist one row per iteration. The summary is the
            // explicit `## Iteration Summary` / `## Change Summary` /
            // `## Plan Summary` section the generator appends per its
            // prompt instructions; falls back to the response tail if the
            // agent forgot the heading.
            var genSummary = GeneratorSummaryExtractor.Extract(gen.OutputText);
            var row = new DeliverableRow
            {
                WorkItemId = request.WorkItemId,
                CrewId = request.CrewId,
                GeneratorAgentId = request.GeneratorAgentId,
                EvaluatorAgentId = pinnedEvaluator,
                Iter = iter,
                Score = lastScore.Score,
                DimsJson = JsonSerializer.Serialize(lastScore.Dims),
                Verdict = lastScore.Verdict,
                Notes = lastScore.Notes,
                GeneratorSummary = genSummary,
                GenCostUsd = gen.CostUsd,
                EvalCostUsd = eval.CostUsd,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            this.db.Deliverables.Add(row);
            await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
            rowIds.Add(row.Id);

            // Phase 65 — capture per-iter commit snapshot. The producer's
            // callback runs in the workspace it owns and knows how to
            // resolve the iter's HEAD + numstat against the prior iter's
            // baseline. Non-null return → UPDATE the just-persisted row
            // with the snapshot so the iter-KPI strip can show a real
            // file count instead of the 0f fallback the event-aggregation
            // path produces when worktree-tick events miss the window.
            // Failures here are non-fatal — the iter row already exists
            // with its evaluator-derived state; missing commit metadata
            // is recoverable (the iter-KPI endpoint falls back to the
            // event aggregation when the row column is null).
            if (request.OnIterationCommit is not null)
            {
                try
                {
                    var snap = await request.OnIterationCommit(iter, row.Id, ct);
                    if (snap is not null)
                    {
                        row.CommitSha = snap.CommitSha;
                        row.CommitMessage = string.IsNullOrEmpty(snap.CommitMessage)
                            ? null
                            : snap.CommitMessage.Length <= 512
                                ? snap.CommitMessage
                                : snap.CommitMessage[..512];
                        row.FilesAdded = snap.FilesAdded;
                        row.FilesModified = snap.FilesModified;
                        row.FilesDeleted = snap.FilesDeleted;
                        row.FilesChanged = snap.FilesChanged;
                        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Emit a transcript event so the iter-KPI strip can
                    // surface "metadata-capture failed" instead of just
                    // showing 0f/—. Non-fatal: the iter row persists and
                    // the loop continues to the gates / next iter.
                    await this.EmitAsync(request.WorkItemId, "task.iter-commit-failed", new
                    {
                        iter,
                        deliverableId = row.Id,
                        error = ex.Message,
                    }, ct);
                }
            }

            // Phase 52 — bump pair-assignment running totals on each
            // iteration so the live UI can read per-pair iters/tokens/cost
            // without recomputing from agent_usage. Cheap (single UPDATE)
            // and decoupled from the agent_usage writes above.
            await this.UpdatePairAssignmentTotalsAsync(
                pairAssignmentId,
                iter,
                gen.InputTokens + eval.InputTokens,
                gen.OutputTokens + eval.OutputTokens,
                gen.CostUsd + eval.CostUsd,
                lastScore.Score,
                ct);

            // Phase 39 (Layer C) — extract structured open questions from the
            // generator's output and persist any new ones. We then read the
            // full unanswered set so the verdict gate below can override
            // pass→iterate. Re-emitted ids in a later iter become new rows so
            // the history is preserved; the wizard dedupes on externalId.
            //
            // Phase 50 fix (2026-05-11) — agents re-emit the same
            // `## Open Questions` block every iter (their prompt template
            // requires it) even after the operator answered the question.
            // Without forwarding the answer onto the fresh row, the dedupe
            // (latest-per-id wins) flips the question back to Answer=null
            // and the loop blocks pass forever → score plateaus 82-86 with
            // iter count climbing meaninglessly. Carry forward the latest
            // non-empty answer for the same ExternalId so the regenerated
            // row stays "answered" until the operator explicitly clears or
            // updates it via the wizard.
            var newQuestions = OpenQuestionsExtractor.Extract(gen.OutputText);
            if (newQuestions.Count > 0)
            {
                var priorAnswers = await this.db.DeliverableQuestions.AsNoTracking()
                    .Where(q => q.WorkItemId == request.WorkItemId
                        && q.Answer != null && q.Answer != string.Empty)
                    .ToListAsync(ct);
                var priorByExtId = priorAnswers
                    .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(),
                        StringComparer.OrdinalIgnoreCase);

                foreach (var q in newQuestions)
                {
                    var inheritedAnswer = priorByExtId.TryGetValue(q.ExternalId, out var prev) ? prev : null;
                    this.db.DeliverableQuestions.Add(new DeliverableQuestionRow
                    {
                        WorkItemId = request.WorkItemId,
                        ExternalId = q.ExternalId,
                        Iter = iter,
                        Text = q.Text,
                        Kind = q.Kind,
                        ChoicesJson = q.Choices is { Count: > 0 } ? JsonSerializer.Serialize(q.Choices) : null,
                        Answer = inheritedAnswer?.Answer,
                        AnsweredAt = inheritedAnswer?.AnsweredAt,
                        AnsweredBy = inheritedAnswer?.AnsweredBy,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
            }

            // Compute "currently unanswered for this work item" as a hard
            // gate on the pass verdict. An externalId is "unanswered" iff
            // NO row for that id has an Answer set — this means a
            // re-emitted question (same id, fresh row with Answer=null
            // because the carry-forward inherited the prior answer onto
            // the new row) won't flip the gate back to blocking. See
            // CountUnansweredQuestionsAsync for the full rationale.
            var unansweredCount = await CountUnansweredQuestionsAsync(this.control, this.db, request.WorkItemId, ct);

            verdict = lastScore.Verdict;

            // Phase 67 — Fast-stop on no-progress score-zero iter. When the
            // iter completed with score==0 AND the per-iter commit
            // snapshot (Phase 65) recorded zero file change on the branch
            // (FilesAdded+FilesModified+FilesDeleted == 0), the agent
            // produced nothing useful. Burning more iters at $9+/iter
            // rebuilding the same context the agent has already shown it
            // can't use is operator-visible waste.
            //
            // Observed 2026-05-15 on code-intelligence:AGENT-MEMORY
            // e2e-scenarios.md iter 3 (12m52 / 600K tokens / $9.81 / zero
            // commits). Phase 61's 3-trailing-zero threshold would have
            // waited two more iters at the same cost each; this gate
            // fires after a single iter.
            //
            // Skipped conservatively when the snapshot fields are null
            // (OnIterationCommit didn't run / threw) — better to risk one
            // wasted iter than wedge the loop on a snapshot config error.
            //
            // Routes through the existing Phase 52 sloppy infrastructure
            // (verdict=sloppy → CrewQueueWorker pending-reassign →
            // SloppyReassignmentReconciler) with reason
            // `no-progress-score-zero` so the UI chip, pair-reassign flow,
            // and PairAttemptCount terminal cap behave identically.
            var snapshotPresent = row.FilesAdded.HasValue
                || row.FilesModified.HasValue
                || row.FilesDeleted.HasValue;
            var fileChangeTotal = (row.FilesAdded ?? 0)
                + (row.FilesModified ?? 0)
                + (row.FilesDeleted ?? 0);
            if (lastScore.Score == 0 && snapshotPresent && fileChangeTotal == 0)
            {
                verdict = "sloppy";
                row.Verdict = verdict;
                var noProgressNote = $"SLOPPY: no-progress score-zero — iter {iter} produced score=0 AND zero file change on branch (snapshot: +{row.FilesAdded ?? 0} ~{row.FilesModified ?? 0} -{row.FilesDeleted ?? 0}). The agent ran but produced no useful output. Reassigning to a fresh pair on the next reconciler tick.";
                row.Notes = string.IsNullOrEmpty(row.Notes) ? noProgressNote : row.Notes + "\n\n" + noProgressNote;
                lastScore = lastScore with { Verdict = verdict, Notes = row.Notes };
                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);

                await this.StampPairAssignmentAsync(
                    pairAssignmentId,
                    AgentPairAssignmentOutcomes.ReassignedSloppy,
                    sloppyReason: SloppyReasons.NoProgressScoreZero,
                    finalScore: lastScore.Score,
                    ct);

                await this.EmitAsync(request.WorkItemId, "task.sloppy", new
                {
                    iter,
                    score = lastScore.Score,
                    reason = SloppyReasons.NoProgressScoreZero,
                    filesAdded = row.FilesAdded,
                    filesModified = row.FilesModified,
                    filesDeleted = row.FilesDeleted,
                    pairAssignmentId,
                }, ct);

                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost, SloppyReasons.NoProgressScoreZero);
            }

            // Phase 50 — checkbox-attention hard gate. The evaluator now
            // emits "What still needs work" as `- [ ] N. <item>` checkboxes,
            // and the generator prompt requires those checkboxes to be
            // echoed back into the next iter's `### Prior feedback
            // resolution` section with each one marked `- [x]` FIXED or
            // `- [x]` DEFERRED. Silently skipping a checkbox is the
            // dominant cause of convergence loops (DefaultAction appeared
            // unaddressed in 41/51 architecture.md iters on
            // qq:TELEGRAM-MESSENGER-S). When the prior iter listed N
            // unchecked items and this iter's generator output has fewer
            // than N `- [x]` markings, demote pass→iterate so the
            // evaluator's next round catches and re-lists the skipped
            // items.
            //
            // priorChain[Count-1] holds iter N-1's eval notes (the chain
            // hasn't been extended with iter N's row yet at this point).
            // For iter 1 there is no prior, so the gate is a no-op.
            if (verdict == "pass" && priorChain.Count > 0)
            {
                var priorEval = priorChain[priorChain.Count - 1].Notes ?? string.Empty;
                var priorUnchecked = CountCheckboxMarkers(priorEval, lookForUnchecked: true);
                if (priorUnchecked > 0)
                {
                    var genChecked = CountCheckboxMarkers(gen.OutputText ?? string.Empty, lookForUnchecked: false);
                    if (genChecked < priorUnchecked)
                    {
                        verdict = "iterate";
                        row.Verdict = verdict;
                        var skipNote = $"BLOCKED: prior iteration's evaluator listed {priorUnchecked} `- [ ]` checkbox item(s); the generator's reply only marked {genChecked} as `- [x]`. Every prior checkbox must be marked FIXED or DEFERRED-with-rationale in the `### Prior feedback resolution` block before pass is allowed. Silently skipping items is the dominant cause of convergence loops — the next iteration must address the remaining {priorUnchecked - genChecked} item(s).";
                        row.Notes = string.IsNullOrEmpty(row.Notes) ? skipNote : row.Notes + "\n\n" + skipNote;
                        lastScore = lastScore with { Verdict = verdict, Notes = row.Notes };
                        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                    }
                }
            }

            // Phase 56 -- pass-validator hook. When the caller wired one,
            // run it BEFORE stamping pair-assignment Passed / work item
            // active / task.iteration.passed. A non-empty / non-whitespace
            // return demotes pass→iterate, appends the returned reason
            // to the deliverable Notes (and via priorChain to the next
            // iter's evaluator chain), and continues the loop. Same
            // demotion mechanism as the unanswered-questions gate above
            // and the checkbox-attention gate immediately above. The
            // callback is also allowed to throw -- exceptions propagate
            // to the caller's catch (which routes the workstream to
            // stalled) after we DEMOTE THE PERSISTED VERDICT TO
            // "iterate" so subsequent Resume / orchestrator-tick calls
            // don't see verdict="pass" (incorrectly routes to
            // kind=resume-delivery, which now skips both gen/eval AND
            // gates — committing broken code) or verdict="stalled"
            // (which StorySpawnOrchestrator.DriveOneWorkstreamImplAsync
            // line 2154 treats as a TERMINAL triage state, refusing to
            // re-enter the gen/eval loop until the operator clicks
            // Retry / Restart). "iterate" is the only verdict where
            // the orchestrator's "fall through to ProduceWorkstreamChangesAsync"
            // branch fires AND DeliverableLoop's seeding (line 348)
            // picks up at iter+1 with the full prior chain. The
            // BuildFailedException catch in DriveImplementAsync still
            // flips ws.Status="stalled" via the StallReason path, so
            // operator action is still required to relaunch -- but the
            // verdict shape now matches the resumable-iter semantic,
            // which is what the operator's Retry button needs to flip
            // ws.Status="active" without also touching the verdict.
            if (verdict == "pass" && request.OnPassValidate is not null)
            {
                string? rejection;
                try
                {
                    rejection = await request.OnPassValidate(iter, ct);
                }
                catch (Exception ex)
                {
                    verdict = "iterate";
                    row.Verdict = verdict;
                    var stallNote = $"GATE EXHAUSTED -- pass rejected, recovery cap hit. {ex.Message}";
                    row.Notes = string.IsNullOrEmpty(row.Notes) ? stallNote : row.Notes + "\n\n" + stallNote;
                    lastScore = lastScore with { Verdict = verdict, Notes = row.Notes };
                    try { await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct); }
                    catch { /* best-effort -- exception we're about to re-throw is more important */ }
                    throw;
                }
                if (!string.IsNullOrWhiteSpace(rejection))
                {
                    verdict = "iterate";
                    row.Verdict = verdict;
                    row.Notes = string.IsNullOrEmpty(row.Notes) ? rejection : row.Notes + "\n\n" + rejection;
                    lastScore = lastScore with { Verdict = verdict, Notes = row.Notes };
                    await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                }
            }

            priorChain.Add((iter, lastScore.Score, verdict, lastScore.Notes));
            notesForNext = FormatPriorChain(priorChain);

            await this.EmitAsync(request.WorkItemId, "task.iteration.scored", new
            {
                iter,
                score = lastScore.Score,
                verdict,
                dims = lastScore.Dims,
                generatorCostUsd = gen.CostUsd,
                evaluatorCostUsd = eval.CostUsd,
                totalCostUsd = totalCost,
                unansweredQuestions = unansweredCount,
            }, ct);

            // (e) terminal pass → mark active (delivery phase will set
            //     "resolved" once branch is pushed and PR is created).
            if (verdict == "pass")
            {
                await TrySetWorkItemStateAsync(this.control, this.db, request.WorkItemId, "active", ct);
                // Phase 52 — stamp pair-assignment as passed. The audit row
                // is now closed; the next claim against this work item (if
                // any) opens a fresh sequence.
                await this.StampPairAssignmentAsync(
                    pairAssignmentId,
                    AgentPairAssignmentOutcomes.Passed,
                    sloppyReason: null,
                    finalScore: lastScore.Score,
                    ct);
                await this.EmitAsync(request.WorkItemId, "task.iteration.passed", new { iter, score = lastScore.Score }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

            // (f) terminal reject → bounce back to lead, exit.
            if (verdict == "reject")
            {
                await TrySetWorkItemStateAsync(this.control, this.db, request.WorkItemId, "proposed", ct);
                // Phase 52 — close the pair-assignment as cancelled (no
                // reassignment; lead must triage). The audit row reflects
                // the pair's final score for the cost-vs-quality readout.
                await this.StampPairAssignmentAsync(
                    pairAssignmentId,
                    AgentPairAssignmentOutcomes.Cancelled,
                    sloppyReason: null,
                    finalScore: lastScore.Score,
                    ct);
                await this.EmitAsync(request.WorkItemId, "task.iteration.rejected", new { iter, score = lastScore.Score }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

            // budget cap (per-task) — stop early as 'stalled-budget'.
            if (request.MaxTaskBudgetUsd is decimal cap && totalCost >= cap)
            {
                verdict = "stalled-budget";
                row.Verdict = verdict;
                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                await this.EmitAsync(request.WorkItemId, "task.budget-warning", new { iter, totalCostUsd = totalCost, cap }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

            // Phase 50 — convergence detector. When the same topics show
            // up in the evaluator's "What still needs work" list across N
            // consecutive iters, the loop is going in circles (observed
            // on qq:TELEGRAM-MESSENGER-S architecture.md: DefaultAction
            // re-litigated in 41/51 iters; P95-latency in 46/51).
            // Continuing burns budget chasing a sibling-doc moving target
            // the agent can't resolve unilaterally. Exit with
            // `stalled-no-convergence` so the operator can decision-pin
            // or restart with smaller scope.
            //
            // We require at least 4 prior iters worth of chain before
            // running the check — fewer than that and the topic-overlap
            // signal is too noisy (a single repeated section ref between
            // iter 1 and iter 2 is normal).
            if (priorChain.Count >= 4)
            {
                var (looping, recurringTopics) = DetectConvergenceLoop(priorChain);
                if (looping)
                {
                    verdict = "stalled-no-convergence";
                    row.Verdict = verdict;
                    var loopNote = "STALLED: convergence loop detected — the same topics ("
                        + string.Join(", ", recurringTopics.Take(8))
                        + ") have appeared in the evaluator's `What still needs work` list across the last "
                        + Math.Min(priorChain.Count, 5)
                        + " iterations. The agent cannot resolve these unilaterally — operator should either pin a decision via the spawn's decision-pins endpoint, restart the loop with smaller scope, or accept the doc at its current score.";
                    row.Notes = string.IsNullOrEmpty(row.Notes) ? loopNote : row.Notes + "\n\n" + loopNote;
                    await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                    // Phase 52 — close the pair-assignment as cancelled.
                    // stalled-no-convergence is a cross-doc decision-pin
                    // signal, not a sloppy pair signal; operator action is
                    // required, no auto-reassign.
                    await this.StampPairAssignmentAsync(
                        pairAssignmentId,
                        AgentPairAssignmentOutcomes.Cancelled,
                        sloppyReason: null,
                        finalScore: lastScore.Score,
                        ct);
                    await this.EmitAsync(request.WorkItemId, "task.iteration.stalled", new
                    {
                        iter,
                        score = lastScore.Score,
                        reason = "no-convergence",
                        recurringTopics,
                        priorIters = priorChain.Count,
                    }, ct);
                    return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
                }
            }

            // Phase 52 — sloppy detection. Runs after all the existing
            // terminal gates (pass / reject / stalled-budget / no-convergence)
            // so we only evaluate it when verdict == "iterate". Two
            // independent triggers — whichever fires first wins:
            //   (a) Q52.1 score plateau against personal best.
            //   (b) Q52.2 generator's cumulative input+output tokens since
            //       WorkItemRow.BudgetWindowStartUtc exceed
            //       ScoringConstants.SloppyGeneratorTokenCap.
            // The detector skips when priorChain.Count < 4 (Q52.1 needs at
            // least 4 iters to evaluate the plateau condition); the token
            // cap is checked independently and can fire from iter 1 if the
            // generator goes wild with context.
            //
            // On sloppy, we rewrite the just-persisted deliverable's verdict
            // to "sloppy" (the verdict-routing in CrewQueueWorker pivots on
            // this), stamp the pair-assignment row with the trigger reason,
            // park the work item in pending-reassign so the
            // SloppyReassignmentReconciler picks it up next tick, and
            // return. Worktree / transcript / deliverables wipe happens in
            // the reconciler, not here — keeps the loop's side effects
            // localized.
            var scoresInOrder = priorChain.Select(c => c.Score).ToList();
            var sloppy = await SloppyDetector.CheckAsync(
                this.control, this.db,
                request.WorkItemId,
                request.GeneratorAgentId,
                budgetWindowStartUtc,
                scoresInOrder,
                ct);
            if (sloppy.IsSloppy)
            {
                verdict = "sloppy";
                row.Verdict = verdict;
                var reasonText = sloppy.Reason switch
                {
                    SloppyReasons.TokenCap =>
                        $"SLOPPY: generator-token cap exceeded — used {sloppy.TotalTokens:N0} input+output tokens against a {ScoringConstants.SloppyGeneratorTokenCap:N0}-token budget since the budget window opened. Reassigning to a fresh pair on the next reconciler tick.",
                    SloppyReasons.ScorePlateau =>
                        $"SLOPPY: score plateau detected — the trailing 3 iterations (scores {string.Join(", ", scoresInOrder.TakeLast(3))}) did not surpass the personal best of {sloppy.BestScore} set at iter {sloppy.BestIter}. Reassigning to a fresh pair on the next reconciler tick.",
                    SloppyReasons.ConsecutiveScoreZero =>
                        $"SLOPPY: {ScoringConstants.SloppyConsecutiveZeroThreshold} consecutive score-0 iterations — the trailing scores {{{string.Join(", ", scoresInOrder.TakeLast(ScoringConstants.SloppyConsecutiveZeroThreshold))}}} indicate the generator could not produce a passing artifact under its turn/context budget. Reassigning to a fresh pair on the next reconciler tick.",
                    _ => "SLOPPY: reassigning to a fresh pair on the next reconciler tick.",
                };
                row.Notes = string.IsNullOrEmpty(row.Notes) ? reasonText : row.Notes + "\n\n" + reasonText;
                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);

                await this.StampPairAssignmentAsync(
                    pairAssignmentId,
                    AgentPairAssignmentOutcomes.ReassignedSloppy,
                    sloppyReason: sloppy.Reason,
                    finalScore: lastScore.Score,
                    ct);

                // Park the work item for the reconciler. We deliberately do
                // NOT set ExecutionStatus directly here — the CrewQueueWorker
                // routes verdict=sloppy → ExecutionStatus=pending-reassign
                // in the same epilogue path that handles stalled-budget →
                // cooldown-budget (Phase 51). Keeps the verdict-to-status
                // mapping in one place.

                await this.EmitAsync(request.WorkItemId, "task.sloppy", new
                {
                    iter,
                    score = lastScore.Score,
                    reason = sloppy.Reason,
                    bestIter = sloppy.BestIter,
                    bestScore = sloppy.BestScore,
                    totalTokens = sloppy.TotalTokens,
                    tokenCap = ScoringConstants.SloppyGeneratorTokenCap,
                    pairAssignmentId,
                }, ct);

                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost, sloppy.Reason);
            }

            // verdict == "iterate" → loop re-enters
            await this.EmitAsync(request.WorkItemId, "task.iteration.iterated", new { iter, score = lastScore.Score, notes = lastScore.Notes }, ct);
        }

        // Cap hit without pass. Phase 52 — convert the legacy "stalled"
        // verdict to "sloppy" with reason=iter-cap so the reconciler picks
        // it up like any other sloppy trigger. Slow-progress stalls now
        // feed the pair-attempt cap (Q52.3 / Q52.5).
        verdict = "sloppy";
        var lastId = rowIds[rowIds.Count - 1];
        var lastRow = await this.db.Deliverables.FirstAsync(d => d.Id == lastId, ct);
        lastRow.Verdict = verdict;
        var iterCapNote = $"SLOPPY: iteration cap reached — {iter} iters consumed without a pass (best score {priorChain.Max(c => c.Score)}). Reassigning to a fresh pair on the next reconciler tick.";
        lastRow.Notes = string.IsNullOrEmpty(lastRow.Notes) ? iterCapNote : lastRow.Notes + "\n\n" + iterCapNote;
        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);

        await this.StampPairAssignmentAsync(
            pairAssignmentId,
            AgentPairAssignmentOutcomes.ReassignedSloppy,
            sloppyReason: SloppyReasons.IterCap,
            finalScore: lastScore?.Score ?? 0,
            ct);

        await this.EmitAsync(request.WorkItemId, "task.sloppy", new
        {
            iter,
            score = lastScore?.Score ?? 0,
            reason = SloppyReasons.IterCap,
            pairAssignmentId,
        }, ct);

        return new DeliverableLoopResult(verdict, iter, lastScore?.Score ?? 0, rowIds, totalCost, SloppyReasons.IterCap);
    }

    /// <summary>
    /// Phase 39 (Layer C) — render every answered question for a work item
    /// as a `## Operator answers` block prepended to the next iteration's
    /// generator prompt. Dedupes by ExternalId taking the most-recent
    /// answered row so a re-answered question carries the latest value.
    /// Returns empty string when no answers exist (caller skips the
    /// prepend).
    /// </summary>
    public static async Task<string> BuildOperatorAnswersBlockAsync(
        ControlDbContext control, ProductDbContext db, string workItemId, CancellationToken ct)
    {
        var rows = await db.DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == workItemId && q.Answer != null && q.Answer != string.Empty)
            .ToListAsync(ct);
        if (rows.Count == 0) return string.Empty;
        var latestPerId = rows
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First())
            .OrderBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Operator answers (apply these in this iteration)");
        foreach (var q in latestPerId)
        {
            sb.AppendLine();
            sb.AppendLine($"- **{q.ExternalId}**: {q.Text}");
            sb.AppendLine($"  → {q.Answer}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 39 (Layer C) — count <see cref="DeliverableQuestionRow"/> ids
    /// that are still unanswered for a work item.
    ///
    /// <para>Phase 50 fix (2026-05-11) — semantic change: an externalId is
    /// "unanswered" iff <em>no</em> row for that id has a non-empty
    /// <see cref="DeliverableQuestionRow.Answer"/>. Previously the count
    /// took the latest-per-id and treated re-emissions as fresh unanswered
    /// questions, but agents re-emit the same `## Open Questions` block
    /// every iter (their prompt template requires it) even after the
    /// operator answered. The old logic flipped answered questions back
    /// to unanswered on every iter, hard-demoting `verdict=pass` →
    /// `iterate` and causing the loop to plateau (observed
    /// qq:TELEGRAM-MESSENGER-S iter 17 score 86, never reaching the 90
    /// pass threshold). The new behaviour: once a question is answered,
    /// it stays answered until the operator explicitly clears the answer
    /// via the wizard.</para>
    /// </summary>
    public static async Task<int> CountUnansweredQuestionsAsync(
        ControlDbContext control, ProductDbContext db, string workItemId, CancellationToken ct)
    {
        // SQLite can't ORDER BY DateTimeOffset; pull rows for this work
        // item, group + dedupe in memory.
        var rows = await db.DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == workItemId)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        // An externalId is "answered" if ANY row for that id has a
        // non-empty Answer. Re-emitted rows in later iters with Answer=null
        // do NOT flip the id back to unanswered.
        var unanswered = rows
            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(q => string.IsNullOrEmpty(q.Answer)))
            .Count();
        return unanswered;
    }

    /// <summary>
    /// Phase 50 — count markdown checkbox markers in a free-form text
    /// block. <paramref name="lookForUnchecked"/>=true counts <c>- [ ]</c> /
    /// <c>* [ ]</c> entries (unaddressed evaluator items); false counts
    /// <c>- [x]</c> / <c>* [x]</c> / <c>- [X]</c> / <c>* [X]</c>
    /// (generator-marked FIXED or DEFERRED items).
    ///
    /// <para>Used by the checkbox-attention hard gate
    /// (<c>RunAsync</c>) to demote pass→iterate when the generator's
    /// output marks fewer items as resolved than the prior evaluator
    /// listed as unresolved.</para>
    ///
    /// <para>Phase 69 (2026-05-15) — when counting UNCHECKED, exclude
    /// markers whose item text is a semantically-empty "zero items
    /// remain" placeholder (e.g. <c>- [ ] 1. None.</c>,
    /// <c>- [ ] N/A</c>, <c>- [ ] —</c>, <c>- [ ]</c> with empty body).
    /// Without this guard the checkbox-attention gate spins forever:
    /// the evaluator writes "Still needs improvement: - [ ] 1. None." to
    /// mean "nothing left", the regex counts it as 1 unfulfilled item,
    /// the next iter's generator has nothing to FIX so produces 0
    /// <c>- [x]</c> markings, the gate demotes pass→iterate, repeat.
    /// Observed 2026-05-15 on
    /// <c>failover-cluster:XRAFT:stage-4.1-grpc-transport-layer</c> iter 6
    /// at score 98 ($1367.98 burned across 6 iters).</para>
    /// </summary>
    internal static int CountCheckboxMarkers(string text, bool lookForUnchecked)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (!lookForUnchecked)
        {
            // Checked counting is unchanged — every `- [x]` is a real
            // claim of "I fixed (or deferred) this".
            const string checkedPattern = @"(?:^|\n)\s*[-*]\s+\[[xX]\]";
            return System.Text.RegularExpressions.Regex.Matches(text, checkedPattern).Count;
        }

        // Unchecked counting (Phase 69): capture each `- [ ]` line's
        // trailing text and skip the ones that mean "no items remain".
        // We grab up to the next newline (or end-of-string) so a
        // multi-line item still gets its first-line body inspected.
        const string uncheckedWithBody =
            @"(?:^|\n)\s*[-*]\s+\[ \](?<body>[^\n]*)";
        var matches = System.Text.RegularExpressions.Regex.Matches(text, uncheckedWithBody);
        var count = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var body = m.Groups["body"].Value ?? string.Empty;
            if (!IsSemanticallyEmptyCheckboxBody(body)) count++;
        }
        return count;
    }

    /// <summary>
    /// Phase 69 — true when the body following a <c>- [ ]</c> marker
    /// represents "no items remain" rather than an actual unaddressed
    /// item. Strips numbering prefixes (<c>1.</c>, <c>1)</c>), surrounding
    /// punctuation, and normalises to lowercase before matching against
    /// the closed-set placeholder list.
    ///
    /// <para>Matched placeholders (case-insensitive, with optional
    /// trailing punctuation): empty string, <c>none</c>, <c>n/a</c>,
    /// <c>na</c>, <c>—</c> / <c>–</c> / <c>-</c>, <c>(none)</c>,
    /// <c>nothing</c>, <c>no items</c>, <c>no outstanding items</c>.
    /// </para>
    /// </summary>
    internal static bool IsSemanticallyEmptyCheckboxBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return true;
        var s = body.Trim();
        // Strip a numbering prefix: "1. ", "1) ", "1: ".
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"^\d+\s*[\.\):]\s*", string.Empty).Trim();
        // Strip trailing punctuation.
        s = s.TrimEnd('.', ',', ';', ':', '!', '—', '–', '-', '(', ')');
        // Strip surrounding parens / quotes.
        s = s.Trim('"', '\'', '(', ')', '[', ']');
        s = s.Trim();
        if (s.Length == 0) return true;
        var lower = s.ToLowerInvariant();
        return lower is "none"
            or "n/a"
            or "na"
            or "—"
            or "–"
            or "-"
            or "nothing"
            or "no items"
            or "no outstanding items"
            or "no remaining items"
            or "no items remain";
    }

    /// <summary>
    /// Phase 50 — item-text convergence detector. Returns
    /// <c>(looping=true, recurringTopics)</c> when the evaluator's
    /// `What still needs work` checkboxes from the latest iter have
    /// ≥ 55 % Jaccard token overlap with items from at least 2 of the
    /// last 3 prior iters — i.e. the same ISSUE keeps appearing across
    /// iters, not just the same section ref.
    ///
    /// <para>Each evaluator item is extracted as a single normalized
    /// string via <see cref="ExtractCheckboxItems"/>, tokenized via
    /// <see cref="TokenizeItem"/> (lowercase, stopwords dropped,
    /// section-ref tokens stripped so different issues inside §3.1 don't
    /// false-match), and compared via <see cref="JaccardSimilarity"/>.</para>
    ///
    /// <para>Threshold: an item is "recurring" when it has Jaccard
    /// similarity ≥ 0.55 (≥ 55 % token overlap, ignoring stopwords and
    /// section refs) with at least one item from each of at least 2 of
    /// the previous 3 iters in the trailing window. The loop is
    /// "looping" when ≥ 2 distinct items from the latest iter are
    /// recurring — a single recurring item might be a genuinely-stuck
    /// issue that the next iter could resolve; two or more is a pattern
    /// the agent can't fix unilaterally.</para>
    ///
    /// <para>Phase 50 follow-up (2026-05-11) — the original token-based
    /// detector produced false positives by counting section refs
    /// (§3.1, §4.6) as recurring topics when each iter's specific issue
    /// inside those sections differed. Observed misfire on
    /// qq:TELEGRAM-MESSENGER-S iter 56: the agent correctly fixed 3 of
    /// 4 prior items but the section-ref noise tripped the detector.
    /// The new item-text Jaccard detector only fires when the LITERAL
    /// item descriptions overlap significantly across iters — i.e. when
    /// the SAME ISSUE keeps appearing, not just the same section being
    /// touched.</para>
    /// </summary>
    internal static (bool looping, IReadOnlyList<string> recurringTopics) DetectConvergenceLoop(
        IReadOnlyList<(int Iter, int Score, string Verdict, string Notes)> chain)
    {
        const int WindowSize = 4;
        const int MinRecurringItems = 2;
        const int MinPriorIterMatches = 2;
        const double SimilarityThreshold = 0.55;
        const int MinTokensPerItem = 4;

        if (chain.Count < 3) return (false, Array.Empty<string>());

        var window = chain.Skip(Math.Max(0, chain.Count - WindowSize)).ToList();

        // Extract the per-iter "items" — one normalized string per
        // checkbox / numbered bullet under the evaluator's "What still
        // needs work" or "Still needs improvement" section.
        var perIterItems = window
            .Select(c => ExtractCheckboxItems(c.Notes ?? string.Empty))
            .ToList();

        var latestItems = perIterItems[perIterItems.Count - 1];
        if (latestItems.Count == 0) return (false, Array.Empty<string>());

        // Pre-tokenize every item once.
        var latestTokens = latestItems.Select(TokenizeItem).ToList();
        var priorIterTokens = perIterItems
            .Take(perIterItems.Count - 1)
            .Select(items => items.Select(TokenizeItem).ToList())
            .ToList();

        var recurring = new List<string>();
        for (var i = 0; i < latestItems.Count; i++)
        {
            var li = latestTokens[i];
            if (li.Count < MinTokensPerItem) continue;

            var priorIterMatches = 0;
            foreach (var priorItems in priorIterTokens)
            {
                var matchedThisIter = false;
                foreach (var pi in priorItems)
                {
                    if (pi.Count < MinTokensPerItem) continue;
                    if (JaccardSimilarity(li, pi) >= SimilarityThreshold)
                    {
                        matchedThisIter = true;
                        break;
                    }
                }
                if (matchedThisIter) priorIterMatches++;
            }

            if (priorIterMatches >= MinPriorIterMatches)
            {
                var summary = latestItems[i];
                if (summary.Length > 100) summary = summary[..97] + "...";
                recurring.Add(summary);
            }
        }

        var looping = recurring.Count >= MinRecurringItems;
        return (looping, recurring);
    }

    /// <summary>
    /// Extract one normalized string per checkbox / numbered / bulleted
    /// item under the evaluator's "What still needs work" /
    /// "Still needs improvement" section. Handles all three formats so
    /// legacy iters without the Phase 50 checkbox format still produce
    /// useful items.
    /// </summary>
    private static IReadOnlyList<string> ExtractCheckboxItems(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return Array.Empty<string>();

        // Find the "What still needs work" / "Still needs improvement"
        // section. Stop at the next H2/H3 heading or the "Why this score"
        // section.
        var sectionMatch = System.Text.RegularExpressions.Regex.Match(
            notes,
            @"(?ix)
              (?:what\s+still\s+needs\s+work|still\s+needs\s+improvement)
              [\s:*]*\n+
              (?<body>.+?)
              (?=
                \n\s*(?:\#{1,4}\s|\*{1,2}\s*why\s+this\s+score)
                | \Z
              )",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var body = sectionMatch.Success ? sectionMatch.Groups["body"].Value : notes;

        var items = new List<string>();
        // Capture each bullet/numbered/checkbox item, multi-line, until
        // the next bullet marker or a blank line.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                body,
                @"(?m)^\s*(?:[-*]\s+(?:\[[\sxX]\]\s+)?|\d+\.\s+)(?<text>.+(?:\n(?!\s*(?:[-*]\s|\d+\.\s|$)).+)*)"))
        {
            var text = m.Groups["text"].Value;
            // Strip leading numbering / checkbox markers that survived
            // the bullet matcher, collapse whitespace.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*\d+\.\s+", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length >= 25) items.Add(text);
        }

        return items;
    }

    /// <summary>
    /// Normalise an item to a token set: lowercase, drop punctuation,
    /// drop stopwords and section-ref tokens (§N.M-style), drop tokens
    /// shorter than 4 chars. The resulting set is what Jaccard
    /// similarity compares across iters.
    /// </summary>
    private static HashSet<string> TokenizeItem(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;

        // Strip section markers — they're recurring noise. Different
        // issues inside §3.1 shouldn't match just because both mention §3.1.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?ix)
              (?:§|\bsection\s+)\s*\d+(?:\.\d+)*
              | \bline\s+\d+
              | \bstage\s+\d+(?:\.\d+)?",
            " ");

        foreach (var raw in System.Text.RegularExpressions.Regex.Split(
            cleaned.ToLowerInvariant(),
            @"[^a-z0-9_.\-]+"))
        {
            if (raw.Length < 4) continue;
            if (Stopwords.Contains(raw)) continue;
            // Strip trailing punctuation/dots that survived the splitter.
            var tok = raw.Trim('.', '-', '_');
            if (tok.Length < 4) continue;
            result.Add(tok);
        }
        return result;
    }

    /// <summary>Jaccard set similarity: |A ∩ B| / |A ∪ B|.</summary>
    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersect = 0;
        foreach (var token in a)
        {
            if (b.Contains(token)) intersect++;
        }
        var union = a.Count + b.Count - intersect;
        return union == 0 ? 0 : (double)intersect / union;
    }

    /// <summary>
    /// English stopwords plus doc-template noise that shows up in nearly
    /// every evaluator note (e.g. "implementation", "section",
    /// "architecture", "document"). Kept short — common-word subtraction
    /// is what makes the Jaccard score discriminate on issue-specific
    /// terms rather than boilerplate framing.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "this", "that", "with", "from", "have", "has", "had", "for", "and", "but", "not",
        "are", "was", "were", "been", "being", "into", "onto", "out", "over", "under",
        "still", "needs", "work", "what", "improvement", "should", "would", "could", "must",
        "either", "while", "before", "after", "current", "currently", "currently",
        "doc", "docs", "document", "documents", "implementation", "plan", "tech", "spec",
        "scenarios", "architecture", "section", "sentence", "field", "table", "block",
        "describes", "described", "describes", "states", "says", "say", "saying",
        "between", "across", "without", "consistent", "alignment", "aligned",
        "according", "carries", "carrying", "leaves", "leave", "lacks", "lack",
    };

    /// <summary>
    /// Phase 50 legacy — kept for the topic-token extraction used by
    /// older callers; the convergence detector itself now uses
    /// <see cref="ExtractCheckboxItems"/> + <see cref="JaccardSimilarity"/>.
    /// </summary>
    /// Extract "topic tokens" from an evaluator notes string. Targets:
    /// <list type="bullet">
    ///   <item>Section refs: <c>§3.1</c>, <c>§10.4</c>, <c>Section 5.5</c>,
    ///         <c>5.5</c> bare (when preceded by section/§).</item>
    ///   <item>Dotted symbols: <c>telegram.send.latency_ms</c>,
    ///         <c>AgentQuestion.DefaultAction</c>.</item>
    ///   <item>CamelCase / PascalCase identifiers: <c>DefaultAction</c>,
    ///         <c>OperatorBinding</c>.</item>
    ///   <item>Slash commands / paths: <c>/handoff</c>, <c>/approve</c>.</item>
    /// </list>
    /// Short tokens (&lt; 4 chars) and stopwords are dropped.
    /// </summary>
    private static HashSet<string> ExtractTopicTokens(string? notes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(notes)) return result;

        // Section markers — capture the number for cross-iter equality.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"§\s*([0-9]+(?:\.[0-9]+)*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            result.Add($"§{m.Groups[1].Value}");
        }
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"\bSection\s+([0-9]+(?:\.[0-9]+)*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            result.Add($"§{m.Groups[1].Value}");
        }

        // Slash commands.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"(?<![A-Za-z])/[a-z][a-z0-9_-]{2,30}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            result.Add(m.Value.ToLowerInvariant());
        }

        // Dotted symbols — at least one dot, leading letter, terms
        // separated by dots, underscores, or dashes.
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"\b[A-Za-z][A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+){1,5}\b"))
        {
            if (m.Value.Length >= 6) result.Add(m.Value);
        }

        // CamelCase / PascalCase identifiers: at least one inner
        // uppercase after a lowercase, length ≥ 5 to drop noise like
        // "API" or "Bar".
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                notes,
                @"\b[A-Z][a-z]+(?:[A-Z][a-z]+){1,5}\b"))
        {
            if (m.Value.Length >= 5) result.Add(m.Value);
        }

        return result;
    }

    /// <summary>
    /// Best-effort state flip on the linked work item. Tolerates missing
    /// rows (the loop should never crash because of a stale id); existing
    /// state-machine constraints (e.g. closed → closed is a no-op) are
    /// respected by the regular <c>WorkItemRow</c> writes.
    /// </summary>
    private static async Task TrySetWorkItemStateAsync(ControlDbContext control, ProductDbContext db, string workItemId, string newState, CancellationToken ct)
    {
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null) return;
        if (string.Equals(wi.State, newState, StringComparison.OrdinalIgnoreCase)) return;
        wi.State = newState;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 52 — ensure exactly one in-progress
    /// <see cref="AgentPairAssignmentRow"/> exists for the
    /// <c>(workItemId, generator, evaluator)</c> tuple. Reuses the existing
    /// row when the pair matches; opens a new row (with the next
    /// <see cref="AgentPairAssignmentRow.SequenceNo"/>) when no in-progress
    /// row exists, OR when an in-progress row exists with a DIFFERENT pair
    /// (Phase 51 cooldown rotation or operator manual-assign already
    /// switched pairs without going through Phase 52 — close the old row
    /// as cancelled and open a fresh one).
    /// </summary>
    private async Task<long> EnsureActivePairAssignmentAsync(
        string workItemId,
        string generatorAgentId,
        string evaluatorAgentId,
        string crewId,
        CancellationToken ct)
    {
        var existing = await this.db.AgentPairAssignments
            .Where(p => p.WorkItemId == workItemId
                && p.Outcome == AgentPairAssignmentOutcomes.InProgress)
            .OrderByDescending(p => p.SequenceNo)
            .FirstOrDefaultAsync(ct);

        if (existing is not null
            && string.Equals(existing.GeneratorAgentId, generatorAgentId, StringComparison.Ordinal)
            && string.Equals(existing.EvaluatorAgentId, evaluatorAgentId, StringComparison.Ordinal))
        {
            return existing.Id;
        }

        // Pair changed under us (Phase 51 cooldown rotation, restart, etc).
        // Close the orphaned row as cancelled so the audit trail accurately
        // records "the old pair stopped working this row, even though they
        // didn't formally pass/reject/sloppy". A cancelled row counts in
        // crew salary but NOT in Phase 14.7 demotion math.
        if (existing is not null)
        {
            existing.Outcome = AgentPairAssignmentOutcomes.Cancelled;
            existing.EndedAt = DateTimeOffset.UtcNow;
            await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
        }

        var maxSeq = await this.db.AgentPairAssignments
            .Where(p => p.WorkItemId == workItemId)
            .Select(p => (int?)p.SequenceNo)
            .MaxAsync(ct) ?? 0;

        var newRow = new AgentPairAssignmentRow
        {
            WorkItemId = workItemId,
            SequenceNo = maxSeq + 1,
            GeneratorAgentId = generatorAgentId,
            EvaluatorAgentId = evaluatorAgentId,
            CrewId = crewId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = AgentPairAssignmentOutcomes.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this.db.AgentPairAssignments.Add(newRow);
        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
        return newRow.Id;
    }

    /// <summary>
    /// Phase 52 — bump running totals on the in-progress pair-assignment
    /// row after one iteration's evaluator scoring completes. Cheap UPDATE
    /// keyed on the row id from <see cref="EnsureActivePairAssignmentAsync"/>;
    /// no-op if the row was deleted out from under us (defensive — the
    /// reconciler might race with a manual cleanup).
    /// </summary>
    private async Task UpdatePairAssignmentTotalsAsync(
        long pairAssignmentId,
        int iter,
        long iterInputTokens,
        long iterOutputTokens,
        decimal iterCostUsd,
        int latestScore,
        CancellationToken ct)
    {
        var row = await this.db.AgentPairAssignments
            .FirstOrDefaultAsync(p => p.Id == pairAssignmentId, ct);
        if (row is null) return;
        row.TotalIters = iter;
        row.TotalInputTokens += iterInputTokens;
        row.TotalOutputTokens += iterOutputTokens;
        row.TotalCostUsd += iterCostUsd;
        row.FinalScore = latestScore; // running tip; final value persists when stamped
        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 52 — close the in-progress pair-assignment row with a terminal
    /// outcome. Stamps <c>EndedAt</c>, <c>Outcome</c>,
    /// optional <c>SloppyReason</c>, and the final score. No-op when the row
    /// is missing.
    /// </summary>
    private async Task StampPairAssignmentAsync(
        long pairAssignmentId,
        string outcome,
        string? sloppyReason,
        int finalScore,
        CancellationToken ct)
    {
        var row = await this.db.AgentPairAssignments
            .FirstOrDefaultAsync(p => p.Id == pairAssignmentId, ct);
        if (row is null) return;
        row.Outcome = outcome;
        row.SloppyReason = sloppyReason;
        row.FinalScore = finalScore;
        row.EndedAt = DateTimeOffset.UtcNow;
        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 16.4 — split-cost ledger writer. Records one
    /// <see cref="AgentUsageRow"/> per generator/evaluator turn so the
    /// per-pair salary is reportable separately. <c>category</c> is one
    /// of <c>generator | evaluator | judge</c>.
    /// Phase 54 — <paramref name="turnSamplesJson"/> carries the optional
    /// per-<c>AssistantUsageEvent</c> samples produced by the delegate.
    /// 2026-05-16 Phase 72 — cache / reasoning / conversation token
    /// telemetry (nullable; null when the runner / backend didn't
    /// report it).
    /// </summary>
    private async Task WriteUsageAsync(
        string workItemId,
        string? agentId,
        string crewId,
        string category,
        decimal costUsd,
        long inputTokens,
        long outputTokens,
        string? turnSamplesJson,
        long? cacheReadTokens,
        long? cacheWriteTokens,
        long? reasoningTokens,
        long? conversationTokens,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        var now = DateTimeOffset.UtcNow;
        this.control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = agentId,
            CrewId = crewId,
            TaskId = workItemId,
            Model = string.Empty,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostUsd = costUsd,
            StartedAt = now,
            EndedAt = now,
            Status = "finished",
            CostCategory = category,
            TurnSamplesJson = turnSamplesJson,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            ReasoningTokens = reasoningTokens,
            ConversationTokens = conversationTokens,
        });
        await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
    }

    private async Task EmitAsync(string workItemId, string kind, object payload, CancellationToken ct)
    {
        if (this.channel is null) return;
        await this.channel.EmitAsync(workItemId, kind, payload, ct: ct);
    }

    /// <summary>
    /// Render the running chain of prior (iter, score, verdict, notes) into
    /// the block the generator gets at the top of its next prompt. The
    /// generator delegate splices this in under "Evaluator feedback from
    /// iteration N-1" — the variable name there is now misleading (it's the
    /// whole chain, not just N-1) but the rendered output makes the
    /// trajectory explicit so the model can see "I've been told this same
    /// thing three rounds in a row" instead of treating each round as
    /// independent.
    /// </summary>
    private static string FormatPriorChain(IReadOnlyList<(int Iter, int Score, string Verdict, string Notes)> chain)
    {
        if (chain.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();

        // Show the LATEST iteration's feedback prominently at the top.
        var latest = chain[chain.Count - 1];
        sb.AppendLine($"## LATEST evaluator feedback (iteration {latest.Iter}, score {latest.Score}, verdict: {latest.Verdict})");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(latest.Notes) ? "(no notes)" : latest.Notes);
        sb.AppendLine();

        // Phase 50 (2026-05-11) — explicit instruction to treat the
        // `## What still needs work` numbered items as a checklist.
        // Score-regression observed on qq:TELEGRAM-MESSENGER-S iter 43
        // (88 → 85): the agent fixed the symptom of iter 42's comments
        // locally but introduced new structural bugs (MessengerMessage
        // inserted before OperatorBinding table; AlertType renamed in
        // §4.6 but missed in §5.6's example). The fix-one-place-break-
        // another pattern needs an explicit acknowledgement step in the
        // next iter so the agent can't drift away from prior critique.
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("**REQUIRED — Prior-feedback resolution checklist.** In your next iteration's `## Iteration Summary` / `## Change Summary`, add a `### Prior feedback resolution` subsection that mirrors EACH numbered item from the `## What still needs work` list above and marks it:");
        sb.AppendLine();
        sb.AppendLine("- `1. ADDRESSED — <one-line description of the fix + section/file ref>`");
        sb.AppendLine("- `2. DEFERRED — <reason + which iter/workstream will handle it>`");
        sb.AppendLine();
        sb.AppendLine("Silently skipping items is the most common cause of score regression. If you fixed three of five points, the remaining two MUST appear as `DEFERRED` with a rationale.");
        sb.AppendLine();

        // If there's more than one prior iteration, show a condensed summary
        // of older ones so the generator sees the pattern without drowning
        // in repetitive text.
        if (chain.Count > 1)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"### Iteration history ({chain.Count} total attempts)");
            sb.AppendLine();

            // Show second-most-recent in full (useful for diff vs latest),
            // older ones condensed to one line.
            for (var idx = 0; idx < chain.Count - 1; idx++)
            {
                var (i, s, v, n) = chain[idx];
                if (idx == chain.Count - 2)
                {
                    sb.AppendLine($"**Iteration {i}** — score {s} ({v})");
                    sb.AppendLine(string.IsNullOrWhiteSpace(n) ? "(no notes)" : n);
                }
                else
                {
                    var oneLiner = string.IsNullOrWhiteSpace(n) ? "(no notes)" : n.Split('\n')[0];
                    if (oneLiner.Length > 120) oneLiner = oneLiner[..117] + "...";
                    sb.AppendLine($"- Iter {i}: score {s} ({v}) — {oneLiner}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("⚠ If you see the SAME critique repeated across iterations, your prior approach did NOT work. You MUST try a fundamentally different strategy — do not repeat the same edit.");
        return sb.ToString();
    }
}
