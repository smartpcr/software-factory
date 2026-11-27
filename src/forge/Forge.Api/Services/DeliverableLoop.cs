// -----------------------------------------------------------------------
// <copyright file="DeliverableLoop.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Common.Chat;
using Forge.Api.Services.Chat;
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
    string? SessionId = null,
    /// <summary>
    /// Phase 88 — count of <see cref="AgentEventKind.ToolCall"/> events
    /// observed during the generator turn. <c>null</c> means the runner
    /// did not instrument tool calls (legacy callers / test fakes). When
    /// non-null and zero, combined with a refusal-like output text,
    /// triggers <see cref="SloppyReasons.AgentRefusal"/> in
    /// <see cref="DeliverableLoop"/>.
    /// </summary>
    long? ToolCallCount = null,
    /// <summary>
    /// 2026-05-30 — the generator model's reasoning ("thinking") text for
    /// this iteration, accumulated from the Copilot SDK's
    /// <c>AssistantReasoningDeltaEvent</c> stream. Persisted on the
    /// deliverable so the Summary tab can show per-iteration reasoning next
    /// to the generator/evaluator summaries. Null when the model/backend
    /// didn't emit reasoning.
    /// </summary>
    string? ReasoningText = null);

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
    /// Phase 74 — dispatcher refused the StartAsync because another
    /// invocation for the same taskId is already live. Classified as
    /// a transient failure: the in-flight invocation will produce the
    /// real verdict on this same workItemId, and any caller that
    /// observes this kind should treat its own iter as a no-op
    /// (verdict=iterate, persisted with the explanatory note so the
    /// operator sees what happened in the iter-KPI strip).
    /// </summary>
    public const string ConcurrentDispatch = "generator-concurrent-dispatch";

    /// <summary>
    /// 2026-06-22 — the runner's session acquire failed authentication
    /// (<c>AUTHENTICATION_FAILED</c>). NON-transient: the spawned Copilot CLI
    /// has no valid credentials, so retrying won't self-heal. Recovery: re-run
    /// <c>gh auth login</c> / <c>copilot login</c> for the operator account,
    /// then Resume.
    /// </summary>
    public const string AuthFailed = "generator-auth-failed";

    /// <summary>
    /// 2026-06-23 — the crew's configured model id is not available on the
    /// provider (e.g. a phantom Copilot variant like <c>claude-opus-4.7-1m-internal</c>
    /// the CLI no longer offers). The server rejects <c>session.create</c> with
    /// "Model \"X\" is not available." This is a CONFIG error, not a transient
    /// transport blip — recovery is "fix the crew's model", so it must NOT
    /// auto-retry. Treated as non-transient so the loop stalls after one iter
    /// with a clear reason instead of burning the full iteration cap (observed:
    /// 40/40 generator-inactive iters per crew with a phantom model on
    /// code-intelligence:ENABLE-MCP — arch, eng, rev, qa, and all night crews).
    /// </summary>
    public const string ModelUnavailable = "generator-model-unavailable";

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
        // Phase 74 — concurrent-dispatch refusal MUST be checked before
        // any other pattern because the InvalidOperationException
        // message also contains words that match other heuristics.
        if (error.Contains(
                "Concurrent dispatch refused",
                StringComparison.OrdinalIgnoreCase))
        {
            return ConcurrentDispatch;
        }
        // 2026-06-22 — an unauthenticated CLI is a NON-retryable config error
        // (the spawned Copilot CLI has no credentials). Wrapped by the acquire
        // path as "...session acquire failed: ...AUTHENTICATION_FAILED", so it
        // MUST be checked before the generic "session acquire failed" → Inactive
        // clause below, which would otherwise burn the full iter cap retrying.
        // (The model-unavailable counterpart is handled just below, next to the
        // session-acquire clause.)
        if (error.Contains("AUTHENTICATION_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return AuthFailed;
        }
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
        // 2026-05-19 — Phase 75: dispatcher's SendAsync transport-failure
        // catch (AgentRunnerDispatcher.cs ~line 1526) emits the literal
        // "transport died at SendAsync" prefix. Classify as Inactive so
        // DeliverableLoop's failure short-circuit treats it as transient
        // (verdict=iterate, auto-retry next iter against a fresh pool
        // entry that the same dispatcher's `finally` block force-disposed
        // after the failure landed). Without this pattern, the message
        // would fall through to `Other` and the loop's transient/non-
        // transient split would route it to `stalled` instead.
        if (error.Contains("transport died at SendAsync", StringComparison.OrdinalIgnoreCase))
        {
            return Inactive;
        }
        // 2026-05-20 — counterpart to the SendAsync transport fix above.
        // The session-acquire path (AgentRunnerDispatcher.cs ~line 1444)
        // wraps any non-cancellation acquire failure (Copilot CLI crash,
        // JSON-RPC connection dropped during CreateSessionAsync, server
        // restart racing the new request, etc.) with the literal prefix
        // "Copilot SDK session acquire failed:". The dispatcher's
        // intent — stated verbatim in its log line — is "surfacing as
        // transient transport failure" and it sets
        // `transportFailedSendAsync = true` to force the next iter to
        // open a fresh pool entry against the recovered shared client.
        // Without this pattern, observed messages like
        // "Copilot SDK session acquire failed: Communication error with
        // Copilot CLI: The JSON-RPC connection was lost." fell through
        // to `Other`, which DeliverableLoop's transient/non-transient
        // split routed to verdict=stalled — terminating the loop
        // immediately instead of auto-retrying. Symptom on
        // code-intelligence:AGENT-MEMORY load-test-calibration: ws went
        // STALLED at iter 20 of 50 on a single dropped JSON-RPC
        // connection even though the dispatcher had already disposed
        // the bad pool entry and the next iter would have started
        // cleanly.
        // 2026-06-23 — an unavailable model id is a CONFIG error, not a
        // transient blip. The Copilot server rejects session.create with
        // "Model \"X\" is not available."; wrapped by the acquire path it reads
        // "...session acquire failed: ...session.create failed with message:
        // Model \"X\" is not available." MUST be checked BEFORE the generic
        // "session acquire failed" → Inactive clause below, which otherwise
        // marks it transient and burns the full iter cap (every crew with a
        // phantom model variant did exactly this). Fail fast so the operator
        // fixes the crew's model.
        if (error.Contains("is not available", StringComparison.OrdinalIgnoreCase)
            && error.Contains("Model", StringComparison.OrdinalIgnoreCase))
        {
            return ModelUnavailable;
        }
        if (error.Contains("session acquire failed", StringComparison.OrdinalIgnoreCase))
        {
            return Inactive;
        }
        // 2026-05-20 — Copilot SDK JSON-RPC pipe drop surfaces as an
        // IOException "The pipe is being closed" (or broken-pipe variants),
        // often concurrent-dispatch collateral (a sibling invocation disposed
        // the shared session). The channel died, not the work — classify as
        // Inactive so the loop treats it as transient (verdict=iterate,
        // auto-retry against a fresh pool entry) instead of falling through to
        // Other → terminal stall.
        if (error.Contains("pipe is being closed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("pipe is broken", StringComparison.OrdinalIgnoreCase)
            || error.Contains("broken pipe", StringComparison.OrdinalIgnoreCase))
        {
            return Inactive;
        }
        if (error.Contains(" exit=", StringComparison.OrdinalIgnoreCase))
        {
            return CliExit;
        }
        // 2026-05-31 — Copilot CAPI transient model-endpoint failure. The
        // Copilot SDK wraps an upstream model timeout/5xx after exhausting
        // its OWN retry budget as
        //   "Failed to get response from the AI model; retried N times
        //    (total retry wait time: …) Last error: CAPIError: Request timed out."
        // This is an upstream blip, not a broken generator — it must be
        // classified as transient so DeliverableLoop's failure short-circuit
        // routes it to verdict=iterate (auto-retry next iter) instead of
        // falling through to Other → terminal stall. The "retried N times"
        // wrapper only appears for retryable errors, so it is reliably
        // transient; the bare-CAPI clause is gated on "timed out" to avoid
        // over-matching CAPI 4xx/auth errors (which are NOT retryable). The
        // Phase-61/67 sloppy detectors (ConsecutiveScoreZero / NoProgress)
        // still cap genuinely-persistent CAPI outages at ≤3 retries.
        // Symptom: code-intelligence:REPO-SCANNER react-and-neo4j-nvl-web-ui
        // stage-vite-and-typescript-scaffold-e2e (2026-05-31) — a single CAPI
        // request timeout at iter 3 stalled the workstream and blocked the
        // entire react-ui phase + 18 downstream workstreams behind it.
        if (error.Contains("Failed to get response from the AI model", StringComparison.OrdinalIgnoreCase)
            || (error.Contains("CAPIError", StringComparison.OrdinalIgnoreCase)
                && error.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
        {
            return Timeout;
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
    string? SloppyReason = null,
    /// <summary>
    /// True when no iteration ran: the loop returned a verdict already
    /// persisted at (or past) the iteration cap. A plain re-drive (spawn
    /// Retry) always lands here again, so callers must tell the operator to
    /// Restart the item (wipe iterations) instead of retrying.
    /// </summary>
    bool ReplayedFromHistory = false,
    /// <summary>The iteration cap in force for this run (set on replays).</summary>
    int? IterationCap = null);

/// <inheritdoc cref="IDeliverableLoop"/>
public sealed class DeliverableLoop : IDeliverableLoop
{
    /// <summary>
    /// 2026-05-20 — persistence-layer cap on evaluator <c>Notes</c>. The
    /// parsed notes are both stored on <c>deliverables.Notes</c> and fed
    /// back into the next iteration's generator prompt, so an unbounded
    /// value bloats the per-product DB, makes the conversation / iter views
    /// unloadable, and balloons the next prompt's input cost. A runaway
    /// evaluator turn wrote a 110 MB Notes value on
    /// code-intelligence:CLEAN-CODE architecture.md before this cap existed.
    /// 64 KB is far above any legitimate critique while keeping rows small.
    /// </summary>
    private const int MaxNotesChars = 64 * 1024;

    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly TaskChannel? channel;

    /// <summary>
    /// Phase 95 (Phase 4) — optional chat-bridge producer. Null in unit tests /
    /// offline; injected by the DI factory in production. When set, the loop
    /// enqueues an open-questions alert when a work item parks on unanswered
    /// questions (best-effort — never affects the loop).
    /// </summary>
    private readonly IChatNotifier? chatNotifier;

    /// <summary>
    /// Phase 74 — per-<c>workItemId</c> fast-fail mutex. Two concurrent
    /// <see cref="RunAsync"/> calls on the same work item used to share
    /// the <c>CopilotSessionPool</c> entry; when the first reached a
    /// terminal verdict its <c>finally</c> disposed the pool session,
    /// killing the second mid-flight. The second then surfaced as a
    /// fake iter row with <c>score=0, verdict=iterate, EvalCost=$0</c>.
    /// <para>The mutex uses <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    /// with a <c>TimeSpan.Zero</c> timeout: the second caller returns
    /// immediately with a synthetic <c>verdict="skipped-concurrent"</c>
    /// result rather than blocking. The orchestrator's tick treats
    /// that verdict as a no-op — the in-flight call owns the turn.</para>
    /// <para>Static so the mutex spans every <see cref="DeliverableLoop"/>
    /// scoped instance the DI container hands out per request /
    /// background-task scope. Keyed by <c>workItemId</c> because that
    /// matches the <see cref="CopilotSessionPool"/> partition (which
    /// is the resource being raced on).</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WorkItemMutexes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Verdict literal returned by <see cref="RunAsync"/> when the
    /// per-<c>workItemId</c> mutex was already held. Callers MUST
    /// treat this as a no-op (don't commit, don't open PR, don't flip
    /// workstream Status) — the in-flight caller owns the turn.
    /// </summary>
    public const string SkippedConcurrentVerdict = "skipped-concurrent";

    /// <summary>
    /// The work-item mutexes the current async flow already holds through
    /// <see cref="TryReserveWorkItem"/>, so <see cref="RunAsync"/> inside that
    /// flow runs without taking the (non-reentrant) mutex a second time.
    /// </summary>
    private static readonly AsyncLocal<System.Collections.Immutable.ImmutableHashSet<string>?> ReservedInFlow = new();

    /// <summary>
    /// Takes the per-<c>workItemId</c> mutex for a whole drive, before the
    /// drive touches the worktree, and holds it until the returned handle is
    /// disposed. Returns null when another run holds it: the caller must then
    /// change nothing (no worktree reset, no upstream merge, no scratch
    /// cleanup) because the in-flight run owns the worktree.
    /// <para>
    /// Observed 2026-11-27 on harness:DSH-M2 stage 5.9: while an iteration was
    /// held on background work, the orchestrator re-drove the workstream every
    /// 30 s. Each drive prepared the worktree and merged the base branch into it
    /// before <see cref="RunAsync"/> found the mutex held, so a base-branch
    /// update merged into the live worktree and the merge's scratch scrub
    /// deleted the agent's <c>.forge/</c> notes and wait declaration.
    /// </para>
    /// <see cref="RunAsync"/> called later in the same async flow recognises
    /// the reservation and does not refuse its own drive.
    /// </summary>
    public static IDisposable? TryReserveWorkItem(string? workItemId)
    {
        var key = workItemId ?? string.Empty;

        // Re-entrant within one async flow: the orchestrator's outer drive
        // reserves, and the strategy's producer it calls reserves again.
        if (ReservedInFlow.Value?.Contains(key) == true)
        {
            return NoReservation.Instance;
        }

        var mutex = WorkItemMutexes.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!mutex.Wait(0))
        {
            return null;
        }

        var held = ReservedInFlow.Value ?? System.Collections.Immutable.ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        ReservedInFlow.Value = held.Add(key);
        return new WorkItemReservation(mutex, key);
    }

    /// <summary>True when some run holds the mutex for <paramref name="workItemId"/>.</summary>
    public static bool IsWorkItemInFlight(string? workItemId)
        => WorkItemMutexes.TryGetValue(workItemId ?? string.Empty, out var mutex) && mutex.CurrentCount == 0;

    /// <summary>A nested reservation in a flow that already holds the mutex; disposing it does nothing.</summary>
    private sealed class NoReservation : IDisposable
    {
        public static readonly NoReservation Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>A held work-item mutex; disposing it releases the mutex once.</summary>
    private sealed class WorkItemReservation(SemaphoreSlim mutex, string key) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            // Drop only this key, so other reservations the flow holds stay marked.
            if (ReservedInFlow.Value is { } held)
            {
                ReservedInFlow.Value = held.Remove(key);
            }

            try { mutex.Release(); } catch (SemaphoreFullException) { /* already released */ }
        }
    }

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
    public DeliverableLoop(ControlDbContext control, ProductDbContext db, TaskChannel? channel, IChatNotifier? chatNotifier = null,
        IQuestionOptionsSynthesizer? questionOptions = null)
    {
        this.control = control;
        this.db = db;
        this.channel = channel;
        this.chatNotifier = chatNotifier;
        this.questionOptions = questionOptions;
    }

    /// <summary>
    /// Drafts options for a question an agent asked without any. Null in unit tests /
    /// offline, where <see cref="QuestionOptions.Fallback"/> still guarantees options.
    /// </summary>
    private readonly IQuestionOptionsSynthesizer? questionOptions;

    /// <summary>
    /// Phase 74 test helper — clear the per-process mutex map between
    /// test runs. Production code never calls this.
    /// </summary>
    public static void ResetWorkItemMutexesForTesting()
    {
        foreach (var sem in WorkItemMutexes.Values) { try { sem.Dispose(); } catch { } }
        WorkItemMutexes.Clear();
    }

    /// <summary>
    /// Phase 95 (Phase 4) — enqueue a chat-bridge open-questions alert for the
    /// operator. Buttons are emitted for the first choice question (one per
    /// choice, callback <c>oq|{externalId}|{index}</c>); open questions are
    /// answered by replying to the message. Fully best-effort.
    /// </summary>
    private async Task TryEnqueueOpenQuestionsChatAlertAsync(
        string workItemId, int iter, int unansweredCount,
        IReadOnlyList<OpenQuestionsExtractor.OpenQuestion> questions, CancellationToken ct)
    {
        try
        {
            var title = $"❓ {unansweredCount} open question(s) need your answer";

            // Operator rule: answers are picked, never typed. Every question gets
            // its own option buttons (numbered to match the list when there are
            // several), and the agent's recommended option is starred — the same
            // match rule as the wizard UI (OpenQuestionsWizard.tsx `rec === c`).
            var numbered = questions.Count > 1;
            var body = string.Join("\n", questions.Select((q, i) => $"{i + 1}. {q.Text}"))
                + "\n\n👉 Tap an option to answer (or answer in the Forge UI).";

            var buttons = questions
                .SelectMany((q, qi) =>
                {
                    var recommended = TryGetRecommendation(q.ExplanationJson);
                    return (q.Choices ?? Array.Empty<string>()).Select((c, idx) => new ChatButton(
                        (numbered ? $"{qi + 1}: " : string.Empty)
                            + (string.Equals(c, recommended, StringComparison.Ordinal) ? "★ " : string.Empty) + c,
                        $"oq|{q.ExternalId}|{idx}"));
                })
                .ToList();

            // Part B — route this alert into the owning spawn's forum topic (group
            // mode). Resolved from the workstream; null → group root.
            var spawnId = await this.db.Workstreams
                .Where(w => w.Id == workItemId)
                .Select(w => w.StorySpawnId)
                .FirstOrDefaultAsync(ct);

            // The spawn route key is `spawn/{productId}/{spawnId}` — an empty
            // product renders `spawn//…`, which the channel store rejects and
            // the alert dies after max attempts (release RELEASE-PROVIDER,
            // 2026-07-17: 8 open-questions alerts failed while the stalled
            // alert — whose producer stamps the slug — delivered).
            var productId = this.db.ProductSlug
                ?? ProductDbContextFactory.CurrentAsyncLocalSlug
                ?? string.Empty;

            await this.chatNotifier!.EnqueueOpenQuestionsAsync(
                new OpenQuestionsAlert(
                    ProductId: productId,
                    RepoId: null,
                    WorkItemId: workItemId,
                    Iter: iter,
                    Title: title,
                    Body: body,
                    Buttons: buttons.Count > 0 ? buttons : null,
                    SpawnId: spawnId),
                ct);
        }
        catch
        {
            // Never let a chat-delivery hiccup affect the deliverable loop.
        }
    }

    /// <summary>
    /// Pull the agent's <c>recommendation</c> string out of a question's
    /// <c>ExplanationJson</c> payload (see <see cref="OpenQuestionsExtractor"/>).
    /// Null when absent or malformed — best-effort, the marker is cosmetic.
    /// </summary>
    internal static string? TryGetRecommendation(string? explanationJson)
    {
        if (string.IsNullOrWhiteSpace(explanationJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(explanationJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("recommendation", out var rec)
                && rec.ValueKind == System.Text.Json.JsonValueKind.String
                ? rec.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<DeliverableLoopResult> RunAsync(DeliverableLoopRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Generator);
        ArgumentNullException.ThrowIfNull(request.Evaluator);

        // Phase 74 — per-workItemId fast-fail mutex. Block concurrent
        // RunAsync on the same workItemId so two dispatcher invocations
        // can't share (and then race the disposal of) the same
        // CopilotSessionPool entry. Acquired here, released in `finally`
        // after the loop body returns or throws.
        var mutexKey = request.WorkItemId ?? string.Empty;

        // The drive that called us already holds the mutex (TryReserveWorkItem).
        if (ReservedInFlow.Value?.Contains(mutexKey) == true)
        {
            return await this.RunAsyncCore(request, ct).ConfigureAwait(false);
        }

        var mutex = WorkItemMutexes.GetOrAdd(mutexKey, _ => new SemaphoreSlim(1, 1));
        var acquired = await mutex.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        if (!acquired)
        {
            await this.EmitAsync(request.WorkItemId, "task.deliverable-loop.skip-concurrent", new
            {
                workItemId = request.WorkItemId,
                reason = "Another DeliverableLoop.RunAsync is already in-flight for this workItemId. "
                    + "Skipping to avoid CopilotSessionPool collision.",
            }, ct);
            return new DeliverableLoopResult(
                FinalVerdict: SkippedConcurrentVerdict,
                Iterations: 0,
                FinalScore: 0,
                DeliverableRowIds: Array.Empty<long>(),
                TotalCostUsd: 0m,
                SloppyReason: null);
        }

        try
        {
            return await this.RunAsyncCore(request, ct).ConfigureAwait(false);
        }
        finally
        {
            try { mutex.Release(); } catch { /* best-effort */ }
        }
    }

    private async Task<DeliverableLoopResult> RunAsyncCore(DeliverableLoopRequest request, CancellationToken ct)
    {

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
                    0m,
                    ReplayedFromHistory: true,
                    IterationCap: maxIter);
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

        await this.TryMarkWorkItemLiveAsync(request, ct);

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
                        : failKind switch
                        {
                            GeneratorFailureKinds.ModelUnavailable =>
                                "The crew/agent is pinned to a model the Copilot backend will not serve "
                                + "(retired or not in the live catalog). Repoint crew.Model / "
                                + "agent.GeneratorModel to a live model (GET /api/models), then Resume.",
                            GeneratorFailureKinds.AuthFailed =>
                                "The spawned Copilot CLI is unauthenticated. Re-run `gh auth login` "
                                + "(or `copilot login`) for the operator account, then Resume.",
                            _ =>
                                "Resume after fixing root cause "
                                + "(e.g., bump WorkItem.Points or TaskQueue.MaxIterOverride for "
                                + $"`{GeneratorFailureKinds.MaxTurns}`).",
                        });
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
                    PairAttempt = workItemRow?.PairAttemptCount ?? 1,
                    Score = 0,
                    DimsJson = "{}",
                    Verdict = failVerdict,
                    Notes = failNote,
                    GeneratorSummary = GeneratorSummaryExtractor.Extract(gen.OutputText ?? string.Empty),
                    ReasoningText = gen.ReasoningText,
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

            // Phase 88 — refusal guard. When the runner instrumented tool
            // calls (ToolCallCount != null) AND the generator emitted zero
            // tool calls with a short refusal-like output ("DONE", "ok",
            // "n/a", etc.), the agent is self-vetoing the task. Rather
            // than burning $7/iter for 65 iters waiting for iter-cap, exit
            // immediately so SloppyReassignmentReconciler can rotate the
            // pair (typically a wrong-crew routing — e.g.,
            // code-refactor-defender drawn into a qa-e2e workstream).
            // Observed 2026-05-25: code-intelligence:CLEAN-CODE stage 6.1-e2e
            // HD3 emitted literally "DONE" with 0 tool calls for 65 iters
            // burning $458.91 before operator restart.
            if (gen.ToolCallCount == 0 && IsRefusalLikeOutput(gen.OutputText))
            {
                totalCost += gen.CostUsd;
                var refusalText = (gen.OutputText ?? string.Empty).Trim();
                var refusalNote =
                    $"SLOPPY ({SloppyReasons.AgentRefusal}): generator emitted "
                    + $"'{refusalText}' with 0 tool calls. Likely a wrong-crew "
                    + "routing or model self-veto against the task. "
                    + "Reassigning to a fresh pair on the next reconciler tick.";
                if (!string.IsNullOrEmpty(notesForNext))
                {
                    refusalNote += "\n\n---\n\n" + notesForNext;
                }
                var refusalRow = new DeliverableRow
                {
                    WorkItemId = request.WorkItemId,
                    CrewId = request.CrewId,
                    GeneratorAgentId = request.GeneratorAgentId,
                    EvaluatorAgentId = pinnedEvaluator,
                    Iter = iter,
                    PairAttempt = workItemRow?.PairAttemptCount ?? 1,
                    Score = 0,
                    DimsJson = "{}",
                    Verdict = "sloppy",
                    Notes = refusalNote,
                    GeneratorSummary = GeneratorSummaryExtractor.Extract(gen.OutputText ?? string.Empty),
                    ReasoningText = gen.ReasoningText,
                    GenCostUsd = gen.CostUsd,
                    EvalCostUsd = 0m,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                this.db.Deliverables.Add(refusalRow);
                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                rowIds.Add(refusalRow.Id);

                await this.UpdatePairAssignmentTotalsAsync(
                    pairAssignmentId,
                    iter,
                    gen.InputTokens,
                    gen.OutputTokens,
                    gen.CostUsd,
                    latestScore: 0,
                    ct);

                await this.StampPairAssignmentAsync(
                    pairAssignmentId,
                    AgentPairAssignmentOutcomes.ReassignedSloppy,
                    sloppyReason: SloppyReasons.AgentRefusal,
                    finalScore: 0,
                    ct);

                await this.EmitAsync(request.WorkItemId, "task.sloppy", new
                {
                    iter,
                    score = 0,
                    reason = SloppyReasons.AgentRefusal,
                    outputText = refusalText,
                    generatorAgentId = request.GeneratorAgentId,
                    evaluatorAgentId = pinnedEvaluator,
                    pairAssignmentId,
                }, ct);

                return new DeliverableLoopResult(
                    "sloppy",
                    iter,
                    FinalScore: 0,
                    rowIds,
                    totalCost,
                    SloppyReasons.AgentRefusal);
            }

            // (b) evaluator
            var eval = await request.Evaluator(iter, gen.OutputText, ct);
            lastScore = EvaluatorScoreParser.Parse(eval.AssistantText, request.WorkItemPoints);

            // 2026-05-20 — cap the evaluator Notes before they are persisted
            // (deliverables.Notes) AND fed back into the next iter's generator
            // prompt. A runaway evaluator turn produced a 110 MB Notes value
            // on code-intelligence:CLEAN-CODE architecture.md, which bloated
            // the per-product DB, made the conversation / iter views
            // unloadable, and re-entered the next prompt to balloon input cost
            // exponentially. RunCopilotSdk's output ceiling prevents most of
            // this upstream; this is the persistence-layer backstop.
            if (lastScore.Notes is { Length: > MaxNotesChars } longNotes)
            {
                lastScore = lastScore with
                {
                    Notes = string.Concat(
                        longNotes.AsSpan(0, MaxNotesChars),
                        $"\n\n…[truncated {longNotes.Length - MaxNotesChars:N0} chars by Forge: evaluator notes exceeded the {MaxNotesChars:N0}-char cap]"),
                };
            }
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
                PairAttempt = workItemRow?.PairAttemptCount ?? 1,
                Score = lastScore.Score,
                DimsJson = JsonSerializer.Serialize(lastScore.Dims),
                Verdict = lastScore.Verdict,
                Notes = lastScore.Notes,
                GeneratorSummary = genSummary,
                ReasoningText = gen.ReasoningText,
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
            IReadOnlyList<OpenQuestionsExtractor.OpenQuestion> newQuestions = OpenQuestionsExtractor.Extract(gen.OutputText);
            if (newQuestions.Count > 0)
            {
                // Operator rule: never ask a question without options. Reuse options
                // already stored for the same id before spending a model call.
                var storedWithOptions = (await this.db.DeliverableQuestions.AsNoTracking()
                        .Where(q => q.WorkItemId == request.WorkItemId && q.ChoicesJson != null)
                        .ToListAsync(ct))
                    .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(),
                        StringComparer.OrdinalIgnoreCase);
                newQuestions = await QuestionOptions.EnsureAsync(newQuestions, storedWithOptions, this.questionOptions, ct);

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
                        ExplanationJson = q.ExplanationJson,
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

            // Phase 95 (Phase 4) — alert the operator over chat that this work
            // item is now parked on open questions. Best-effort; guarded so a
            // chat hiccup never affects the deliverable loop.
            if (this.chatNotifier is not null && unansweredCount > 0 && newQuestions.Count > 0)
            {
                await this.TryEnqueueOpenQuestionsChatAlertAsync(request.WorkItemId, iter, unansweredCount, newQuestions, ct);
            }

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
            // 2026-05-28 — high-confidence pass bypass. When the current
            // score is well above the pass threshold AND the prior iter
            // also passed the threshold, two independent evaluators have
            // endorsed the work — trust them over the checkbox heuristic.
            // Without this bypass, a generator that genuinely finished
            // early (the artifact is done, no new file changes required)
            // gets demoted forever and the no-progress.detected event
            // eventually stalls the workstream. Observed
            // code-intelligence:AST-PARSER-FOR-ADDIT rusttreesitterparser
            // implementation 2026-05-28: iters 5/6/7 all scored 94 (well
            // above PassThreshold=90), but the prior eval's `- [ ] N.`
            // items weren't echoed back as `- [x]` so the gate demoted
            // all three to iterate. Iter 8 had zero file changes (the
            // generator agreed there was nothing left to do) →
            // task.no-progress.detected at consecutiveNoChange=3 →
            // ws.Status='stalled' with "No progress: agent produced
            // zero file changes in 3 consecutive iterations". Bypass
            // requires BOTH a strong current score AND a passing prior
            // score so a one-time spike doesn't escape; intentional
            // skipping over multiple iters is still caught by the
            // sloppy-attempts cap and the operator-visible iter count.
            var highConfidencePass = verdict == "pass"
                && priorChain.Count > 0
                && lastScore.Score >= ScoringConstants.PassThreshold + 3
                && priorChain[priorChain.Count - 1].Score >= ScoringConstants.PassThreshold;
            if (verdict == "pass" && priorChain.Count > 0 && !highConfidencePass)
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
            // Only iterations scored since the operator's latest answer or steer count
            // toward a plateau (see SloppyDetector.ScoresSinceOperatorInputAsync).
            var attemptScores = await SloppyDetector.ScoresSinceOperatorInputAsync(
                this.db, request.WorkItemId, scoresInOrder, ct);
            var sloppy = await SloppyDetector.CheckAsync(
                this.control, this.db,
                request.WorkItemId,
                request.GeneratorAgentId,
                budgetWindowStartUtc,
                attemptScores.ToList(),
                ct);
            if (sloppy.IsSloppy && unansweredCount > 0)
            {
                // A flat score while questions are unanswered means the loop is waiting
                // on the operator, not stuck: park for answers instead of reassigning
                // (the orchestrator soft-parks 'stalled-questions' and resumes once
                // every question is answered or dismissed).
                verdict = "stalled-questions";
                row.Verdict = verdict;
                var parkNote = $"PARKED: the score stayed flat ({sloppy.Reason}) while {unansweredCount} operator question(s) are unanswered, so the loop is waiting on the operator rather than stuck. It resumes when the questions are answered or dismissed.";
                row.Notes = string.IsNullOrEmpty(row.Notes) ? parkNote : row.Notes + "\n\n" + parkNote;
                var parkedWi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == request.WorkItemId, ct);
                if (parkedWi is not null)
                {
                    parkedWi.ExecutionStatus = ExecutionStatuses.AwaitingAnswers;
                    parkedWi.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await this.control.SaveChangesAsync(ct); await this.db.SaveChangesAsync(ct);
                await this.StampPairAssignmentAsync(
                    pairAssignmentId,
                    AgentPairAssignmentOutcomes.Cancelled,
                    sloppyReason: null,
                    finalScore: lastScore.Score,
                    ct);
                await this.EmitAsync(request.WorkItemId, "task.iteration.parked-for-questions", new
                {
                    iter,
                    score = lastScore.Score,
                    unansweredQuestions = unansweredCount,
                    suppressedSloppyReason = sloppy.Reason,
                }, ct);
                return new DeliverableLoopResult(verdict, iter, lastScore.Score, rowIds, totalCost);
            }

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
        // priorChain only gains an entry after a generator iteration reaches
        // the evaluator. When every iteration is a transient generator failure
        // (e.g. a dead Copilot session that keeps failing to resume), each one
        // hits the `continue` above before appending, so the chain is empty at
        // the cap. A seed-less Max() on it throws "Sequence contains no
        // elements", which escapes up to StorySpawnOrchestrator and halts the
        // whole plan-doc pipeline. Guard the empty case — "best score 0".
        var bestPriorScore = priorChain.Count > 0 ? priorChain.Max(c => c.Score) : 0;
        var iterCapNote = $"SLOPPY: iteration cap reached — {iter} iters consumed without a pass (best score {bestPriorScore}). Reassigning to a fresh pair on the next reconciler tick.";
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

        // Score-trend guard (2026-05-30) — the topic-recurrence signal above is
        // SCORE-BLIND, and on its own it fires on a CLIMBING score. Observed on
        // repo-scanner worker-adopts-ancestrywriter-e2e: score went 55→78→82→85
        // (5 pts from pass, monotonic) yet stalled `no-convergence` at iter 4
        // (of maxIters=50) because the evaluator kept naming the shrinking
        // remainder in "What still needs work" — which is what improving-but-
        // not-done work looks like, NOT a loop. A loop "going in circles" has a
        // FLAT or declining score; a rising score means the agent IS resolving
        // the recurring topics. So only treat recurring topics as a stuck loop
        // when the score is NOT materially improving across the window. (Flat
        // 85→85→85 or bouncing 88→85→90→85 still trip it; a genuine climb does
        // not.) This complements the score-plateau sloppy guard, which owns the
        // flat-score case.
        const int MinScoreGain = 2;
        var scoreImproving = window.Count >= 2
            && (window[^1].Score - window[0].Score) >= MinScoreGain;
        var looping = recurring.Count >= MinRecurringItems && !scoreImproving;
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

        // Prose fallback (2026-05-27). When the evaluator writes a paragraph
        // instead of a "What still needs work" checklist — the qa-e2e
        // evaluator does exactly this — the bullet matcher above yields zero
        // items, so DetectConvergenceLoop sees an empty latest set and never
        // fires. A frozen-feedback qa-e2e loop then burns its whole iteration
        // cap (observed on common-infra:HTTP-MEMORY stage 2.1-e2e: 40 iters,
        // byte-identical evaluator feedback, score pinned at 68, ×3 pair
        // attempts). Split the body into sentences and treat each substantive
        // one as an item so the Jaccard-overlap machinery can still detect
        // near-identical feedback recurring across the window. Only runs when
        // the checkbox/bullet path found nothing, so the established
        // checklist-format behaviour is unchanged.
        if (items.Count == 0)
        {
            foreach (var raw in System.Text.RegularExpressions.Regex.Split(body, @"(?<=[.!?])\s+|\n+"))
            {
                var sentence = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
                // Skip the loop's own terminal status lines (SLOPPY:/STALLED:
                // appended by DeliverableLoop on the final iter) — they're not
                // evaluator content and would add noise to the overlap check.
                if (sentence.StartsWith("SLOPPY:", StringComparison.OrdinalIgnoreCase)
                    || sentence.StartsWith("STALLED:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (sentence.Length >= 40) items.Add(sentence);
            }
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
    /// Marks inline deliverable loops (notably plan-doc runs, which bypass
    /// <see cref="CrewQueueWorker"/>) as live before the first generator turn.
    /// </summary>
    private async Task TryMarkWorkItemLiveAsync(DeliverableLoopRequest request, CancellationToken ct)
    {
        var wi = await this.db.WorkItems.FirstOrDefaultAsync(w => w.Id == request.WorkItemId, ct);
        if (wi is null) return;

        var fromStatus = string.IsNullOrWhiteSpace(wi.ExecutionStatus)
            ? ExecutionStatuses.Idle
            : wi.ExecutionStatus;
        if (IsParkedOrTerminalExecutionStatus(fromStatus))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var wasLive = string.Equals(fromStatus, ExecutionStatuses.Live, StringComparison.Ordinal);
        var agentIds = new List<string> { request.GeneratorAgentId };
        if (!string.IsNullOrWhiteSpace(request.EvaluatorAgentId)
            && !string.Equals(request.EvaluatorAgentId, request.GeneratorAgentId, StringComparison.Ordinal))
        {
            agentIds.Add(request.EvaluatorAgentId);
        }

        wi.ExecutionStatus = ExecutionStatuses.Live;
        wi.AutoDispatchedAt ??= now;
        wi.UpdatedAt = now;
        if (!wasLive)
        {
            wi.TrialCount++;
        }

        if (!wasLive || string.IsNullOrWhiteSpace(wi.AssignedAgentJson))
        {
            wi.AssignedAgentJson = JsonSerializer.Serialize(new
            {
                agents = agentIds,
                team = (string?)null,
                crewId = request.CrewId,
                role = "generator",
                assignedAt = now.ToString("O"),
                assignedBy = "deliverable-loop",
            });
        }

        if (!wasLive)
        {
            this.db.AgentAssignments.Add(new AgentAssignmentRow
            {
                WorkItemId = wi.Id,
                Kind = AgentAssignmentKinds.Claimed,
                AgentIdsJson = JsonSerializer.Serialize(agentIds),
                Team = null,
                CrewId = request.CrewId,
                Role = "generator",
                FromExecutionStatus = fromStatus,
                ToExecutionStatus = ExecutionStatuses.Live,
                AssignedBy = "deliverable-loop",
                At = now,
            });
        }

        await this.db.SaveChangesAsync(ct);
    }

    private static bool IsParkedOrTerminalExecutionStatus(string status)
    {
        return status is ExecutionStatuses.Completed
            or ExecutionStatuses.Failed
            or ExecutionStatuses.Cancelled
            or ExecutionStatuses.CooldownBudget
            or ExecutionStatuses.PendingReassign
            or ExecutionStatuses.AwaitingAnswers;
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
    /// <summary>
    /// Phase 88 — detect refusal-like generator outputs that indicate the
    /// agent self-vetoed the task. Combined with
    /// <see cref="GeneratorRunOutput.ToolCallCount"/> == 0, this triggers
    /// <see cref="SloppyReasons.AgentRefusal"/> immediately rather than
    /// burning a full iter-cap window. The list is intentionally small —
    /// a short denylist is safer than a length-only predicate, which
    /// would false-positive on legitimate brief evaluator-iterate
    /// responses (e.g., "Reading the test file first."). Match is
    /// case-insensitive and ignores trailing punctuation.
    /// </summary>
    internal static bool IsRefusalLikeOutput(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return true;
        if (t.Length > 80) return false;
        // Strip trailing punctuation and lowercase for matching.
        var trimmed = t.TrimEnd('.', '!', ' ', '\t', '\r', '\n').ToLowerInvariant();
        return trimmed
            is "done"
            or "ok"
            or "okay"
            or "n/a"
            or "na"
            or "nothing to do"
            or "no changes needed"
            or "no changes required"
            or "no action needed"
            or "no action required"
            or "complete"
            or "completed"
            or "finished";
    }

    /// <summary>
    /// Phase 75 follow-up (root cause for Stage 2.1 stall, 2026-05-27):
    /// strip the literal <c>"GENERATOR FAILED (kind): reason"</c> marker
    /// that <see cref="GeneratorFailureKinds"/>'s failure-path
    /// (line 840) persists into the deliverable <c>Notes</c> column when
    /// the generator hits a transient failure (timeout, inactive).
    /// <para>
    /// When this text re-enters the next iter's prelude via
    /// <see cref="FormatPriorChain"/>, an agent under prompt-bloat
    /// (3.5M+ token context — observed on local-http-memory:
    /// extend-requesttraceexpressions-with-stream-evaluator iter 2)
    /// tends to echo the phrase into its own
    /// <c>## Change Summary</c> output. That echo lands in the next
    /// deliverable's <c>GeneratorSummary</c>, then
    /// <c>StorySpawnOrchestrator.DriveOneWorkstreamImplAsync</c>'s
    /// PR-body assembly pipes the summary into the
    /// <c>### What changed</c> section verbatim, and
    /// <c>StorySpawnOrchestrator.PrBodyHasFailureMarker</c> stalls the
    /// workstream on PR-open with the verbatim
    /// "Phase 75: refused to open PR because the body embedded a
    /// generator-failure marker" message.
    /// </para>
    /// <para>
    /// The fix is to replace the marker phrase with a neutralized
    /// descriptor BEFORE feeding the prior chain to the next generator —
    /// the agent still learns the prior iter aborted (and why) but
    /// cannot propagate the literal trigger phrase downstream. Raw
    /// <c>DeliverableRow.Notes</c> remains unchanged (operator UI and
    /// the Phase 64 parser still see the original prefix). Also
    /// neutralizes a <c>**Score:** 0 |</c> header pattern in case a
    /// prior iter's notes ever embed it (defense for the second Gate
    /// B regex trigger; harmless for the common case where no such
    /// header appears in notes).
    /// </para>
    /// </summary>
    internal static string SanitizePriorIterMarkers(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return notes ?? string.Empty;
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            notes,
            @"GENERATOR FAILED \(([^)]+)\)",
            "[Prior iter aborted: $1]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"\*\*Score:\*\*\s*0\s*\|",
            "**Score:** prior-zero |",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return sanitized;
    }

    private static string FormatPriorChain(IReadOnlyList<(int Iter, int Score, string Verdict, string Notes)> chain)
    {
        if (chain.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();

        // Show the LATEST iteration's feedback prominently at the top.
        var latest = chain[chain.Count - 1];
        sb.AppendLine($"## LATEST evaluator feedback (iteration {latest.Iter}, score {latest.Score}, verdict: {latest.Verdict})");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(latest.Notes) ? "(no notes)" : SanitizePriorIterMarkers(latest.Notes));
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
                    sb.AppendLine(string.IsNullOrWhiteSpace(n) ? "(no notes)" : SanitizePriorIterMarkers(n));
                }
                else
                {
                    var oneLiner = string.IsNullOrWhiteSpace(n) ? "(no notes)" : SanitizePriorIterMarkers(n).Split('\n')[0];
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
