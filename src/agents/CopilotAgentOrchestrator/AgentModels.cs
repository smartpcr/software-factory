// -----------------------------------------------------------------------
//
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator;

using GitHub.Copilot.SDK;

/// <summary>
/// Configuration for a subagent to be spawned by the orchestrator.
/// Each subagent gets its own <see cref="CopilotSession"/> with a fresh context window.
/// </summary>
public class SubAgentConfig
{
    /// <summary>
    /// Unique identifier for this agent. Used as a key in aggregated results.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable description of what this agent does (for logging/UI).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The type of agent, which determines default model and system prompt.
    /// </summary>
    public AgentType AgentType { get; init; } = AgentType.GeneralPurpose;

    /// <summary>
    /// Override the model for this specific agent. If null, uses the default for the <see cref="AgentType"/>.
    /// All models are accessed through the Copilot enterprise subscription.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// System prompt for this agent. If null, uses the default for the <see cref="AgentType"/>.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// The prompt/task to send to this agent.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// File attachments to include with the prompt. Each agent gets its own copy in its fresh context.
    /// </summary>
    public List<string>? FileAttachments { get; init; }

    /// <summary>
    /// Timeout for this agent's execution. Default: <see cref="TimeSpan.Zero"/> (use orchestrator default).
    /// </summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>
    /// Maximum number of retries on failure. Default: 0.
    /// </summary>
    public int MaxRetries { get; init; }

    /// <summary>
    /// Optional working directory for the agent session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Additional tools to provide to this specific agent.
    /// </summary>
    public ICollection<Microsoft.Extensions.AI.AIFunction>? Tools { get; init; }

    /// <summary>
    /// IDs of agents that must complete before this agent can start.
    /// Enables DAG-based scheduling: agents with no dependencies run in parallel,
    /// agents with dependencies wait until all predecessors finish.
    /// Use <c>{result:agent-id}</c> placeholders in <see cref="Prompt"/> to inject
    /// a predecessor's output into this agent's prompt automatically.
    /// </summary>
    public List<string>? DependsOn { get; init; }
}

/// <summary>
/// Result from a single subagent execution.
/// </summary>
public class AgentResult
{
    /// <summary>
    /// The agent ID that produced this result.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// Whether the agent completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The text content of the agent's final response.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Error message if the agent failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Time taken for this agent's execution.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The model that was actually used for this agent.
    /// </summary>
    public string? ModelUsed { get; init; }

    /// <summary>
    /// UTC timestamp when this agent started executing.
    /// </summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when this agent finished executing.
    /// </summary>
    public DateTime CompletedAtUtc { get; init; }

    /// <summary>
    /// Number of input tokens consumed.
    /// </summary>
    public int InputTokens { get; init; }

    /// <summary>
    /// Number of output tokens generated.
    /// </summary>
    public int OutputTokens { get; init; }

    /// <summary>
    /// Number of tokens read from prompt cache.
    /// </summary>
    public int CacheReadTokens { get; init; }

    /// <summary>
    /// Number of tokens written to prompt cache.
    /// </summary>
    public int CacheWriteTokens { get; init; }
}

/// <summary>
/// Aggregated result from multiple subagents.
/// </summary>
public class OrchestratedResult
{
    /// <summary>
    /// Individual results from each subagent.
    /// </summary>
    public required IReadOnlyList<AgentResult> AgentResults { get; init; }

    /// <summary>
    /// The synthesized/aggregated content (populated when using <see cref="AggregationStrategy.Synthesize"/>
    /// or <see cref="AggregationStrategy.Concatenate"/>).
    /// </summary>
    public string? AggregatedContent { get; init; }

    /// <summary>
    /// Total wall-clock time for the entire orchestration.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Number of agents that completed successfully.
    /// </summary>
    public int SuccessCount => this.AgentResults.Count(r => r.Success);

    /// <summary>
    /// Number of agents that failed.
    /// </summary>
    public int FailureCount => this.AgentResults.Count(r => !r.Success);
}

/// <summary>
/// Tracks live progress of a running subagent.
/// Updated in real-time by the orchestrator as streaming events arrive.
/// </summary>
public class AgentProgress
{
    /// <summary>
    /// The agent ID.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// The model being used.
    /// </summary>
    public string? ModelUsed { get; set; }

    /// <summary>
    /// Current status: "pending", "running", "completed", "failed", or "timeout".
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>
    /// UTC timestamp when the agent started executing.
    /// </summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// Accumulated input tokens so far.
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// Accumulated output tokens so far.
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// Accumulated cache read tokens so far.
    /// </summary>
    public int CacheReadTokens { get; set; }

    /// <summary>
    /// Accumulated cache write tokens so far.
    /// </summary>
    public int CacheWriteTokens { get; set; }
}

/// <summary>
/// Configuration for the orchestrator itself.
/// </summary>
public class OrchestratorConfig
{
    /// <summary>
    /// Maximum number of subagents to run concurrently. Default: 5.
    /// </summary>
    public int MaxConcurrency { get; init; } = 7;

    /// <summary>
    /// Model to use for the synthesis/aggregation agent.
    /// Only used when <see cref="AggregationStrategy.Synthesize"/> is selected.
    /// Defaults to the premium model for highest quality synthesis.
    /// </summary>
    public string SynthesisModel { get; init; } = AgentDefaults.PremiumModel;

    /// <summary>
    /// System prompt for the synthesis agent.
    /// </summary>
    public string? SynthesisSystemPrompt { get; init; }

    /// <summary>
    /// Default timeout for subagents if not specified per-agent. Default: 30 minutes
    /// since opus 4.6 thinking model takes significantly more time to respond,
    /// and we want to avoid premature timeouts in complex orchestrations.
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether to continue executing remaining agents if one fails. Default: true.
    /// </summary>
    public bool ContinueOnFailure { get; init; } = true;
}
