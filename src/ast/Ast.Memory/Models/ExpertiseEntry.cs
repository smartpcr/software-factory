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
/// Agent-authored knowledge node. Not a HypergraphNode — has its own dedicated table.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);

/// <summary>
/// Kind of expertise per architecture §4.2.
/// </summary>
public enum ExpertiseKind
{
    public string Id { get; init; } = string.Empty;
    public ExpertiseKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string TargetNodeId { get; init; } = string.Empty;
    public string Scope { get; init; } = "exact";
    public double Confidence { get; set; } = 1.0;

    public bool Archived { get; set; }
    public string SourceAgent { get; init; } = string.Empty;
    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    public float[]? Embedding { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Generates a content-addressable ID from the entry's content.
    /// </summary>
    public static string ComputeContentHash(string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash8 = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }

    /// <summary>
    /// Generates the content hash from title, body, and target node ID.
    /// </summary>
    public static string ComputeContentHash(string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
