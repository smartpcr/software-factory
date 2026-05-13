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
    /// Deterministic ID in the format "expertise:{kind}:{hash8}".
    /// </summary>
    public string Id { get; }

    public ExpertiseKind Kind { get; }
    public string Title { get; }
    public string Body { get; }
    public string TargetNodeId { get; }

    private string scope = "exact";

    public string Scope
    {
        get => this.scope;
        init
        {
            if (value is not ("exact" or "subtree"))
            {
                throw new ArgumentException($"Scope must be \"exact\" or \"subtree\", got \"{value}\".", nameof(this.Scope));
            }

            this.scope = value;
        }
    }

    private double confidence = 1.0;

    public double Confidence
    {
        get => this.confidence;
        init
        {
            if (value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(this.Confidence), value, "Confidence must be between 0.0 and 1.0.");
            }

            this.confidence = value;
        }
    }

    public bool Archived { get; init; }
    public string SourceAgent { get; }
    public IReadOnlyList<Citation> Evidence { get; init; } = Array.Empty<Citation>();
    public float[]? Embedding { get; init; }

    /// <summary>SHA-256 hex of Title + Body + TargetNodeId (per architecture.md §4.2, A-18).</summary>
    public string ContentHash { get; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ExpertiseEntry(ExpertiseKind kind, string title, string body, string targetNodeId, string sourceAgent)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(body);
        ArgumentException.ThrowIfNullOrEmpty(targetNodeId);
        ArgumentException.ThrowIfNullOrEmpty(sourceAgent);

        this.Kind = kind;
        this.Title = title;
        this.Body = body;
        this.TargetNodeId = targetNodeId;
        this.SourceAgent = sourceAgent;

        var idHash = ComputeHash8(title + body + targetNodeId);
        this.Id = $"expertise:{kind.ToString().ToLowerInvariant()}:{idHash}";
        this.ContentHash = ComputeFullHash(title + body + targetNodeId);
    }

    /// <summary>
    /// Creates a copy with updated confidence and timestamp.
    /// </summary>
    public ExpertiseEntry WithConfidence(double newConfidence)
    {
        return new ExpertiseEntry(this.Kind, this.Title, this.Body, this.TargetNodeId, this.SourceAgent)
        {
            Confidence = Math.Clamp(newConfidence, 0.0, 1.0),
            Archived = this.Archived,
            Evidence = this.Evidence,
            Embedding = this.Embedding,
            Scope = this.Scope,
            CreatedAt = this.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string ComputeHash8(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    public ExpertiseKind Kind { get; init; }
    public string Title { get; init; }
    public string Body { get; init; }
    public string TargetNodeId { get; init; }
    public string Scope { get; init; }
    public double Confidence { get; set; }
    public bool Archived { get; set; }
    public string? SourceAgent { get; init; }
    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    public float[]? Embedding { get; set; }
    public string ContentHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An agent-authored knowledge entry attached to a code node.
/// Per architecture.md §4.2.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);

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