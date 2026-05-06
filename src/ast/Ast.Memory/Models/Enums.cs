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
    Pattern,
    Pitfall,
    Preference,
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
