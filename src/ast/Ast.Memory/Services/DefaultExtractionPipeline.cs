// -----------------------------------------------------------------------
// <copyright file="DefaultExtractionPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Services;

using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Csharp;
using Ast.PowerShell;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Default extraction pipeline that walks source files and populates the memory graph
/// using the real <see cref="CSharpHyperedgeExtractor"/> and <see cref="PowerShellHyperedgeExtractor"/>
/// from Ast.Csharp / Ast.PowerShell to build a structural and call-chain hypergraph.
/// </summary>
public sealed class DefaultExtractionPipeline : IExtractionPipeline
{
    private static readonly HashSet<string> CSharpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs" };
    private static readonly HashSet<string> PowerShellExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".psm1", ".psd1" };

    private readonly IMemoryGraphStore store;

    public DefaultExtractionPipeline(IMemoryGraphStore store)
    {
        this.store = store;
    }

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var scopeRoot = options.ScopeSubtree != null
            ? Path.Combine(repoRoot, options.ScopeSubtree)
            : repoRoot;

        if (!Directory.Exists(scopeRoot))
        {
            return new ExtractionResult(0, 0, 0, 0, new[] { $"Scope path does not exist: {scopeRoot}" });
        }

        var extensions = GetExtensionsForLanguages(options.Languages);
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(scopeRoot, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                })
                .Where(f => extensions.Contains(Path.GetExtension(f)))
                .ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ExtractionResult(0, 0, 0, 0, new[] { $"Access denied enumerating {scopeRoot}: {ex.Message}" });
        }
        catch (IOException ex)
        {
            return new ExtractionResult(0, 0, 0, 0, new[] { $"I/O error enumerating {scopeRoot}: {ex.Message}" });
        }

        var nodesCreated = 0;
        var edgesCreated = 0;
        var filesSkipped = 0;
        var commitSha = options.CommitSha ?? "HEAD";
        var commitIndex = options.CommitIndex ?? 0;
        var version = new VersionRange(commitSha, commitIndex);

        // Create repository root node (canonical ID: "/" per architecture.md §4.2 A-5)
        var repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var repoNode = new HypergraphNode
        {
            Id = "/",
            Name = repoName,
            NodeType = "Repository",
            Language = "mixed",
            FilePath = string.Empty,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
            },
        };
        await this.store.UpsertNodeAsync(repoNode, version, ct);
        nodesCreated++;

        // Track directories to create containment hierarchy
        var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var relativePath = Path.GetRelativePath(repoRoot, file);
                var content = await File.ReadAllTextAsync(file, ct);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
                var language = InferLanguage(file);

                // Create directory nodes for the path hierarchy
                var dirPath = Path.GetDirectoryName(relativePath);
                if (!string.IsNullOrEmpty(dirPath))
                {
                    var parts = dirPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var accumulated = string.Empty;
                    var parentId = repoNode.Id;

                    for (int i = 0; i < parts.Length; i++)
                    {
                        accumulated = i == 0 ? parts[i] : Path.Combine(accumulated, parts[i]);
                        var dirId = $"/{accumulated.Replace('\\', '/')}";

                        if (knownDirs.Add(dirId))
                        {
                            var dirNode = new HypergraphNode
                            {
                                Id = dirId,
                                Name = parts[i],
                                NodeType = "Module",
                                Language = "mixed",
                                FilePath = accumulated,
                                Metadata = new Dictionary<string, object?>
                                {
                                    [MemoryMetadataKeys.MemoryLayer] = "structural",
                                },
                            };
                            await this.store.UpsertNodeAsync(dirNode, version, ct);
                            nodesCreated++;

                            // CONTAINS is a code-derived edge (architecture.md §2.2);
                            // use ClassDefinition (structural definition type), NOT MemoryRelation.
                            var containsEdge = new Hyperedge
                            {
                                Id = $"edge:contains:{parentId}→{dirId}",
                                EdgeType = HyperedgeType.ClassDefinition,
                                NodeIds = new List<string> { parentId, dirId },
                                Roles = new Dictionary<string, string>
                                {
                                    [parentId] = "parent",
                                    [dirId] = "child",
                                },
                                Metadata = new Dictionary<string, object?>
                                {
                                    [MemoryMetadataKeys.MemoryLayer] = "structural",
                                    [MemoryMetadataKeys.SemanticRelation] = "CONTAINS",
                                },
                            };
                            await this.store.CreateEdgeAsync(containsEdge, ct);
                            edgesCreated++;
                        }

                        parentId = dirId;
                    }
                }

                // Create file node (canonical ID: "/{relativePath}" per A-5)
                var normalizedRelPath = relativePath.Replace('\\', '/');
                var fileNodeId = $"/{normalizedRelPath}";
                var fileNode = new HypergraphNode
                {
                    Id = fileNodeId,
                    Name = Path.GetFileName(file),
                    NodeType = "File",
                    Language = language,
                    FilePath = relativePath,
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "structural",
                        [MemoryMetadataKeys.ContentHash] = hash,
                    },
                };
                await this.store.UpsertNodeAsync(fileNode, version, ct);
                nodesCreated++;

                // Edge: directory → file (code-derived CONTAINS, not MemoryRelation)
                var fileDirId = !string.IsNullOrEmpty(dirPath)
                    ? $"/{dirPath.Replace('\\', '/')}"
                    : repoNode.Id;
                var fileEdge = new Hyperedge
                {
                    Id = $"edge:contains:{fileDirId}→{fileNodeId}",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = new List<string> { fileDirId, fileNodeId },
                    Roles = new Dictionary<string, string>
                    {
                        [fileDirId] = "parent",
                        [fileNodeId] = "child",
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "structural",
                        [MemoryMetadataKeys.SemanticRelation] = "CONTAINS",
                    },
                };
                await this.store.CreateEdgeAsync(fileEdge, ct);
                edgesCreated++;

                // Use real AST extractors for code structure
                var (nc, ec) = await this.ExtractWithHyperedgeExtractorAsync(
                    content, relativePath, fileNodeId, language, version, ct);
                nodesCreated += nc;
                edgesCreated += ec;
            }
            catch (IOException ex)
            {
                errors.Add($"{file}: {ex.Message}");
                filesSkipped++;
            }
            catch (UnauthorizedAccessException ex)
            {
                errors.Add($"{file}: {ex.Message}");
                filesSkipped++;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{file}: {ex.Message}");
                filesSkipped++;
            }
        }

        await this.store.LogAuditAsync(
            "cli", "extract",
            new { repoRoot, nodesCreated, edgesCreated, filesSkipped, errorCount = errors.Count }, ct);

        return new ExtractionResult(nodesCreated, edgesCreated, 0, filesSkipped, errors);
    }

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var nodesCreated = 0;
        var edgesCreated = 0;
        var filesSkipped = 0;
        var version = new VersionRange(options.CommitSha ?? "HEAD", options.CommitIndex ?? 0);

        foreach (var rel in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(repoRoot, rel);

            if (!File.Exists(fullPath))
            {
                errors.Add($"File not found: {rel}");
                filesSkipped++;
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
                var language = InferLanguage(fullPath);
                var normalizedRel = rel.Replace('\\', '/');
                var fileNodeId = $"/{normalizedRel}";

                var fileNode = new HypergraphNode
                {
                    Id = fileNodeId,
                    Name = Path.GetFileName(rel),
                    NodeType = "File",
                    Language = language,
                    FilePath = rel,
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "structural",
                        [MemoryMetadataKeys.ContentHash] = hash,
                    },
                };
                await this.store.UpsertNodeAsync(fileNode, version, ct);
                nodesCreated++;

                // Use real AST extractors for code structure
                var (nc, ec) = await this.ExtractWithHyperedgeExtractorAsync(
                    content, rel, fileNodeId, language, version, ct);
                nodesCreated += nc;
                edgesCreated += ec;
            }
            catch (IOException ex)
            {
                errors.Add($"{rel}: {ex.Message}");
                filesSkipped++;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{rel}: {ex.Message}");
                filesSkipped++;
            }
        }

        await this.store.LogAuditAsync(
            "cli", "extract-files",
            new { repoRoot, fileCount = relativePaths.Count, nodesCreated, edgesCreated }, ct);

        return new ExtractionResult(nodesCreated, edgesCreated, 0, filesSkipped, errors);
    }

    /// <summary>
    /// Uses the real <see cref="CSharpHyperedgeExtractor"/> or <see cref="PowerShellHyperedgeExtractor"/>
    /// to parse source code and ingest the resulting nodes and edges into the memory graph store.
    /// </summary>
    private async Task<(int nodesCreated, int edgesCreated)> ExtractWithHyperedgeExtractorAsync(
        string content, string relativePath, string fileNodeId, string language,
        VersionRange version, CancellationToken ct = default)
    {
        var nodesCreated = 0;
        var edgesCreated = 0;

        Dictionary<string, HypergraphNode> extractedNodes;
        List<Hyperedge> extractedEdges;

        try
        {
            if (language == "C#")
            {
                var extractor = new CSharpHyperedgeExtractor(relativePath);
                (extractedNodes, extractedEdges) = extractor.Extract(content);
            }
            else if (language == "PowerShell")
            {
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                (extractedNodes, extractedEdges) = extractor.Extract(content);
            }
            else
            {
                return (0, 0);
            }
        }
        catch (Exception ex)
        {
            // Surface parse errors so callers can report skipped files
            throw new InvalidOperationException(
                $"AST extraction failed for {relativePath}: {ex.Message}", ex);
        }

        // Build a mapping from extractor hash IDs → canonical IDs per architecture.md §4.2
        // Canonical format: "/{relativePath}::{qualifiedName}" (A-5)
        var normalizedPath = relativePath.Replace('\\', '/');
        var idMapping = new Dictionary<string, string>();

        foreach (var (hashId, node) in extractedNodes)
        {
            // Derive qualifiedName from Scope + Name (extractor sets both)
            var scope = node.Scope ?? string.Empty;
            var qualifiedName = string.IsNullOrEmpty(scope)
                ? node.Name
                : $"{scope}.{node.Name}";
            var canonicalId = $"/{normalizedPath}::{qualifiedName}";
            idMapping[hashId] = canonicalId;
        }

        // Ingest extracted nodes with canonical IDs and memory-layer metadata
        foreach (var (hashId, node) in extractedNodes)
        {
            ct.ThrowIfCancellationRequested();

            var canonicalId = idMapping[hashId];

            // Create a new node with canonical ID (Id is init-only)
            var metadata = new Dictionary<string, object?>(node.Metadata)
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.QualifiedName] = canonicalId,
            };
            var canonicalNode = new HypergraphNode
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
                Metadata = metadata,
            };

            await this.store.UpsertNodeAsync(canonicalNode, version, ct);
            nodesCreated++;

            // Create containment edge from file to top-level extracted nodes
            // Uses the extractor's own edge type (structural definition), not MemoryRelation
            if (!hashId.Contains('.') || node.NodeType is "namespace" or "class" or "interface" or "struct" or "record" or "enum")
            {
                var containEdge = new Hyperedge
                {
                    Id = $"edge:contains:{fileNodeId}→{canonicalId}",
                    EdgeType = HyperedgeType.ClassDefinition,
                    NodeIds = new List<string> { fileNodeId, canonicalId },
                    Roles = new Dictionary<string, string>
                    {
                        [fileNodeId] = "parent",
                        [canonicalId] = "child",
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "structural",
                        [MemoryMetadataKeys.SemanticRelation] = "CONTAINS",
                    },
                };
                await this.store.CreateEdgeAsync(containEdge, ct);
                edgesCreated++;
            }
        }

        // Ingest extracted edges with namespaced IDs and remapped node references
        foreach (var edge in extractedEdges)
        {
            ct.ThrowIfCancellationRequested();

            // Remap node references from extractor hash IDs to canonical IDs
            var remappedNodeIds = edge.NodeIds
                .Select(id => idMapping.TryGetValue(id, out var canonical) ? canonical : id)
                .ToList();

            // Remap role keys from hash IDs to canonical IDs
            var remappedRoles = new Dictionary<string, string>();
            foreach (var (key, role) in edge.Roles)
            {
                var remappedKey = idMapping.TryGetValue(key, out var canonical) ? canonical : key;
                remappedRoles[remappedKey] = role;
            }

            // Preserve the original edge type, tag with memory layer per §2.2
            // Structural: CONTAINS, DEPENDS_ON; Call-Chain: CALLS, EXTENDS, IMPLEMENTS, REFERENCES
            var memoryLayer = edge.EdgeType switch
            {
                HyperedgeType.MethodInvocation or HyperedgeType.ConstructorInvocation => "call_chain",
                HyperedgeType.Implements or HyperedgeType.Extends => "call_chain",
                HyperedgeType.CmdletCall or HyperedgeType.Pipeline
                    or HyperedgeType.ModuleFunctionCall => "call_chain",
                HyperedgeType.DataFlow or HyperedgeType.Assignment
                    or HyperedgeType.CrossLanguageTypeReference
                    or HyperedgeType.CrossLanguageMethodCall => "call_chain",
                _ => "structural",
            };

            var edgeMetadata = new Dictionary<string, object?>(edge.Metadata)
            {
                [MemoryMetadataKeys.MemoryLayer] = memoryLayer,
            };
            if (!edgeMetadata.ContainsKey(MemoryMetadataKeys.SemanticRelation))
            {
                edgeMetadata[MemoryMetadataKeys.SemanticRelation] = MapToCanonicalRelation(edge.EdgeType);
            }

            // Create new edge with namespaced ID and remapped references (init-only properties)
            var remappedEdge = new Hyperedge
            {
                Id = $"edge:{normalizedPath}:{edge.Id}",
                EdgeType = edge.EdgeType,
                NodeIds = remappedNodeIds,
                Roles = remappedRoles,
                Languages = edge.Languages,
                Metadata = edgeMetadata,
            };

            await this.store.CreateEdgeAsync(remappedEdge, ct);
            edgesCreated++;
        }

        return (nodesCreated, edgesCreated);
    }

    /// <summary>
    /// Maps <see cref="HyperedgeType"/> values to canonical SemanticRelation names
    /// as specified in architecture.md §2.2 (CALLS, CONTAINS, IMPLEMENTS, EXTENDS, REFERENCES, DEPENDS_ON).
    /// </summary>
    private static string MapToCanonicalRelation(HyperedgeType edgeType)
    {
        return edgeType switch
        {
            HyperedgeType.MethodInvocation or HyperedgeType.ConstructorInvocation
                or HyperedgeType.CmdletCall or HyperedgeType.ModuleFunctionCall
                or HyperedgeType.CrossLanguageMethodCall => "CALLS",
            HyperedgeType.Implements => "IMPLEMENTS",
            HyperedgeType.Extends => "EXTENDS",
            HyperedgeType.UsingDirective or HyperedgeType.ModuleImport
                or HyperedgeType.DotSource => "DEPENDS_ON",
            HyperedgeType.DataFlow or HyperedgeType.Assignment
                or HyperedgeType.CrossLanguageTypeReference => "REFERENCES",
            HyperedgeType.MemoryRelation => "APPLIES_TO",
            _ => "REFERENCES",
        };
    }

    private static string InferLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (CSharpExtensions.Contains(ext))
        {
            return "C#";
        }

        if (PowerShellExtensions.Contains(ext))
        {
            return "PowerShell";
        }

        return "Unknown";
    }

    private static HashSet<string> GetExtensionsForLanguages(IReadOnlyList<string> languages)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages)
        {
            if (lang.Equals("C#", StringComparison.OrdinalIgnoreCase) ||
                lang.Equals("CSharp", StringComparison.OrdinalIgnoreCase))
            {
                extensions.UnionWith(CSharpExtensions);
            }
            else if (lang.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
            {
                extensions.UnionWith(PowerShellExtensions);
            }
        }

        return extensions;
    }
}