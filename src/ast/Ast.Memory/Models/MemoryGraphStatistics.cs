// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Statistics about the memory graph, extending base graph statistics
/// with memory-layer-specific breakdowns.
/// </summary>
public class MemoryGraphStatistics
{
    /// <summary>Total node count.</summary>
    public long TotalNodes { get; set; }

    /// <summary>Total edge count.</summary>
    public long TotalEdges { get; set; }

    /// <summary>Node counts per memory layer.</summary>
    public Dictionary<string, long> NodesByLayer { get; set; } = new();

    /// <summary>Edge counts per semantic relation.</summary>
    public Dictionary<string, long> EdgesByRelation { get; set; } = new();

    /// <summary>Total expertise entries.</summary>
    public long TotalExpertise { get; set; }

    /// <summary>Archived expertise entries.</summary>
    public long ArchivedExpertise { get; set; }
}