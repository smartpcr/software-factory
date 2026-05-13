// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Kind of expertise entry recorded by agents.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>A reusable design pattern or best practice.</summary>
    Pattern,

    /// <summary>A known pitfall or anti-pattern to avoid.</summary>
    Pitfall,

    /// <summary>An operator or team preference.</summary>
    Preference,

    /// <summary>A general architectural or behavioral insight.</summary>
    Insight
}

/// <summary>
/// Retrieval mode for querying the memory graph.
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
/// Storage backend for the memory graph.
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