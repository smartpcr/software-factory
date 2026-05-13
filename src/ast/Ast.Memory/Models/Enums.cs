// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Kind of expertise entry authored by an agent.
/// </summary>
public enum ExpertiseKind
{
    Pattern,
    Pitfall,
    Preference,
    Insight
}

/// <summary>
/// Retrieval mode for memory queries.
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
/// Backend for the memory graph store.
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