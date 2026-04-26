// -----------------------------------------------------------------------
// <copyright file="ClaudeAgentRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 11.5 — concrete agent runner. Two paths:
///   * <c>local-stub</c> model — deterministic offline token stream so tests
///     and offline dev exercise the full event + ledger plumbing without
///     making network calls.
///   * Anthropic models — POST <c>/v1/messages</c> with streaming SSE,
///     prompt-caching the soul.md system block, retry-with-backoff on
///     429 / 503 / 529.
/// One scoped <see cref="HttpClient"/> per invocation; no shared state across
/// agents. Per-agent state (idle / working / paused) lives in a process-local
/// dictionary keyed on <c>agentId</c>.
/// </summary>
public sealed class ClaudeAgentRunner : IAgentRunner
{
    public const string ClientName = "AnthropicMessages";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory http;
    private readonly IModelRegistry models;
    private readonly IOptions<AgentRunnerOptions> options;
    private readonly ILogger<ClaudeAgentRunner> log;

    private readonly ConcurrentDictionary<string, AgentState> agentStates = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();

    public ClaudeAgentRunner(
        IHttpClientFactory http,
        IModelRegistry models,
        IOptions<AgentRunnerOptions> options,
        ILogger<ClaudeAgentRunner> log)
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

        var raw = string.Equals(context.Model, "local-stub", StringComparison.OrdinalIgnoreCase)
            ? this.RunLocalStub(invocationId, prompt, context, linked.Token)
            : this.RunClaude(invocationId, prompt, context, linked.Token);

        var stream = this.WrapEvents(invocationId, agentId, raw);
        return Task.FromResult(new AgentInvocation(invocationId, agentId, context.CrewId, taskId, context.Model, stream));
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

    private async IAsyncEnumerable<AgentEvent> RunLocalStub(
        string invocationId,
        string prompt,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Started, DateTimeOffset.UtcNow);

        var deltas = SplitForStub(prompt);
        long outTokens = 0;
        foreach (var delta in deltas)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct);
            outTokens += Math.Max(1, delta.Length / 4);
            yield return new AgentEvent(AgentEventKind.Token, DateTimeOffset.UtcNow, Text: delta);
        }

        var inTokens = Math.Max(1, prompt.Length / 4);
        var model = this.models.Find(context.Model);
        var cost = ComputeCost(model, inTokens, outTokens);
        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inTokens,
            OutputTokens: outTokens,
            CostUsd: cost);
    }

    private async IAsyncEnumerable<AgentEvent> RunClaude(
        string invocationId,
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

        var model = this.models.Find(context.Model);
        if (model is null)
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
            model = model.Id,
            max_tokens = 4096,
            stream = true,
            // Phase 11.5 — prompt-cache the soul.md system block (5-min TTL).
            system = new[]
            {
                new
                {
                    type = "text",
                    text = context.SoulMd,
                    cache_control = new { type = "ephemeral" },
                },
            },
            messages = new[]
            {
                new { role = "user", content = prompt },
            },
        };
        var payload = JsonSerializer.Serialize(body, JsonOpts);

        long inputTokens = 0;
        long outputTokens = 0;
        string? failReason = null;

        // Retry loop on 429 / 503 / 529.
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

            // Stream parse: yield each token delta, accumulate usage from the
            // message_stop event.
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

        var cost = ComputeCost(model, inputTokens, outputTokens);
        yield return new AgentEvent(
            AgentEventKind.Finished,
            DateTimeOffset.UtcNow,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CostUsd: cost);
    }

    private static int BackoffMs(int attempt)
        => Math.Min(2000 * (int)Math.Pow(2, attempt - 1), 30000);

    private static decimal ComputeCost(Forge.Api.Contracts.ModelInfo? model, long inputTokens, long outputTokens)
    {
        if (model is null) return 0m;
        var input = model.InputPricePerMTok * inputTokens / 1_000_000m;
        var output = model.OutputPricePerMTok * outputTokens / 1_000_000m;
        return Math.Round(input + output, 6);
    }

    private static IEnumerable<string> SplitForStub(string prompt)
    {
        // Deterministic tokens for offline tests: split on whitespace,
        // preserve the spaces so the assembled output is recoverable.
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
}
