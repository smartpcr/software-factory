// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Types of expertise knowledge that agents can record.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>A recurring design or implementation pattern worth reusing.</summary>
    Pattern,

    /// <summary>A known hazard, footgun, or fragile assumption.</summary>
    Pitfall,

    /// <summary>A team or codebase convention / style preference.</summary>
    Preference,

    /// <summary>A general insight or observation about code behavior.</summary>
    Insight
}

/// <summary>
/// Retrieval modes for memory queries.
/// </summary>
public enum RetrievalMode
{
    StructuralDrill,
    CallTrace,
    SemanticSearch,
    ExpertiseRecall,
    Hybrid
}

/// <summary>
/// Backend storage options for the memory graph.
/// </summary>
public enum MemoryBackend
{
    Sqlite,
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