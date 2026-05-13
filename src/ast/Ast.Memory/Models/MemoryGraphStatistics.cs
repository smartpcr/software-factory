// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Statistics about the memory graph by layer.
/// </summary>
public sealed class MemoryGraphStatistics
{
    /// <summary>
    /// Gets or sets the total node count.
    /// </summary>
    public int TotalNodes { get; set; }

    /// <summary>
    /// Gets or sets the total edge count.
    /// </summary>
    public int TotalEdges { get; set; }

    /// <summary>
    /// Gets or sets the active expertise entry count.
    /// </summary>
    public int ActiveExpertiseCount { get; set; }

    /// <summary>
    /// Gets or sets the archived expertise entry count.
    /// </summary>
    public int ArchivedExpertiseCount { get; set; }

    /// <summary>
    /// Gets or sets node counts by layer.
    /// </summary>
    public Dictionary<string, int> NodeCountsByLayer { get; set; } = [];

    /// <summary>
    /// Gets or sets edge counts by semantic relation.
    /// </summary>
    public Dictionary<string, int> EdgeCountsByRelation { get; set; } = [];
}