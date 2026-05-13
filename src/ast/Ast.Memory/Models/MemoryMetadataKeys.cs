// -----------------------------------------------------------------------
// <copyright file="MemoryMetadataKeys.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Constants for all curated Metadata dictionary keys used by the memory layer.
/// Prevents typo-induced bugs and enables IDE autocomplete.
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
    public const string QualifiedName = "QualifiedName";
    public const string LastTouched = "LastTouched";
    public const string Embedding = "Embedding";
    public const string EmbeddingModel = "EmbeddingModel";
    public const string EmbeddingStatus = "EmbeddingStatus";
    public const string Package = "Package";
}

/// <summary>
/// Canonical semantic relation names used in edge metadata.
/// </summary>
public static class SemanticRelations
{
    public const string Contains = "CONTAINS";
    public const string DependsOn = "DEPENDS_ON";
    public const string Calls = "CALLS";
    public const string Extends = "EXTENDS";
    public const string Implements = "IMPLEMENTS";
    public const string References = "REFERENCES";
    public const string SimilarTo = "SIMILAR_TO";
    public const string BelongsToCluster = "BELONGS_TO_CLUSTER";
    public const string AppliesTo = "APPLIES_TO";
    public const string Supersedes = "SUPERSEDES";
    public const string DerivedFrom = "DERIVED_FROM";
}

/// <summary>
/// Memory layer tag values.
/// </summary>
public static class MemoryLayers
{
    public const string Structural = "structural";
    public const string CallChain = "call_chain";
    public const string Semantic = "semantic";
    public const string Expertise = "expertise";
}
