// -----------------------------------------------------------------------
// <copyright file="IAgentMemoryStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Storage interface for agent memory. Provides CRUD operations
/// for episodes, expertise records, and memory edges.
/// Implementations may be in-memory (for testing), file-based,
/// or backed by a database.
/// </summary>
public interface IAgentMemoryStore
{
    // ── Episodes ──────────────────────────────────────────────────────

    /// <summary>
    /// Records a new episode.
    /// </summary>
    Task RecordEpisodeAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an episode by its ID.
    /// </summary>
    Task<Episode?> GetEpisodeAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries episodes matching the filter criteria.
    /// </summary>
    Task<IReadOnlyList<Episode>> GetEpisodesAsync(EpisodeFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing episode (e.g., to set outcome after completion).
    /// </summary>
    Task UpdateEpisodeAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an episode and its associated memory edges.
    /// </summary>
    Task DeleteEpisodeAsync(string id, CancellationToken cancellationToken = default);

    // ── Expertise ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates an expertise record.
    /// </summary>
    Task UpsertExpertiseAsync(ExpertiseRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an expertise record by its ID.
    /// </summary>
    Task<ExpertiseRecord?> GetExpertiseAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all expertise records for a given subject (file, type, project, etc.).
    /// </summary>
    Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseForSubjectAsync(
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets expertise records matching the specified category.
    /// </summary>
    Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByCategoryAsync(
        ExpertiseCategory category,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an expertise record.
    /// </summary>
    Task DeleteExpertiseAsync(string id, CancellationToken cancellationToken = default);

    // ── Memory Edges ──────────────────────────────────────────────────

    /// <summary>
    /// Adds a memory edge linking episodes/expertise/code nodes.
    /// </summary>
    Task AddMemoryEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all memory edges originating from a given node.
    /// </summary>
    Task<IReadOnlyList<MemoryEdge>> GetOutgoingEdgesAsync(
        string sourceId,
        MemoryEdgeType? edgeType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all memory edges pointing to a given node.
    /// </summary>
    Task<IReadOnlyList<MemoryEdge>> GetIncomingEdgesAsync(
        string targetId,
        MemoryEdgeType? edgeType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a memory edge.
    /// </summary>
    Task DeleteMemoryEdgeAsync(string id, CancellationToken cancellationToken = default);

    // ── Bulk / Stats ──────────────────────────────────────────────────

    /// <summary>
    /// Gets statistics about the memory store.
    /// </summary>
    Task<MemoryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all memory data.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about the agent memory store.
/// </summary>
public class MemoryStatistics
{
    /// <summary>Total number of episodes.</summary>
    public int EpisodeCount { get; set; }

    /// <summary>Total number of expertise records.</summary>
    public int ExpertiseCount { get; set; }

    /// <summary>Total number of memory edges.</summary>
    public int EdgeCount { get; set; }

    /// <summary>Breakdown of episodes by type.</summary>
    public Dictionary<EpisodeType, int> EpisodesByType { get; set; } = [];

    /// <summary>Breakdown of episodes by outcome.</summary>
    public Dictionary<EpisodeOutcome, int> EpisodesByOutcome { get; set; } = [];

    /// <summary>Breakdown of expertise by category.</summary>
    public Dictionary<ExpertiseCategory, int> ExpertiseByCategory { get; set; } = [];

    /// <summary>Average expertise confidence.</summary>
    public double AverageConfidence { get; set; }
}