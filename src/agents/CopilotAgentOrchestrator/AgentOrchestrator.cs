// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator;

using System.Collections.Concurrent;
using System.Diagnostics;
using GitHub.Copilot.SDK;

/// <summary>
/// Orchestrates multiple subagents running in parallel with independent context windows.
/// Analogous to Claude Code's Task tool with explore/task/general-purpose agent types.
/// All models are accessed through the Copilot enterprise subscription.
/// </summary>
/// <remarks>
/// <para>
/// Each subagent is a separate <see cref="CopilotSession"/> with its own fresh context window,
/// system prompt, and model selection. This avoids the context pollution problem where a single
/// session's exploration overhead starves the synthesis budget.
/// </para>
/// <para>
/// Usage pattern: spawn N specialist workers in parallel → collect results → synthesize in
/// a fresh aggregation session with only the distilled outputs.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var client = new CopilotClient();
/// var orchestrator = new AgentOrchestrator(client);
///
/// var result = await orchestrator.RunAsync(new[]
/// {
///     new SubAgentConfig { Id = "di", AgentType = AgentType.Explore, Prompt = "Analyze DI registration..." },
///     new SubAgentConfig { Id = "serial", AgentType = AgentType.Explore, Prompt = "Analyze serialization..." },
/// }, AggregationStrategy.Synthesize);
///
/// Console.WriteLine(result.AggregatedContent);
/// </code>
/// </example>
public class AgentOrchestrator : IAsyncDisposable
{
    private readonly CopilotClient client;
    private readonly OrchestratorConfig config;
    private readonly ConcurrentDictionary<string, Task<AgentResult>> backgroundAgents = new();
    private readonly List<CopilotSession> managedSessions = [];
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, AgentProgress> liveProgress = new();

    /// <summary>
    /// Live progress of all agents being executed. Updated in real-time as streaming events arrive.
    /// </summary>
    public IReadOnlyDictionary<string, AgentProgress> LiveProgress => this.liveProgress;

    /// <summary>
    /// Test-only hook: when set, <see cref="ExecuteSingleAgentAsync"/> delegates to this
    /// function instead of creating a real Copilot session. Allows unit tests to verify
    /// scheduling behavior (concurrency, wave ordering) without a live server.
    /// </summary>
    internal Func<SubAgentConfig, CancellationToken, Task<AgentResult>>? ExecuteOverride { get; set; }

    /// <summary>
    /// Initializes a new <see cref="AgentOrchestrator"/> that uses the given Copilot client
    /// to spawn subagent sessions via the enterprise subscription.
    /// </summary>
    /// <param name="client">A <see cref="CopilotClient"/> connected to the Copilot CLI server.</param>
    /// <param name="config">Optional orchestrator configuration. Uses defaults if null.</param>
    public AgentOrchestrator(CopilotClient client, OrchestratorConfig? config = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.config = config ?? new OrchestratorConfig();
    }

