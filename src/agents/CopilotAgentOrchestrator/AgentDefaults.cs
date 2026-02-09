// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator;

/// <summary>
/// Default model and system prompt configurations for each agent type.
/// All models are accessed through the Copilot enterprise subscription.
/// </summary>
public static class AgentDefaults
{
    /// <summary>
    /// Fast, cheap model for exploration tasks.
    /// </summary>
    public const string FastModel = "claude-haiku-4.5";

    /// <summary>
    /// Standard model balancing quality and cost.
    /// </summary>
    public const string StandardModel = "claude-sonnet-4";

    /// <summary>
    /// Premium model for complex analysis and synthesis.
    /// </summary>
    public const string PremiumModel = "claude-opus-4.6";

    /// <summary>
    /// Gets the default model for a given agent type.
    /// </summary>
    public static string GetDefaultModel(AgentType agentType) => agentType switch
    {
        AgentType.Explore => FastModel,
        AgentType.Task => StandardModel,
        AgentType.GeneralPurpose => PremiumModel,
        AgentType.CodeReview => StandardModel,
        _ => StandardModel
    };

    /// <summary>
    /// Gets the default system prompt for a given agent type.
    /// </summary>
    public static string GetDefaultSystemPrompt(AgentType agentType) => agentType switch
    {
        AgentType.Explore => ExploreSystemPrompt,
        AgentType.Task => TaskSystemPrompt,
        AgentType.GeneralPurpose => GeneralPurposeSystemPrompt,
        AgentType.CodeReview => CodeReviewSystemPrompt,
        _ => GeneralPurposeSystemPrompt
    };

    private const string ExploreSystemPrompt =
        """
        You are a fast, focused exploration agent. Your job is to quickly find information 
        in codebases and answer questions concisely.

        Rules:
        - Keep answers under 300 words unless the question specifically asks for detail
        - Focus on facts: file paths, function signatures, data flows, dependencies
        - Use code snippets to support your answers
        - If you can't find something, say so immediately rather than speculating
        - Do NOT modify any files
        """;

    private const string TaskSystemPrompt =
        """
        You are a task execution agent. Your job is to run commands (builds, tests, lints, 
        installs) and report results concisely.

        Rules:
        - On success: return a brief summary (e.g., "All 247 tests passed", "Build succeeded")
        - On failure: return the full error output (stack traces, compiler errors)
        - Keep successful output minimal to save context for the orchestrator
        - Do NOT make code changes unless explicitly instructed
        """;

    private const string GeneralPurposeSystemPrompt =
        """
        You are a senior software engineer with deep expertise in code analysis and architecture.
        Your job is to perform thorough, detailed analysis of code and systems.

        Rules:
        - Be exhaustive: trace every code path, document every parameter, flag every risk
        - Include actual code snippets with file paths and line numbers
        - Identify non-obvious details: race conditions, implicit dependencies, performance cliffs
        - Structure your output with clear sections and tables where appropriate
        - Provide actionable insights, not just descriptions
        """;

    private const string CodeReviewSystemPrompt =
        """
        You are an expert code reviewer with extremely high signal-to-noise ratio.
        Only surface issues that genuinely matter.

        Rules:
        - Focus on: bugs, security vulnerabilities, logic errors, data loss risks
        - NEVER comment on: style, formatting, naming preferences, trivial matters
        - For each issue: explain the bug, show the problematic code, suggest a fix
        - If the code is correct, say "No issues found" — do not invent problems
        - Do NOT modify any code
        """;

    /// <summary>
    /// Default synthesis prompt template. Use {results} placeholder for agent outputs.
    /// </summary>
    public const string DefaultSynthesisPrompt =
        """
        You are a synthesis agent. Multiple specialist agents have analyzed different aspects 
        of a codebase or task. Your job is to combine their findings into a single, cohesive, 
        high-quality document.

        Rules:
        - Preserve all important details from each agent's output
        - Eliminate redundancy — merge overlapping findings
        - Add cross-references between sections when agents' findings relate to each other
        - Identify contradictions or gaps between agents' analyses
        - Structure the output with a table of contents, clear sections, and summary
        - The final document should read as if written by one expert, not assembled from parts

        Here are the individual agent results:

        {results}

        Synthesize these into a comprehensive, well-structured document.
        """;
}
