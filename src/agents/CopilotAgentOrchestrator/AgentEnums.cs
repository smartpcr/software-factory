// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator;

/// <summary>
/// Defines the type of subagent to spawn, analogous to Claude Code's agent types.
/// Each type has different model defaults and context optimizations.
/// </summary>
public enum AgentType
{
    /// <summary>
    /// Fast, cheap agent for codebase exploration and information gathering.
    /// Uses a smaller/faster model (e.g., claude-haiku-4.5, gpt-4.1).
    /// Returns focused answers. Safe to run many in parallel.
    /// </summary>
    Explore,

    /// <summary>
    /// Agent for executing commands with verbose output (tests, builds, lints).
    /// Returns brief summary on success, full output on failure.
    /// </summary>
    Task,

    /// <summary>
    /// Full-capability agent with high-quality reasoning.
    /// Uses a premium model (e.g., claude-opus-4.6, gpt-5).
    /// Best for complex multi-step analysis and synthesis.
    /// </summary>
    GeneralPurpose,

    /// <summary>
    /// Specialized agent for code review tasks.
    /// High signal-to-noise ratio — only surfaces genuine issues.
    /// </summary>
    CodeReview
}

/// <summary>
/// Defines how results from multiple subagents are combined.
/// </summary>
public enum AggregationStrategy
{
    /// <summary>
    /// Simply concatenate all subagent results with section headers.
    /// No additional LLM call. Fastest and cheapest.
    /// </summary>
    Concatenate,

    /// <summary>
    /// Use a synthesis agent to combine all results into a cohesive document.
    /// Requires an additional LLM call but produces the highest quality output.
    /// </summary>
    Synthesize,

    /// <summary>
    /// Return results as a structured dictionary keyed by agent name/id.
    /// Useful when the caller wants to process results programmatically.
    /// </summary>
    Structured
}

/// <summary>
/// Execution mode for subagents.
/// </summary>
public enum AgentExecutionMode
{
    /// <summary>
    /// Wait for the agent to complete before returning.
    /// </summary>
    Synchronous,

    /// <summary>
    /// Run the agent in the background. Use <see cref="AgentOrchestrator.WaitForAgentAsync"/> to get results.
    /// </summary>
    Background
}
