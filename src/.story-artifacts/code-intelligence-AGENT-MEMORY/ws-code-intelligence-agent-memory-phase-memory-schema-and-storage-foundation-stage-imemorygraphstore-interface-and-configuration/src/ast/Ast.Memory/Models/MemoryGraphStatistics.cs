// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Aggregate statistics for the memory graph.
/// All counts reflect active (non-retired) entities unless noted otherwise.
/// </summary>
public class MemoryGraphStatistics
{
    /// <summary>Per-layer breakdown of active node and edge counts.</summary>
    public IReadOnlyDictionary<string, LayerStatistics> LayerStats { get; init; } =
        new Dictionary<string, LayerStatistics>();

    /// <summary>Total active nodes across all layers.</summary>
    public int TotalNodes { get; init; }

    /// <summary>Total active edges across all layers.</summary>
    public int TotalEdges { get; init; }

    /// <summary>Total non-archived expertise entries.</summary>
    public int TotalExpertiseEntries { get; init; }

    /// <summary>Total archived expertise entries.</summary>
    public int ArchivedExpertiseEntries { get; init; }

    /// <summary>Timestamp of the most recent write operation, if available.</summary>
    public DateTimeOffset? LastUpdated { get; init; }
}

/// <summary>
/// Node and edge counts for a single memory layer.
/// </summary>
public record LayerStatistics(int NodeCount, int EdgeCount);
