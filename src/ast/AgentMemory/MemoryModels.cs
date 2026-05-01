// -----------------------------------------------------------------------
// <copyright file="MemoryModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Type of agent interaction episode.
/// </summary>
public enum EpisodeType
{
    /// <summary>Exploring code to understand behavior.</summary>
    Investigation,

    /// <summary>Fixing a bug or defect.</summary>
    BugFix,

    /// <summary>Restructuring code without changing behavior.</summary>
    Refactor,

    /// <summary>Adding new functionality.</summary>
    Feature,

    /// <summary>Reviewing code for quality or correctness.</summary>
    Review,

    /// <summary>Writing or updating tests.</summary>
    Testing,

    /// <summary>Updating documentation.</summary>
    Documentation
}

/// <summary>
/// Outcome of an agent episode.
/// </summary>
public enum EpisodeOutcome
{
    /// <summary>Task completed successfully.</summary>
    Success,

    /// <summary>Task failed to achieve its goal.</summary>
    Failed,

    /// <summary>Task partially completed.</summary>
    Partial,

    /// <summary>Task was abandoned before completion.</summary>
    Abandoned,

    /// <summary>Outcome not yet determined (episode still in progress).</summary>
    InProgress
}

/// <summary>
/// Category of consolidated expertise.
/// </summary>
public enum ExpertiseCategory
{
    /// <summary>Knowledge about a specific file's purpose and behavior.</summary>
    FileExpertise,

    /// <summary>Knowledge about a type (class, interface, struct).</summary>
    TypeExpertise,

    /// <summary>Knowledge about a specific method or function.</summary>
    MethodExpertise,

    /// <summary>Knowledge about a project or module.</summary>
    ProjectExpertise,

    /// <summary>A learned coding pattern or idiom.</summary>
    PatternKnowledge,

    /// <summary>A coding convention or style rule.</summary>
    Convention,

    /// <summary>Knowledge about architectural decisions.</summary>
    ArchitecturalKnowledge,

    /// <summary>Knowledge about common failure modes.</summary>
    FailurePattern,

    /// <summary>Groups of symbols that frequently change together.</summary>
    CoChangeGroup
}

/// <summary>
/// Types of edges in the memory graph. Kept separate from
/// <see cref="Ast.Core.HyperedgeType"/> to avoid coupling
/// memory semantics to code-analysis semantics.
/// </summary>
public enum MemoryEdgeType
{
    /// <summary>Episode touched (read) a file.</summary>
    EpisodeTouchedFile,

    /// <summary>Episode touched (read/modified) a code symbol.</summary>
    EpisodeTouchedSymbol,

    /// <summary>Episode produced an expertise record.</summary>
    EpisodeProducedExpertise,

    /// <summary>Expertise is about a code symbol.</summary>
    ExpertiseAboutSymbol,

    /// <summary>Expertise is about a file.</summary>
    ExpertiseAboutFile,

    /// <summary>Two symbols frequently change together.</summary>
    CoChangedWith,

    /// <summary>Temporal or causal link between episodes.</summary>
    RelatedEpisode,

    /// <summary>Newer expertise supersedes older expertise.</summary>
    SupersededBy
}

/// <summary>
/// How a file or symbol was touched during an episode.
/// Distinguishes read-only exploration from active modification.
/// </summary>
public enum TouchType
{
    /// <summary>File/symbol was read but not modified.</summary>
    Read,

    /// <summary>File/symbol was modified.</summary>
    Modified,

    /// <summary>File/symbol was tested.</summary>
    Tested,

    /// <summary>File/symbol was created.</summary>
    Created,

    /// <summary>File/symbol was deleted.</summary>
    Deleted
}

/// <summary>
/// A single piece of evidence captured during an episode or
/// supporting an expertise record. Uses a list rather than
/// a dictionary to avoid losing evidence with duplicate keys.
/// </summary>
public class EvidenceItem
{
    /// <summary>Category or type of evidence (e.g., "error_message", "code_pattern", "test_result").</summary>
    public required string Category { get; init; }

    /// <summary>The evidence content.</summary>
    public required string Content { get; init; }

    /// <summary>Source of this evidence (e.g., file path, agent ID, episode ID).</summary>
    public string? Source { get; init; }

    /// <summary>When this evidence was captured.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}