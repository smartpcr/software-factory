// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Agent-authored knowledge node per architecture §4.2.
/// Not a <see cref="Ast.Core.HypergraphNode"/> — has its own dedicated table
/// with core fields (Confidence, Archived, Embedding, SourceAgent).
/// ID format: "expertise:{kind}:{hash8}".
/// </summary>
public sealed class ExpertiseEntry
{
    /// <summary>Content-addressable ID: "expertise:{kind}:{hash8}".</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Kind of expertise: Pattern, Pitfall, Preference, Insight.</summary>
    public ExpertiseKind Kind { get; init; }

    /// <summary>Headline summary.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Detailed description.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Code node this applies to (canonical ID).</summary>
    public string TargetNodeId { get; init; } = string.Empty;

    /// <summary>"exact" (default) or "subtree".</summary>
    public string Scope { get; init; } = "exact";

    /// <summary>Confidence score 0.0–1.0, default 1.0.</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>Soft-delete flag set by DecayService.</summary>
    public bool Archived { get; set; }

    /// <summary>Agent that authored this entry.</summary>
    public string SourceAgent { get; init; } = string.Empty;

    /// <summary>Supporting references.</summary>
    public IReadOnlyList<Citation> Evidence { get; init; } = Array.Empty<Citation>();

    /// <summary>Embedding vector of Title + " " + Body. Stored both inline and in vector store.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>SHA-256(Title + Body + TargetNodeId) for dedup.</summary>
    public string ContentHash { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Agent-authored knowledge node. These are not HypergraphNode instances —
/// they have their own dedicated storage because their fields (Confidence,
/// Archived, Embedding, SourceAgent) are core data, not incidental metadata.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);

/// <summary>
/// Kind of expertise per architecture §4.2.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>
    /// Unique identifier in the format "expertise:{kind}:{hash8}".
    /// Content-addressable for deduplication.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The kind of expertise (Pattern, Pitfall, Preference, Insight).
    /// </summary>
    public ExpertiseKind Kind { get; init; }

    /// <summary>
    /// Headline of the expertise entry.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of the expertise.
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// The code node this expertise applies to.
    /// </summary>
    public string TargetNodeId { get; init; } = string.Empty;

    /// <summary>
    /// Scope of applicability: "exact" (default) or "subtree".
    /// </summary>
    public string Scope { get; init; } = "exact";

    /// <summary>
    /// Confidence level from 0.0 to 1.0. Default 1.0.
    /// Modified by event-driven decay (code changes).
    /// </summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>
    /// Whether this entry has been archived (soft-deleted by decay).
    /// </summary>
    public bool Archived { get; set; }

    /// <summary>
    /// The agent session that authored this entry.
    /// </summary>
    public string SourceAgent { get; init; } = string.Empty;

    /// <summary>
    /// Supporting references for this expertise.
    /// </summary>
    public IReadOnlyList<Citation> Evidence { get; init; } = [];

    /// <summary>
    /// Embedding vector of "Title + ' ' + Body".
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// SHA-256 of Title + Body + TargetNodeId for deduplication.
    /// </summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>
    /// When this entry was first created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this entry was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Generates the content hash for deduplication: SHA-256(Title + Body + TargetNodeId).
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates the expertise ID: "expertise:{kind}:{hash8}" where hash8 is the
    /// first 8 characters of SHA-256(Title + Body + TargetNodeId).
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var hash = ComputeContentHash(title, body, targetNodeId);
        var hash8 = hash[..8];
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }
}