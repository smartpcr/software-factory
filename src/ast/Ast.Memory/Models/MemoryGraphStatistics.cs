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
public sealed class MemoryGraphStatistics
{
    public int StructuralNodes { get; init; }
    public int CallChainNodes { get; init; }
    public int SemanticNodes { get; init; }
    public int ExpertiseEntries { get; init; }
    public int StructuralEdges { get; init; }
    public int CallChainEdges { get; init; }
    public int SemanticEdges { get; init; }
    public int ExpertiseEdges { get; init; }
    public int ArchivedExpertise { get; init; }
    public int TotalNodes { get; init; }
    public int TotalEdges { get; init; }
}