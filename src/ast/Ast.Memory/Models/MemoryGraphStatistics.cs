// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Per-layer node/edge counts for the memory graph.
/// </summary>
public class MemoryGraphStatistics
{
    public long TotalNodes { get; set; }
    public long TotalEdges { get; set; }
    public long TotalExpertise { get; set; }
    public Dictionary<string, long> NodesByLayer { get; set; } = [];
    public Dictionary<string, long> EdgesByLayer { get; set; } = [];
    public Dictionary<string, long> NodesByType { get; set; } = [];
}
