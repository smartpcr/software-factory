// -----------------------------------------------------------------------
// <copyright file="ContextWindow.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

using Ast.Core;

/// <summary>
/// The assembled context for an agent task. Combines structural,
/// relational, episodic, and semantic memory into a single payload
/// that can be provided to an agent for informed decision-making.
/// </summary>
public class ContextWindow
{
    /// <summary>
    /// Description of the task for which context was built.
    /// </summary>
    public required string TaskDescription { get; init; }

    /// <summary>
    /// Structural context: relevant code nodes from the repo hierarchy.
    /// Ordered top-down (project → namespace → type → member).
    /// </summary>
    public List<HypergraphNode> StructuralNodes { get; init; } = [];

    /// <summary>
    /// Relational context: relevant edges from the code graph
    /// (call chains, dependencies, inheritance).
    /// </summary>
    public List<Hyperedge> RelationalEdges { get; init; } = [];

    /// <summary>
    /// Episodic context: past agent interactions with the relevant code areas.
    /// Ordered by relevance (most relevant first).
    /// </summary>
    public List<Episode> RelevantEpisodes { get; init; } = [];

    /// <summary>
    /// Semantic context: accumulated expertise about the relevant code areas.
    /// Ordered by confidence (highest first).
    /// </summary>
    public List<ExpertiseRecord> Expertise { get; init; } = [];

    /// <summary>
    /// Call-chain context: ordered method call sequences relevant to the task.
    /// Provides n-ary relational context beyond binary edges.
    /// </summary>
    public List<CallChain> CallChains { get; init; } = [];

    /// <summary>
    /// Overall relevance score for this context window (0.0 to 1.0).
    /// </summary>
    public double RelevanceScore { get; set; }

    /// <summary>
    /// Per-symbol familiarity scores derived from expertise.
    /// Keys are symbol IDs or file paths, values are familiarity (0.0 to 1.0).
    /// </summary>
    public Dictionary<string, double> SymbolFamiliarity { get; init; } = [];

    /// <summary>
    /// Warnings about stale expertise or outdated knowledge.
    /// </summary>
    public List<string> FreshnessWarnings { get; init; } = [];
}

/// <summary>
/// Request parameters for building a context window.
/// </summary>
public class ContextRequest
{
    /// <summary>
    /// Description of the task the agent needs context for.
    /// </summary>
    public required string TaskDescription { get; init; }

    /// <summary>
    /// Specific file paths to include in context (if known).
    /// </summary>
    public List<string> FilePaths { get; init; } = [];

    /// <summary>
    /// Specific symbol IDs to include in context (if known).
    /// </summary>
    public List<string> SymbolIds { get; init; } = [];

    /// <summary>
    /// Maximum number of episodes to include.
    /// </summary>
    public int MaxEpisodes { get; init; } = 10;

    /// <summary>
    /// Maximum number of expertise records to include.
    /// </summary>
    public int MaxExpertise { get; init; } = 20;

    /// <summary>
    /// Maximum number of call chains to include.
    /// </summary>
    public int MaxCallChains { get; init; } = 10;

    /// <summary>
    /// Maximum graph traversal depth for relational context.
    /// </summary>
    public int MaxGraphDepth { get; init; } = 2;

    /// <summary>
    /// Minimum confidence threshold for including expertise.
    /// </summary>
    public double MinConfidence { get; init; } = 0.3;

    /// <summary>
    /// Agent ID to filter episodes by (null = all agents).
    /// </summary>
    public string? AgentId { get; init; }
}

/// <summary>
/// Filter criteria for querying episodes.
/// </summary>
public class EpisodeFilter
{
    /// <summary>
    /// Filter by agent ID.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Filter by episode type.
    /// </summary>
    public EpisodeType? Type { get; set; }

    /// <summary>
    /// Filter by outcome.
    /// </summary>
    public EpisodeOutcome? Outcome { get; set; }

    /// <summary>
    /// Filter to episodes that touched any of these files.
    /// </summary>
    public List<string>? TouchedFiles { get; set; }

    /// <summary>
    /// Filter to episodes that touched any of these symbols.
    /// </summary>
    public List<string>? TouchedSymbolIds { get; set; }

    /// <summary>
    /// Filter to episodes with any of these tags.
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Filter to episodes after this timestamp.
    /// </summary>
    public DateTime? After { get; set; }

    /// <summary>
    /// Filter to episodes before this timestamp.
    /// </summary>
    public DateTime? Before { get; set; }

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int Limit { get; set; } = 50;
}