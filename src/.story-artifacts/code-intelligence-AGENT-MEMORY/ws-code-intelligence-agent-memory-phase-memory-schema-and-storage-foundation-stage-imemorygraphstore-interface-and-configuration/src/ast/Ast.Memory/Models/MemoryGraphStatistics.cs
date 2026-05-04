// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Aggregate statistics about the memory graph, broken down by memory layer.
/// Returned by <see cref="Interfaces.IMemoryGraphStore.GetMemoryStatisticsAsync"/>.
/// </summary>
public class MemoryGraphStatistics
{
    /// <summary>Total number of nodes across all layers.</summary>
    public long TotalNodes { get; init; }

    /// <summary>Total number of edges across all layers.</summary>
    public long TotalEdges { get; init; }

    /// <summary>Total number of expertise entries (active + archived).</summary>
    public long TotalExpertiseEntries { get; init; }

    /// <summary>Number of archived expertise entries.</summary>
    public long ArchivedExpertiseEntries { get; init; }

    /// <summary>
    /// Per-layer breakdown of node and edge counts.
    /// Key is the memory layer name (e.g. "structural", "semantic", "community", "expertise").
    /// </summary>
    public IReadOnlyDictionary<string, LayerStatistics> ByLayer { get; init; }
        = new Dictionary<string, LayerStatistics>();
}

/// <summary>
/// Node and edge counts for a single memory layer.
/// </summary>
public class LayerStatistics
{
    /// <summary>Number of nodes in this layer.</summary>
    public long NodeCount { get; init; }

    /// <summary>Number of edges in this layer.</summary>
    public long EdgeCount { get; init; }
}