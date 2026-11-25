// -----------------------------------------------------------------------
// <copyright file="AgentTypes.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

/// <summary>
/// Phase 11.5b — which AI vendor backs an invocation. Mirrors
/// <c>ExecutionEngine.Enums.AIBackend</c> on the workflow side. Default
/// (per Q11.5b decision): <c>Copilot</c> — every Forge dev box has a
/// gh / copilot CLI installed already, so the zero-config path works.
/// </summary>
public enum AgentBackend
{
    /// <summary>GitHub Copilot — uses the copilot CLI under the hood (SDK mode wraps it).</summary>
    Copilot = 0,

    /// <summary>Anthropic Claude — SDK mode hits /v1/messages directly; CLI mode shells out to <c>claude --print</c>.</summary>
    Claude = 1,
}

/// <summary>
/// Phase 11.5b — how Forge invokes the chosen backend.
///   * <c>Sdk</c>  — in-process. Streaming events flow through the runner
///     as the model emits deltas / tool calls. Lower latency, richer surface.
///   * <c>Cli</c>  — out-of-process shell. The runner pipes the prompt,
///     captures stdout, returns one big content block. Simpler, no SDK dep.
/// </summary>
public enum AgentInvokeMode
{
    Sdk = 0,
    Cli = 1,
}

/// <summary>
/// Phase 11.1 — agent state as exposed on the <see cref="Forge.Api.Persistence.OverviewAgentRow"/>
/// and the <c>/crews</c> view. <c>Working</c> means the agent has an active
/// invocation; <c>Paused</c> is a PO-driven hold.
/// </summary>
public enum AgentState
{
    Idle,
    Working,
    Blocked,
    Paused,
}

/// <summary>
/// Kind of <see cref="AgentEvent"/>. Mirrors the SignalR channel events
/// described in Phase 11.7.
/// </summary>
public enum AgentEventKind
{
    Started,
    Token,
    ToolCall,
    ToolResult,
    Finished,
    Failed,
    Paused,

    /// <summary>
    /// Phase 55 — provider-native session metadata. The Claude/CLI
    /// stream-json runner emits one of these when it sees the CLI's
    /// <c>system / init</c> event so the caller can persist
    /// <see cref="AgentEvent.SessionId"/> for the next iteration's
    /// <c>--resume</c>. Copilot/SDK long-lived pool (Phase E) will use
    /// the same shape.
    /// </summary>
    SessionInfo,

    /// <summary>
    /// 2026-05-30 — a chunk of the model's reasoning ("thinking") stream,
    /// from the Copilot SDK's <c>AssistantReasoningDeltaEvent</c>. Carries
    /// the reasoning text delta in <see cref="AgentEvent.Text"/>. Fires only
    /// for reasoning-capable, GitHub-hosted models (not BYOM Anthropic/OpenAI).
    /// The accumulated full text also lands on the final
    /// <see cref="Finished"/> event's <see cref="AgentEvent.ReasoningText"/>.
    /// </summary>
    Reasoning,
}

