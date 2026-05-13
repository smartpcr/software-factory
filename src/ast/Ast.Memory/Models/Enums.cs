// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Types of expertise entries that agents can author.
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
/// Modes for memory retrieval.
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
/// Backend storage implementations.
/// </summary>
public enum MemoryBackend
{
    Sqlite,
    Neo4j
}
