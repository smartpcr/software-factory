// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator;

/// <summary>
/// Convenience factory methods for creating common subagent configurations.
/// Provides Claude-Code-style agent presets: explore, task, general-purpose, code-review.
/// </summary>
public static class AgentFactory
{
    /// <summary>
    /// Creates an exploration agent — fast, cheap, focused on finding information.
    /// Uses <see cref="AgentDefaults.FastModel"/> by default.
    /// </summary>
    /// <param name="id">Unique agent identifier.</param>
    /// <param name="prompt">The exploration question or task.</param>
    /// <param name="files">Optional file paths to attach for context.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="dependsOn">Optional list of agent IDs that must complete before this agent starts.</param>
    /// <returns>A configured <see cref="SubAgentConfig"/>.</returns>
    public static SubAgentConfig Explore(
        string id,
        string prompt,
        List<string>? files = null,
        string? workingDirectory = null,
        List<string>? dependsOn = null) => new()
    {
        Id = id,
        AgentType = AgentType.Explore,
        Prompt = prompt,
        FileAttachments = files,
        WorkingDirectory = workingDirectory,
        DependsOn = dependsOn,
        Description = $"Explore: {id}"
    };

    /// <summary>
    /// Creates a task execution agent — runs commands, reports results.
    /// Uses <see cref="AgentDefaults.StandardModel"/> by default.
    /// </summary>
    /// <param name="id">Unique agent identifier.</param>
    /// <param name="prompt">The command or task to execute.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="dependsOn">Optional list of agent IDs that must complete before this agent starts.</param>
    /// <returns>A configured <see cref="SubAgentConfig"/>.</returns>
    public static SubAgentConfig Task(
        string id,
        string prompt,
        string? workingDirectory = null,
        List<string>? dependsOn = null) => new()
    {
        Id = id,
        AgentType = AgentType.Task,
        Prompt = prompt,
        WorkingDirectory = workingDirectory,
        DependsOn = dependsOn,
        Description = $"Task: {id}"
    };

    /// <summary>
    /// Creates a general-purpose agent — premium model, full analysis capability.
    /// Uses <see cref="AgentDefaults.PremiumModel"/> by default.
    /// </summary>
    /// <param name="id">Unique agent identifier.</param>
    /// <param name="prompt">The analysis task.</param>
    /// <param name="files">Optional file paths to attach for context.</param>
    /// <param name="model">Override model (null = use premium default).</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="dependsOn">Optional list of agent IDs that must complete before this agent starts.</param>
    /// <returns>A configured <see cref="SubAgentConfig"/>.</returns>
    public static SubAgentConfig GeneralPurpose(
        string id,
        string prompt,
        List<string>? files = null,
        string? model = null,
        string? workingDirectory = null,
        List<string>? dependsOn = null) => new()
    {
        Id = id,
        AgentType = AgentType.GeneralPurpose,
        Model = model,
        Prompt = prompt,
        FileAttachments = files,
        WorkingDirectory = workingDirectory,
        DependsOn = dependsOn,
        Description = $"General: {id}"
    };

    /// <summary>
    /// Creates a code review agent — high signal-to-noise, only real issues.
    /// Uses <see cref="AgentDefaults.StandardModel"/> by default.
    /// </summary>
    /// <param name="id">Unique agent identifier.</param>
    /// <param name="prompt">What to review (e.g., "Review the caching layer for thread safety").</param>
    /// <param name="files">File paths to review.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="dependsOn">Optional list of agent IDs that must complete before this agent starts.</param>
    /// <returns>A configured <see cref="SubAgentConfig"/>.</returns>
    public static SubAgentConfig CodeReview(
        string id,
        string prompt,
        List<string>? files = null,
        string? workingDirectory = null,
        List<string>? dependsOn = null) => new()
    {
        Id = id,
        AgentType = AgentType.CodeReview,
        Prompt = prompt,
        FileAttachments = files,
        WorkingDirectory = workingDirectory,
        DependsOn = dependsOn,
        Description = $"Review: {id}"
    };

    /// <summary>
    /// Creates a custom agent with full control over all parameters.
    /// </summary>
    /// <param name="id">Unique agent identifier.</param>
    /// <param name="prompt">The task prompt.</param>
    /// <param name="model">Model to use.</param>
    /// <param name="systemPrompt">System prompt for the agent.</param>
    /// <param name="files">Optional file paths to attach.</param>
    /// <param name="timeout">Timeout for execution.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="dependsOn">Optional list of agent IDs that must complete before this agent starts.</param>
    /// <returns>A configured <see cref="SubAgentConfig"/>.</returns>
    public static SubAgentConfig Custom(
        string id,
        string prompt,
        string model,
        string systemPrompt,
        List<string>? files = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        List<string>? dependsOn = null) => new()
    {
        Id = id,
        AgentType = AgentType.GeneralPurpose,
        Model = model,
        SystemPrompt = systemPrompt,
        Prompt = prompt,
        FileAttachments = files,
        Timeout = timeout ?? default,
        WorkingDirectory = workingDirectory,
        DependsOn = dependsOn,
        Description = $"Custom: {id}"
    };
}
