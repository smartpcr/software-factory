// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Classifies the kind of expertise an agent has learned.
/// Per architecture.md §2.1.
/// </summary>
public enum ExpertiseKind
{
    Pattern,
    Pitfall,
    Preference,
    Insight
}

/// <summary>
/// Determines how memory is retrieved from the graph.
/// Per architecture.md §5.3.
/// </summary>
public enum RetrievalMode
{
    /// <summary>Walk structural containment hierarchy.</summary>
    StructuralDrill,

    /// <summary>Follow call-chain edges.</summary>
    CallTrace,

    /// <summary>Embedding-based similarity search.</summary>
    SemanticSearch,

    /// <summary>Recall expertise entries.</summary>
    ExpertiseRecall,

    /// <summary>Combine all modes with weighted scoring.</summary>
    Hybrid
}

/// <summary>
/// Backend storage engine for memory persistence.
/// Per architecture.md §2.1.
/// </summary>
public enum MemoryBackend
{
    /// <summary>SQLite with FTS5 and sqlite-vss.</summary>
    Sqlite,

    /// <summary>Neo4j graph database.</summary>
    Neo4j
}

/// <summary>
/// Status of a file in a git diff.
/// </summary>
public enum FileChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed
}