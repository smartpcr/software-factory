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

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory http;
    private readonly IModelRegistry models;
    private readonly IOptions<AgentRunnerOptions> options;
    private readonly ILogger<AgentRunnerDispatcher> log;

    private readonly ConcurrentDictionary<string, AgentState> agentStates = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();

    public AgentRunnerDispatcher(
        IHttpClientFactory http,
        IModelRegistry models,
        IOptions<AgentRunnerOptions> options,
        ILogger<AgentRunnerDispatcher> log)
    {
        this.http = http;
        this.models = models;
        this.options = options;
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
        this.active[invocationId] = linked;
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
                (AgentBackend.Copilot, AgentInvokeMode.Sdk) => this.RunCopilotSdk(prompt, ctx, linked.Token),
                (AgentBackend.Copilot, AgentInvokeMode.Cli) => this.RunCopilotCli(prompt, ctx, linked.Token),
                _ => RunUnsupported(ctx),
            };
        }

        var stream = this.WrapEvents(invocationId, agentId, raw);
        return Task.FromResult(new AgentInvocation(invocationId, agentId, ctx.CrewId, taskId, resolvedModel, stream));
    }

    public Task CancelAsync(string invocationId, CancellationToken ct)
    {
        if (this.active.TryRemove(invocationId, out var cts))
        {
            cts.Cancel();
        }
        return Task.CompletedTask;
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
            if (this.active.TryRemove(invocationId, out var cts)) cts.Dispose();
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
            messages = new[] { new { role = "user", content = prompt } },
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
            yield return new AgentEvent(AgentEventKind.Failed, DateTimeOffset.UtcNow, Error: failReason);
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

    private async IAsyncEnumerable<AgentEvent> RunClaudeCli(
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
            Arguments = $"--print --model {context.Model}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(context.WorktreePath) ? null : context.WorktreePath,
        };

        await foreach (var ev in this.RunCliAsync(psi, fullPrompt, context, ct))
        {
            yield return ev;
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

        // copilot CLI: `--prompt -` reads from stdin, `-m` selects model.
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
        psi.ArgumentList.Add("-m");
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

        var output = new StringBuilder();
        Task<string>? stderrTask = null;
        try
        {
            using var ctReg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already exited */ }
            });

            await process.StandardInput.WriteAsync(stdinPrompt);
            process.StandardInput.Close();

            stderrTask = process.StandardError.ReadToEndAsync(ct);

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                output.AppendLine(line);
                if (line.Length > 0)
                {
                    yield return new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: line + "\n");
                }
            }

            await process.WaitForExitAsync(ct);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort cleanup */ }
        }

        var stderr = stderrTask is null ? string.Empty : await stderrTask;
        var exitCode = process.ExitCode;
        process.Dispose();

        if (exitCode != 0)
        {
            yield return new AgentEvent(
                AgentEventKind.Failed,
                DateTimeOffset.UtcNow,
                Error: $"{Path.GetFileName(psi.FileName)} exit={exitCode}: {stderr.Trim()}");
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
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        var contentBuilder = new StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;
        string? failReason = null;
        int turnCount = 0;
        var maxTurns = this.options.Value.MaxTurns;

        var clientOptions = new CopilotClientOptions
        {
            CliArgs = this.options.Value.CopilotCliExtraArgs,
            CliPath = ResolveCli(this.options.Value.CopilotCliPath, "copilot"),
            Cwd = string.IsNullOrEmpty(context.WorktreePath) ? null : context.WorktreePath,
        };

        // Stage results into a channel so the SDK's event subscription (which
        // is not async-iter-friendly) feeds an IAsyncEnumerable cleanly.
        var queue = Channel.CreateUnbounded<AgentEvent>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        CopilotSession? session = null;
        IDisposable? subscription = null;
        CopilotClient? client = null;
        try
        {
            client = new CopilotClient(clientOptions);
            var sessionConfig = new SessionConfig
            {
                Model = context.Model,
                OnPermissionRequest = PermissionHandler.ApproveAll,
            };
            if (!string.IsNullOrEmpty(context.SoulMd))
            {
                sessionConfig.SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = context.SoulMd,
                };
            }

            session = await client.CreateSessionAsync(sessionConfig, ct);

            subscription = session.On(evt =>
            {
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

                        case ToolExecutionStartEvent tStart:
                            queue.Writer.TryWrite(new AgentEvent(
                                AgentEventKind.ToolCall,
                                DateTimeOffset.UtcNow,
                                ToolName: tStart.Data.ToolName,
                                ToolPayload: tStart.Data.Arguments is null
                                    ? null
                                    : JsonSerializer.Serialize(tStart.Data.Arguments)));
                            break;

                        case ToolExecutionCompleteEvent tEnd:
                            turnCount++;
                            queue.Writer.TryWrite(new AgentEvent(
                                AgentEventKind.ToolResult,
                                DateTimeOffset.UtcNow,
                                ToolPayload: tEnd.Data.Result is null
                                    ? null
                                    : JsonSerializer.Serialize(tEnd.Data.Result),
                                Error: tEnd.Data.Success
                                    ? null
                                    : tEnd.Data.Error?.ToString()));
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
                            break;

                        case SessionIdleEvent:
                            done.TrySetResult();
                            break;

                        case SessionErrorEvent:
                            failReason = "Copilot session error";
                            done.TrySetResult();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    this.log.LogWarning(ex, "Copilot SDK event handler failed");
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt }, ct);

            // Drain the channel while the session pumps events. Exit when the
            // SDK signals idle/error OR cancellation fires.
            var hardDeadline = DateTime.UtcNow + this.options.Value.WallClockBudget;
            _ = Task.Run(async () =>
            {
                try { await done.Task.WaitAsync(ct); }
                catch { /* fall through to completer */ }
                queue.Writer.TryComplete();
            }, ct);

            while (await queue.Reader.WaitToReadAsync(ct))
            {
                while (queue.Reader.TryRead(out var ev))
                {
                    yield return ev;
                }
                if (DateTime.UtcNow >= hardDeadline)
                {
                    failReason ??= $"Hard timeout: exceeded {this.options.Value.WallClockBudget.TotalMinutes:0}m budget";
                    queue.Writer.TryComplete();
                    break;
                }
            }
        }
        finally
        {
            subscription?.Dispose();
            if (session is not null)
            {
                try { await session.DisposeAsync(); } catch { /* best-effort */ }
            }
            if (client is not null)
            {
                try { await client.DisposeAsync(); } catch { /* best-effort */ }
            }
        }

        if (failReason is not null)
        {
            yield return new AgentEvent(AgentEventKind.Failed, DateTimeOffset.UtcNow, Error: failReason);
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
            CostUsd: ComputeCost(modelInfo, inputTokens, outputTokens));
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
}
