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
    /// <summary>Total nodes per layer.</summary>
    public Dictionary<string, int> NodesByLayer { get; set; } = [];

    /// <summary>Total edges per layer.</summary>
    public Dictionary<string, int> EdgesByLayer { get; set; } = [];

    /// <summary>Total active expertise entries.</summary>
    public int ActiveExpertiseCount { get; set; }

    /// <summary>Total archived expertise entries.</summary>
    public int ArchivedExpertiseCount { get; set; }

    /// <summary>Total audit log entries.</summary>
    public int AuditLogCount { get; set; }
}