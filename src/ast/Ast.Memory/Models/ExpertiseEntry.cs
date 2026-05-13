// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Agent-authored knowledge nodes. These are not HypergraphNode instances — they
/// have their own dedicated table because their fields (Confidence, Archived,
/// Embedding, SourceAgent) are core data, not incidental metadata.
/// Per architecture.md §4.2.
/// </summary>
public sealed class ExpertiseEntry
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

    /// <summary>SHA-256(Title + Body + TargetNodeId).</summary>
    public required string ContentHash { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A supporting reference for an expertise entry.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);

/// <summary>
/// Kind of expertise entry.
/// </summary>
public enum ExpertiseKind
{
    Pattern,
    Pitfall,
    Preference,
    Insight
}