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

    /// <summary>
    /// Phase 64 — Copilot SDK inactivity watchdog threshold. If no SDK
    /// callback fires within this window the dispatcher kills the
    /// invocation with <c>generator-inactive</c> on the assumption
    /// that the JSON-RPC transport died (process crash, socket close,
    /// restart).
    /// <para>Default 20 min — raised from 10 (which itself replaced the
    /// original 5). A single silent tool call on the FRP <c>update</c>
    /// repo (a cold <c>dotnet build</c> of a 590-project tree, or a
    /// scoped <c>dotnet test</c> that still spins up an in-proc service
    /// host) routinely sits quiet past 10 min, and the 10 min threshold
    /// was killing healthy agents mid-work as <c>generator-inactive</c>
    /// (observed 2026-05-21 on local:REFACTOR-INTEGRATION
    /// stage-4.2 iter-5: the agent's own validation run went silent
    /// 03:25→03:35 and the iteration was discarded). Kept BELOW the
    /// wall-clock budget so a truly dead transport is still reaped well
    /// inside the per-iter cap; a stuck SINGLE tool call is bounded
    /// separately by <see cref="PerTurnTimeout"/>. Bind via
    /// <c>Forge:Agents:InactivityTimeout</c>.</para>
    /// </summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Phase 64 — per-tool-call wall-clock cap. The dispatcher tracks
    /// the most recent <c>ToolExecutionStartedEvent</c>; if no matching
    /// <c>ToolExecutionCompleteEvent</c> arrives within this window
    /// the invocation is killed with <c>generator-stuck-tool-call</c>.
    /// Distinguishes a genuinely stuck tool (e.g. a hung CLI subprocess)
    /// from a healthy agent doing legitimately long work in a single
    /// tool call (e.g. a multi-minute build). Default 15 min — a generous
    /// upper bound on any single tool round-trip in this repo.
    /// </summary>
    public TimeSpan PerTurnTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Max retries on 429 / 503 / 529.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Max output tokens (<c>max_tokens</c>) for the Claude · SDK direct
    /// <c>/v1/messages</c> path.
    /// <para>
    /// Phase (2026-05-20 fix) — raised from a hard-coded <c>4096</c>. That
    /// ceiling silently truncated any single assistant turn that emitted
    /// more than ~4K tokens of output, which includes a <c>create</c>
    /// tool-call whose <c>file_text</c> argument carries a non-trivial
    /// source file. Observed on local:REFACTOR-INTEGRATION
    /// stage-4.2-migrate-cachecontrollerintegrationtests: the engineer
    /// deleted the 957-line legacy test then could not recreate it —
    /// every <c>create</c> arrived with <c>{"path":…}</c> and NO
    /// <c>file_text</c> because the tool_use JSON was cut off mid-content,
    /// so the file stayed deleted (net diff: one deletion, score 0).
    /// </para>
    /// <para>
    /// 32000 is safe for every model in the registry (all are Claude 4.x,
    /// which support ≥32K output). Bind via
    /// <c>Forge:Agents:ClaudeMaxOutputTokens</c>. NOTE: this only governs
    /// the Claude · SDK path; the Copilot · SDK path's output cap lives in
    /// the copilot server and is not settable from <c>SessionConfig</c> —
    /// there the engineer prompt's in-place-edit / chunked-write guidance
    /// is the corresponding mitigation.
    /// </para>
    /// </summary>
    public int ClaudeMaxOutputTokens { get; set; } = 32000;

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

    /// <summary>
    /// 2026-05-20 — hard ceiling on accumulated assistant output characters
    /// per Copilot-SDK invocation. When the running total of streamed
    /// assistant content crosses this value the dispatcher kills the turn
    /// with a <c>generator-runaway-output</c> failure instead of letting a
    /// degenerate / repeating generation grow without bound.
    /// <para>
    /// Default 8,000,000 chars (~8 MB ≈ ~2 M tokens). That is two orders of
    /// magnitude above any legitimate plan doc / code diff this repo
    /// produces (a real <c>architecture.md</c> is ~190 KB) yet far below
    /// System.Text.Json's hard cap for a single value
    /// (<c>MaxUnescapedTokenSize = 166,666,666</c>). Without it, a runaway
    /// architect loop bloated its replayed conversation until the SDK's
    /// JSON-RPC (de)serialisation threw
    /// <c>"The JSON value of length 185861791 is too large and not
    /// supported."</c> and halted the entire plan-doc pipeline (observed
    /// 2026-05-20 on code-intelligence:CLEAN-CODE architecture.md, 43
    /// iterations / $4,911 with exponential per-iter cost growth).
    /// </para>
    /// <para>Set to 0 to disable the ceiling. Bind via
    /// <c>Forge:Agents:MaxOutputChars</c>.</para>
    /// </summary>
    public int MaxOutputChars { get; set; } = 8_000_000;
}
