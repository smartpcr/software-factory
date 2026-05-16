// -----------------------------------------------------------------------
// <copyright file="AgentRunnerDispatcher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Forge.Api.Auth;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 11.5b — single <see cref="IAgentRunner"/> that dispatches across
/// four (backend × mode) cells:
///
///   <list type="bullet">
///     <item><description>Copilot · SDK — in-process <see cref="CopilotClient"/> with streaming events (default).</description></item>
///     <item><description>Copilot · CLI — shells out to <c>copilot</c> with the prompt on stdin, captures stdout.</description></item>
///     <item><description>Claude · SDK — POST <c>/v1/messages</c> with streaming SSE.</description></item>
///     <item><description>Claude · CLI — shells out to <c>claude --print</c>, captures stdout.</description></item>
///   </list>
///
/// Mirrors the workflow-side pattern in <c>ExecutionEngine.AI.IAIExecutor</c>
/// (Copilot SDK + Claude CLI) but extends it to the full 2×2 matrix and
/// surfaces results as the streaming <see cref="AgentEvent"/> shape Forge
/// already uses everywhere downstream (cost ledger, agent-state hub, etc.).
///
/// Per-agent state (idle / working / paused) lives in a process-local
/// dictionary keyed on <c>agentId</c>; cancellation is keyed on
/// <c>invocationId</c>. The <c>local-stub</c> model bypasses every backend
/// and emits a deterministic stream — used by tests and offline dev so the
/// event + ledger plumbing exercises without network or CLI.
/// </summary>
public sealed class AgentRunnerDispatcher : IAgentRunner
{
    public const string ClientName = "AnthropicMessages";

    /// <summary>
    /// Hard floor for the Copilot SDK inactivity watchdog — even if
    /// <see cref="AgentRunnerOptions.InactivityTimeout"/> is misconfigured
    /// to something tiny, the watchdog never fires below this. 1 minute
    /// is the minimum useful window for a JSON-RPC heartbeat detector.
    /// </summary>
    private static readonly TimeSpan InactivitySdkFloor = TimeSpan.FromMinutes(1);

    /// <summary>How often the watchdog checks the gap. Cheap, so 30 s is fine.</summary>
    private static readonly TimeSpan InactivitySdkPollInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory http;
    private readonly IModelRegistry models;
    private readonly IOptions<AgentRunnerOptions> options;
    private readonly CopilotServerManager copilotServer;
    private readonly IAuthService? ghAuth;
    private readonly ILogger<AgentRunnerDispatcher> log;
    private readonly CopilotSessionPool copilotPool;

    private readonly ConcurrentDictionary<string, AgentState> agentStates = new();

    /// <summary>
    /// Active invocations keyed by invocation id. The tuple carries the
    /// <c>taskId</c> (a.k.a. workItemId — what the caller passed to
    /// <see cref="StartAsync"/>) so <see cref="CancelByTaskAsync"/> can
    /// cancel every invocation tied to a given work item / workstream
    /// without the caller having to know the invocation id.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveInvocation> active = new();

    private sealed record ActiveInvocation(string TaskId, CancellationTokenSource Cts);

    public AgentRunnerDispatcher(
        IHttpClientFactory http,
        IModelRegistry models,
        IOptions<AgentRunnerOptions> options,
        CopilotServerManager copilotServer,
        IEnumerable<IAuthService> authServices,
        CopilotSessionPool copilotPool,
        ILogger<AgentRunnerDispatcher> log)
    {
        this.http = http;
        this.models = models;
        this.options = options;
        this.copilotServer = copilotServer;
        this.ghAuth = authServices.FirstOrDefault(a => a.Kind == "github");
        this.copilotPool = copilotPool;
        this.log = log;
    }

    public Task<AgentInvocation> StartAsync(
        string taskId,
        string agentId,
        string prompt,
        AgentContext context,
        CancellationToken ct)
    {
        var invocationId = Guid.NewGuid().ToString("N");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.active[invocationId] = new ActiveInvocation(taskId, linked);
        this.agentStates[agentId] = AgentState.Working;

        // Normalize model ID for the chosen backend. Crews store Anthropic
        // IDs (claude-opus-4-7) but the Copilot SDK/CLI expects dotted IDs
        // (claude-opus-4.7). Translate when necessary.
        var resolvedModel = NormalizeModelForBackend(context.Model, context.Backend);
        var ctx = resolvedModel == context.Model ? context : context with { Model = resolvedModel };

        IAsyncEnumerable<AgentEvent> raw;
        if (string.Equals(ctx.Model, "local-stub", StringComparison.OrdinalIgnoreCase))
        {
            // local-stub bypasses all backends — used by tests + offline dev.
            raw = this.RunLocalStub(prompt, ctx, linked.Token);
        }
        else
        {
            raw = (ctx.Backend, ctx.InvokeMode) switch
            {
                (AgentBackend.Claude,  AgentInvokeMode.Sdk) => this.RunClaudeSdk(prompt, ctx, linked.Token),
                (AgentBackend.Claude,  AgentInvokeMode.Cli) => this.RunClaudeCli(prompt, ctx, linked.Token),
                (AgentBackend.Copilot, AgentInvokeMode.Sdk) => this.RunCopilotSdk(prompt, ctx, taskId, agentId, linked.Token),
                (AgentBackend.Copilot, AgentInvokeMode.Cli) => this.RunCopilotCli(prompt, ctx, linked.Token),
                _ => RunUnsupported(ctx),
            };
        }

        var stream = this.WrapEvents(invocationId, agentId, raw);
        return Task.FromResult(new AgentInvocation(invocationId, agentId, ctx.CrewId, taskId, resolvedModel, stream));
    }

    public Task CancelAsync(string invocationId, CancellationToken ct)
    {
        if (this.active.TryRemove(invocationId, out var entry))
        {
            entry.Cts.Cancel();
        }
        return Task.CompletedTask;
    }

    public bool IsTaskActive(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return false;
        // O(N) over active invocations — N is small (one-handful per host).
        // Cheap enough to avoid maintaining a parallel TaskId->Count index.
        foreach (var kv in this.active)
        {
            if (string.Equals(kv.Value.TaskId, taskId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public Task<int> CancelByTaskAsync(string taskId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskId)) return Task.FromResult(0);

        var matches = this.active
            .Where(kv => string.Equals(kv.Value.TaskId, taskId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        var cancelled = 0;
        foreach (var invocationId in matches)
        {
            if (this.active.TryRemove(invocationId, out var entry))
            {
                try { entry.Cts.Cancel(); cancelled++; }
                catch (ObjectDisposedException) { /* already torn down */ }
            }
        }
        if (cancelled > 0)
        {
            this.log.LogInformation(
                "Cancelled {Count} active invocation(s) for task {TaskId}.", cancelled, taskId);
        }
        return Task.FromResult(cancelled);
    }

    public Task PauseAsync(string agentId, CancellationToken ct)
    {
        this.agentStates[agentId] = AgentState.Paused;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string agentId, CancellationToken ct)
    {
        this.agentStates[agentId] = AgentState.Idle;
        return Task.CompletedTask;
    }

    public Task<AgentState> GetStateAsync(string agentId, CancellationToken ct)
        => Task.FromResult(this.agentStates.TryGetValue(agentId, out var s) ? s : AgentState.Idle);

    private async IAsyncEnumerable<AgentEvent> WrapEvents(
        string invocationId,
        string agentId,
        IAsyncEnumerable<AgentEvent> inner)
    {
        try
        {
            await foreach (var e in inner)
            {
                yield return e;
            }
        }
        finally
        {
            this.agentStates[agentId] = AgentState.Idle;
            if (this.active.TryRemove(invocationId, out var entry)) entry.Cts.Dispose();
        }
    }

    // ── local-stub (no backend) ─────────────────────────────────────────────

    private async IAsyncEnumerable<AgentEvent> RunLocalStub(
        string prompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        long outTokens = 0;
        foreach (var delta in SplitForStub(prompt))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct);
            outTokens += Math.Max(1, delta.Length / 4);
            yield return new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: delta);
        }

        var inTokens = Math.Max(1, prompt.Length / 4);
        var modelInfo = this.models.Find(context.Model);
        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inTokens,
            OutputTokens: outTokens,
            CostUsd: ComputeCost(modelInfo, inTokens, outTokens));
    }

