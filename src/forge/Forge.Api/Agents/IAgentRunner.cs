// -----------------------------------------------------------------------
// <copyright file="IAgentRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

/// <summary>
/// Phase 11.1 — runs a single agent invocation. One invocation = one task.
/// Long-running agents are modeled as a sequence of invocations, not a
/// single process. The default implementation is
/// <see cref="AgentRunnerDispatcher"/>, which routes each invocation to one
/// of four (backend × mode) cells: Copilot/SDK, Copilot/CLI, Claude/SDK,
/// Claude/CLI. The <c>local-stub</c> model bypasses every backend.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Start a new invocation. The returned <see cref="AgentInvocation"/>
    /// surfaces a token / tool-call / status stream and is the correlation
    /// id for the cost ledger entry written on completion.
    /// </summary>
    Task<AgentInvocation> StartAsync(
        string taskId,
        string agentId,
        string prompt,
        AgentContext context,
        CancellationToken ct);

    /// <summary>Stop a running invocation; the runner records a partial-finish ledger row.</summary>
    Task CancelAsync(string invocationId, CancellationToken ct);

    /// <summary>
    /// Cancel every active invocation tied to a given <paramref name="taskId"/>
    /// (a.k.a. workItemId — the value the caller passed to <see cref="StartAsync"/>'s
    /// first argument). Returns the number of invocations cancelled. Used by
    /// the workstream/work-item Cancel endpoints to actually stop the agent
    /// stream — flipping DB state alone leaves the running process emitting
    /// events past the cancel call.
    /// </summary>
    Task<int> CancelByTaskAsync(string taskId, CancellationToken ct);

    /// <summary>Halt the agent's task pickup loop. Live invocations run to completion.</summary>
    Task PauseAsync(string agentId, CancellationToken ct);

    /// <summary>Resume after a <see cref="PauseAsync"/>; the agent starts taking tasks again.</summary>
    Task ResumeAsync(string agentId, CancellationToken ct);

    /// <summary>Current state of the agent (idle / working / blocked / paused).</summary>
    Task<AgentState> GetStateAsync(string agentId, CancellationToken ct);
}
