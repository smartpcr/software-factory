// -----------------------------------------------------------------------
// <copyright file="RoslynExtractionPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Csharp;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.PowerShell;
using Microsoft.Extensions.Logging;

/// <summary>
/// Extraction pipeline that uses the Ast.Csharp Roslyn-based parser for C#
/// files and <see cref="PowerShellHyperedgeExtractor"/> for PowerShell files.
/// Produces <see cref="HypergraphNode"/> and <see cref="Hyperedge"/> objects
/// with memory-layer metadata (<see cref="MemoryMetadataKeys"/>) so that
/// <see cref="IncrementalSync.SyncFilesAsync"/> can diff them against the
/// existing graph baseline.
/// </summary>
public sealed class RoslynExtractionPipeline : IExtractionPipeline
{
    private static readonly HashSet<string> CSharpExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs" };

    private static readonly HashSet<string> PowerShellExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".psm1", ".psd1" };

    private static readonly HashSet<string> AllSupportedExtensions =
        new(CSharpExtensions.Concat(PowerShellExtensions), StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum retries when reading a file that may be locked by another process.</summary>
    private const int MaxFileReadRetries = 4;

    private static readonly int[] RetryDelaysMs = [100, 250, 500, 1000];

    private readonly ILogger<RoslynExtractionPipeline> logger;

    public RoslynExtractionPipeline(ILogger<RoslynExtractionPipeline> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        var files = new List<string>();
        foreach (var ext in AllSupportedExtensions)
        {
            files.AddRange(
                Directory.GetFiles(repoRoot, $"*{ext}", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(repoRoot, f).Replace('\\', '/')));
        }

        return this.ExtractFilesAsync(repoRoot, files, options, ct);
    }

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        var allNodes = new List<HypergraphNode>();
        var allEdges = new List<Hyperedge>();
        var errors = new List<string>();
        var skippedPaths = new List<string>();
        var filesSkipped = 0;

        foreach (var relativePath in relativePaths)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(relativePath);
            if (!AllSupportedExtensions.Contains(ext))
            {
                filesSkipped++;
                skippedPaths.Add(relativePath);
                this.logger.LogDebug(
                    "Skipping {FilePath}: extension {Ext} not supported",
                    relativePath, ext);
                continue;
            }

            var fullPath = Path.Combine(
                repoRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                errors.Add($"File not found: {relativePath}");
                continue;
            }

            try
            {
                var sourceCode = await ReadFileWithRetryAsync(fullPath, ct).ConfigureAwait(false);
                var fileContentHash = ComputeContentHash(sourceCode);

                // Select the correct extractor based on file extension
                Dictionary<string, HypergraphNode> nodes;
                List<Hyperedge> edges;

                if (CSharpExtensions.Contains(ext))
                {
                    var extractor = new CSharpHyperedgeExtractor(relativePath);
                    (nodes, edges) = extractor.Extract(sourceCode);
                }
                else
                {
                    // PowerShell extensions
                    var extractor = new PowerShellHyperedgeExtractor(relativePath);
                    (nodes, edges) = extractor.Extract(sourceCode);
                }

                // Remap node IDs from MD5 line-based to canonical {filePath}::{qualifiedName}
                // per architecture.md §4.2. Build a mapping from old → new IDs so edges can
                // be updated to reference canonical IDs.
                var idRemapTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var canonicalIdCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var remappedNodes = new Dictionary<string, HypergraphNode>(StringComparer.OrdinalIgnoreCase);

                foreach (var node in nodes.Values)
                {
                    var qualifiedName = BuildQualifiedName(node);
                    var canonicalId = $"{relativePath}::{qualifiedName}";

                    // Handle collisions (e.g., overloaded methods) by appending #N
                    if (canonicalIdCounts.TryGetValue(canonicalId, out var count))
                    {
                        canonicalIdCounts[canonicalId] = count + 1;
                        canonicalId = $"{canonicalId}#{count + 1}";
                    }
                    else
                    {
                        canonicalIdCounts[canonicalId] = 1;
                    }

                    idRemapTable[node.Id] = canonicalId;

                    var remapped = new HypergraphNode
                    {
                        Id = canonicalId,
                        Name = node.Name,
                        NodeType = node.NodeType,
                        Language = node.Language,
                        FilePath = node.FilePath,
                        Scope = node.Scope,
                        LineStart = node.LineStart,
                        LineEnd = node.LineEnd,
                        Modifiers = node.Modifiers,
                        Metadata = new Dictionary<string, object?>(node.Metadata)
                    };

                    // Store the qualified name in metadata for stable-key matching
                    remapped.Metadata[MemoryMetadataKeys.QualifiedName] = qualifiedName;

                    remappedNodes[canonicalId] = remapped;
                }

                // Fix collision counts: first occurrence should NOT have a suffix
                // but if there are collisions, rename it to #1
                foreach (var kvp in canonicalIdCounts.Where(c => c.Value > 1))
                {
                    var baseId = kvp.Key;
                    if (remappedNodes.ContainsKey(baseId))
                    {
                        var newId = $"{baseId}#1";
                        var oldNode = remappedNodes[baseId];
                        remappedNodes.Remove(baseId);
                        var renamed = new HypergraphNode
                        {
                            Id = newId,
                            Name = oldNode.Name,
                            NodeType = oldNode.NodeType,
                            Language = oldNode.Language,
                            FilePath = oldNode.FilePath,
                            Scope = oldNode.Scope,
                            LineStart = oldNode.LineStart,
                            LineEnd = oldNode.LineEnd,
                            Modifiers = oldNode.Modifiers,
                            Metadata = new Dictionary<string, object?>(oldNode.Metadata)
                        };
                        remappedNodes[newId] = renamed;
                        // Update remap table entries pointing to the base ID
                        foreach (var rk in idRemapTable.Where(r => r.Value == baseId).Select(r => r.Key).ToList())
                        {
                            idRemapTable[rk] = newId;
                        }
                    }
                }

                // Enrich remapped nodes with memory-layer metadata
                var sourceLines = sourceCode.Split('\n');
                foreach (var node in remappedNodes.Values)
                {
                    node.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
                    node.Metadata[MemoryMetadataKeys.FileContentHash] = fileContentHash;
                    // ContentHash = SHA-256 of the node's source text region (architecture: SHA-256 of source text)
                    node.Metadata[MemoryMetadataKeys.ContentHash] = ComputeNodeSourceHash(node, sourceLines);
                    // StructuralHash = identity-only hash for stable-key matching
                    node.Metadata[MemoryMetadataKeys.StructuralHash] = ComputeNodeStructuralHash(node);
                }

                allNodes.AddRange(remappedNodes.Values);

                // Make edge IDs globally unique by prefixing with a file hash
                // and stamp SourceFile ownership metadata for edge retirement.
                // Also remap edge NodeIds to canonical node IDs.
                var filePrefix = ComputeContentHash(relativePath)[..8];
                foreach (var edge in edges)
                {
                    var semanticRelation = MapEdgeTypeToSemanticRelation(edge.EdgeType);
                    var memoryLayer = MapEdgeTypeToMemoryLayer(edge.EdgeType);

                    // Remap edge endpoints to canonical node IDs
                    var remappedNodeIds = edge.NodeIds
                        .Select(nid => idRemapTable.TryGetValue(nid, out var cid) ? cid : nid)
                        .ToList();

                    // Remap roles dictionary keys to canonical IDs
                    var remappedRoles = new Dictionary<string, string>();
                    foreach (var role in edge.Roles)
                    {
                        var newKey = idRemapTable.TryGetValue(role.Key, out var cid) ? cid : role.Key;
                        remappedRoles[newKey] = role.Value;
                    }

                    var metadata = new Dictionary<string, object?>(edge.Metadata)
                    {
                        [MemoryMetadataKeys.SourceFile] = relativePath,
                        [MemoryMetadataKeys.SemanticRelation] = semanticRelation,
                        [MemoryMetadataKeys.MemoryLayer] = memoryLayer
                    };

                    // Build a stable canonical edge ID from semantic relation + endpoints
                    // Uses SHA-256 truncation instead of string.GetHashCode() which is
                    // process-randomized in .NET 6+ and produces unstable IDs across restarts.
                    var endpointKey = string.Join("|", remappedNodeIds);
                    var edgeIdSource = $"{semanticRelation}:{endpointKey}";
                    var edgeIdHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(edgeIdSource))).ToLowerInvariant()[..16];
                    var canonicalEdgeId = $"{filePrefix}_{semanticRelation}_{edgeIdHash}";

                    var stableEdge = new Hyperedge
                    {
                        Id = canonicalEdgeId,
                        EdgeType = edge.EdgeType,
                        NodeIds = remappedNodeIds,
                        Roles = remappedRoles,
                        Languages = edge.Languages,
                        Weight = edge.Weight,
                        Metadata = metadata
                    };
                    allEdges.Add(stableEdge);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Extraction error in {relativePath}: {ex.Message}");
                this.logger.LogWarning(ex, "Extraction failed for {FilePath}", relativePath);
            }
        }

        var result = new ExtractionResult(
            NodesCreated: allNodes.Count,
            EdgesCreated: allEdges.Count,
            EmbeddingsGenerated: 0,
            FilesSkipped: filesSkipped,
            Errors: errors)
        {
            ExtractedNodes = allNodes,
            ExtractedEdges = allEdges,
            SkippedPaths = skippedPaths
        };

        return result;
    }

    /// <summary>
    /// Reads a file with retry logic for handling transient file locks
    /// from editors/IDEs saving files. Uses exponential backoff.
    /// </summary>
    internal static async Task<string> ReadFileWithRetryAsync(string fullPath, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt <= MaxFileReadRetries; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaxFileReadRetries)
            {
                await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
            }
        }

        // Should not reach here, but satisfy the compiler
        throw new IOException($"Failed to read {fullPath} after {MaxFileReadRetries + 1} attempts");
    }

    /// <summary>
    /// Computes a content hash for a node based on its actual source text region.
    /// Per architecture.md, ContentHash is the SHA-256 of source text — this ensures
    /// any code change (body-only, logic-only, whitespace-only) produces a different
    /// hash so that node upsert and expertise decay are triggered correctly.
    /// Falls back to a comprehensive property hash when line range is unavailable.
    /// </summary>
    private static string ComputeNodeSourceHash(HypergraphNode node, string[] sourceLines)
    {
        // Extract the source text for this node's line range (1-based)
        if (node.LineStart > 0 && node.LineEnd >= node.LineStart && node.LineStart <= sourceLines.Length)
        {
            var startIdx = node.LineStart - 1;
            var endIdx = Math.Min(node.LineEnd, sourceLines.Length);
            var regionText = string.Join('\n', sourceLines[startIdx..endIdx]);
            return ComputeContentHash(regionText);
        }

        // Fallback: hash all significant node properties including name, type, scope, modifiers
        var sb = new StringBuilder();
        sb.Append(node.NodeType ?? "");
        sb.Append(':');
        sb.Append(node.Name ?? "");
        sb.Append(':');
        sb.Append(node.Scope ?? "");
        sb.Append(':');
        sb.Append(node.LineStart);
        sb.Append(':');
        sb.Append(node.LineEnd);
        if (node.Modifiers.Count > 0)
        {
            sb.Append(':');
            sb.AppendJoin(',', node.Modifiers.OrderBy(m => m, StringComparer.Ordinal));
        }

        return ComputeContentHash(sb.ToString());
    }

    /// <summary>
    /// Computes a structural hash for a node based on its identity and structure
    /// (name, type, modifiers, scope). Line numbers are intentionally excluded
    /// so that whitespace-only or comment-only edits (which shift line positions
    /// but leave the code structure unchanged) produce the same hash — enabling
    /// the MinorDecayFactor path in IncrementalSync.
    /// </summary>
    private static string ComputeNodeStructuralHash(HypergraphNode node)
    {
        var sb = new StringBuilder();
        sb.Append(node.NodeType ?? "");
        sb.Append(':');
        sb.Append(node.Name ?? "");
        sb.Append(':');
        sb.Append(node.Scope ?? "");
        sb.Append(':');
        if (node.Modifiers.Count > 0)
        {
            sb.AppendJoin(',', node.Modifiers.OrderBy(m => m, StringComparer.Ordinal));
        }

        if (node.Metadata.TryGetValue(MemoryMetadataKeys.MemberCount, out var mc) && mc != null)
        {
            sb.Append(":mc=");
            sb.Append(mc);
        }

        if (node.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn) && qn != null)
        {
            sb.Append(":qn=");
            sb.Append(qn);
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string ComputeContentHash(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Builds a qualified name for a node from its Scope and Name properties.
    /// Used to construct canonical node IDs in the format {filePath}::{qualifiedName}.
    /// Example: "MyNamespace.MyClass.MyMethod" for a method node.
    /// </summary>
    private static string BuildQualifiedName(HypergraphNode node)
    {
        var scope = node.Scope ?? string.Empty;
        var name = node.Name ?? string.Empty;

        if (string.IsNullOrEmpty(scope))
        {
            return string.IsNullOrEmpty(name) ? node.NodeType : name;
        }

        return string.IsNullOrEmpty(name) ? scope : $"{scope}.{name}";
    }

    /// <summary>
    /// Maps an <see cref="HyperedgeType"/> to its canonical SemanticRelation name
    /// as defined in architecture.md §2.2. Every edge must carry this metadata
    /// so retrieval can filter by <c>Metadata["SemanticRelation"]</c>.
    /// </summary>
    private static string MapEdgeTypeToSemanticRelation(HyperedgeType edgeType)
    {
        return edgeType switch
        {
            // Structural layer: CONTAINS
            HyperedgeType.ClassDefinition or
            HyperedgeType.InterfaceDefinition or
            HyperedgeType.StructDefinition or
            HyperedgeType.RecordDefinition or
            HyperedgeType.MethodDefinition or
            HyperedgeType.ConstructorDefinition or
            HyperedgeType.PropertyDefinition or
            HyperedgeType.FunctionDefinition => "CONTAINS",

            // Structural layer: DEPENDS_ON
            HyperedgeType.UsingDirective or
            HyperedgeType.ModuleImport or
            HyperedgeType.DotSource => "DEPENDS_ON",

            // Call-chain layer: CALLS
            HyperedgeType.MethodInvocation or
            HyperedgeType.ConstructorInvocation or
            HyperedgeType.CmdletCall or
            HyperedgeType.ModuleFunctionCall or
            HyperedgeType.CrossLanguageMethodCall => "CALLS",

            // Call-chain layer: EXTENDS / IMPLEMENTS
            HyperedgeType.Extends => "EXTENDS",
            HyperedgeType.Implements => "IMPLEMENTS",

            // Call-chain layer: REFERENCES
            HyperedgeType.DataFlow or
            HyperedgeType.Assignment or
            HyperedgeType.CrossLanguageTypeReference => "REFERENCES",

            // Default for other edge types
            _ => "REFERENCES"
        };
    }

    /// <summary>
    /// Maps an <see cref="HyperedgeType"/> to its MemoryLayer as defined
    /// in architecture.md §2.1.
    /// </summary>
    private static string MapEdgeTypeToMemoryLayer(HyperedgeType edgeType)
    {
        return edgeType switch
        {
            // Structural layer
            HyperedgeType.ClassDefinition or
            HyperedgeType.InterfaceDefinition or
            HyperedgeType.StructDefinition or
            HyperedgeType.RecordDefinition or
            HyperedgeType.MethodDefinition or
            HyperedgeType.ConstructorDefinition or
            HyperedgeType.PropertyDefinition or
            HyperedgeType.FunctionDefinition or
            HyperedgeType.UsingDirective or
            HyperedgeType.ModuleImport or
            HyperedgeType.DotSource => "structural",

            // Call-chain layer (all invocation/relationship types)
            _ => "call_chain"
        };
    }
}
