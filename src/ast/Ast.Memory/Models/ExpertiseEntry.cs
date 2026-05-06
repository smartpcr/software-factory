// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Agent-authored knowledge node — per architecture.md §4.2.
/// These are <b>not</b> <c>HypergraphNode</c> instances; they have their own
/// dedicated table (<c>expertise</c>) because their fields (<c>Confidence</c>,
/// <c>Archived</c>, <c>Embedding</c>, <c>SourceAgent</c>) are core data.
/// <para>
/// ID format: <c>expertise:{kind}:{hash8}</c> where <c>hash8</c> is the first 8
/// characters of <c>SHA-256(Title + Body + TargetNodeId)</c>.
/// </para>
/// </summary>
public class ExpertiseEntry
{
    /// <summary>Unique ID — format: <c>expertise:{kind}:{hash8}</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Kind of expertise.</summary>
    public ExpertiseKind Kind { get; set; } = ExpertiseKind.Insight;

    /// <summary>Headline.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Detailed description.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Code node this applies to.</summary>
    public string TargetNodeId { get; set; } = string.Empty;

    /// <summary>Scope: <c>"exact"</c> (default) or <c>"subtree"</c>.</summary>
    public string Scope { get; set; } = "exact";

    /// <summary>Confidence score (0.0–1.0).</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>Whether this entry has been archived (soft-deleted).</summary>
    public bool Archived { get; set; }

    /// <summary>Agent that authored this entry.</summary>
    public string SourceAgent { get; set; } = string.Empty;

    /// <summary>Supporting references.</summary>
    public IReadOnlyList<Citation> Evidence { get; set; } = Array.Empty<Citation>();

    /// <summary>Embedding vector of <c>Title + " " + Body</c>.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>SHA-256(Title + Body + TargetNodeId) for content-addressable dedup.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Timestamp when this entry was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Timestamp when this entry was last modified.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}