// -----------------------------------------------------------------------
// <copyright file="ITieredExpertiseStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Capability interface for tier-aware expertise queries.
/// Separates hierarchical memory from the base <see cref="IAgentMemoryStore"/>.
/// </summary>
public interface ITieredExpertiseStore
{
    /// <summary>
    /// Gets expertise records at the specified memory tier.
    /// </summary>
    Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByTierAsync(
        MemoryTier tier,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets expertise records at the specified tier and category.
    /// </summary>
    Task<IReadOnlyList<ExpertiseRecord>> GetExpertiseByTierAndCategoryAsync(
        MemoryTier tier,
        ExpertiseCategory category,
        double minConfidence = 0.0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of expertise records at each tier.
    /// </summary>
    Task<Dictionary<MemoryTier, int>> GetTierDistributionAsync(
        CancellationToken cancellationToken = default);
}
