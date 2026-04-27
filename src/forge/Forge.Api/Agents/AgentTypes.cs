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
    decimal? CostUsd = null);

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
    AgentInvokeMode InvokeMode = AgentInvokeMode.Sdk);
