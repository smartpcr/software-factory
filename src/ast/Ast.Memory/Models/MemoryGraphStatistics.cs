// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Aggregate statistics for the memory graph.
/// </summary>
public sealed class MemoryGraphStatistics
{
    public IReadOnlyDictionary<string, LayerStatistics> LayerStats { get; init; } =
        new Dictionary<string, LayerStatistics>();

    public int TotalNodes { get; init; }
    public int TotalEdges { get; init; }
    public int TotalExpertiseEntries { get; init; }
    public int ArchivedExpertiseEntries { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

/// <summary>
/// Node and edge counts for a single memory layer.
/// </summary>
public record LayerStatistics(int NodeCount, int EdgeCount);