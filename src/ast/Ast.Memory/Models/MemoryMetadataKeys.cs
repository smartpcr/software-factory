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
    public const string SemanticRelation = "SemanticRelation";
}