/// <summary>
/// Phase 11.1 — single message on an invocation's event stream.
/// Token deltas live in <see cref="Text"/>; tool calls fill <see cref="ToolName"/>
/// and <see cref="ToolPayload"/>; final cost lands on the <see cref="AgentEventKind.Finished"/> event.
/// </summary>
public sealed record AgentEvent(
    AgentEventKind Kind,
    DateTimeOffset At,
    string? Text = null,
    string? ToolName = null,
    string? ToolPayload = null,
    string? Error = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? CostUsd = null,
    /// <summary>
    /// Phase 55 — provider-native session id surfaced by the runner so
    /// the gen-eval loop can persist it for the next iteration's
    /// <c>--resume</c>. Non-null only on
    /// <see cref="AgentEventKind.SessionInfo"/> and (defensively) on the
    /// final <see cref="AgentEventKind.Finished"/> event from the
    /// Claude/CLI stream-json path.
    /// </summary>
    string? SessionId = null,
    /// <summary>
    /// 2026-05-16 — prompt-cache hit tokens. Reported by the Copilot
    /// SDK on <c>AssistantUsageEvent.Data.CacheReadTokens</c> and by
    /// Anthropic's API on the corresponding <c>cache_read_input_tokens</c>
    /// field. Non-zero means the prefix of the prompt matched a
    /// previously-cached version on the provider side and was billed
    /// at the discounted cache-read rate (~10% of fresh input). Used
    /// by the iter-kpis API to expose a "cache-hit %" so operators
    /// can verify <c>AgentMemoryStore</c> + <c>CopilotSessionPool</c>
    /// are actually reusing context across iterations.
    /// </summary>
    long? CacheReadTokens = null,
    /// <summary>
    /// 2026-05-16 — prompt-cache write tokens. Reported by the
    /// Copilot SDK on <c>AssistantUsageEvent.Data.CacheWriteTokens</c>.
    /// Counts tokens written to the prompt cache for future reuse —
    /// the first iter that processes a given system prompt or
    /// long tool-definition block pays this once, then subsequent
    /// iters re-using the same prefix hit the cheaper cache-read path.
    /// </summary>
    long? CacheWriteTokens = null,
    /// <summary>
    /// 2026-05-16 — reasoning tokens consumed by the model's internal
    /// chain-of-thought (Anthropic <c>thinking</c>, OpenAI <c>o1</c>
    /// reasoning). Reported by the Copilot SDK on
    /// <c>AssistantUsageEvent.Data.ReasoningTokens</c>. Billed
    /// separately from output tokens and surfaced in the iter-kpis
    /// breakdown so operators can see when reasoning-heavy models
    /// (e.g. <c>claude-opus-4.7-high</c>) inflate cost relative to
    /// visible output.
    /// </summary>
    long? ReasoningTokens = null,
    /// <summary>
    /// 2026-05-16 — total conversation tokens reported by the
    /// Copilot SDK on <c>SessionUsageInfoEvent.Data.ConversationTokens</c>.
    /// Snapshot of the live session's context size at the moment
    /// the model processed the prompt. Useful for spotting context
    /// bloat: if iter N reports conversationTokens=200K when the
    /// raw prompt is 6K, the SDK is replaying the entire prior
    /// dialog and we're paying the full input token cost on every
    /// turn (until the prompt-cache kicks in).
    /// </summary>
    long? ConversationTokens = null,
    /// <summary>
    /// 2026-05-16 — provider-native tool call id from the Copilot SDK
    /// (<c>ToolExecutionStartData.ToolCallId</c> /
    /// <c>ToolExecutionCompleteData.ToolCallId</c>). Used by the UI
    /// Tools tab to pair start/complete events deterministically
    /// instead of relying on name-based matching (which breaks when
    /// the SDK overlaps concurrent calls). Non-null only on
    /// <see cref="AgentEventKind.ToolCall"/> and
    /// <see cref="AgentEventKind.ToolResult"/>.
    /// </summary>
    string? ToolCallId = null,
    /// <summary>
    /// 2026-05-16 — parent tool call id when the tool was dispatched
    /// from within another tool (e.g. an MCP "execute many" tool
    /// fans out to sub-tools). Forms a parent/child tree the UI
    /// can render. Non-null only on ToolCall / ToolResult events.
    /// </summary>
    string? ParentToolCallId = null,
    /// <summary>
    /// 2026-05-16 — wall-clock duration of one tool invocation in
    /// milliseconds, computed as completeAt - startAt. Stamped on
    /// <see cref="AgentEventKind.ToolResult"/> so the UI doesn't
    /// have to recompute from event timestamps (and so plan-doc
    /// transcripts that get persisted to disk retain the duration
    /// even if event ordering is rewritten by retention pruning).
    /// </summary>
    long? ToolDurationMs = null,
    /// <summary>
    /// 2026-05-16 — process exit code for shell-flavored tool calls
    /// (<c>run_in_terminal</c>, <c>bash</c>, etc.). Extracted from
    /// the Copilot SDK's <c>ToolExecutionCompleteContentTerminal</c>
    /// typed content. <c>null</c> for non-shell tools.
    /// </summary>
    int? ToolExitCode = null,
    /// <summary>
    /// 2026-05-16 — MCP server name when the tool was dispatched
    /// through an MCP integration (Copilot SDK
    /// <c>ToolExecutionStartData.McpServerName</c>). Non-null
    /// triggers the UI Tools tab's MCP classification regardless of
    /// the tool name itself.
    /// </summary>
    string? McpServerName = null,
    /// <summary>
    /// 2026-05-30 — the model's full accumulated reasoning ("thinking")
    /// text for the turn, assembled from the Copilot SDK's
    /// <c>AssistantReasoningDeltaEvent</c> stream. Stamped on the final
    /// <see cref="AgentEventKind.Finished"/> event so the gen↔eval loop
    /// can persist it on the deliverable for the Summary tab. Null when the
    /// model/backend didn't emit reasoning (non-reasoning model, BYOM
    /// Anthropic/OpenAI, CLI/Claude paths).
    /// </summary>
    string? ReasoningText = null,
    /// <summary>
    /// True on a <see cref="AgentEventKind.Finished"/> event when the
    /// <see cref="WaitOnlyGuard"/> ended the turn because the agent was only
    /// waiting on background work. The work is graded like any finished turn,
    /// but an iteration that ended this way is not a "no progress" iteration.
    /// </summary>
    bool EndedWhileWaiting = false);

/// <summary>
/// Phase 11.1 — handle returned by <see cref="IAgentRunner.StartAsync"/>.
/// Caller iterates <see cref="Events"/> for streaming output and uses
/// <see cref="InvocationId"/> when correlating with the cost ledger.
/// </summary>
public sealed class AgentInvocation
{
    public AgentInvocation(string invocationId, string agentId, string crewId, string taskId, string model, IAsyncEnumerable<AgentEvent> events)
    {
        this.InvocationId = invocationId;
        this.AgentId = agentId;
        this.CrewId = crewId;
        this.TaskId = taskId;
        this.Model = model;
        this.Events = events;
    }

