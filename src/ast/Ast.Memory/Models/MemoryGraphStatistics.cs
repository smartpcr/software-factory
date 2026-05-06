// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Statistics about the memory graph, with per-layer node and edge counts.
/// </summary>
public sealed class MemoryGraphStatistics
{
    public int TotalNodes { get; init; }
    public int TotalEdges { get; init; }
    public int TotalExpertise { get; init; }
    public int ArchivedExpertise { get; init; }
    public Dictionary<string, int> NodesByLayer { get; init; } = new();
    public Dictionary<string, int> EdgesByLayer { get; init; } = new();
    public Dictionary<string, int> NodesByType { get; init; } = new();
}