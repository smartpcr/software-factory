// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Agent-authored knowledge nodes. These are not HypergraphNode instances — they
/// have their own dedicated table because their fields (Confidence, Archived,
/// Embedding, SourceAgent) are core data, not incidental metadata.
/// Per architecture.md §4.2.
/// </summary>
public class ExpertiseEntry
{
    /// <summary>Format: "expertise:{kind}:{hash8}".</summary>
    public required string Id { get; init; }

    public required ExpertiseKind Kind { get; init; }

    /// <summary>Headline.</summary>
    public required string Title { get; init; }

    /// <summary>Detailed description.</summary>
    public required string Body { get; init; }

    /// <summary>Code node this applies to.</summary>
    public required string TargetNodeId { get; init; }

    /// <summary>"exact" (default) or "subtree".</summary>
    public string Scope { get; init; } = "exact";

    /// <summary>0.0–1.0, default 1.0.</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>Default false; set by DecayService.</summary>
    public bool Archived { get; set; }

    /// <summary>Agent that authored this entry.</summary>
    public required string SourceAgent { get; init; }

    /// <summary>Supporting references.</summary>
    public IReadOnlyList<Citation> Evidence { get; init; } = [];

    /// <summary>Vector of Title + " " + Body.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>SHA-256(Title + Body + TargetNodeId) for dedup.</summary>
    public string ContentHash { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An expertise node representing agent-authored knowledge about code.
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
}

    /// <summary>
    /// Generates the content hash for deduplication: SHA-256(Title + Body + TargetNodeId).
    /// </summary>
    public static string ComputeContentHash(string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

/// <summary>
/// Kind of expertise entry.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>
    /// Gets the unique identifier in format "expertise:{kind}:{hash8}".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the kind of expertise.
    /// </summary>
    public required ExpertiseKind Kind { get; init; }

    /// <summary>
    /// Gets the headline title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the detailed description.
    /// </summary>
    public required string Body { get; init; }

    /// <summary>
    /// Gets the ID of the code node this expertise applies to.
    /// </summary>
    public required string TargetNodeId { get; init; }

    /// <summary>
    /// Gets the scope: "exact" (single node) or "subtree" (node and descendants).
    /// </summary>
    public string Scope { get; init; } = "exact";

    /// <summary>
    /// Gets or sets the confidence score (0.0–1.0).
    /// </summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets whether this entry is archived (soft-deleted).
    /// </summary>
    public bool Archived { get; set; }

    /// <summary>
    /// Gets the agent session that created this entry.
    /// </summary>
    public required string SourceAgent { get; init; }

    /// <summary>
    /// Gets the evidence citations supporting this expertise.
    /// </summary>
    public IReadOnlyList<Citation>? Evidence { get; init; }

    /// <summary>
    /// Gets or sets the embedding vector for similarity search.
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Gets the SHA-256 content hash of Title+Body for deduplication.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Gets the creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the last update timestamp.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Generates the content hash for deduplication.
    /// </summary>
    public static string ComputeContentHash(string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates the ID for an expertise entry.
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var hash = ComputeContentHash(title, body, targetNodeId);
        var hash8 = hash[..8];
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }
}