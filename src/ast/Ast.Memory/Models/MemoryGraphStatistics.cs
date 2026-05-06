// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Per-layer node and edge counts for the memory graph.
/// </summary>
public class MemoryGraphStatistics
{
    public Dictionary<string, long> NodesByLayer { get; init; } = [];
    public Dictionary<string, long> EdgesByLayer { get; init; } = [];
    public long TotalNodes { get; init; }
    public long TotalEdges { get; init; }
    public long TotalExpertise { get; init; }
    public long ArchivedExpertise { get; init; }
}
