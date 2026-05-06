// -----------------------------------------------------------------------
// <copyright file="MemoryMetadataKeys.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Constants for curated Metadata key names used across the memory layer.
/// Per architecture.md §4.5.
/// </summary>
public static class MemoryMetadataKeys
{
    /// <summary>Graph layer tag: structural, call_chain, semantic, expertise.</summary>
    public const string MemoryLayer = "MemoryLayer";

    /// <summary>Canonical edge kind: CONTAINS, CALLS, SIMILAR_TO, etc.</summary>
    public const string SemanticRelation = "SemanticRelation";

    /// <summary>SHA-256 hash of the source text for change detection.</summary>
    public const string ContentHash = "ContentHash";

    /// <summary>Commit SHA when this node/edge was first observed.</summary>
    public const string SinceCommit = "SinceCommit";

    /// <summary>Commit index when this node/edge was first observed.</summary>
    public const string SinceIndex = "SinceIndex";

    /// <summary>Commit SHA when this node/edge was retired.</summary>
    public const string UntilCommit = "UntilCommit";

    /// <summary>Commit index when this node/edge was retired.</summary>
    public const string UntilIndex = "UntilIndex";

    /// <summary>Summary text for collapsed nodes.</summary>
    public const string Summary = "Summary";

    /// <summary>Number of members in a container node.</summary>
    public const string MemberCount = "MemberCount";

    /// <summary>Cosine similarity score on SIMILAR_TO edges.</summary>
    public const string SimilarityScore = "SimilarityScore";

    /// <summary>Original HyperedgeType enum value for code-derived edges promoted to memory edges.</summary>
    public const string OriginalEdgeType = "OriginalEdgeType";

    /// <summary>Confidence score (0.0–1.0).</summary>
    public const string Confidence = "Confidence";

    /// <summary>Whether this node/edge is considered stale.</summary>
    public const string Stale = "Stale";

    /// <summary>Fully qualified symbol name.</summary>
    public const string QualifiedName = "QualifiedName";

    /// <summary>Timestamp of last access or modification.</summary>
    public const string LastTouched = "LastTouched";

    /// <summary>Embedding vector (float[]).</summary>
    public const string Embedding = "Embedding";

    /// <summary>Name of the model that produced the embedding.</summary>
    public const string EmbeddingModel = "EmbeddingModel";

    /// <summary>Package or project name.</summary>
    public const string Package = "Package";
}