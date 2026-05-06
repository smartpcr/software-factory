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
/// A supporting reference for an expertise entry.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);

/// <summary>
/// Kind of expertise per architecture §4.2.
/// </summary>
public enum ExpertiseKind
{
    Pattern,
    Pitfall,
    Preference,
    Insight,
}
