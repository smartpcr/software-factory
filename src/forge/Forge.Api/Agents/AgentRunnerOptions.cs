// -----------------------------------------------------------------------
// <copyright file="AgentRunnerOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

/// <summary>
/// Phase 11.5 — runner configuration. Bound from <c>Forge:Agents:*</c>.
/// </summary>
public sealed class AgentRunnerOptions
{
    public const string SectionName = "Forge:Agents";

    /// <summary>Anthropic API key (kept in user-secrets in dev).</summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>Anthropic API endpoint. Override for proxies / VCR replay.</summary>
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Anthropic API version header.</summary>
    public string AnthropicApiVersion { get; set; } = "2023-06-01";

    /// <summary>Default per-invocation wall-clock budget.</summary>
    public TimeSpan WallClockBudget { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Default per-invocation token budget. Null = no token cap (only wall-clock + crew budget apply).</summary>
    public long? DefaultTokenBudget { get; set; }

    /// <summary>Max retries on 429 / 503 / 529.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Phase 11.5b — backend used when an <see cref="AgentContext"/> doesn't override it.</summary>
    public AgentBackend DefaultBackend { get; set; } = AgentBackend.Copilot;

    /// <summary>Phase 11.5b — mode used when an <see cref="AgentContext"/> doesn't override it.</summary>
    public AgentInvokeMode DefaultInvokeMode { get; set; } = AgentInvokeMode.Sdk;

    /// <summary>Optional override for the copilot CLI path. Resolved via PATH when null.</summary>
    public string? CopilotCliPath { get; set; }

    /// <summary>Optional override for the claude CLI path. Resolved via PATH when null.</summary>
    public string? ClaudeCliPath { get; set; }

    /// <summary>Extra args passed to the copilot SDK / CLI. Defaults to <c>["--yolo"]</c>.</summary>
    public string[] CopilotCliExtraArgs { get; set; } = new[] { "--yolo" };

    /// <summary>
    /// Max agent turns (tool-call rounds) per invocation.
    /// <para>
    /// A "turn" here is one <c>ToolExecutionCompleteEvent</c> — a single
    /// successful tool round-trip. The cap fires inside
    /// <c>AgentRunnerDispatcher.RunCopilotSdk</c> / <c>RunClaudeSdk</c> by
    /// short-circuiting the SDK's event stream with <c>failReason = "Max
    /// turns (N) reached"</c>.
    /// </para>
    /// <para>
    /// Phase 56 — lowered from 500 → 60 (operator pain 2026-05-13,
    /// failover-cluster:XRAFT stage 3.1 iter 2: a single generator turn
    /// consumed 4.3M tokens / $192 doing hundreds of tool calls,
    /// because <c>work_item.Points == 0</c> on synthetic workstream IDs
    /// → <c>taskMaxTurns</c> resolved null → the runner used this
    /// global default of 500). 60 is "comfortably handles a real iter
    /// shape" (Read a few files, ~20 Edit/Write rounds, a couple of
    /// Bash invocations for builds/tests) while making a runaway
    /// agent-loop terminate within ~600K tokens worst case instead of
    /// 5M. Work items with explicit points still get <c>points * 30</c>
    /// from <c>CrewQueueWorker</c>, which dominates this when set.
    /// </para>
    /// </summary>
    public int MaxTurns { get; set; } = 60;
}
