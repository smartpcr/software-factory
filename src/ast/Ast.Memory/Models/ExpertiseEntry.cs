// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Agent-authored knowledge node. Not a HypergraphNode — has its own
/// dedicated table with domain-specific columns. See architecture.md §4.2.
/// </summary>
public sealed class ExpertiseEntry
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
    public string ContentHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An expertise node representing agent-authored knowledge about code.
/// </summary>
public sealed class ExpertiseEntry
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
    public static string GenerateId(ExpertiseKind kind, string contentHash)
    {
        var hash8 = contentHash[..8];
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }
}