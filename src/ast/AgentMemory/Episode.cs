// -----------------------------------------------------------------------
// <copyright file="Episode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Records a single agent interaction with the codebase.
/// Each episode captures what the agent did, what it observed,
/// and what the outcome was. Episodes are the raw material
/// from which expertise is consolidated.
/// </summary>
public class Episode
{
    /// <summary>
    /// Unique identifier for this episode.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// When the episode occurred.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Identifier for the agent that performed this episode.
    /// </summary>
    public required string AgentId { get; init; }

    /// <summary>
    /// Description of the task the agent was performing.
    /// </summary>
    public required string TaskDescription { get; init; }

    /// <summary>
    /// Type of interaction.
    /// </summary>
    public EpisodeType Type { get; init; } = EpisodeType.Investigation;

    /// <summary>
    /// Outcome of the episode.
    /// </summary>
    public EpisodeOutcome Outcome { get; set; } = EpisodeOutcome.InProgress;

    /// <summary>
    /// Human-readable summary of what happened.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// File paths that were read or modified during this episode.
    /// </summary>
    public List<string> TouchedFiles { get; init; } = [];

    /// <summary>
    /// IDs of HypergraphNode symbols that were involved.
    /// These reference nodes in the existing code graph.
    /// </summary>
    public List<string> TouchedSymbolIds { get; init; } = [];

    /// <summary>
    /// Categorization tags for this episode.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// Git commit SHA at the time of the episode, for freshness tracking.
    /// </summary>
    public string? CommitSha { get; init; }

    /// <summary>
    /// Git branch at the time of the episode.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Key observations made during the episode.
    /// Uses a list to avoid losing observations with duplicate categories.
    /// </summary>
    public List<EvidenceItem> Observations { get; init; } = [];

    /// <summary>
    /// Supporting evidence collected during the episode.
    /// Uses a list to avoid losing evidence with duplicate categories.
    /// </summary>
    public List<EvidenceItem> Evidence { get; init; } = [];

    /// <summary>
    /// How long the episode took.
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
}