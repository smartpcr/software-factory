// -----------------------------------------------------------------------
// <copyright file="NodeFilter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Optional filter criteria for querying memory graph nodes.
/// Used by <see cref="Interfaces.IMemoryGraphStore.GetNodesByLayerAsync"/>.
/// </summary>
public class NodeFilter
{
    /// <summary>
    /// If set, only return nodes whose NodeType matches one of these values.
    /// </summary>
    public IReadOnlyList<string>? NodeTypes { get; init; }

    /// <summary>
    /// If set, only return nodes whose content hash matches this value.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// If set, only return nodes whose qualified name contains this substring (case-insensitive).
    /// </summary>
    public string? QualifiedNameContains { get; init; }

    /// <summary>
    /// If set, only return nodes that have a metadata key matching all provided entries.
    /// </summary>
    public IReadOnlyDictionary<string, string>? MetadataMatches { get; init; }
}