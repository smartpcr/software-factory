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
/// Agent-authored knowledge node linked to a code entity via APPLIES_TO edges.
/// </summary>
public sealed class ExpertiseEntry
{
    public ExpertiseEntry(
        ExpertiseKind kind,
        string title,
        string body,
        string targetNodeId,
        string sourceAgent,
        string scope = "exact",
        double confidence = 1.0,
        IReadOnlyList<Citation>? evidence = null)
    {
        this.Kind = kind;
        this.Title = title ?? throw new ArgumentNullException(nameof(title));
        this.Body = body ?? throw new ArgumentNullException(nameof(body));
        this.TargetNodeId = targetNodeId ?? throw new ArgumentNullException(nameof(targetNodeId));
        this.SourceAgent = sourceAgent ?? throw new ArgumentNullException(nameof(sourceAgent));
        this.Scope = scope is "exact" or "subtree" ? scope : throw new ArgumentException("Scope must be 'exact' or 'subtree'.", nameof(scope));
        this.Confidence = Math.Clamp(confidence, 0.0, 1.0);
        this.Evidence = evidence ?? Array.Empty<Citation>();
        this.ContentHash = ComputeHash(title, body, targetNodeId);
        this.Id = $"expertise:{kind.ToString().ToLowerInvariant()}:{this.ContentHash[..8]}";
        this.CreatedAt = DateTime.UtcNow;
        this.UpdatedAt = this.CreatedAt;
    }

    /// <summary>Deterministic ID: expertise:{kind}:{hash8}.</summary>
    public string Id { get; init; }

    public ExpertiseKind Kind { get; init; }
    public string Title { get; init; }
    public string Body { get; init; }
    public string TargetNodeId { get; init; }
    public string Scope { get; init; }
    public double Confidence { get; set; }
    public bool Archived { get; set; }
    public string SourceAgent { get; init; }
    public IReadOnlyList<Citation> Evidence { get; init; }
    public float[]? Embedding { get; set; }
    public string ContentHash { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }

    private static string ComputeHash(string title, string body, string targetNodeId)
    {
        var input = $"{title}{body}{targetNodeId}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}