    /// <summary>
    /// Spawns multiple subagents in parallel, each with a fresh context window,
    /// waits for all to complete, and aggregates results.
    /// </summary>
    /// <param name="agents">The subagent configurations to execute.</param>
    /// <param name="strategy">How to combine the results.</param>
    /// <param name="synthesisPrompt">
    /// Custom synthesis prompt (only used with <see cref="AggregationStrategy.Synthesize"/>).
    /// Use {results} placeholder for agent outputs. If null, uses default.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The orchestrated result containing individual and aggregated outputs.</returns>
    public async Task<OrchestratedResult> RunAsync(
        IEnumerable<SubAgentConfig> agents,
        AggregationStrategy strategy = AggregationStrategy.Concatenate,
        string? synthesisPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var agentList = agents.ToList();

        // Execute all agents in parallel with concurrency limit
        var results = await this.ExecuteAgentsAsync(agentList, cancellationToken);

        // Aggregate based on strategy
        var aggregated = strategy switch
        {
            AggregationStrategy.Concatenate => this.AggregateConcatenate(results),
            AggregationStrategy.Synthesize => await this.AggregateSynthesizeAsync(
                results, synthesisPrompt, cancellationToken),
            AggregationStrategy.Structured => null, // Caller processes AgentResults directly
            _ => null
        };

        stopwatch.Stop();

        return new OrchestratedResult
        {
            AgentResults = results,
            AggregatedContent = aggregated,
            TotalDuration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Spawns a single subagent in the background. Use <see cref="WaitForAgentAsync"/> to get the result.
    /// </summary>
    /// <param name="agentConfig">The agent configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent ID for later retrieval.</returns>
    public string SpawnBackground(SubAgentConfig agentConfig, CancellationToken cancellationToken = default)
    {
        var task = this.ExecuteSingleAgentAsync(agentConfig, cancellationToken);
        this.backgroundAgents[agentConfig.Id] = task;
        return agentConfig.Id;
    }

    /// <summary>
    /// Waits for a background agent to complete and returns its result.
    /// </summary>
    /// <param name="agentId">The agent ID returned by <see cref="SpawnBackground"/>.</param>
    /// <param name="timeout">Maximum time to wait. Default: 5 minutes.</param>
    /// <returns>The agent's result.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no agent with the given ID exists.</exception>
    /// <exception cref="TimeoutException">Thrown when the agent doesn't complete within the timeout.</exception>
    public async Task<AgentResult> WaitForAgentAsync(
        string agentId,
        TimeSpan? timeout = null)
    {
        if (!this.backgroundAgents.TryGetValue(agentId, out var task))
        {
            throw new KeyNotFoundException($"No background agent with ID '{agentId}'");
        }

        var effectiveTimeout = timeout ?? this.config.DefaultTimeout;
        var completed = await System.Threading.Tasks.Task.WhenAny(
            task,
            System.Threading.Tasks.Task.Delay(effectiveTimeout));

        if (completed != task)
        {
            throw new TimeoutException($"Agent '{agentId}' did not complete within {effectiveTimeout}");
        }

        return await task;
    }

    /// <summary>
    /// Lists all background agents and their current status.
    /// </summary>
    public IReadOnlyDictionary<string, bool> ListBackgroundAgents()
    {
        return this.backgroundAgents.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.IsCompleted);
    }

    private async Task<List<AgentResult>> ExecuteAgentsAsync(
        List<SubAgentConfig> agents,
        CancellationToken cancellationToken)
    {
        // Check if any agent has dependencies — if so, use DAG scheduler
        var hasDependencies = agents.Any(a => a.DependsOn is { Count: > 0 });
        if (hasDependencies)
        {
            return await this.ExecuteAgentsWithDependenciesAsync(agents, cancellationToken);
        }

        // Fast path: no dependencies, run all in parallel with concurrency limit
        return await this.ExecuteAllParallelAsync(agents, cancellationToken);
    }

    private async Task<List<AgentResult>> ExecuteAllParallelAsync(
        List<SubAgentConfig> agents,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(this.config.MaxConcurrency);
        var tasks = agents.Select(async agent =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await this.ExecuteSingleAgentAsync(agent, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        if (this.config.ContinueOnFailure)
        {
            await System.Threading.Tasks.Task.WhenAll(tasks);
            return tasks.Select(t => t.Result).ToList();
        }

        var results = new List<AgentResult>();
        foreach (var task in tasks)
        {
            results.Add(await task);
        }

        return results;
    }

    /// <summary>
    /// DAG-based scheduler: topologically sorts agents by their dependencies,
    /// runs each wave of independent agents in parallel, and injects predecessor
    /// results into downstream agent prompts via <c>{result:agent-id}</c> placeholders.
    /// </summary>
    private async Task<List<AgentResult>> ExecuteAgentsWithDependenciesAsync(
        List<SubAgentConfig> agents,
        CancellationToken cancellationToken)
    {
        var agentMap = agents.ToDictionary(a => a.Id);
        var completedResults = new ConcurrentDictionary<string, AgentResult>();
        var allResults = new List<AgentResult>();

        // Validate dependencies exist
        foreach (var agent in agents)
        {
            if (agent.DependsOn == null)
            {
                continue;
            }

            foreach (var dep in agent.DependsOn)
            {
                if (!agentMap.ContainsKey(dep))
                {
                    throw new InvalidOperationException(
                        $"Agent '{agent.Id}' depends on '{dep}', which does not exist in the agent list.");
                }
            }
        }

        // Detect cycles via topological sort
        var sortedWaves = TopologicalSort(agents);

        // Execute wave by wave with concurrency limiting within each wave.
        // Agents in later waves wait for their dependencies to complete first.
        using var semaphore = new SemaphoreSlim(this.config.MaxConcurrency);
        foreach (var wave in sortedWaves)
        {
            var waveTasks = wave.Select(async agent =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var resolvedAgent = this.ResolvePromptPlaceholders(agent, completedResults);
                    return await this.ExecuteSingleAgentAsync(resolvedAgent, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            AgentResult[] waveResults;
            if (this.config.ContinueOnFailure)
            {
                await System.Threading.Tasks.Task.WhenAll(waveTasks);
                waveResults = waveTasks.Select(t => t.Result).ToArray();
            }
            else
            {
                waveResults = new AgentResult[waveTasks.Count];
                for (int i = 0; i < waveTasks.Count; i++)
                {
                    waveResults[i] = await waveTasks[i];
                    if (!waveResults[i].Success)
                    {
                        allResults.AddRange(waveResults.Where(r => r != null)!);
                        return allResults;
                    }
                }
            }

            foreach (var result in waveResults)
            {
                completedResults[result.AgentId] = result;
                allResults.Add(result);
            }
        }

        return allResults;
    }

    /// <summary>
    /// Replaces <c>{result:agent-id}</c> placeholders in an agent's prompt
    /// with the actual content from completed predecessor agents.
    /// </summary>
    internal SubAgentConfig ResolvePromptPlaceholders(
        SubAgentConfig agent,
        ConcurrentDictionary<string, AgentResult> completedResults)
    {
        var prompt = agent.Prompt;
        if (agent.DependsOn == null || !prompt.Contains("{result:"))
        {
            return agent;
        }

        foreach (var depId in agent.DependsOn)
        {
            var placeholder = $"{{result:{depId}}}";
            if (prompt.Contains(placeholder) && completedResults.TryGetValue(depId, out var depResult))
            {
                var replacement = depResult.Success
                    ? depResult.Content ?? "(no content)"
                    : $"(FAILED: {depResult.Error})";
                prompt = prompt.Replace(placeholder, replacement);
            }
        }

        // Return a new config with the resolved prompt (SubAgentConfig is init-only, so rebuild)
        return new SubAgentConfig
        {
            Id = agent.Id,
            Description = agent.Description,
            AgentType = agent.AgentType,
            Model = agent.Model,
            SystemPrompt = agent.SystemPrompt,
            Prompt = prompt,
            FileAttachments = agent.FileAttachments,
            Timeout = agent.Timeout,
            MaxRetries = agent.MaxRetries,
            WorkingDirectory = agent.WorkingDirectory,
            Tools = agent.Tools,
            DependsOn = agent.DependsOn
        };
    }

    /// <summary>
    /// Topological sort of agents into execution waves.
    /// Each wave contains agents whose dependencies are all in prior waves.
    /// Detects cycles.
    /// </summary>
    internal static List<List<SubAgentConfig>> TopologicalSort(List<SubAgentConfig> agents)
    {
        var agentMap = agents.ToDictionary(a => a.Id);
        var inDegree = agents.ToDictionary(a => a.Id, _ => 0);
        var dependents = agents.ToDictionary(a => a.Id, _ => new List<string>());

        foreach (var agent in agents)
        {
            if (agent.DependsOn == null)
            {
                continue;
            }

            foreach (var dep in agent.DependsOn)
            {
                inDegree[agent.Id]++;
                dependents[dep].Add(agent.Id);
            }
        }

        var waves = new List<List<SubAgentConfig>>();
        var ready = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var processed = 0;

        while (ready.Count > 0)
        {
            var wave = new List<SubAgentConfig>();
            var nextReady = new List<string>();

            while (ready.Count > 0)
            {
                var id = ready.Dequeue();
                wave.Add(agentMap[id]);
                processed++;

                foreach (var dep in dependents[id])
                {
                    inDegree[dep]--;
                    if (inDegree[dep] == 0)
                    {
                        nextReady.Add(dep);
                    }
                }
            }

            waves.Add(wave);
            foreach (var id in nextReady)
            {
                ready.Enqueue(id);
            }
        }

        if (processed != agents.Count)
        {
            var cycleNodes = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
            throw new InvalidOperationException(
                $"Circular dependency detected among agents: {string.Join(", ", cycleNodes)}");
        }

        return waves;
    }

    private async Task<AgentResult> ExecuteSingleAgentAsync(
        SubAgentConfig agentConfig,
        CancellationToken cancellationToken)
    {
        if (this.ExecuteOverride != null)
        {
            return await this.ExecuteOverride(agentConfig, cancellationToken);
        }

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var model = agentConfig.Model ?? AgentDefaults.GetDefaultModel(agentConfig.AgentType);
        var systemPrompt = agentConfig.SystemPrompt ?? AgentDefaults.GetDefaultSystemPrompt(agentConfig.AgentType);

        var progress = new AgentProgress
        {
            AgentId = agentConfig.Id,
            ModelUsed = model,
            Status = "running",
            StartedAtUtc = startedAt
        };
        this.liveProgress[agentConfig.Id] = progress;

        CopilotSession? session = null;
        IDisposable? subscription = null;
        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        var currentBlock = new System.Text.StringBuilder();
        var messageBlocks = new List<string>();
        var done = new TaskCompletionSource();

        try
        {
            // Each agent gets its own fresh session = fresh context window
            session = await this.client.CreateSessionAsync(new SessionConfig
            {
                Model = model,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = systemPrompt
                },
                WorkingDirectory = agentConfig.WorkingDirectory,
                Tools = agentConfig.Tools,
                OnPermissionRequest = PermissionHandler.ApproveAll,
            }, cancellationToken);

            await this.TrackSessionAsync(session);

            // Subscribe to events for token tracking.
            // Collect text in blocks separated by AssistantMessageEvent boundaries.
            // When the model uses tools, it produces multiple text blocks:
            // narration ("Let me read...") → tool call → analysis text → tool call → ...
            // We collect all blocks and pick the best one at the end.
            subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        currentBlock.Append(delta.Data.DeltaContent);
                        break;

                    case AssistantMessageEvent msg:
                        // End of a complete message block — save it and start a new one.
                        // If no deltas were received, use the full message content.
                        if (currentBlock.Length == 0 && !string.IsNullOrEmpty(msg.Data.Content))
                        {
                            currentBlock.Append(msg.Data.Content);
                        }

                        var blockText = currentBlock.ToString().Trim();
                        if (!string.IsNullOrEmpty(blockText))
                        {
                            messageBlocks.Add(blockText);
                        }

                        currentBlock.Clear();
                        break;

                    case AssistantUsageEvent usage:
                        inputTokens += (int)(usage.Data.InputTokens ?? 0);
                        outputTokens += (int)(usage.Data.OutputTokens ?? 0);
                        cacheReadTokens += (int)(usage.Data.CacheReadTokens ?? 0);
                        cacheWriteTokens += (int)(usage.Data.CacheWriteTokens ?? 0);
                        progress.InputTokens = inputTokens;
                        progress.OutputTokens = outputTokens;
                        progress.CacheReadTokens = cacheReadTokens;
                        progress.CacheWriteTokens = cacheWriteTokens;
                        break;

                    case SessionIdleEvent:
                        // Flush any remaining content
                        var remaining = currentBlock.ToString().Trim();
                        if (!string.IsNullOrEmpty(remaining))
                        {
                            messageBlocks.Add(remaining);
                        }

                        done.TrySetResult();
                        break;

                    case SessionErrorEvent:
                        done.TrySetResult();
                        break;
                }
            });

            // Build message options
            var options = new MessageOptions { Prompt = agentConfig.Prompt };

            if (agentConfig.FileAttachments is { Count: > 0 })
            {
                options.Attachments = agentConfig.FileAttachments
                    .Select(path => (UserMessageDataAttachmentsItem)new UserMessageDataAttachmentsItemFile
                    {
                        Path = path,
                        DisplayName = System.IO.Path.GetFileName(path)
                    })
                    .ToList();
            }

            // Execute with timeout via event-driven approach
            var effectiveTimeout = agentConfig.Timeout != default
                ? agentConfig.Timeout
                : this.config.DefaultTimeout;

            await session.SendAsync(options, cancellationToken);

            // Wait for completion or timeout
            var completed = await System.Threading.Tasks.Task.WhenAny(
                done.Task,
                System.Threading.Tasks.Task.Delay(effectiveTimeout, cancellationToken));

            stopwatch.Stop();
            var completedAt = DateTime.UtcNow;

            if (completed != done.Task)
            {
                progress.Status = "timeout";
                return new AgentResult
                {
                    AgentId = agentConfig.Id,
                    Success = false,
                    Error = $"Timeout after {effectiveTimeout.TotalSeconds:F0}s",
                    Duration = stopwatch.Elapsed,
                    ModelUsed = model,
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = completedAt,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CacheReadTokens = cacheReadTokens,
                    CacheWriteTokens = cacheWriteTokens
                };
            }

            var content = ExtractBestResponse(messageBlocks);

            progress.Status = "completed";
            return new AgentResult
            {
                AgentId = agentConfig.Id,
                Success = true,
                Content = string.IsNullOrEmpty(content) ? null : content,
                Duration = stopwatch.Elapsed,
                ModelUsed = model,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheReadTokens,
                CacheWriteTokens = cacheWriteTokens
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            if (this.liveProgress.TryGetValue(agentConfig.Id, out var failedProgress))
            {
                failedProgress.Status = "failed";
            }

            return new AgentResult
            {
                AgentId = agentConfig.Id,
                Success = false,
                Error = ex.Message,
                Duration = stopwatch.Elapsed,
                ModelUsed = model,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheReadTokens,
                CacheWriteTokens = cacheWriteTokens
            };
        }
        finally
        {
            subscription?.Dispose();
            if (session != null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    private string AggregateConcatenate(List<AgentResult> results)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var result in results)
        {
            sb.AppendLine($"## Agent: {result.AgentId}");
            sb.AppendLine($"**Model:** {result.ModelUsed} | **Duration:** {result.Duration.TotalSeconds:F1}s | **Status:** {(result.Success ? "✓" : "✗")}");
            sb.AppendLine();

            if (result.Success)
            {
                sb.AppendLine(result.Content);
            }
            else
            {
                sb.AppendLine($"**Error:** {result.Error}");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> AggregateSynthesizeAsync(
        List<AgentResult> results,
        string? customPrompt,
        CancellationToken cancellationToken)
    {
        // First build the concatenated results
        var rawResults = this.AggregateConcatenate(results);

        // Build the synthesis prompt
        var promptTemplate = customPrompt ?? this.config.SynthesisSystemPrompt ?? AgentDefaults.DefaultSynthesisPrompt;
        var synthesisPrompt = promptTemplate.Replace("{results}", rawResults);

        // Spawn a fresh synthesis session with premium model and clean context
        CopilotSession? synthesisSession = null;
        try
        {
            synthesisSession = await this.client.CreateSessionAsync(new SessionConfig
            {
                Model = this.config.SynthesisModel,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = "You are a synthesis agent that combines multiple specialist analyses into cohesive documents."
                }
            }, cancellationToken);

            await this.TrackSessionAsync(synthesisSession);

            var response = await synthesisSession.SendAndWaitAsync(
                new MessageOptions { Prompt = synthesisPrompt },
                TimeSpan.FromMinutes(10),
                cancellationToken);

            return response?.Data?.Content ?? rawResults;
        }
        catch
        {
            // Fall back to concatenation if synthesis fails
            return rawResults;
        }
        finally
        {
            if (synthesisSession != null)
            {
                try
                {
                    await synthesisSession.DisposeAsync();
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    /// <summary>
    /// Extracts the full response from collected message blocks.
    /// When the model uses tools, it produces multiple text blocks interleaved
    /// with tool calls. We concatenate ALL blocks to preserve all analysis text.
    /// Short narration blocks ("Let me read...", "Now I'll check...") are filtered
    /// out when there are also substantial blocks present.
    /// </summary>
    private static string ExtractBestResponse(List<string> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        // If there are substantial blocks (≥200 chars), filter out short narration.
        // Otherwise, keep everything.
        const int narrationThreshold = 200;
        var hasSubstantial = blocks.Any(b => b.Length >= narrationThreshold);

        var filtered = hasSubstantial
            ? blocks.Where(b => b.Length >= narrationThreshold).ToList()
            : blocks;

        return string.Join("\n\n", filtered);
    }

    private async Task TrackSessionAsync(CopilotSession session)
    {
        await this.sessionLock.WaitAsync();
        try
        {
            this.managedSessions.Add(session);
        }
        finally
        {
            this.sessionLock.Release();
        }
    }

    /// <summary>
    /// Disposes all managed sessions.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await this.sessionLock.WaitAsync();
        try
        {
            foreach (var session in this.managedSessions)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch
                {
                    // Best-effort cleanup
                }
            }

            this.managedSessions.Clear();
        }
        finally
        {
            this.sessionLock.Release();
        }

        GC.SuppressFinalize(this);
    }
}
