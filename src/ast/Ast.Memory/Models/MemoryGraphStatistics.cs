// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Statistics about the memory graph, broken down by layer.
/// </summary>
public class MemoryGraphStatistics
{
    public int TotalNodes { get; set; }

    public int TotalEdges { get; set; }

    public int TotalExpertise { get; set; }

    public Dictionary<string, int> NodesByLayer { get; set; } = new();

    public Dictionary<string, int> EdgesByLayer { get; set; } = new();

    public Dictionary<string, int> ExpertiseByKind { get; set; } = new();

    public int ArchivedExpertise { get; set; }
}