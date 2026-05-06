// -----------------------------------------------------------------------
// <copyright file="MemoryMetadataKeys.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Curated constants for all well-known Metadata dictionary keys
/// used by MemoryNode, MemoryEdge, and related types.
/// Per architecture.md §4.5.
/// </summary>
public static class MemoryMetadataKeys
{
    public const string MemoryLayer = "MemoryLayer";
    public const string SemanticRelation = "SemanticRelation";
    public const string ContentHash = "ContentHash";
    public const string SinceCommit = "SinceCommit";
    public const string SinceIndex = "SinceIndex";
    public const string UntilCommit = "UntilCommit";
    public const string UntilIndex = "UntilIndex";
    public const string Summary = "Summary";
    public const string MemberCount = "MemberCount";
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
}