// -----------------------------------------------------------------------
// <copyright file="MemoryEdge.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using Ast.Core;

/// <summary>
/// Composition wrapper around <see cref="Hyperedge"/> adding memory-specific accessors.
/// </summary>
public sealed class MemoryEdge
{
    public MemoryEdge(Hyperedge inner, VersionRange? version = null)
    {
        this.Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Version = version ?? TryExtractVersionRange(inner);
    }

    /// <summary>
    /// The underlying hyperedge.
    /// </summary>
    public Hyperedge Inner { get; }

    /// <summary>
    /// The canonical semantic relation name (e.g., "CONTAINS", "CALLS").
    /// </summary>
    public string SemanticRelation =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var v) && v is string s
            ? s
            : string.Empty;

    /// <summary>
    /// The memory layer this edge belongs to.
    /// </summary>
    public string MemoryLayer =>
        this.Inner.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var v) && v is string s
            ? s
            : string.Empty;

    /// <summary>
    /// Temporal version range.
    /// </summary>
    public VersionRange? Version { get; }

    /// <summary>
    /// Checks whether this edge is active at the given commit index.
    /// </summary>
    public bool IsActiveAt(int commitIndex) =>
        this.Version?.IsActiveAt(commitIndex) ?? true;

    private static VersionRange? TryExtractVersionRange(Hyperedge edge)
    {
        if (!edge.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sinceCommitObj)
            || sinceCommitObj is not string sinceCommit)
        {
            return null;
        }

        var sinceIndex = edge.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si)
            ? Convert.ToInt32(si)
            : 0;

        var untilCommit = edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var uc)
            ? uc as string
            : null;

        int? untilIndex = edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) && ui != null
            ? Convert.ToInt32(ui)
            : null;

        return new VersionRange(sinceCommit, sinceIndex, untilCommit, untilIndex);
    }
}
