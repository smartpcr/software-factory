// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator;

using GitHub.Copilot.SDK;

/// <summary>
/// Extension methods for <see cref="CopilotClient"/> to provide convenient
/// multi-agent orchestration directly from the client.
/// </summary>
public static class CopilotClientExtensions
{
    /// <summary>
    /// Creates an <see cref="AgentOrchestrator"/> using this client's enterprise subscription.
    /// </summary>
    /// <param name="client">The Copilot client.</param>
    /// <param name="config">Optional orchestrator configuration.</param>
    /// <returns>A new orchestrator instance.</returns>
    public static AgentOrchestrator CreateOrchestrator(
        this CopilotClient client,
        OrchestratorConfig? config = null) => new(client, config);

    /// <summary>
    /// Shorthand: spawn parallel explore agents, synthesize results with a premium model.
    /// This is the most common pattern for deep code analysis.
    /// </summary>
    /// <param name="client">The Copilot client.</param>
    /// <param name="subtasks">Dictionary of agent-id → prompt for each exploration task.</param>
    /// <param name="synthesisPrompt">Optional custom synthesis prompt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The synthesized result.</returns>
    /// <example>
    /// <code>
    /// var result = await client.ExploreAndSynthesizeAsync(new Dictionary&lt;string, string&gt;
    /// {
    ///     ["di-registration"] = "Analyze how dependency injection is configured...",
    ///     ["serialization"] = "Map all serialization strategies...",
    ///     ["concurrency"] = "Identify all thread-safety mechanisms...",
    /// });
    /// Console.WriteLine(result.AggregatedContent);
    /// </code>
    /// </example>
    public static async Task<OrchestratedResult> ExploreAndSynthesizeAsync(
        this CopilotClient client,
        Dictionary<string, string> subtasks,
        string? synthesisPrompt = null,
        CancellationToken cancellationToken = default)
    {
        await using var orchestrator = client.CreateOrchestrator();

        var agents = subtasks.Select(kvp =>
            AgentFactory.Explore(kvp.Key, kvp.Value));

        return await orchestrator.RunAsync(
            agents,
            AggregationStrategy.Synthesize,
            synthesisPrompt,
            cancellationToken);
    }

    /// <summary>
    /// Shorthand: run a single task on a premium model in isolation (fresh context).
    /// Equivalent to Claude Code's general-purpose Task agent.
    /// </summary>
    /// <param name="client">The Copilot client.</param>
    /// <param name="prompt">The task prompt.</param>
    /// <param name="model">Optional model override.</param>
    /// <param name="files">Optional file attachments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent result.</returns>
    public static async Task<AgentResult> RunIsolatedAsync(
        this CopilotClient client,
        string prompt,
        string? model = null,
        List<string>? files = null,
        CancellationToken cancellationToken = default)
    {
        await using var orchestrator = client.CreateOrchestrator();

        var result = await orchestrator.RunAsync(
            [AgentFactory.GeneralPurpose("isolated", prompt, files, model)],
            AggregationStrategy.Structured,
            cancellationToken: cancellationToken);

        return result.AgentResults[0];
    }
}
