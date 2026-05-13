// -----------------------------------------------------------------------
// <copyright file="MemoryMetadataKeys.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Well-known metadata keys for memory graph nodes.
/// </summary>
public static class MemoryMetadataKeys
{
    public const string MemoryLayer = "MemoryLayer";
    public const string ContentHash = "ContentHash";
    public const string QualifiedName = "QualifiedName";
    public const string SinceIndex = "SinceIndex";
    public const string UntilIndex = "UntilIndex";
    public const string Embedding = "Embedding";
    public const string CanonicalId = "CanonicalId";
    public const string PackageName = "PackageName";
    public const string NamespaceName = "NamespaceName";
    public const string SimilarityScore = "SimilarityScore";
    public const string OriginalEdgeType = "OriginalEdgeType";
    public const string Confidence = "Confidence";
    public const string Stale = "Stale";
    public const string SourceCode = "SourceCode";
    public const string QualifiedName = "QualifiedName";
    public const string LastTouched = "LastTouched";
    public const string Embedding = "Embedding";
    public const string EmbeddingModel = "EmbeddingModel";
    public const string EmbeddingStatus = "EmbeddingStatus";
    public const string Package = "Package";

    /// <summary>
    /// Tracks which source file an edge was extracted from (edge ownership).
    /// Only edges owned by a given file should be retired when that file is re-synced.
    /// </summary>
    public const string SourceFile = "SourceFile";

    /// <summary>
    /// Structural hash of a node (name, type, scope, modifiers). Used to detect
    /// minor vs substantive changes: if StructuralHash is the same but ContentHash
    /// differs, the change is considered substantive (code logic changed) but the
    /// node's identity (name, type, position in the hierarchy) is unchanged.
    /// </summary>
    public const string StructuralHash = "StructuralHash";
}
