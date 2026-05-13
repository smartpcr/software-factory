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
    Pattern,
    Pitfall,
    Preference,
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