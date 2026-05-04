// -----------------------------------------------------------------------
// <copyright file="ICallChainStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// Capability interface for call chain storage and retrieval.
/// Call chains represent ordered n-ary method call sequences,
/// stored separately from binary <see cref="MemoryEdge"/>s.
/// </summary>
public interface ICallChainStore
{
    /// <summary>
    /// Records a new call chain or updates an existing one.
    /// </summary>
    Task UpsertCallChainAsync(CallChain chain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a call chain by its ID.
    /// </summary>
    Task<CallChain?> GetCallChainAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets call chains that contain the specified symbol at any step.
    /// </summary>
    Task<IReadOnlyList<CallChain>> GetCallChainsForSymbolAsync(
        string symbolId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets call chains matching the filter criteria.
    /// </summary>
    Task<IReadOnlyList<CallChain>> GetCallChainsAsync(
        CallChainFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a call chain by its ID.
    /// </summary>
    Task DeleteCallChainAsync(string id, CancellationToken cancellationToken = default);
}
