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
    /// <summary>The underlying hyperedge.</summary>
    public Hyperedge Inner { get; }

    /// <summary>
    /// Temporal version range for this edge. Per architecture.md §4.1.
    /// </summary>
    public VersionRange Version { get; }
}