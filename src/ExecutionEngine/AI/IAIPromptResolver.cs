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

    /// <summary>Gets or sets the LLM-extracted input parameters.</summary>
    public List<AgentInputParam> Inputs { get; set; } = [];

    /// <summary>Gets or sets the path to the executable script (for skills with .ps1/.py files).</summary>
    public string? ScriptPath { get; set; }

    /// <summary>Gets or sets the full system prompt body (content after frontmatter).</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA256 hash of the source .md file for cache invalidation.</summary>
    public string? SourceHash { get; set; }

    /// <summary>Gets or sets when the extraction was performed.</summary>
    public DateTime? ExtractedAt { get; set; }
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