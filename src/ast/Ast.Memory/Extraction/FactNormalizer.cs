// -----------------------------------------------------------------------
// <copyright file="FactNormalizer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Deduplicates nodes across languages and assigns canonical IDs per architecture §4.2.
/// Cross-language merging matches by QualifiedName so a C# definition in one file and
/// a PowerShell stub reference in another file merge into a single canonical node.
/// Normalizes NodeType and Language values from extractors (lowercase → PascalCase).
/// Synthesizes package/module hierarchy per operator answer.
/// </summary>
public class FactNormalizer
{
    private static readonly Dictionary<string, string> NodeTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "class", "Class" },
        { "interface", "Interface" },
        { "struct", "Struct" },
        { "record", "Record" },
        { "method", "Method" },
        { "function", "Function" },
        { "property", "Property" },
        { "constructor", "Constructor" },
        { "namespace", "Namespace" },
        { "enum", "Enum" },
        { "field", "Field" },
        { "file", "Module" },
        { "module", "Module" },
        { "repository", "Repo" },
        { "repo", "Repo" },
        { "package", "Package" },
    };

    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "csharp", "C#" },
        { "c#", "C#" },
        { "powershell", "PowerShell" },
        { "ps", "PowerShell" },
    };

    private readonly ILogger<FactNormalizer> logger;

    public FactNormalizer(ILogger<FactNormalizer> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Normalizes a <see cref="HypergraphData"/> in-place: normalizes NodeType/Language,
    /// assigns canonical IDs, merges cross-language duplicates (by QualifiedName),
    /// stamps metadata (MemoryLayer, ContentHash, QualifiedName), and synthesizes hierarchy.
    /// </summary>
    public NormalizationResult Normalize(HypergraphData data, string repoRoot)
    {
        var idMappings = new Dictionary<string, string>();
        var mergedStubFiles = new Dictionary<string, string>();
        var duplicatesMerged = 0;

        // Phase 0: Normalize NodeType and Language on all nodes (create new nodes since init-only)
        foreach (var kvp in data.Nodes.ToList())
        {
            var node = kvp.Value;
            var normalizedType = NormalizeNodeType(node.NodeType);
            var normalizedLang = NormalizeLanguage(node.Language);
            if (normalizedType != node.NodeType || normalizedLang != node.Language)
            {
                data.Nodes[kvp.Key] = new HypergraphNode
                {
                    Id = node.Id,
                    Name = node.Name,
                    NodeType = normalizedType,
                    Language = normalizedLang,
                    FilePath = node.FilePath,
                    Scope = node.Scope,
                    LineStart = node.LineStart,
                    LineEnd = node.LineEnd,
                    Modifiers = node.Modifiers,
                    Metadata = node.Metadata,
                };
            }
        }

        // Phase 1: Assign canonical IDs to all nodes
        var canonicalNodes = new Dictionary<string, HypergraphNode>();
        var oldToCanonical = new Dictionary<string, string>();

        foreach (var kvp in data.Nodes.ToList())
        {
            var node = kvp.Value;
            var oldId = kvp.Key;
            var canonicalId = this.ComputeCanonicalId(node, repoRoot);

            oldToCanonical[oldId] = canonicalId;

            if (!string.Equals(oldId, canonicalId, StringComparison.Ordinal))
            {
                idMappings[oldId] = canonicalId;
            }

            if (canonicalNodes.TryGetValue(canonicalId, out var existing))
            {
                var winner = this.PickWinner(existing, node);
                canonicalNodes[canonicalId] = this.CloneWithId(winner, canonicalId, repoRoot);
                duplicatesMerged++;

                var loser = ReferenceEquals(winner, existing) ? node : existing;
                if (!string.IsNullOrEmpty(loser.FilePath))
                {
                    mergedStubFiles[oldId] = loser.FilePath;
                }

                this.logger.LogDebug(
                    "Merged duplicate node {OldId} into {CanonicalId} (kept {Winner})",
                    oldId, canonicalId, winner.Language);
            }
            else
            {
                canonicalNodes[canonicalId] = this.CloneWithId(node, canonicalId, repoRoot);
            }
        }

        // Phase 1.5: Cross-language dedup by QualifiedName.
        // Only merge nodes from DIFFERENT languages (e.g., C# class + PowerShell stub).
        // Nodes from the same language with identical QualifiedName are NOT merged —
        // they may be distinct symbols that happen to share a short name.
        // Per operator answer: cross-language merge should work for simple names too
        // (e.g., "PaymentService" without dots) when languages differ.
        var qualNameGroups = canonicalNodes
            .Where(kvp => IsDefinitionType(kvp.Value.NodeType))
            .Where(kvp =>
            {
                var qn = this.GetQualifiedName(kvp.Value);
                return !string.IsNullOrEmpty(qn);
            })
            .GroupBy(kvp => this.GetQualifiedName(kvp.Value), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1
                && g.Select(x => NormalizeLanguage(x.Value.Language))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        foreach (var group in qualNameGroups)
        {
            var items = group.ToList();
            var winner = items[0];
            for (var i = 1; i < items.Count; i++)
            {
                var loser = items[i];
                var pickedWinner = this.PickWinner(winner.Value, loser.Value);
                var pickedWinnerId = ReferenceEquals(pickedWinner, winner.Value) ? winner.Key : loser.Key;
                var pickedLoserId = ReferenceEquals(pickedWinner, winner.Value) ? loser.Key : winner.Key;

                // Redirect loser to winner's canonical ID
                oldToCanonical[pickedLoserId] = pickedWinnerId;
                idMappings[pickedLoserId] = pickedWinnerId;

                // Also redirect any old IDs that pointed to the loser
                foreach (var key in oldToCanonical.Keys.ToList())
                {
                    if (oldToCanonical[key] == pickedLoserId)
                    {
                        oldToCanonical[key] = pickedWinnerId;
                    }
                }

                canonicalNodes.Remove(pickedLoserId);
                canonicalNodes[pickedWinnerId] = this.CloneWithId(pickedWinner, pickedWinnerId, repoRoot);
                duplicatesMerged++;

                if (!string.IsNullOrEmpty(loser.Value.FilePath))
                {
                    mergedStubFiles[pickedLoserId] = loser.Value.FilePath;
                }

                this.logger.LogDebug(
                    "Cross-language merge: {LoserId} into {WinnerId}",
                    pickedLoserId, pickedWinnerId);

                winner = new KeyValuePair<string, HypergraphNode>(pickedWinnerId, pickedWinner);
            }
        }

        // Phase 2: Rebuild data with canonical IDs
        data.Nodes.Clear();
        foreach (var kvp in canonicalNodes)
        {
            data.Nodes[kvp.Key] = kvp.Value;
        }

        // Phase 3: Rewrite edge references from old IDs to canonical IDs
        var deduplicatedEdges = new Dictionary<string, Hyperedge>();
        foreach (var edge in data.Hyperedges)
        {
            var newNodeIds = edge.NodeIds
                .Select(id => oldToCanonical.TryGetValue(id, out var mapped) ? mapped : id)
                .ToList();

            var newRoles = new Dictionary<string, string>();
            foreach (var role in edge.Roles)
            {
                var newKey = oldToCanonical.TryGetValue(role.Key, out var mapped) ? mapped : role.Key;
                if (!newRoles.ContainsKey(newKey))
                {
                    newRoles[newKey] = role.Value;
                }
            }

            // Skip self-referencing edges
            if (newNodeIds.Distinct().Count() < 2 && newNodeIds.Count >= 2)
            {
                continue;
            }

            // Ensure at least one referenced node exists; create external canonical
            // placeholders for endpoints not in the current data set (e.g., unchanged
            // file symbols referenced by edges from changed files during incremental).
            if (!newNodeIds.Any(id => data.Nodes.ContainsKey(id)))
            {
                continue;
            }

            foreach (var nid in newNodeIds)
            {
                if (!data.Nodes.ContainsKey(nid) && CanonicalId.IsInRepo(nid))
                {
                    var extPath = CanonicalId.ExtractFilePath(nid);
                    var extQn = CanonicalId.ExtractQualifiedName(nid);
                    data.Nodes[nid] = new HypergraphNode
                    {
                        Id = nid,
                        Name = extQn ?? extPath ?? nid,
                        NodeType = extQn != null ? "Class" : "Module",
                        Language = "Unknown",
                        FilePath = extPath ?? string.Empty,
                        Metadata = new Dictionary<string, object?>
                        {
                            { MemoryMetadataKeys.MemoryLayer, "structural" },
                            { "ExternalCanonical", true },
                        },
                    };
                }
            }

            // Ensure edge has semantic relation, MemoryLayer, and OriginalEdgeType metadata
            var metadata = new Dictionary<string, object?>(edge.Metadata);
            if (!metadata.ContainsKey(MemoryMetadataKeys.SemanticRelation))
            {
                metadata[MemoryMetadataKeys.SemanticRelation] = InferSemanticRelation(edge.EdgeType);
            }

            if (!metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
            {
                metadata[MemoryMetadataKeys.MemoryLayer] = InferMemoryLayer(edge.EdgeType);
            }

            if (!metadata.ContainsKey(MemoryMetadataKeys.OriginalEdgeType))
            {
                metadata[MemoryMetadataKeys.OriginalEdgeType] = edge.EdgeType.ToString();
            }

            var newEdge = new Hyperedge
            {
                Id = edge.Id,
                EdgeType = edge.EdgeType,
                NodeIds = newNodeIds,
                Roles = newRoles,
                Languages = edge.Languages,
                Weight = edge.Weight,
                Metadata = metadata,
            };

            // Deduplicate edges by type + SemanticRelation + roles + endpoint pair.
            // This preserves distinct relationships between the same nodes
            // (e.g., CALLS vs CONTAINS between the same two nodes).
            var isUndirected = newEdge.EdgeType == HyperedgeType.MemoryRelation;
            var endpointKey = isUndirected
                ? string.Join(",", newNodeIds.OrderBy(x => x))
                : string.Join(",", newNodeIds);
            var semanticRel = metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr)
                ? sr?.ToString() ?? string.Empty
                : InferSemanticRelation(newEdge.EdgeType);
            var rolesKey = string.Join(";", newRoles.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}"));
            var edgeKey = $"{newEdge.EdgeType}:{semanticRel}:{rolesKey}:{endpointKey}";
            if (!deduplicatedEdges.ContainsKey(edgeKey))
            {
                deduplicatedEdges[edgeKey] = newEdge;
            }
        }

        data.Hyperedges.Clear();
        data.Hyperedges.AddRange(deduplicatedEdges.Values);

        // Phase 4: Synthesize repo root node if not already present
        if (!data.Nodes.ContainsKey(CanonicalId.RepoRoot))
        {
            var repoRootNode = new HypergraphNode
            {
                Id = CanonicalId.RepoRoot,
                Name = Path.GetFileName(repoRoot) ?? "repo",
                NodeType = "Repo",                Language = "None",
                FilePath = string.Empty,
            };
            repoRootNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
            data.Nodes[CanonicalId.RepoRoot] = repoRootNode;
        }

        this.logger.LogInformation(
            "FactNormalizer: Normalized {Total} nodes, merged {Merged} duplicates, {Mappings} IDs remapped",
            data.Nodes.Count, duplicatesMerged, idMappings.Count);

        return new NormalizationResult
        {
            NodesNormalized = data.Nodes.Count,
            DuplicatesMerged = duplicatesMerged,
            IdMappings = idMappings,
            MergedStubFiles = mergedStubFiles,
        };
    }

    /// <summary>
    /// Normalizes freshly parsed data for changed files only.
    /// Deduplicates only within the changed-file set per operator answer.
    /// Filters nodes to the changed-file scope, but preserves unchanged-file endpoint
    /// nodes referenced by retained edges as canonical references (not Unknown placeholders).
    /// Does NOT remove freshly parsed nodes — retirement of stale nodes from prior
    /// indexing is handled by the extraction pipeline via the graph store.
    /// </summary>
    public NormalizationResult NormalizeFiles(
        HypergraphData data,
        string repoRoot,
        IReadOnlyList<string> changedRelativePaths)
    {
        // Build a set of normalized changed file paths for comparison
        var changedPathSet = new HashSet<string>(
            changedRelativePaths.Select(p => CanonicalId.NormalizePath(p)),
            StringComparer.OrdinalIgnoreCase);

        // Classify nodes: changed-file vs unchanged-file
        var changedFileNodeIds = new HashSet<string>();
        var unchangedFileNodes = new Dictionary<string, HypergraphNode>();
        foreach (var kvp in data.Nodes)
        {
            var node = kvp.Value;
            if (string.IsNullOrEmpty(node.FilePath))
            {
                changedFileNodeIds.Add(kvp.Key);
                continue;
            }

            var normalizedPath = CanonicalId.NormalizePath(node.FilePath);
            if (changedPathSet.Contains(normalizedPath))
            {
                changedFileNodeIds.Add(kvp.Key);
            }
            else
            {
                unchangedFileNodes[kvp.Key] = node;
            }
        }

        // Filter edges: keep only those with at least one endpoint in a changed file
        var edgesToKeep = data.Hyperedges
            .Where(e => e.NodeIds.Any(id => changedFileNodeIds.Contains(id)))
            .ToList();

        // Find unchanged-file node IDs that are still referenced by retained edges
        var referencedUnchangedNodeIds = new HashSet<string>();
        foreach (var edge in edgesToKeep)
        {
            foreach (var nid in edge.NodeIds)
            {
                if (unchangedFileNodes.ContainsKey(nid))
                {
                    referencedUnchangedNodeIds.Add(nid);
                }
            }
        }

        // Remove unchanged-file nodes that are NOT referenced by any retained edge
        foreach (var kvp in unchangedFileNodes)
        {
            if (!referencedUnchangedNodeIds.Contains(kvp.Key))
            {
                data.Nodes.Remove(kvp.Key);
            }
        }

        data.Hyperedges.Clear();
        data.Hyperedges.AddRange(edgesToKeep);

        // Now normalize the filtered data (canonical IDs, dedup within changed set, metadata)
        return this.Normalize(data, repoRoot);
    }

    /// <summary>
    /// Normalizes extractor-emitted NodeType (lowercase → PascalCase) and Language values.
    /// Returns the normalized values without modifying the node (since NodeType/Language are init-only).
    /// </summary>
    internal static string NormalizeNodeType(string nodeType)
    {
        return NodeTypeMap.TryGetValue(nodeType, out var normalized) ? normalized : nodeType;
    }

    internal static string NormalizeLanguage(string language)
    {
        return LanguageMap.TryGetValue(language, out var normalized) ? normalized : language;
    }

    private string ComputeCanonicalId(HypergraphNode node, string repoRoot)
    {
        var qualifiedName = this.GetQualifiedName(node);
        var relativePath = this.GetRelativePath(node.FilePath, repoRoot);

        if (node.NodeType is "File" or "Module")
        {
            return CanonicalId.ForFile(relativePath);
        }

        if (node.NodeType is "Repository" or "Repo")
        {
            return CanonicalId.RepoRoot;
        }

        if (string.IsNullOrEmpty(qualifiedName))
        {
            return CanonicalId.ForSymbol(relativePath, node.Name);
        }

        return CanonicalId.ForSymbol(relativePath, qualifiedName);
    }

    private string GetQualifiedName(HypergraphNode node)
    {
        if (node.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn) && qn is string name)
        {
            return name;
        }

        if (!string.IsNullOrEmpty(node.Scope) && node.NodeType is not "File" and not "Module" and not "Repo")
        {
            var scope = this.GetNormalizedScope(node);
            if (!string.IsNullOrEmpty(scope))
            {
                return $"{scope}.{node.Name}";
            }
        }

        return node.Name;
    }

    private string GetNormalizedScope(HypergraphNode node)
    {
        var scope = node.Scope;
        if (string.IsNullOrEmpty(scope))
        {
            return scope;
        }

        // The C# extractor scope stack often includes the file stem and may double-push
        // the namespace (e.g., "MyApp.Services.MyApp.Services.PaymentService" for a class
        // named PaymentService in namespace MyApp.Services). We need to extract just the
        // actual namespace portion.
        var parts = scope.Split('.');
        var fileStem = !string.IsNullOrEmpty(node.FilePath)
            ? Path.GetFileNameWithoutExtension(node.FilePath)
            : null;

        // Remove parts that match the node's own name or the file stem
        var cleanedParts = new List<string>();
        foreach (var part in parts)
        {
            if (fileStem != null && string.Equals(part, fileStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(part, node.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cleanedParts.Add(part);
        }

        if (cleanedParts.Count == 0)
        {
            return string.Empty;
        }

        // Deduplicate repeated namespace segments
        // e.g., ["MyApp", "Services", "MyApp", "Services"] → ["MyApp", "Services"]
        var deduped = DeduplicateRepeatingSegments(cleanedParts);
        return string.Join(".", deduped);
    }

    /// <summary>
    /// Detects and removes repeating namespace segment patterns.
    /// Handles both exact repetition (e.g., [A, B, A, B] → [A, B]) and
    /// prefix repetition (e.g., [A, B, C, A, B] → [A, B, C]).
    /// </summary>
    private static List<string> DeduplicateRepeatingSegments(List<string> parts)
    {
        // First: exact repeating pattern where total length is a multiple of pattern length
        for (var len = 1; len <= parts.Count / 2; len++)
        {
            if (parts.Count % len != 0)
            {
                continue;
            }

            var isRepeating = true;
            for (var i = len; i < parts.Count; i++)
            {
                if (!string.Equals(parts[i], parts[i % len], StringComparison.OrdinalIgnoreCase))
                {
                    isRepeating = false;
                    break;
                }
            }

            if (isRepeating)
            {
                return parts.Take(len).ToList();
            }
        }

        // Second: prefix repetition where the second half starts repeating the first half
        // e.g., [A, B, C, A, B] or [A, B, C, D, A, B, C]
        for (var prefixLen = 1; prefixLen <= parts.Count / 2; prefixLen++)
        {
            var suffixStart = prefixLen;
            var suffixLen = parts.Count - prefixLen;
            if (suffixLen < prefixLen)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < suffixLen && i < prefixLen; i++)
            {
                if (!string.Equals(parts[suffixStart + i], parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return parts.Take(prefixLen).ToList();
            }
        }

        return parts;
    }

    private string GetRelativePath(string filePath, string repoRoot)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(filePath))
        {
            try
            {
                return Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
            }
            catch
            {
                return filePath.Replace('\\', '/');
            }
        }

        return filePath.Replace('\\', '/');
    }

    /// <summary>
    /// Picks the winner when merging two nodes with the same canonical ID or QualifiedName.
    /// Prefers definitions over references, richer metadata, and C# over PowerShell stubs.
    /// </summary>
    private HypergraphNode PickWinner(HypergraphNode a, HypergraphNode b)
    {
        var aIsDef = IsDefinitionType(a.NodeType);
        var bIsDef = IsDefinitionType(b.NodeType);
        if (aIsDef && !bIsDef)
        {
            return a;
        }

        if (bIsDef && !aIsDef)
        {
            return b;
        }

        // Prefer C# (typically richer) over PowerShell stubs
        var aIsCSharp = NormalizeLanguage(a.Language) == "C#";
        var bIsCSharp = NormalizeLanguage(b.Language) == "C#";
        if (aIsCSharp && !bIsCSharp)
        {
            return a;
        }

        if (bIsCSharp && !aIsCSharp)
        {
            return b;
        }

        // Prefer the one with more metadata
        if (a.Metadata.Count != b.Metadata.Count)
        {
            return a.Metadata.Count > b.Metadata.Count ? a : b;
        }

        return a;
    }

    private HypergraphNode CloneWithId(HypergraphNode node, string newId, string repoRoot)
    {
        var relativePath = this.GetRelativePath(node.FilePath, repoRoot);
        var qualifiedName = this.GetQualifiedName(node);
        var clone = new HypergraphNode
        {
            Id = newId,
            Name = node.Name,
            NodeType = NormalizeNodeType(node.NodeType),
            Language = NormalizeLanguage(node.Language),
            FilePath = node.FilePath,
            Scope = node.Scope,
            LineStart = node.LineStart,
            LineEnd = node.LineEnd,
            Modifiers = new List<string>(node.Modifiers),
            Metadata = new Dictionary<string, object?>(node.Metadata),
        };

        if (!string.IsNullOrEmpty(qualifiedName))
        {
            clone.Metadata[MemoryMetadataKeys.QualifiedName] = qualifiedName;
        }

        if (!clone.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
        {
            clone.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
        }

        if (!clone.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash))
        {
            var hashInput = $"{node.NodeType}:{qualifiedName}:{relativePath}:{node.LineStart}";
            clone.Metadata[MemoryMetadataKeys.ContentHash] = ComputeHash(hashInput);
        }

        return clone;
    }

    /// <summary>
    /// Maps HyperedgeType to canonical SemanticRelation per architecture §2.2 and §2.3.
    /// Uses canonical names only — retired names (IMPORTS, DEFINES) are not emitted.
    /// </summary>
    internal static string InferSemanticRelation(HyperedgeType edgeType) => edgeType switch
    {
        HyperedgeType.MethodInvocation => "CALLS",
        HyperedgeType.ConstructorInvocation => "CALLS",
        HyperedgeType.CmdletCall => "CALLS",
        HyperedgeType.ModuleFunctionCall => "CALLS",
        HyperedgeType.CrossLanguageMethodCall => "CALLS",
        HyperedgeType.Implements => "IMPLEMENTS",
        HyperedgeType.Extends => "EXTENDS",
        HyperedgeType.UsingDirective => "DEPENDS_ON",
        HyperedgeType.ModuleImport => "DEPENDS_ON",
        HyperedgeType.DotSource => "DEPENDS_ON",
        HyperedgeType.ClassDefinition => "CONTAINS",
        HyperedgeType.InterfaceDefinition => "CONTAINS",
        HyperedgeType.StructDefinition => "CONTAINS",
        HyperedgeType.RecordDefinition => "CONTAINS",
        HyperedgeType.MethodDefinition => "CONTAINS",
        HyperedgeType.ConstructorDefinition => "CONTAINS",
        HyperedgeType.FunctionDefinition => "CONTAINS",
        HyperedgeType.PropertyDefinition => "CONTAINS",
        HyperedgeType.DataFlow => "REFERENCES",
        HyperedgeType.Assignment => "REFERENCES",
        HyperedgeType.CrossLanguageTypeReference => "REFERENCES",
        HyperedgeType.MemoryRelation => "SIMILAR_TO",
        _ => "RELATED_TO",
    };

    /// <summary>
    /// Infers the memory layer for an edge based on its SemanticRelation per architecture §2.1.
    /// </summary>
    internal static string InferMemoryLayer(HyperedgeType edgeType)
    {
        var relation = InferSemanticRelation(edgeType);
        return relation switch
        {
            "CONTAINS" or "DEPENDS_ON" => "structural",
            "CALLS" or "EXTENDS" or "IMPLEMENTS" or "REFERENCES" => "call_chain",
            "SIMILAR_TO" or "BELONGS_TO_CLUSTER" => "semantic",
            "APPLIES_TO" or "SUPERSEDES" or "DERIVED_FROM" => "expertise",
            _ => "structural",
        };
    }

    private static bool IsDefinitionType(string nodeType)
    {
        return string.Equals(nodeType, "Class", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Interface", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Struct", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Record", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Method", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Function", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Property", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Constructor", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}