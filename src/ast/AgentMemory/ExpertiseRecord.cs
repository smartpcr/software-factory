// -----------------------------------------------------------------------
// <copyright file="ExpertiseRecord.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Consolidated knowledge derived from one or more episodes.
/// Expertise records are evidence-backed and reversible: they track
/// which episodes contributed to the knowledge and maintain both
/// positive and negative evidence.
/// </summary>
public class ExpertiseRecord
{
    /// <summary>
    /// Unique identifier for this expertise record.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The subject of this expertise (file path, type name, project name, or pattern name).
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Category of expertise.
    /// </summary>
    public ExpertiseCategory Category { get; init; } = ExpertiseCategory.FileExpertise;

    /// <summary>
    /// Hierarchical memory tier. Defaults to the natural tier for the category.
    /// Higher tiers represent more abstract, cross-cutting knowledge.
    /// </summary>
    public MemoryTier Tier { get; set; } = MemoryTier.Interaction;

    /// <summary>
    /// Confidence level from 0.0 (no confidence) to 1.0 (fully confident).
    /// Decays over time and with code churn; reinforced by access and validation.
    /// </summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>
    /// Number of episodes that contributed to this expertise.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>
    /// IDs of episodes from which this expertise was derived.
    /// </summary>
    public List<string> DerivedFromEpisodeIds { get; init; } = [];

    /// <summary>
    /// Evidence supporting this expertise.
    /// </summary>
    public List<EvidenceItem> PositiveEvidence { get; init; } = [];

    /// <summary>
    /// Evidence contradicting or qualifying this expertise.
    /// </summary>
    public List<EvidenceItem> NegativeEvidence { get; init; } = [];

    /// <summary>
    /// Human-readable summary of the expertise.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// When this expertise was first observed.
    /// </summary>
    public DateTime FirstObserved { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this expertise was last validated against the current codebase.
    /// </summary>
    public DateTime LastValidated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Git commit SHA when this expertise was last validated.
    /// Used to detect staleness when code changes.
    /// </summary>
    public string? CommitSha { get; set; }

    /// <summary>
    /// Git branch where this expertise was derived.
    /// Used to scope staleness checks to relevant branches.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Number of times this expertise was retrieved for context.
    /// Higher access counts reinforce the expertise against decay.
    /// </summary>
    public int AccessCount { get; set; }

    /// <summary>
    /// Additional structured properties of this expertise.
    /// Keys describe the property, values are the data.
    /// </summary>
    public Dictionary<string, string> Properties { get; init; } = [];
}