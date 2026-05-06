// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Collections.Generic;

/// <summary>
/// Statistics about the memory graph per architecture §5.1.
/// </summary>
public class MemoryGraphStatistics
{
    public long TotalNodes { get; set; }

    public long TotalEdges { get; set; }

    public long ActiveNodes { get; set; }

    public long RetiredNodes { get; set; }

    public long ActiveEdges { get; set; }

    public long RetiredEdges { get; set; }

    public long ExpertiseEntries { get; set; }

    public long ArchivedExpertise { get; set; }

    public Dictionary<string, long> NodesByLayer { get; set; } = new();

    public Dictionary<string, long> EdgesByRelation { get; set; } = new();

    public Dictionary<string, long> NodesByLanguage { get; set; } = new();
}
