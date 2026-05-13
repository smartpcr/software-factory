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
/// An agent-authored knowledge entry attached to a code node.
/// Per architecture.md §4.2.
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

    /// <summary>
    /// Generates a deterministic ID from kind, title, body, and target node.
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash8 = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }
}