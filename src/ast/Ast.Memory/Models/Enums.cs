// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Retrieval modes for the memory retrieval engine.
/// Each mode corresponds to a distinct traversal or search strategy.
/// </summary>
public enum RetrievalMode
{
    /// <summary>BFS from a seed node along CONTAINS/DEPENDS_ON edges.</summary>
    StructuralDrill,

    /// <summary>Forward/backward traversal along CALLS/EXTENDS/IMPLEMENTS edges.</summary>
    CallTrace,

    /// <summary>Embed query text, then ANN lookup against pre-computed node embeddings.</summary>
    SemanticSearch,

    /// <summary>Lookup expertise entries via APPLIES_TO edges and vector search.</summary>
    ExpertiseRecall,

    /// <summary>Combine all applicable modes based on request shape (default).</summary>
    Hybrid
}

/// <summary>
/// Kinds of expertise that can be recorded against code nodes.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>A reusable coding pattern or idiom.</summary>
    Pattern,

    /// <summary>A known pitfall, anti-pattern, or fragile area.</summary>
    Pitfall,

    /// <summary>An agent or developer preference for code style or approach.</summary>
    Preference,

    /// <summary>A general insight or observation about the code.</summary>
    Insight
}

/// <summary>
/// Available memory graph storage backends.
/// </summary>
public enum MemoryBackend
{
    /// <summary>SQLite-backed graph store (default, local).</summary>
    Sqlite,

    /// <summary>Neo4j-backed graph store (production).</summary>
    Neo4j
}