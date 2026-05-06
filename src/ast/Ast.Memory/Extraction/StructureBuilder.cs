// -----------------------------------------------------------------------
// <copyright file="StructureBuilder.cs" company="Microsoft Corp.">
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
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Concrete implementation of <see cref="IStructureBuilder"/>.
/// Synthesizes package/module hierarchy nodes (project → namespace → class)
/// and assigns structural metadata.
/// </summary>
public class StructureBuilder : IStructureBuilder
{
    private readonly ILogger<StructureBuilder> logger;

    public StructureBuilder(ILogger<StructureBuilder> logger)
    {
        this.logger = logger;
    }

    public Task<StageResult> BuildAsync(
        HypergraphData data,
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        this.logger.LogInformation("StructureBuilder: starting structural extraction for {RepoRoot}", repoRoot);

        var nodesAdded = 0;
        var edgesAdded = 0;
        var errors = new List<string>();

        try
        {
            // Synthesize repo root node
            var repoRootNode = new HypergraphNode
            {
                Id = CanonicalId.RepoRoot,
                Name = Path.GetFileName(repoRoot) ?? "repo",
                NodeType = "Repo",
                Language = "None",
                FilePath = string.Empty,
            };
            repoRootNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
            if (!data.Nodes.ContainsKey(repoRootNode.Id))
            {
                data.Nodes[repoRootNode.Id] = repoRootNode;
                nodesAdded++;
            }

            // Discover .csproj files for package hierarchy.
            // Only scan directories that contain files already in the data set.
            // This prevents scanning the entire repo during incremental extraction.
            var projectFiles = new List<string>();
            try
            {
                if (Directory.Exists(repoRoot))
                {
                    // Determine relevant directories from existing file nodes
                    var relevantDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var node in data.Nodes.Values)
                    {
                        if (!string.IsNullOrEmpty(node.FilePath))
                        {
                            var absPath = Path.IsPathRooted(node.FilePath)
                                ? node.FilePath
                                : Path.Combine(repoRoot, node.FilePath.Replace('/', Path.DirectorySeparatorChar));
                            var dir = Path.GetDirectoryName(absPath);
                            while (!string.IsNullOrEmpty(dir)
                                && dir.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                relevantDirs.Add(dir);
                                dir = Path.GetDirectoryName(dir);
                            }
                        }
                    }

                    if (relevantDirs.Count == 0)
                    {
                        // No file nodes yet — scan everything (full extraction)
                        projectFiles = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
                            .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
                            .ToList();
                    }
                    else
                    {
                        // Only scan directories relevant to the current data set
                        projectFiles = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
                            .Where(p =>
                            {
                                var dir = Path.GetDirectoryName(p);
                                return dir != null && relevantDirs.Contains(dir);
                            })
                            .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Could not enumerate project files");
                errors.Add($"Project enumeration failed: {ex.Message}");
            }

            // Create package nodes for each project
            foreach (var projFile in projectFiles)
            {
                ct.ThrowIfCancellationRequested();
                var projName = Path.GetFileNameWithoutExtension(projFile);

                // Skip test projects unless requested
                if (!options.IncludeTestProjects && IsTestProject(projName))
                {
                    continue;
                }

                var packageId = CanonicalId.ForPackage(projFile);
                if (!data.Nodes.ContainsKey(packageId))
                {
                    var packageNode = new HypergraphNode
                    {
                        Id = packageId,
                        Name = projName,
                        NodeType = "Package",
                        Language = "C#",
                        FilePath = projFile,
                    };
                    packageNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
                    packageNode.Metadata[MemoryMetadataKeys.PackageName] = projName;
                    data.Nodes[packageId] = packageNode;
                    nodesAdded++;

                    // Package → repo root containment edge
                    data.Hyperedges.Add(new Hyperedge
                    {
                        Id = $"contains:{CanonicalId.RepoRoot}→{packageId}",
                        EdgeType = HyperedgeType.DataFlow,
                        NodeIds = new List<string> { CanonicalId.RepoRoot, packageId },
                        Roles = new Dictionary<string, string>
                        {
                            { CanonicalId.RepoRoot, "parent" },
                            { packageId, "child" },
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            { MemoryMetadataKeys.SemanticRelation, "CONTAINS" },
                            { MemoryMetadataKeys.MemoryLayer, "structural" },
                            { MemoryMetadataKeys.OriginalEdgeType, HyperedgeType.DataFlow.ToString() },
                        },
                    });
                    edgesAdded++;
                }
            }

            // Process existing nodes: assign structural metadata and hierarchy edges
            foreach (var kvp in data.Nodes.ToList())
            {
                ct.ThrowIfCancellationRequested();
                var node = kvp.Value;

                // Assign content hash if not already set
                if (!node.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash))
                {
                    var hashInput = $"{node.NodeType}:{node.Name}:{node.FilePath}:{node.LineStart}";
                    node.Metadata[MemoryMetadataKeys.ContentHash] = ComputeHash(hashInput);
                }

                // Assign memory layer based on node type
                if (!node.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
                {
                    node.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
                }

                // Synthesize namespace nodes for classes (case-insensitive)
                if (IsTypeNode(node.NodeType)
                    && !string.IsNullOrEmpty(node.Scope))
                {
                    var nsId = CanonicalId.ForSymbol(node.FilePath, node.Scope);
                    if (!data.Nodes.ContainsKey(nsId))
                    {
                        var nsNode = new HypergraphNode
                        {
                            Id = nsId,
                            Name = node.Scope,
                            NodeType = "Namespace",
                            Language = node.Language,
                            FilePath = node.FilePath,
                        };
                        nsNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
                        nsNode.Metadata[MemoryMetadataKeys.NamespaceName] = node.Scope;
                        data.Nodes[nsId] = nsNode;
                        nodesAdded++;
                    }

                    // Namespace → class containment
                    data.Hyperedges.Add(new Hyperedge
                    {
                        Id = $"contains:{nsId}→{node.Id}",
                        EdgeType = HyperedgeType.DataFlow,
                        NodeIds = new List<string> { nsId, node.Id },
                        Roles = new Dictionary<string, string>
                        {
                            { nsId, "parent" },
                            { node.Id, "child" },
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            { MemoryMetadataKeys.SemanticRelation, "CONTAINS" },
                            { MemoryMetadataKeys.MemoryLayer, "structural" },
                            { MemoryMetadataKeys.OriginalEdgeType, HyperedgeType.DataFlow.ToString() },
                        },
                    });
                    edgesAdded++;
                }

                // File → symbol containment (case-insensitive NodeType matching)
                if (!string.IsNullOrEmpty(node.FilePath) && !IsStructuralOnlyNode(node.NodeType))
                {
                    var fileId = CanonicalId.ForFile(node.FilePath);
                    if (data.Nodes.ContainsKey(fileId))
                    {
                        data.Hyperedges.Add(new Hyperedge
                        {
                            Id = $"contains:{fileId}→{node.Id}",
                            EdgeType = HyperedgeType.DataFlow,
                            NodeIds = new List<string> { fileId, node.Id },
                            Roles = new Dictionary<string, string>
                            {
                                { fileId, "parent" },
                                { node.Id, "child" },
                            },
                            Metadata = new Dictionary<string, object?>
                            {
                                { MemoryMetadataKeys.SemanticRelation, "CONTAINS" },
                                { MemoryMetadataKeys.MemoryLayer, "structural" },
                                { MemoryMetadataKeys.OriginalEdgeType, HyperedgeType.DataFlow.ToString() },
                            },
                        });
                        edgesAdded++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "StructureBuilder stage failed");
            errors.Add($"StructureBuilder failed: {ex.Message}");
        }

        this.logger.LogInformation(
            "StructureBuilder: completed. Nodes added: {NodesAdded}, Edges added: {EdgesAdded}",
            nodesAdded, edgesAdded);

        return Task.FromResult(new StageResult
        {
            NodesCreated = nodesAdded,
            EdgesCreated = edgesAdded,
            Errors = errors,
        });
    }

    private static bool IsTestProject(string name)
    {
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
            || name.Contains("UnitTest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("IntegrationTest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTypeNode(string nodeType)
    {
        return string.Equals(nodeType, "Class", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Interface", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Struct", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Record", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructuralOnlyNode(string nodeType)
    {
        return string.Equals(nodeType, "File", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Module", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Repository", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Package", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeType, "Namespace", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}