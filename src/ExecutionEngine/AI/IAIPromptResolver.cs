// -----------------------------------------------------------------------
// <copyright file="IAIPromptResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

/// <summary>
/// Resolves system prompts and input parameters from agent/skill definition files.
/// Uses an LLM to summarize and extract structured metadata from free-form .md files.
/// </summary>
public interface IAIPromptResolver
{
    /// <summary>
    /// Loads an agent definition by name. Uses cached extraction if available.
    /// </summary>
    Task<AgentDefinitionInfo?> LoadAgentAsync(string agentName, CancellationToken ct = default);

    /// <summary>
    /// Loads a skill definition by name. Uses cached extraction if available.
    /// </summary>
    Task<AgentDefinitionInfo?> LoadSkillAsync(string skillName, CancellationToken ct = default);

    /// <summary>
    /// Generates a user prompt for the given agent/skill using bound parameter values.
    /// Calls an LLM to produce a high-quality prompt from the summary + parameters.
    /// </summary>
    Task<string> GenerateUserPromptAsync(
        AgentDefinitionInfo agentInfo,
        Dictionary<string, string> resolvedBindings,
        string? additionalContext,
        CancellationToken ct = default);
}

/// <summary>
/// Parsed and LLM-extracted metadata from an agent or skill .md file.
/// </summary>
public class AgentDefinitionInfo
{
    /// <summary>Gets or sets the agent/skill name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the LLM-generated summary of what this agent/skill does.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the model hint from frontmatter (e.g. "opus", "sonnet").</summary>
    public string? ModelHint { get; set; }

    /// <summary>Gets or sets the allowed tools from frontmatter.</summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>Gets or sets the LLM-extracted input parameters (flat, for backward compat).</summary>
    public List<AgentInputParam> Inputs { get; set; } = [];

    /// <summary>Gets or sets the LLM-extracted output fields (flat, for backward compat).</summary>
    public List<AgentOutputField> Outputs { get; set; } = [];

    /// <summary>Gets or sets the named scenarios (parameter sets). When present, Inputs/Outputs are derived from the active scenario.</summary>
    public Dictionary<string, AgentScenario>? Scenarios { get; set; }

    /// <summary>Gets or sets the default scenario name.</summary>
    public string? DefaultScenario { get; set; }

    /// <summary>Gets or sets the path to the executable script (for skills with .ps1/.py files).</summary>
    public string? ScriptPath { get; set; }

    /// <summary>Gets or sets the full system prompt body (content after frontmatter).</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA256 hash of the source .md file for cache invalidation.</summary>
    public string? SourceHash { get; set; }

    /// <summary>Gets or sets when the extraction was performed.</summary>
    public DateTime? ExtractedAt { get; set; }

    /// <summary>
    /// Get inputs/outputs for a specific scenario, falling back to the flat Inputs/Outputs.
    /// </summary>
    public (List<AgentInputParam> Inputs, List<AgentOutputField> Outputs) GetScenarioIO(string? scenarioName = null)
    {
        if (this.Scenarios == null || this.Scenarios.Count == 0)
        {
            return (this.Inputs, this.Outputs);
        }

        var name = scenarioName ?? this.DefaultScenario;
        if (!string.IsNullOrEmpty(name) && this.Scenarios.TryGetValue(name, out var scenario))
        {
            return (scenario.Inputs, scenario.Outputs);
        }

        // Fall back to default scenario or first scenario
        if (!string.IsNullOrEmpty(this.DefaultScenario) && this.Scenarios.TryGetValue(this.DefaultScenario, out var defScenario))
        {
            return (defScenario.Inputs, defScenario.Outputs);
        }

        var first = this.Scenarios.Values.FirstOrDefault();
        return first != null ? (first.Inputs, first.Outputs) : (this.Inputs, this.Outputs);
    }
}

/// <summary>
/// A named scenario (parameter set) with its own inputs and outputs.
/// Inspired by PowerShell ParameterSets.
/// </summary>
public class AgentScenario
{
    /// <summary>Gets or sets the scenario description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the input parameters for this scenario.</summary>
    public List<AgentInputParam> Inputs { get; set; } = [];

    /// <summary>Gets or sets the output fields for this scenario.</summary>
    public List<AgentOutputField> Outputs { get; set; } = [];
}

/// <summary>
/// Declares a required or optional input parameter for an agent or skill.
/// </summary>
public class AgentInputParam
{
    /// <summary>Gets or sets the parameter name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the parameter type (string, integer, boolean, object).</summary>
    public string Type { get; set; } = "string";

    /// <summary>Gets or sets whether this parameter is required.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a description of this parameter.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Describes an output field produced by an agent or skill.
/// </summary>
public class AgentOutputField
{
    /// <summary>Gets or sets the output field name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the output field type (string, object, file, folder).</summary>
    public string Type { get; set; } = "string";

    /// <summary>Gets or sets a description of this output.</summary>
    public string Description { get; set; } = string.Empty;
}