    public string InvocationId { get; }

    public string AgentId { get; }

    public string CrewId { get; }

    public string TaskId { get; }

    public string Model { get; }

    public IAsyncEnumerable<AgentEvent> Events { get; }
}

/// <summary>
/// Phase 11.1 — context passed to <see cref="IAgentRunner.StartAsync"/>.
/// <see cref="WorktreePath"/> is the only writable root for tools (Phase 11.6).
/// Phase 11.5b adds <see cref="Backend"/> + <see cref="InvokeMode"/> so the
/// dispatcher can pick from the four (backend × mode) cells.
/// </summary>
public sealed record AgentContext(
    string CrewId,
    string Model,
    string SoulMd,
    string WorktreePath,
    IReadOnlyList<string> AllowedTools,
    decimal? MaxTaskBudgetUsd = null,
    AgentBackend Backend = AgentBackend.Copilot,
    AgentInvokeMode InvokeMode = AgentInvokeMode.Sdk,
    TimeSpan? WallClockBudget = null,
    int? MaxTurns = null,
    /// <summary>
    /// Phase 41 — Claude-Code-SDK tool names the agent must NOT have access to
    /// for this invocation (passed as <c>--disallowedTools</c>). Distinct from
    /// <see cref="AllowedTools"/>, which gates Forge's own in-process
    /// <see cref="IAgentToolHost"/> methods. This list is enforced by the
    /// Claude CLI itself before it'll dispatch a matching tool call. Common
    /// values: <c>"Bash"</c> (revoke shell access entirely),
    /// <c>"WebFetch"</c>, <c>"Task"</c>.
    /// </summary>
    IReadOnlyList<string>? DisallowedTools = null,
    /// <summary>
    /// Phase 55 (docs/forge/sloppy_guard.md) — cross-iteration memory
    /// handle. When non-null, the runner for the matching (Backend,
    /// InvokeMode) cell uses the carried <see cref="AgentMemoryHandle.SessionId"/>
    /// or <see cref="AgentMemoryHandle.PriorTurns"/> to resume the prior
    /// iteration's provider session. Null = fresh session (the
    /// pre-Phase-55 behaviour and the still-current default for every
    /// cell until Phases C/D/E land). Phase B's
    /// <c>AgentMemoryStore</c> is the producer; the runner is the
    /// consumer.
    /// </summary>
    AgentMemoryHandle? Memory = null,
    /// <summary>
    /// 2026-06-03 — true when this context drives the EVALUATOR half of the
    /// generator↔evaluator loop. The dispatcher uses it to suppress the
    /// workstream-memory pointer (see
    /// <c>AgentRunnerDispatcher.MaybePrependWorkstreamMemoryPointer</c>) so the
    /// evaluator is never instructed to read
    /// <c>.forge/memory/workstream-context.md</c> and re-ingest its own prior
    /// feedback or self-reported "state: failed" as binding requirements/state.
    /// That self-referential leak capped scores just under the pass threshold
    /// (PR-INGESTER stage-5.3, 2026-06-03). Default false = generator runs.
    /// </summary>
    bool IsEvaluator = false);

/// <summary>
/// Phase 55 — cross-iteration memory handed to a runner invocation. The
/// two payload fields are mutually exclusive in practice: CLI / Copilot
/// SDK paths carry <see cref="SessionId"/>; the Claude SDK path carries
/// <see cref="PriorTurns"/>. Records that hold both are valid only on
/// migrations across backends (rare).
/// </summary>
/// <param name="SessionId">Provider-native session handle. Null until a session-backed runner has allocated one.</param>
/// <param name="PriorTurns">Conversation history to replay as the <c>messages</c> array. Null when the backend doesn't need replay.</param>
/// <param name="ResumeSession">True when <see cref="SessionId"/> points at a previously-created provider session; false when it is a caller-assigned id for a new session.</param>
public sealed record AgentMemoryHandle(
    string? SessionId = null,
    IReadOnlyList<ConversationTurn>? PriorTurns = null,
    bool ResumeSession = false);

/// <summary>
/// Phase 55 — one conversation turn persisted in
/// <see cref="Forge.Api.Persistence.AgentMemoryRow.MessagesJson"/> and
/// replayed to the Claude SDK on the next iteration.
/// </summary>
/// <param name="Role"><c>"user"</c> or <c>"assistant"</c> — matches Anthropic's <c>messages</c> shape.</param>
/// <param name="Content">Verbatim turn content. Compaction (Phase F) may rewrite older turns into a single summary user-turn.</param>
/// <param name="Iter">Iteration that produced this turn. Lets the compactor reason about temporal locality.</param>
/// <param name="Tokens">Best-effort input+output token count for this turn (used to sum <see cref="Forge.Api.Persistence.AgentMemoryRow.ApproxInputTokens"/>).</param>
public sealed record ConversationTurn(
    string Role,
    string Content,
    int Iter,
    long Tokens);