    // ── Claude · SDK (Anthropic /v1/messages with streaming SSE) ────────────

    private async IAsyncEnumerable<AgentEvent> RunClaudeSdk(
        string prompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = this.options.Value.AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: "No Anthropic API key configured. Set Forge:Agents:AnthropicApiKey.");
            yield break;
        }

        var modelInfo = this.models.Find(context.Model);
        if (modelInfo is null)
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: $"Model '{context.Model}' not in registry.");
            yield break;
        }

        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        // Phase 55 / Phase D (docs/forge/sloppy_guard.md) -- when the
        // caller attached an AgentMemoryHandle with PriorTurns, replay
        // the full conversation by prepending those turns into the
        // /v1/messages messages array, and pin a cache breakpoint at
        // the second-to-last message so every prior turn benefits from
        // Anthropic's prompt cache (the new user turn at the end is
        // the cache miss). Without this lever each iter pays full
        // input cost for the whole history.
        var priorTurns = context.Memory?.PriorTurns
            ?? (IReadOnlyList<ConversationTurn>)Array.Empty<ConversationTurn>();
        var messagesList = new List<object>(priorTurns.Count + 1);
        foreach (var turn in priorTurns)
        {
            messagesList.Add(new { role = turn.Role, content = turn.Content });
        }
        messagesList.Add(new { role = "user", content = prompt });

        if (priorTurns.Count > 0 && messagesList.Count >= 2)
        {
            var lastPrior = priorTurns[priorTurns.Count - 1];
            messagesList[messagesList.Count - 2] = new
            {
                role = lastPrior.Role,
                content = new object[]
                {
                    new
                    {
                        type = "text",
                        text = lastPrior.Content,
                        cache_control = new { type = "ephemeral" },
                    },
                },
            };
        }

        var body = new
        {
            model = modelInfo.Id,
            max_tokens = 4096,
            stream = true,
            // Phase 11.5 — prompt-cache the soul.md system block (5-min TTL).
            system = new[]
            {
                new { type = "text", text = context.SoulMd, cache_control = new { type = "ephemeral" } },
            },
            messages = messagesList,
        };
        var payload = JsonSerializer.Serialize(body, JsonOpts);

        long inputTokens = 0;
        long outputTokens = 0;
        string? failReason = null;

        var attempt = 0;
        while (true)
        {
            attempt++;
            HttpResponseMessage? resp = null;
            try
            {
                using var client = this.http.CreateClient(ClientName);
                client.BaseAddress = new Uri(this.options.Value.AnthropicBaseUrl);
                using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", this.options.Value.AnthropicApiVersion);
                req.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt <= this.options.Value.MaxRetries)
            {
                this.log.LogWarning(ex, "Claude HTTP failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(BackoffMs(attempt), ct);
                continue;
            }

            var status = (int)resp!.StatusCode;
            if (status is 429 or 503 or 529 && attempt <= this.options.Value.MaxRetries)
            {
                resp.Dispose();
                this.log.LogWarning("Claude returned {Status} (attempt {Attempt}); retrying", status, attempt);
                await Task.Delay(BackoffMs(attempt), ct);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                failReason = $"Anthropic {status}: {err}";
                resp.Dispose();
                break;
            }

            using (resp)
            await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var json = line.Substring(5).Trim();
                    if (json.Length == 0 || json == "[DONE]") continue;

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;

                    var type = typeEl.GetString();
                    if (type == "content_block_delta"
                        && root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: text);
                        }
                    }
                    else if (type == "message_delta"
                             && root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("input_tokens", out var i)) inputTokens = i.GetInt64();
                        if (usage.TryGetProperty("output_tokens", out var o)) outputTokens = o.GetInt64();
                    }
                }
            }
            break;
        }

        if (failReason is not null)
        {
            yield return this.BuildFailedWithUsage(
                failReason,
                inputTokens,
                outputTokens,
                accumulatedCostUsd: null,
                promptCharCount: prompt?.Length ?? 0,
                outputCharCount: 0,
                modelName: context.Model);
            yield break;
        }

        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CostUsd: ComputeCost(modelInfo, inputTokens, outputTokens));
    }

    // ── Claude · CLI (claude --print) ───────────────────────────────────────

    private IAsyncEnumerable<AgentEvent> RunClaudeCli(
        string prompt,
        AgentContext context,
        CancellationToken ct)
    {
        // Phase 55 (docs/forge/sloppy_guard.md Phase C) — when the caller
        // attached an AgentMemoryHandle, route to the stream-json runner so
        // we can pass --resume <sid> and capture the session id the CLI
        // generates / echoes back. The legacy text-mode path stays the
        // default for every crew that hasn't opted into agent memory.
        if (context.Memory is not null)
        {
            return this.RunClaudeCliStreamJson(prompt, context, ct);
        }

        return this.RunClaudeCliText(prompt, context, ct);
    }

    private async IAsyncEnumerable<AgentEvent> RunClaudeCliText(
        string prompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        var cliPath = ResolveCli(this.options.Value.ClaudeCliPath, "claude");
        var fullPrompt = string.IsNullOrEmpty(context.SoulMd)
            ? prompt
            : $"{context.SoulMd}\n\n---\n\n{prompt}";

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(context.WorktreePath) ? null : context.WorktreePath,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(context.Model);

        // Phase 41 — pass --disallowedTools when the caller has decided this
        // agent must NOT have certain tools (e.g. revoke Bash for the
        // architect plan-doc loop after observing the agent shell out to
        // `Remove-Item tech-spec.md` mid-iteration and never rewrite it).
        if (context.DisallowedTools is { Count: > 0 } disallowed)
        {
            psi.ArgumentList.Add("--disallowedTools");
            psi.ArgumentList.Add(string.Join(",", disallowed));
        }

        await foreach (var ev in this.RunCliAsync(psi, fullPrompt, context, ct))
        {
            yield return ev;
        }
    }

    /// <summary>
    /// Phase 55 / Phase C — Claude/CLI runner that uses
    /// <c>--output-format=stream-json --include-partial-messages</c> so we
    /// can capture the CLI-assigned <c>session_id</c> and pass
    /// <c>--resume &lt;sid&gt;</c> on subsequent iterations. Parses the
    /// CLI's NDJSON output line-by-line: init events carry the session id,
    /// stream_event/text_delta events carry token deltas, the final
    /// result event carries real input/output token counts and cost. On
    /// <c>--resume</c> we skip prepending <c>soul.md</c> to the stdin
    /// prompt — the existing session already has it in conversation history.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunClaudeCliStreamJson(
        string prompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        var cliPath = ResolveCli(this.options.Value.ClaudeCliPath, "claude");
        var resumeSessionId = context.Memory?.SessionId;
        var hasResume = !string.IsNullOrEmpty(resumeSessionId);

        // First-iter: prepend soul.md so the new session sees the system
        // prompt. On resume: skip — the prior session already injected it,
        // and re-prepending wastes tokens + may confuse the model.
        var stdinPrompt = hasResume || string.IsNullOrEmpty(context.SoulMd)
            ? prompt
            : $"{context.SoulMd}\n\n---\n\n{prompt}";

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(context.WorktreePath) ? null : context.WorktreePath,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--include-partial-messages");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(context.Model);
        if (hasResume)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(resumeSessionId!);
        }
        if (context.DisallowedTools is { Count: > 0 } disallowed)
        {
            psi.ArgumentList.Add("--disallowedTools");
            psi.ArgumentList.Add(string.Join(",", disallowed));
        }

        await foreach (var ev in this.RunClaudeStreamJsonAsync(psi, stdinPrompt, context, ct))
        {
            yield return ev;
        }
    }

    private async IAsyncEnumerable<AgentEvent> RunClaudeStreamJsonAsync(
        ProcessStartInfo psi,
        string stdinPrompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Process? process = null;
        Exception? launchError = null;
        try { process = Process.Start(psi); }
        catch (Exception ex) { launchError = ex; }

        if (launchError is not null)
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: $"Failed to launch {psi.FileName}: {launchError.Message}");
            yield break;
        }
        if (process is null)
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: $"Process.Start returned null for {psi.FileName}");
            yield break;
        }

        ChildProcessTracker.AddProcess(process);

        var budget = context.WallClockBudget ?? this.options.Value.WallClockBudget;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget > TimeSpan.Zero) timeoutCts.CancelAfter(budget);
        var effectiveCt = timeoutCts.Token;
        var timedOut = false;

        string? sessionId = null;
        long inputTokens = 0;
        long outputTokens = 0;
        decimal costUsd = 0m;
        string? failReason = null;
        var sessionInfoEmitted = false;

        Task<string>? stderrTask = null;
        try
        {
            using var ctReg = effectiveCt.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already exited */ }
            });

            await process.StandardInput.WriteAsync(stdinPrompt);
            process.StandardInput.Close();
            stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(effectiveCt);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                    break;
                }
                if (line is null) break;
                if (line.Length == 0) continue;

                AgentEvent? parsed;
                string? newSession;
                long? deltaIn = null;
                long? deltaOut = null;
                decimal? deltaCost = null;
                try
                {
                    parsed = ParseClaudeStreamJsonLine(line, out newSession, out deltaIn, out deltaOut, out deltaCost);
                }
                catch (Exception ex)
                {
                    this.log.LogDebug(ex, "Claude stream-json parse failed (line ignored): {Line}", line);
                    continue;
                }

                if (!string.IsNullOrEmpty(newSession))
                {
                    sessionId = newSession;
                    if (!sessionInfoEmitted)
                    {
                        sessionInfoEmitted = true;
                        yield return new AgentEvent(
                            AgentEventKind.SessionInfo,
                            DateTimeOffset.UtcNow,
                            SessionId: sessionId);
                    }
                }

                if (deltaIn is long di) inputTokens = di;
                if (deltaOut is long oc) outputTokens = oc;
                if (deltaCost is decimal dc) costUsd = dc;

                if (parsed is not null) yield return parsed;
            }

            if (!timedOut)
            {
                try { await process.WaitForExitAsync(effectiveCt); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                }
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }

        string stderr;
        try { stderr = stderrTask is null ? string.Empty : await stderrTask; }
        catch (OperationCanceledException) { stderr = string.Empty; }
        var exitCode = process.HasExited ? process.ExitCode : -1;
        process.Dispose();

        if (timedOut)
        {
            yield return this.BuildFailedWithUsage(
                error: $"{Path.GetFileName(psi.FileName)} hard timeout: exceeded {budget.TotalMinutes:0}m budget",
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: costUsd,
                promptCharCount: stdinPrompt.Length,
                outputCharCount: 0,
                modelName: context.Model,
                sessionId: sessionId);
            yield break;
        }

        if (exitCode != 0)
        {
            // Surface enough of stderr that the caller can distinguish a
            // resume-failure (e.g. "session not found") from a generic
            // crash and clear the stale memory row.
            yield return this.BuildFailedWithUsage(
                error: $"{Path.GetFileName(psi.FileName)} exit={exitCode}: {stderr.Trim()}",
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: costUsd,
                promptCharCount: stdinPrompt.Length,
                outputCharCount: 0,
                modelName: context.Model,
                sessionId: sessionId);
            yield break;
        }

        if (failReason is not null)
        {
            yield return this.BuildFailedWithUsage(
                error: failReason,
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: costUsd,
                promptCharCount: stdinPrompt.Length,
                outputCharCount: 0,
                modelName: context.Model,
                sessionId: sessionId);
            yield break;
        }

        var modelInfo = this.models.Find(context.Model);
        // The stream-json `result` event already reports authoritative
        // counts; ComputeCost on the model row gives us a recomputed cost
        // when the CLI didn't carry one through (e.g. a backend that
        // doesn't expose pricing). Prefer the CLI-reported cost when
        // present so the ledger lines up with the provider's billing.
        var finalCost = costUsd > 0m ? costUsd : ComputeCost(modelInfo, inputTokens, outputTokens);

        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inputTokens > 0 ? inputTokens : null,
            OutputTokens: outputTokens > 0 ? outputTokens : null,
            CostUsd: finalCost,
            SessionId: sessionId);
    }

    /// <summary>
    /// Phase 55 / Phase C — parser for one line of claude-code's
    /// <c>--output-format=stream-json --include-partial-messages</c>
    /// output. Returns an <see cref="AgentEvent"/> when the line maps to
    /// one (token delta, tool call, etc.) and emits side-channel values
    /// (session id, final token counts, cost) via out-params so the
    /// caller can accumulate them outside the event stream. Returns
    /// <c>null</c> when the line is benign metadata we don't surface
    /// (init, ping, user-echo, etc.) — the caller should continue
    /// reading.
    /// </summary>
    private static AgentEvent? ParseClaudeStreamJsonLine(
        string line,
        out string? sessionId,
        out long? inputTokens,
        out long? outputTokens,
        out decimal? costUsd)
    {
        sessionId = null;
        inputTokens = null;
        outputTokens = null;
        costUsd = null;

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("type", out var typeEl)) return null;
        var type = typeEl.GetString();

        if (root.TryGetProperty("session_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
        {
            sessionId = sidEl.GetString();
        }

        switch (type)
        {
            case "system":
                // init carries session_id, cwd, tools. Already captured above.
                return null;

            case "stream_event":
            {
                if (!root.TryGetProperty("event", out var evEl)) return null;
                if (!evEl.TryGetProperty("type", out var evType)) return null;
                if (evType.GetString() == "content_block_delta"
                    && evEl.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("type", out var deltaType)
                    && deltaType.GetString() == "text_delta"
                    && delta.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: text);
                    }
                }
                return null;
            }

            case "assistant":
            {
                // Without --include-partial-messages this would be the
                // primary token source. With partials on, the stream_event
                // path above carries text deltas and this event still
                // carries tool_use blocks we surface as ToolCall events.
                if (!root.TryGetProperty("message", out var msg)) return null;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var partType)) continue;
                    if (partType.GetString() == "tool_use")
                    {
                        var toolName = part.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                        var toolInput = part.TryGetProperty("input", out var inp) ? inp.GetRawText() : null;
                        return new AgentEvent(
                            AgentEventKind.ToolCall,
                            DateTimeOffset.UtcNow,
                            ToolName: toolName,
                            ToolPayload: toolInput);
                    }
                }
                return null;
            }

            case "user":
                // Echo of tool_result blocks. Surface as ToolResult so the
                // transcript records the result body.
            {
                if (!root.TryGetProperty("message", out var msg)) return null;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var partType)) continue;
                    if (partType.GetString() == "tool_result")
                    {
                        var payload = part.TryGetProperty("content", out var c) ? c.GetRawText() : null;
                        var isError = part.TryGetProperty("is_error", out var er) && er.ValueKind == JsonValueKind.True;
                        return new AgentEvent(
                            AgentEventKind.ToolResult,
                            DateTimeOffset.UtcNow,
                            ToolPayload: payload,
                            Error: isError ? "tool reported is_error=true" : null);
                    }
                }
                return null;
            }

            case "result":
            {
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number)
                        inputTokens = it.GetInt64();
                    if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                        outputTokens = ot.GetInt64();
                }
                if (root.TryGetProperty("total_cost_usd", out var costEl) && costEl.ValueKind == JsonValueKind.Number)
                {
                    costUsd = (decimal)costEl.GetDouble();
                }
                return null;
            }

            default:
                return null;
        }
    }

    // ── Copilot · CLI (copilot stdout-capture) ─────────────────────────────

    private async IAsyncEnumerable<AgentEvent> RunCopilotCli(
        string prompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        var cliPath = ResolveCli(this.options.Value.CopilotCliPath, "copilot");
        var fullPrompt = string.IsNullOrEmpty(context.SoulMd)
            ? prompt
            : $"{context.SoulMd}\n\n---\n\n{prompt}";

        // copilot CLI: `--prompt -` reads from stdin, `--model` selects model.
        // (Recent copilot CLI versions dropped the `-m` short form — the
        // current CLI ONLY accepts `--model <model>`. Caught 2026-05-12
        // after 39 consecutive `unknown option '-m'` exit=1 failures on
        // review-fix invocations.)
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(context.WorktreePath) ? null : context.WorktreePath,
        };
        psi.ArgumentList.Add("--prompt");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(context.Model);
        foreach (var a in this.options.Value.CopilotCliExtraArgs) psi.ArgumentList.Add(a);

        await foreach (var ev in this.RunCliAsync(psi, fullPrompt, context, ct))
        {
            yield return ev;
        }
    }

    /// <summary>
    /// Shared CLI execution path. Pipes <paramref name="stdinPrompt"/> to the
    /// child process, streams stdout line-by-line as <see cref="AgentEventKind.Token"/>
    /// events, and emits a terminal Finished/Failed event with token estimates
    /// (CLIs don't surface usage so we estimate at chars/4).
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunCliAsync(
        ProcessStartInfo psi,
        string stdinPrompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Process? process = null;
        Exception? launchError = null;
        try { process = Process.Start(psi); }
        catch (Exception ex) { launchError = ex; }

        if (launchError is not null)
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: $"Failed to launch {psi.FileName}: {launchError.Message}");
            yield break;
        }

        if (process is null)
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: $"Process.Start returned null for {psi.FileName}");
            yield break;
        }

        // Pin the child to a Windows JobObject so that if Forge.Api dies
        // unclean (crash, taskkill /F) the OS reaps this process tree too.
        // The explicit Kill calls below remain the primary cleanup path; the
        // job is a backstop for the case where neither the ct.Register nor
        // the finally runs.
        ChildProcessTracker.AddProcess(process);

        // Hard wall-clock timeout. Per-invocation override (from work item
        // points) takes precedence over the global default.
        var budget = context.WallClockBudget ?? this.options.Value.WallClockBudget;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget > TimeSpan.Zero) timeoutCts.CancelAfter(budget);
        var effectiveCt = timeoutCts.Token;
        var timedOut = false;

        var output = new StringBuilder();
        Task<string>? stderrTask = null;
        try
        {
            using var ctReg = effectiveCt.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already exited */ }
            });

            await process.StandardInput.WriteAsync(stdinPrompt);
            process.StandardInput.Close();

            stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(effectiveCt);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                    break;
                }
                if (line is null) break;
                output.AppendLine(line);
                if (line.Length > 0)
                {
                    yield return new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: line + "\n");
                }
            }

            if (!timedOut)
            {
                try
                {
                    await process.WaitForExitAsync(effectiveCt);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                }
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort cleanup */ }
        }

        string stderr;
        try { stderr = stderrTask is null ? string.Empty : await stderrTask; }
        catch (OperationCanceledException) { stderr = string.Empty; }
        var exitCode = process.HasExited ? process.ExitCode : -1;
        process.Dispose();

        if (timedOut)
        {
            yield return this.BuildFailedWithUsage(
                error: $"{Path.GetFileName(psi.FileName)} hard timeout: exceeded {budget.TotalMinutes:0}m budget",
                accumulatedInputTokens: 0,
                accumulatedOutputTokens: 0,
                accumulatedCostUsd: null,
                promptCharCount: stdinPrompt.Length,
                outputCharCount: output.Length,
                modelName: context.Model);
            yield break;
        }

        if (exitCode != 0)
        {
            yield return this.BuildFailedWithUsage(
                error: $"{Path.GetFileName(psi.FileName)} exit={exitCode}: {stderr.Trim()}",
                accumulatedInputTokens: 0,
                accumulatedOutputTokens: 0,
                accumulatedCostUsd: null,
                promptCharCount: stdinPrompt.Length,
                outputCharCount: output.Length,
                modelName: context.Model);
            yield break;
        }

        // Token estimate: chars/4 (Anthropic / OpenAI rough heuristic).
        var inTokens = Math.Max(1, stdinPrompt.Length / 4);
        var outTokens = Math.Max(1, output.Length / 4);
        var modelInfo = this.models.Find(context.Model);
        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inTokens,
            OutputTokens: outTokens,
            CostUsd: ComputeCost(modelInfo, inTokens, outTokens));
    }

    // ── Copilot · SDK (in-process CopilotClient with streaming events) ─────

    private async IAsyncEnumerable<AgentEvent> RunCopilotSdk(
        string prompt,
        AgentContext context,
        string taskId,
        string agentId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        var contentBuilder = new StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;
        // 2026-05-16 — Phase 72 token telemetry. Accumulated across every
        // AssistantUsageEvent the SDK emits for this invocation; latest
        // wins for ConversationTokens since SessionUsageInfoEvent is the
        // post-turn snapshot.
        long cacheReadTokens = 0;
        long cacheWriteTokens = 0;
        long reasoningTokens = 0;
        long conversationTokensLatest = 0;
        string? failReason = null;
        int turnCount = 0;
        var maxTurns = context.MaxTurns ?? this.options.Value.MaxTurns;

        // 2026-05-16 — provider-native session id captured after
        // CreateSessionAsync. Surfaces on the final Finished event so
        // AgentMemoryStore can persist it (operators can see whether
        // session reuse worked across Forge.Api restarts via
        // /api/workstreams/{id}/memory).
        string? capturedSessionId = null;

        // 2026-05-16 — pair tool start/complete by ToolCallId so the UI
        // can render duration deterministically without re-scanning the
        // transcript. Single-flight today (one tool at a time) but
        // forward-compatible with future concurrent tool dispatch.
        var toolStartTicks = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        // Hard wall-clock timeout — per-invocation override from work item
        // points takes precedence over the global default.
        var budget = context.WallClockBudget ?? this.options.Value.WallClockBudget;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget > TimeSpan.Zero) timeoutCts.CancelAfter(budget);
        var effectiveCt = timeoutCts.Token;
        var timedOut = false;

        // Connect to the shared Copilot server instead of spawning a new
        // copilot.exe per invocation. The server manager handles lifecycle,
        // health checks, and restart.
        var clientOptions = await this.copilotServer.GetClientOptionsAsync(effectiveCt);

        // Stage results into a channel so the SDK's event subscription (which
        // is not async-iter-friendly) feeds an IAsyncEnumerable cleanly.
        var queue = Channel.CreateUnbounded<AgentEvent>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Inactivity watchdog state. When the underlying JSON-RPC transport
        // dies mid-session (server restart, copilot.exe crash, network drop),
        // the SDK's event subscription stops being invoked but never raises
        // SessionErrorEvent — done.Task never completes, the consumer foreach
        // blocks until the wall-clock budget (hours for a multi-point story).
        // We track the last-event timestamp on every callback invocation and
        // a watchdog Task surfaces transport death as a Failed event after
        // InactivitySdkTimeout of silence.
        long lastEventTicks = DateTimeOffset.UtcNow.Ticks;
        var wasInactive = false;
        // Phase 64 — per-tool-call watchdog state. lastToolStartTicks is
        // 0 when no tool call is currently in flight, otherwise the
        // ticks-at-start of the most recent ToolExecutionStartEvent.
        // The watchdog kills the invocation when the matching
        // ToolExecutionCompleteEvent fails to arrive within PerTurnTimeout,
        // catching genuinely hung tool subprocesses (e.g. a `dotnet build`
        // that wedged) without killing a healthy agent doing legitimately
        // long work split across many fast tool calls.
        long lastToolStartTicks = 0;
        string? lastToolName = null;
        var wasStuckOnTool = false;

        // Phase 55 / Phase E -- when the caller attached an
        // AgentMemoryHandle, route through CopilotSessionPool so iter
        // N+1 reuses iter N's live session and the in-session
        // conversation history is preserved. The pool owns disposal;
        // we release the SendLock at end of this StartAsync call and
        // skip the inline Dispose path.
        var isPooledPath = context.Memory is not null;
        PooledCopilotEntry? pooledEntry = null;
        var pooledLockAcquired = false;

        CopilotSession? session = null;
        IDisposable? subscription = null;
        CopilotClient? client = null;
        try
        {
            // Resolve a GitHub token for per-session auth. When connecting to
            // the shared server via CliUrl the SDK cannot discover auth on its
            // own — we must supply it explicitly.
            string? ghToken = null;
            if (this.ghAuth is not null)
            {
                try { ghToken = await this.ghAuth.GetAccessTokenAsync(effectiveCt); }
                catch (Exception ex) { this.log.LogWarning(ex, "Failed to obtain GitHub token for Copilot session"); }
            }

            var sessionConfig = new SessionConfig
            {
                Model = context.Model,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                GitHubToken = ghToken,
            };
            if (!string.IsNullOrEmpty(context.WorktreePath))
            {
                sessionConfig.WorkingDirectory = context.WorktreePath;
            }
            if (!string.IsNullOrEmpty(context.SoulMd))
            {
                sessionConfig.SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = context.SoulMd,
                };
            }

            if (isPooledPath)
            {
                pooledEntry = await this.copilotPool.AcquireAsync(
                    taskId,
                    agentId,
                    async (factoryCt) =>
                    {
                        // Same retry-once pattern as the non-pooled path
                        // below. The factory runs at most once per pool
                        // key per process lifetime.
                        CopilotClient? c = null;
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            try
                            {
                                // 2026-05-13 -- defensive worktree-existence
                                // pre-check. The shared copilot.exe server
                                // validates cwd inside `session.create` and
                                // returns "Directory does not exist or cannot
                                // be accessed: <path>" when the worktree
                                // got wiped between EnsureWorktreeAsync's
                                // success and this CreateSessionAsync call
                                // (observed on local:UPDATE-HISTORY-IN-CL ->
                                // architecture.md iter 3 at 5:22 PM after a
                                // Forge.Api restart cycle). Throwing here
                                // with a sentinel message routes the error
                                // into the existing IsProcessLaunchError
                                // retry-once arm and surfaces a clearer
                                // diagnostic if the second attempt also
                                // fails.
                                EnsureWorkingDirectoryAccessible(sessionConfig);
                                c = new CopilotClient(clientOptions);
                                var s = await c.CreateSessionAsync(sessionConfig, factoryCt);
                                return (c, s);
                            }
                            catch (Exception ex) when (attempt == 0 && IsProcessLaunchError(ex))
                            {
                                this.log.LogWarning(ex,
                                    "Copilot SDK connection failed (pool factory attempt 1), retrying after delay");
                                try { if (c is not null) await c.DisposeAsync(); } catch { }
                                c = null;
                                await Task.Delay(2000, factoryCt);
                            }
                        }
                        throw new InvalidOperationException(
                            "Failed to create Copilot session after retry; pool factory exhausted attempts.");
                    },
                    effectiveCt);
                client = pooledEntry.Client;
                session = pooledEntry.Session;

                await pooledEntry.SendLock.WaitAsync(effectiveCt);
                pooledLockAcquired = true;
            }
            else
            {
                // Retry once on connection/launch failures. With the shared server
                // this mainly covers transient connection errors or server restarts.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        // 2026-05-13 -- worktree-existence pre-check; see
                        // companion comment in the pooled branch above.
                        EnsureWorkingDirectoryAccessible(sessionConfig);
                        client = new CopilotClient(clientOptions);
                        session = await client.CreateSessionAsync(sessionConfig, effectiveCt);
                        break;
                    }
                    catch (Exception ex) when (attempt == 0 && IsProcessLaunchError(ex))
                    {
                        this.log.LogWarning(ex,
                            "Copilot SDK connection failed (attempt 1), retrying after delay");
                        try { if (client is not null) await client.DisposeAsync(); } catch { }
                        client = null;
                        session = null;
                        await Task.Delay(2000, effectiveCt);
                    }
                }
            }

            // 2026-05-16 — Phase 72: capture provider-native session id
            // for memory persistence. The Copilot SDK assigns a session
            // id on CreateSessionAsync; persisting it via AgentMemoryStore
            // lets future iters surface "session reused" in the UI and
            // enables eventual server-side resume when the SDK exposes
            // it. Stamped on the final Finished/Failed event below.
            if (session is not null)
            {
                capturedSessionId = session.SessionId;
                if (!string.IsNullOrEmpty(capturedSessionId))
                {
                    queue.Writer.TryWrite(new AgentEvent(
                        AgentEventKind.SessionInfo,
                        DateTimeOffset.UtcNow,
                        SessionId: capturedSessionId));
                }
            }

            subscription = session!.On(evt =>
            {
                // Reset the inactivity watchdog before any per-event work.
                // Any callback invocation — even an event we don't surface —
                // is proof the transport is alive.
                Interlocked.Exchange(ref lastEventTicks, DateTimeOffset.UtcNow.Ticks);
                try
                {
                    switch (evt)
                    {
                        case AssistantMessageDeltaEvent delta:
                            var chunk = delta.Data.DeltaContent;
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                contentBuilder.Append(chunk);
                                queue.Writer.TryWrite(new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: chunk));
                            }
                            break;

                        case AssistantMessageEvent msg:
                            var content = msg.Data.Content;
                            if (!string.IsNullOrEmpty(content))
                            {
                                contentBuilder.Append(content);
                                queue.Writer.TryWrite(new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: content));
                            }
                            break;

                        case ToolExecutionStartEvent tStart:
                            // Phase 64 — stamp per-tool-call watchdog state so
                            // the inactivity timer below can distinguish a
                            // legitimate long tool call from a stuck one.
                            Interlocked.Exchange(ref lastToolStartTicks, DateTimeOffset.UtcNow.Ticks);
                            lastToolName = tStart.Data.ToolName;
                            // 2026-05-16 — Phase 72: remember start tick by
                            // ToolCallId so the matching complete event can
                            // compute durationMs without UI-side reconstruction.
                            if (!string.IsNullOrEmpty(tStart.Data.ToolCallId))
                            {
                                toolStartTicks[tStart.Data.ToolCallId!] = DateTimeOffset.UtcNow.Ticks;
                            }
                            queue.Writer.TryWrite(new AgentEvent(
                                AgentEventKind.ToolCall,
                                DateTimeOffset.UtcNow,
                                ToolName: tStart.Data.ToolName,
                                ToolPayload: tStart.Data.Arguments is null
                                    ? null
                                    : JsonSerializer.Serialize(tStart.Data.Arguments),
                                ToolCallId: tStart.Data.ToolCallId,
                                ParentToolCallId: tStart.Data.ParentToolCallId,
                                McpServerName: tStart.Data.McpServerName));
                            break;

                        case ToolExecutionCompleteEvent tEnd:
                            // Phase 64 — clear the per-tool watchdog stamp now
                            // that the tool returned.
                            Interlocked.Exchange(ref lastToolStartTicks, 0);
                            turnCount++;
                            // 2026-05-16 — Phase 72: compute durationMs from
                            // matching start tick, and extract a typed exit
                            // code when the tool returned terminal content
                            // (ToolExecutionCompleteContentTerminal carries
                            // ExitCode + Cwd + Text). Falls back to null on
                            // tools without typed content so non-shell calls
                            // simply omit the field.
                            long? toolDurationMs = null;
                            if (!string.IsNullOrEmpty(tEnd.Data.ToolCallId)
                                && toolStartTicks.TryRemove(tEnd.Data.ToolCallId!, out var startTicks))
                            {
                                toolDurationMs = (DateTimeOffset.UtcNow.Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
                            }
                            int? toolExitCode = ExtractTerminalExitCode(tEnd.Data.Result);
                            queue.Writer.TryWrite(new AgentEvent(
                                AgentEventKind.ToolResult,
                                DateTimeOffset.UtcNow,
                                ToolPayload: tEnd.Data.Result is null
                                    ? null
                                    : JsonSerializer.Serialize(tEnd.Data.Result),
                                Error: tEnd.Data.Success
                                    ? null
                                    : tEnd.Data.Error?.ToString(),
                                ToolCallId: tEnd.Data.ToolCallId,
                                ParentToolCallId: tEnd.Data.ParentToolCallId,
                                ToolDurationMs: toolDurationMs,
                                ToolExitCode: toolExitCode));
                            if (turnCount >= maxTurns)
                            {
                                failReason = $"Max turns ({maxTurns}) reached";
                                done.TrySetResult();
                            }
                            break;

                        case AssistantUsageEvent usage:
                            // SDK exposes usage as double? — cast to long.
                            inputTokens += (long)(usage.Data.InputTokens ?? 0);
                            outputTokens += (long)(usage.Data.OutputTokens ?? 0);
                            // 2026-05-16 — Phase 72: accumulate cache +
                            // reasoning tokens so the iter-kpis API can
                            // expose cache-hit % and break out reasoning
                            // cost separately from visible output. Cast
                            // matches the existing input/output pattern.
                            cacheReadTokens += (long)(usage.Data.CacheReadTokens ?? 0);
                            cacheWriteTokens += (long)(usage.Data.CacheWriteTokens ?? 0);
                            reasoningTokens += (long)(usage.Data.ReasoningTokens ?? 0);
                            break;

                        case SessionUsageInfoEvent sessUsage:
                            // 2026-05-16 — Phase 72: snapshot of the live
                            // SDK session's full context size after the
                            // model processed this turn. Latest wins —
                            // we surface the most recent observation on
                            // the final Finished event so operators can
                            // tell at a glance whether the session is
                            // running with 6K of context (good) or 200K
                            // (bloated; SDK is replaying history).
                            if (sessUsage.Data.ConversationTokens is double ct2)
                            {
                                conversationTokensLatest = (long)ct2;
                            }
                            break;

                        case SessionIdleEvent:
                            done.TrySetResult();
                            break;

                        case SessionErrorEvent err:
                            failReason = $"Copilot session error: {err.Data?.Message ?? "(no details)"}";
                            this.log.LogWarning("Copilot SDK SessionErrorEvent: {Msg}", err.Data?.Message);
                            done.TrySetResult();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    this.log.LogWarning(ex, "Copilot SDK event handler failed");
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt }, effectiveCt);

            // Drain the channel while the session pumps events. Exit when the
            // SDK signals idle/error, cancellation fires, or the wall-clock
            // budget expires (effectiveCt incorporates CancelAfter).
            _ = Task.Run(async () =>
            {
                try { await done.Task.WaitAsync(effectiveCt); }
                catch { /* fall through to completer */ }
                queue.Writer.TryComplete();
            }, CancellationToken.None);

            // Inactivity watchdog. Polls every InactivitySdkPollInterval and,
            // if no SDK callback has fired for the configured inactivity
            // window, treats the session as dead — sets failReason and
            // completes `done` so the consumer loop above unblocks.
            // Phase 64 — threshold is now operator-configurable via
            // Forge:Agents:InactivityTimeout (default 10 min). The
            // original 5-min threshold killed healthy agents whose
            // single tool call (e.g. dotnet build) sat silent legitimately
            // for 4–6 min on this repo.
            var inactivityWindow = this.options.Value.InactivityTimeout < InactivitySdkFloor
                ? InactivitySdkFloor
                : this.options.Value.InactivityTimeout;
            var perTurnWindow = this.options.Value.PerTurnTimeout < InactivitySdkFloor
                ? InactivitySdkFloor
                : this.options.Value.PerTurnTimeout;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!effectiveCt.IsCancellationRequested && !done.Task.IsCompleted)
                    {
                        try { await Task.Delay(InactivitySdkPollInterval, effectiveCt); }
                        catch (OperationCanceledException) { return; }

                        var now = DateTimeOffset.UtcNow;

                        // Phase 64 — per-tool-call check first. A long-running
                        // tool subprocess (e.g. a hung `dotnet build`) keeps
                        // ToolExecutionStartEvent in flight without a matching
                        // Complete. The transport itself is not silent (other
                        // SDK callbacks may still fire), so the inactivity
                        // gap alone won't catch it. Catch it explicitly.
                        var toolStartTicks = Interlocked.Read(ref lastToolStartTicks);
                        if (toolStartTicks != 0)
                        {
                            var toolUtc = new DateTimeOffset(toolStartTicks, TimeSpan.Zero);
                            var toolGap = now - toolUtc;
                            if (toolGap > perTurnWindow)
                            {
                                var toolName = lastToolName ?? "(unknown)";
                                this.log.LogWarning(
                                    "Copilot SDK stuck on tool {Tool} for {Gap} (> {Threshold}) — killing session",
                                    toolName, toolGap, perTurnWindow);
                                wasStuckOnTool = true;
                                failReason = $"Generator stuck on tool '{toolName}' for {toolGap.TotalMinutes:0.0}min (>{perTurnWindow.TotalMinutes:0}min per-turn budget)";
                                done.TrySetResult();
                                return;
                            }
                        }

                        var lastUtc = new DateTimeOffset(Interlocked.Read(ref lastEventTicks), TimeSpan.Zero);
                        var gap = now - lastUtc;
                        if (gap > inactivityWindow)
                        {
                            this.log.LogWarning(
                                "Copilot SDK silent for {Gap} (> {Threshold}) — assuming transport died, completing session",
                                gap, inactivityWindow);
                            wasInactive = true;
                            failReason = $"Copilot transport silent for {gap.TotalMinutes:0.0}min (>{inactivityWindow.TotalMinutes:0}min) — assumed dead";
                            done.TrySetResult();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Watchdog is best-effort; never let it crash the session.
                    this.log.LogDebug(ex, "Inactivity watchdog errored");
                }
            }, CancellationToken.None);

            while (true)
            {
                try
                {
                    if (!await queue.Reader.WaitToReadAsync(effectiveCt)) break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                    break;
                }

                while (queue.Reader.TryRead(out var ev))
                {
                    yield return ev;
                }
            }
        }
        finally
        {
            subscription?.Dispose();
            if (isPooledPath)
            {
                // Pool owns the session + client; do NOT dispose them
                // here. Release the SendLock so the next iter for this
                // (workItem, agent) tuple can run its own SendAsync on
                // the same live session.
                if (pooledLockAcquired && pooledEntry is not null)
                {
                    try { pooledEntry.SendLock.Release(); }
                    catch (Exception ex)
                    {
                        this.log.LogWarning(ex,
                            "CopilotSessionPool: SendLock release threw for ({Task}, {Agent}).",
                            taskId, agentId);
                    }
                }
            }
            else
            {
                if (session is not null)
                {
                    try { await session.DisposeAsync(); } catch { /* best-effort */ }
                }
                if (client is not null)
                {
                    try { await client.DisposeAsync(); } catch { /* best-effort */ }
                }
            }
        }

        if (wasStuckOnTool)
        {
            yield return this.BuildFailedWithUsage(
                error: failReason ?? $"Generator stuck on tool >{this.options.Value.PerTurnTimeout.TotalMinutes:0}min",
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: null,
                promptCharCount: prompt?.Length ?? 0,
                outputCharCount: contentBuilder.Length,
                modelName: context.Model);
            yield break;
        }

        if (wasInactive)
        {
            yield return this.BuildFailedWithUsage(
                error: failReason ?? $"Copilot transport silent for >{this.options.Value.InactivityTimeout.TotalMinutes:0}min",
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: null,
                promptCharCount: prompt?.Length ?? 0,
                outputCharCount: contentBuilder.Length,
                modelName: context.Model);
            yield break;
        }

        if (timedOut)
        {
            yield return this.BuildFailedWithUsage(
                error: $"Copilot SDK hard timeout: exceeded {budget.TotalMinutes:0}m budget",
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: null,
                promptCharCount: prompt?.Length ?? 0,
                outputCharCount: contentBuilder.Length,
                modelName: context.Model);
            yield break;
        }

        if (failReason is not null)
        {
            yield return this.BuildFailedWithUsage(
                error: failReason,
                accumulatedInputTokens: inputTokens,
                accumulatedOutputTokens: outputTokens,
                accumulatedCostUsd: null,
                promptCharCount: prompt?.Length ?? 0,
                outputCharCount: contentBuilder.Length,
                modelName: context.Model);
            yield break;
        }

        // SDK doesn't always emit usage; estimate when missing.
        if (outputTokens == 0) outputTokens = Math.Max(1, contentBuilder.Length / 4);
        if (inputTokens == 0) inputTokens = Math.Max(1, prompt.Length / 4);

        var modelInfo = this.models.Find(context.Model);
        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CostUsd: ComputeCost(modelInfo, inputTokens, outputTokens),
            // 2026-05-16 — Phase 72: stamp telemetry on the Finished
            // event so downstream usage-writers (CrewQueueWorker,
            // CrewStoryArtifactStrategy) can persist them to
            // AgentUsageRow without re-scanning the event stream.
            SessionId: capturedSessionId,
            CacheReadTokens: cacheReadTokens > 0 ? cacheReadTokens : null,
            CacheWriteTokens: cacheWriteTokens > 0 ? cacheWriteTokens : null,
            ReasoningTokens: reasoningTokens > 0 ? reasoningTokens : null,
            ConversationTokens: conversationTokensLatest > 0 ? conversationTokensLatest : null);
    }

    /// <summary>
    /// 2026-05-16 — Phase 72: extract an exit code from a Copilot SDK
    /// <c>ToolExecutionCompleteResult</c>. Terminal-flavored tool
    /// results carry a typed <c>ToolExecutionCompleteContentTerminal</c>
    /// inside <c>Contents</c>; the SDK populates its <c>ExitCode</c>
    /// only when the tool was shell-shaped (<c>run_in_terminal</c>,
    /// <c>bash</c>, etc.). Returns null for non-shell tools so the
    /// caller can skip stamping the field.
    /// </summary>
    private static int? ExtractTerminalExitCode(ToolExecutionCompleteResult? result)
    {
        var contents = result?.Contents;
        if (contents is null) return null;
        foreach (var c in contents)
        {
            // SDK exposes ExitCode as double? (Nullable<double>); cast to
            // int once we've unwrapped the nullable.
            if (c is ToolExecutionCompleteContentTerminal term && term.ExitCode is double ec)
            {
                return (int)ec;
            }
        }
        return null;
    }

    private static async IAsyncEnumerable<AgentEvent> RunUnsupported(AgentContext context)
    {
        await Task.Yield();
        yield return new AgentEvent(
            AgentEventKind.Failed,
            DateTimeOffset.UtcNow,
            Error: $"Unsupported (backend, mode) combo: {context.Backend} / {context.InvokeMode}");
    }

    // ── shared helpers ─────────────────────────────────────────────────────

    private static int BackoffMs(int attempt)
        => Math.Min(2000 * (int)Math.Pow(2, attempt - 1), 30000);

    private static decimal ComputeCost(Forge.Api.Contracts.ModelInfo? model, long inputTokens, long outputTokens)
    {
        if (model is null) return 0m;
        var input = model.InputPricePerMTok * inputTokens / 1_000_000m;
        var output = model.OutputPricePerMTok * outputTokens / 1_000_000m;
        return Math.Round(input + output, 6);
    }

    // 2026-05-13 root-cause fix: every Failed yield from a path that has
    // already done provider work (streamed tokens / executed tool calls)
    // MUST carry the accumulated usage so the cost ledger and Phase 51
    // budget meter reflect what the operator actually paid for. Before
    // this, a Copilot/SDK run that hit the per-task max-turns cap on
    // turn 60 of a 13-pt workstream wrote `agent_usage(InputTokens=0,
    // OutputTokens=0, CostUsd=$0)` even though the underlying provider
    // session burned ~7.5 minutes of real tokens, and the operator
    // saw a 0-cost generator next to a $7.31 evaluator on the same
    // iter — creating the impression that the generator "didn't run".
    //
    // Use accumulated tokens when the SDK surfaced usage events; fall
    // back to chars/4 (the same heuristic the Finished path uses) when
    // the path never received a usage event before failing. Either way
    // the consumer (PumpEvents / CrewQueueWorker accumulator /
    // CrewStoryArtifactStrategy generator delegate) now receives a
    // non-zero estimate instead of silently dropping the spend.
    // The helper is `internal` (rather than private) so the
    // Forge.Api.Tests project can pin its behaviour directly via
    // `Phase58GeneratorCapTests.BuildFailedWithUsage_*`. The CSPROJ
    // already grants InternalsVisibleTo for Forge.Api.Tests.
    internal AgentEvent BuildFailedWithUsage(
        string error,
        long accumulatedInputTokens,
        long accumulatedOutputTokens,
        decimal? accumulatedCostUsd,
        int promptCharCount,
        int outputCharCount,
        string modelName,
        string? sessionId = null)
    {
        var modelInfo = this.models.Find(modelName);
        var inTok = accumulatedInputTokens > 0
            ? accumulatedInputTokens
            : Math.Max(1, promptCharCount / 4);
        var outTok = accumulatedOutputTokens > 0
            ? accumulatedOutputTokens
            : Math.Max(1, outputCharCount / 4);
        var cost = accumulatedCostUsd is decimal c && c > 0m
            ? c
            : ComputeCost(modelInfo, inTok, outTok);
        return new AgentEvent(
            AgentEventKind.Failed,
            DateTimeOffset.UtcNow,
            Error: error,
            InputTokens: inTok,
            OutputTokens: outTok,
            CostUsd: cost,
            SessionId: sessionId);
    }

    /// <summary>
    /// Resolves a CLI executable. Honours the operator override first, then
    /// the well-known npm-global location (Windows), then bare PATH lookup.
    /// </summary>
    private static string ResolveCli(string? overridePath, string name)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            // Some packages publish a native exe alongside the .cmd shim — prefer it.
            var npmExe = Path.Combine(appData, "npm", "node_modules", $"@github/{name}",
                "node_modules", $"@github/{name}-win32-x64", $"{name}.exe");
            if (File.Exists(npmExe)) return npmExe;
            var npmCmd = Path.Combine(appData, "npm", $"{name}.cmd");
            if (File.Exists(npmCmd)) return npmCmd;
        }

        // Fallback — the OS process resolver will walk PATH.
        return name;
    }

    private static IEnumerable<string> SplitForStub(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            yield return "(stub)";
            yield break;
        }
        var parts = prompt.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            yield return i == 0 ? parts[i] : " " + parts[i];
        }
    }

    /// <summary>
    /// Crews store Anthropic-style hyphen IDs (<c>claude-opus-4-7</c>). The
    /// Copilot SDK/CLI requires dotted IDs (<c>claude-opus-4.7</c>). This
    /// method normalizes when the backend is Copilot.
    /// </summary>
    public static string NormalizeModelForBackend(string model, AgentBackend backend)
    {
        if (backend != AgentBackend.Copilot) return model;
        if (string.IsNullOrEmpty(model)) return model;
        // Already dotted → no-op.
        if (model.Contains('.')) return model;

        // Known mappings: anthropic-hyphen → copilot-dotted.
        return model switch
        {
            "claude-opus-4-7"            => "claude-opus-4.7",
            "claude-opus-4-6"            => "claude-opus-4.6",
            "claude-sonnet-4-6"          => "claude-sonnet-4.6",
            "claude-sonnet-4-5"          => "claude-sonnet-4.5",
            "claude-haiku-4-5-20251001"  => "claude-haiku-4.5",
            _ => model,
        };
    }

    /// <summary>
    /// Determines if an exception is a process-launch failure (Win32 / IO /
    /// socket errors from spawning copilot.exe). These are retryable because
    /// the underlying cause is typically transient resource contention (stale
    /// orphan processes locking the binary handle, or socket exhaustion).
    /// Auth, model, and protocol errors are NOT matched here and propagate.
    /// </summary>
    private static bool IsProcessLaunchError(Exception ex)
    {
        // Flatten to innermost for wrapped SDK exceptions.
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;

        // Win32Exception from Process.Start (ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION, etc.)
        if (inner is System.ComponentModel.Win32Exception) return true;

        // IOException from pipe/socket setup during child process spawn.
        if (inner is IOException) return true;

        // Message-based fallback: the .NET runtime wraps some launch failures
        // with this specific phrasing.
        var msg = ex.Message;
        if (msg.Contains("start process", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("ENOTCONN", StringComparison.OrdinalIgnoreCase)) return true;

        // 2026-05-13 -- the shared copilot.exe server returns
        // "Directory does not exist or cannot be accessed: <path>" from
        // its session.create handler when the worktree was wiped between
        // EnsureWorktreeAsync and CreateSessionAsync. Same race semantics
        // as a process-launch error (transient, retryable after a brief
        // delay because EnsureWorktreeAsync upstream is idempotent + the
        // next orchestrator drive cycle will re-create the worktree if
        // the deletion is real, OR the OS will catch up if it was only
        // a filesystem-visibility lag).
        if (msg.Contains("Directory does not exist", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains(WorktreeMissingMarker, StringComparison.Ordinal)) return true;
        if (msg.Contains("cannot be accessed", StringComparison.OrdinalIgnoreCase)
            && msg.Contains("session.create", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// 2026-05-13 -- magic marker embedded in worktree-missing error
    /// messages so <see cref="IsProcessLaunchError"/> recognises our own
    /// pre-check failures as retryable. Keeps the diagnostic string
    /// operator-readable while still being machine-matchable.
    /// </summary>
    private const string WorktreeMissingMarker = "[forge:worktree-missing]";

    /// <summary>
    /// 2026-05-13 -- defensive pre-check before invoking
    /// <see cref="CopilotClient.CreateSessionAsync"/>. The shared
    /// copilot.exe server validates cwd server-side and the SDK marshals
    /// its rejection through as
    /// <c>"Communication error with Copilot CLI: Request session.create
    /// failed with message: Directory does not exist or cannot be
    /// accessed: &lt;path&gt;"</c>. That error halts the orchestrator and
    /// requires operator triage even though the underlying issue is
    /// usually a transient race (worktree wiped during a restart/
    /// resume cycle, Phase 49 migration, or DeleteSpawn). Probing the
    /// path on the .NET side first lets us:
    /// <list type="number">
    /// <item>Throw a clearer, sentinel-tagged error message.</item>
    /// <item>Engage the existing 2 s retry-once arm so brief filesystem
    /// visibility lags self-heal without an operator intervention.</item>
    /// <item>Provide a single grep-able marker
    /// (<see cref="WorktreeMissingMarker"/>) for incident triage.</item>
    /// </list>
    /// </summary>
    private static void EnsureWorkingDirectoryAccessible(SessionConfig cfg)
    {
        var cwd = cfg.WorkingDirectory;
        if (string.IsNullOrEmpty(cwd)) return; // SDK defaults are fine.
        if (Directory.Exists(cwd)) return;
        throw new InvalidOperationException(
            $"{WorktreeMissingMarker} Worktree path does not exist at the time of "
            + $"Copilot session.create: '{cwd}'. The worktree was likely deleted "
            + "between EnsureWorktreeAsync and CreateSessionAsync (e.g. concurrent "
            + "Restart/Resume, Phase 49 migration, or DeleteSpawn). The next "
            + "orchestrator drive cycle will re-invoke EnsureWorktreeAsync; this "
            + "attempt will retry once after 2 s, then propagate.");
    }
}
