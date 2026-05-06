// -----------------------------------------------------------------------
// <copyright file="StructureBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Walks <see cref="HypergraphData"/> from existing parsers (Ast.Csharp, Ast.PowerShell)
/// and emits structural-layer memory nodes and edges into <see cref="IMemoryGraphStore"/>.
/// Per architecture.md §4.2 — Stage 2.1 of the extraction pipeline.
/// </summary>
public class StructureBuilder
{
    internal static readonly HashSet<string> TypeNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "interface", "struct", "record", "enum"
    };

    private static readonly HashSet<string> MemberNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "method", "property", "constructor", "function", "field"
    };

    private static readonly HashSet<HyperedgeType> ImportEdgeTypes =
    [
        HyperedgeType.UsingDirective,
        HyperedgeType.ModuleImport,
        HyperedgeType.DotSource
    ];

    private static readonly HashSet<HyperedgeType> DefinitionEdgeTypes =
    [
        HyperedgeType.ClassDefinition,
        HyperedgeType.InterfaceDefinition,
        HyperedgeType.StructDefinition,
        HyperedgeType.RecordDefinition,
        HyperedgeType.MethodDefinition,
        HyperedgeType.ConstructorDefinition,
        HyperedgeType.PropertyDefinition,
        HyperedgeType.FunctionDefinition,
    ];

    /// <summary>Timeless version for external boundary nodes (NULL since_index).</summary>
    private static readonly VersionRange TimelessVersion = new(null, null, null, null);

    private readonly IMemoryGraphStore store;

    public StructureBuilder(IMemoryGraphStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Processes parsed <see cref="HypergraphData"/> and persists structural-layer
    /// nodes and edges. Derives <see cref="VersionRange"/> from the repository's
    /// current commit SHA (<see cref="RepositoryMetadata.GitCommitSha"/>).
    /// This is the primary public API; VersionRange is always set from the current commit.
    /// </summary>
    public Task BuildAsync(
        HypergraphData data,
        Func<string, string?> sourceFileReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var commitSha = data.Metadata.Repository?.GitCommitSha;
        VersionRange version;
        if (commitSha is not null)
        {
            int commitIndex = 0;
            if (data.Metadata.Repository!.Metadata.TryGetValue("CommitIndex", out var idxObj)
                && idxObj is not null)
            {
                commitIndex = Convert.ToInt32(idxObj);
            }

            version = new VersionRange(commitSha, commitIndex, null, null);
        }
        else
        {
            // No commit SHA available — still set SinceIndex=0 so in-repo nodes
            // are distinguished from timeless External nodes.
            version = new VersionRange(null, 0, null, null);
        }

        return this.BuildCoreAsync(data, version, sourceFileReader, cancellationToken);
    }

    /// <summary>
    /// Processes parsed <see cref="HypergraphData"/> with an explicit
    /// <see cref="VersionRange"/> — used for testing and advanced scenarios.
    /// </summary>
    internal async Task BuildAsync(
        HypergraphData data,
        VersionRange version,
        Func<string, string?> sourceFileReader,
        CancellationToken cancellationToken)
    {
        await this.BuildCoreAsync(data, version, sourceFileReader, cancellationToken);
    }

    /// <summary>
    /// Core implementation: walks HypergraphData and persists structural-layer
    /// nodes (Repo, Package, Module, Class, Interface, Function, Method, Property)
    /// and edges (CONTAINS, DEPENDS_ON) into the memory graph store.
    /// Nodes are persisted via <see cref="IMemoryGraphStore.UpsertNodeAsync"/>;
    /// edges are persisted via the inherited <see cref="IGraphStore.CreateEdgeAsync"/>.
    /// </summary>
    private async Task BuildCoreAsync(
        HypergraphData data,
        VersionRange version,
        Func<string, string?> sourceFileReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(sourceFileReader);

        var memoryNodes = new Dictionary<string, HypergraphNode>();
        var edges = new List<Hyperedge>();
        var externalNodeIds = new HashSet<string>();
        var inRepoNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodeDefiningEdgeType = BuildDefiningEdgeIndex(data);
        var containmentMap = BuildContainmentMap(data);

        // Pre-read all declared source files for ContentHash computation
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in data.Metadata.Files)
        {
            var content = sourceFileReader(file);
            if (content is not null)
            {
                fileContents[file] = content;
            }
        }

        // 1. Repo root node (ContentHash set after all files processed)
        var repoNode = CreateStructuralNode("/", "Repo", "Repo", "", "", version);
        memoryNodes[repoNode.Id] = repoNode;

        // 2. Package nodes — ID is /{directoryPath}
        var projectToPackageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileToProjectPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in data.Metadata.Projects)
        {
            var projectDir = Path.GetDirectoryName(project.ProjectPath) ?? project.ProjectPath;
            var normalizedDir = NormalizePath(projectDir);
            var packageId = "/" + normalizedDir;
            var packageNode = CreateStructuralNode(
                packageId, project.Name, "Package", project.Language,
                projectDir, version);
            memoryNodes[packageId] = packageNode;
            projectToPackageId[project.ProjectPath] = packageId;
            inRepoNamespaces.Add(project.Name);

            foreach (var sourceFile in project.SourceFiles)
            {
                fileToProjectPath[sourceFile] = project.ProjectPath;
            }

            edges.Add(CreateContainsEdge("/", packageId, "Containment"));
        }

        // 3. Module nodes — ContentHash = SHA-256 of full file source text
        var fileToModuleId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in data.Metadata.Files)
        {
            var normalizedFile = NormalizePath(file);
            var moduleId = "/" + normalizedFile;
            var lang = DetermineLanguage(file);

            var moduleNode = CreateStructuralNode(
                moduleId, Path.GetFileName(file), "Module", lang, file, version);

            if (fileContents.TryGetValue(file, out var fileText))
            {
                moduleNode.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(fileText);
            }

            memoryNodes[moduleId] = moduleNode;
            fileToModuleId[file] = moduleId;

            var parentId = ResolveModuleParent(
                file, fileToProjectPath, projectToPackageId, data.Metadata.Projects);
            edges.Add(CreateContainsEdge(parentId, moduleId, "Containment"));
        }

        // Collect non-imported namespace nodes as in-repo namespaces
        var importedNamespaceNodeIds = new HashSet<string>();
        foreach (var edge in data.Hyperedges.Where(e => ImportEdgeTypes.Contains(e.EdgeType)))
        {
            foreach (var nodeId in edge.NodeIds)
            {
                importedNamespaceNodeIds.Add(nodeId);
            }
        }

        foreach (var node in data.Nodes.Values)
        {
            if (string.Equals(node.NodeType, "namespace", StringComparison.OrdinalIgnoreCase)
                && !importedNamespaceNodeIds.Contains(node.Id))
            {
                inRepoNamespaces.Add(node.Name);
            }
        }

        // 4. Type and member nodes — containment derived from hyperedge roles
        var parserToMemoryId = new Dictionary<string, string>();

        var nodesByFile = data.Nodes.Values
            .Where(n => !string.Equals(n.NodeType, "namespace", StringComparison.OrdinalIgnoreCase))
            .GroupBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, fileNodes) in nodesByFile)
        {
            var moduleId = EnsureModuleNode(
                filePath, memoryNodes, fileToModuleId, edges, version,
                fileContents, sourceFileReader);

            var types = fileNodes.Where(n => TypeNodeTypes.Contains(n.NodeType)).ToList();
            var members = fileNodes.Where(n => MemberNodeTypes.Contains(n.NodeType)).ToList();

            fileContents.TryGetValue(filePath, out var fullFileContent);
            var normalizedFile = NormalizePath(filePath);

            // Process types first to build parserToMemoryId map.
            // Topological sort ensures containers appear before nested types.
            var sortedTypes = TopologicalSortTypes(types, containmentMap);
            foreach (var typeNode in sortedTypes)
            {
                // Derive qualifiedName: if nested (has container type), prepend container qualified name
                string qualifiedName;
                string parentMemoryId;
                if (containmentMap.TryGetValue(typeNode.Id, out var containerTypeId) &&
                    parserToMemoryId.TryGetValue(containerTypeId, out var containerMId))
                {
                    var colonIdx = containerMId.IndexOf("::", StringComparison.Ordinal);
                    var containerQualified = containerMId[(colonIdx + 2)..];
                    qualifiedName = containerQualified + "." + typeNode.Name;
                    parentMemoryId = containerMId;
                }
                else
                {
                    qualifiedName = typeNode.Name;
                    parentMemoryId = moduleId;
                }

                var memoryId = "/" + normalizedFile + "::" + qualifiedName;
                var nodeKind = MapNodeKind(typeNode.NodeType);

                var mNode = CreateStructuralNode(
                    memoryId, typeNode.Name, nodeKind, typeNode.Language,
                    filePath, version);

                // ContentHash: SHA-256 of actual source text
                var sourceText = GetSourceText(typeNode)
                    ?? ExtractSourceByLineRange(fullFileContent, typeNode.LineStart, typeNode.LineEnd);
                if (sourceText is not null)
                {
                    mNode.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(sourceText);
                }
                else if (fullFileContent is not null)
                {
                    mNode.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(fullFileContent);
                }

                mNode.Metadata[MemoryMetadataKeys.QualifiedName] = qualifiedName;

                // Preserve namespace information from scope
                var ns = ExtractNamespace(typeNode);
                if (ns is not null)
                {
                    mNode.Metadata["Namespace"] = ns;
                }

                memoryNodes[memoryId] = mNode;
                parserToMemoryId[typeNode.Id] = memoryId;

                var originalEdgeType = nodeDefiningEdgeType.TryGetValue(typeNode.Id, out var defType)
                    ? defType.ToString()
                    : "Containment";
                edges.Add(CreateContainsEdge(parentMemoryId, memoryId, originalEdgeType));
            }

            // Process members — derive parent from containment map (hyperedge roles)
            foreach (var memberNode in members)
            {
                string qualifiedName;
                string parentMemoryId;

                // Primary: use containment map built from definition hyperedge roles
                if (containmentMap.TryGetValue(memberNode.Id, out var containerParserId) &&
                    parserToMemoryId.TryGetValue(containerParserId, out var containerMId))
                {
                    var colonIdx = containerMId.IndexOf("::", StringComparison.Ordinal);
                    var containerQualified = containerMId[(colonIdx + 2)..];
                    qualifiedName = containerQualified + "." + memberNode.Name;
                    parentMemoryId = containerMId;
                }
                else
                {
                    // Fallback: scope-based parent type matching
                    var scopeParent = FindParentType(memberNode, types, filePath, memoryNodes);
                    if (scopeParent is not null)
                    {
                        var colonIdx = scopeParent.IndexOf("::", StringComparison.Ordinal);
                        var containerQualified = colonIdx >= 0 ? scopeParent[(colonIdx + 2)..] : "";
                        qualifiedName = string.IsNullOrEmpty(containerQualified)
                            ? memberNode.Name
                            : containerQualified + "." + memberNode.Name;
                        parentMemoryId = scopeParent;
                    }
                    else
                    {
                        qualifiedName = memberNode.Name;
                        parentMemoryId = moduleId;
                    }
                }

                var memoryId = "/" + normalizedFile + "::" + qualifiedName;
                var nodeKind = MapNodeKind(memberNode.NodeType);

                var mNode = CreateStructuralNode(
                    memoryId, memberNode.Name, nodeKind, memberNode.Language,
                    filePath, version);

                // ContentHash: SHA-256 of actual source text
                var sourceText = GetSourceText(memberNode)
                    ?? ExtractSourceByLineRange(fullFileContent, memberNode.LineStart, memberNode.LineEnd);
                if (sourceText is not null)
                {
                    mNode.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(sourceText);
                }
                else if (fullFileContent is not null)
                {
                    mNode.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(fullFileContent);
                }

                mNode.Metadata[MemoryMetadataKeys.QualifiedName] = qualifiedName;

                // Preserve namespace information from scope
                var ns = ExtractNamespace(memberNode);
                if (ns is not null)
                {
                    mNode.Metadata["Namespace"] = ns;
                }

                memoryNodes[memoryId] = mNode;
                parserToMemoryId[memberNode.Id] = memoryId;

                var originalEdgeType = nodeDefiningEdgeType.TryGetValue(memberNode.Id, out var defType)
                    ? defType.ToString()
                    : "Containment";
                edges.Add(CreateContainsEdge(parentMemoryId, memoryId, originalEdgeType));
            }
        }

        // 5. Package ContentHash = SHA-256 of concatenated source file texts (sorted by path)
        foreach (var (projPath, packageId) in projectToPackageId)
        {
            var project = data.Metadata.Projects.First(p =>
                string.Equals(p.ProjectPath, projPath, StringComparison.OrdinalIgnoreCase));
            var sortedFiles = project.SourceFiles
                .OrderBy(f => NormalizePath(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var textParts = new List<string>();
            foreach (var f in sortedFiles)
            {
                if (fileContents.TryGetValue(f, out var content))
                {
                    textParts.Add(NormalizePath(f) + "\n" + content);
                }
            }

            if (textParts.Count > 0)
            {
                var concatenated = string.Join("\n---\n", textParts);
                memoryNodes[packageId].Metadata[MemoryMetadataKeys.ContentHash] =
                    ComputeContentHash(concatenated);
            }
        }

        // 6. Repo ContentHash = SHA-256 of concatenated all source file texts
        var allSortedFiles = fileContents.Keys
            .OrderBy(f => NormalizePath(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allSortedFiles.Count > 0)
        {
            var allParts = allSortedFiles.Select(f => NormalizePath(f) + "\n" + fileContents[f]);
            var allConcatenated = string.Join("\n---\n", allParts);
            memoryNodes["/"].Metadata[MemoryMetadataKeys.ContentHash] =
                ComputeContentHash(allConcatenated);
        }

        // 6.5 Guarantee ContentHash on every in-repo node.
        // If source text was unavailable, use a synthetic hash based on identity.
        foreach (var (nodeId, node) in memoryNodes)
        {
            if (externalNodeIds.Contains(nodeId))
            {
                continue;
            }

            if (!node.Metadata.ContainsKey(MemoryMetadataKeys.ContentHash))
            {
                // Structural fingerprint hash — deterministic and unique per node
                // without relying on synthetic node IDs.
                node.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(
                    $"{node.Name}|{node.NodeType}|{node.Language}|{NormalizePath(node.FilePath)}|{node.LineStart}:{node.LineEnd}");
            }
        }

        // 7. DEPENDS_ON edges from import directives
        foreach (var edge in data.Hyperedges.Where(e => ImportEdgeTypes.Contains(e.EdgeType)))
        {
            ProcessImportEdge(edge, data, memoryNodes, edges,
                fileToModuleId, inRepoNamespaces, externalNodeIds);
        }

        // 8. Persist in-repo nodes with version
        foreach (var node in memoryNodes.Values)
        {
            if (externalNodeIds.Contains(node.Id))
            {
                continue;
            }

            await this.store.UpsertNodeAsync(node, version, cancellationToken);
        }

        // 9. Persist external boundary nodes with timeless version
        foreach (var extId in externalNodeIds)
        {
            if (memoryNodes.TryGetValue(extId, out var extNode))
            {
                await this.store.UpsertNodeAsync(extNode, TimelessVersion, cancellationToken);
            }
        }

        // 10. Persist all edges via inherited IGraphStore.CreateEdgeAsync
        IGraphStore graphStore = this.store;
        foreach (var edge in edges)
        {
            if (version.Since is not null)
            {
                edge.Metadata[MemoryMetadataKeys.SinceCommit] = version.Since;
            }

            if (version.SinceIndex.HasValue)
            {
                edge.Metadata[MemoryMetadataKeys.SinceIndex] = version.SinceIndex.Value;
            }

            await graphStore.CreateEdgeAsync(edge, cancellationToken);
        }
    }

    // ── Node creation helpers ───────────────────────────────────────────

    private static HypergraphNode CreateStructuralNode(
        string id,
        string name,
        string nodeType,
        string language,
        string filePath,
        VersionRange version)
    {
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
        };

        // Write temporal metadata independently — SinceIndex is always written
        // when present so in-repo nodes are distinguished from timeless External
        // nodes even when no git commit SHA is available.
        if (version.Since is not null)
        {
            metadata[MemoryMetadataKeys.SinceCommit] = version.Since;
        }

        if (version.SinceIndex.HasValue)
        {
            metadata[MemoryMetadataKeys.SinceIndex] = version.SinceIndex.Value;
        }

        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = language,
            FilePath = filePath,
            Metadata = metadata
        };
    }

    private static HypergraphNode CreateExternalNode(string id, string name)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = "External",
            Language = "",
            FilePath = "",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "call_chain",
            }
        };
    }

    // ── Edge creation helpers ───────────────────────────────────────────

    private static Hyperedge CreateContainsEdge(
        string parentId,
        string childId,
        string originalEdgeType)
    {
        var edgeId = "structural:contains:" + ComputeContentHash(parentId + "|" + childId)[..16];

        return new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [parentId, childId],
            Roles = new Dictionary<string, string>
            {
                [parentId] = "parent",
                [childId] = "child",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CONTAINS",
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.OriginalEdgeType] = originalEdgeType,
            }
        };
    }

    private static Hyperedge CreateDependsOnEdge(
        string importerId,
        string importedId,
        string originalEdgeType)
    {
        var edgeId = "structural:depends_on:" + ComputeContentHash(importerId + "|" + importedId)[..16];

        return new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [importerId, importedId],
            Roles = new Dictionary<string, string>
            {
                [importerId] = "importer",
                [importedId] = "imported",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "DEPENDS_ON",
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.OriginalEdgeType] = originalEdgeType,
            }
        };
    }

    // ── Hyperedge index ─────────────────────────────────────────────────

    private static Dictionary<string, HyperedgeType> BuildDefiningEdgeIndex(HypergraphData data)
    {
        var index = new Dictionary<string, HyperedgeType>();
        foreach (var edge in data.Hyperedges.Where(e => DefinitionEdgeTypes.Contains(e.EdgeType)))
        {
            foreach (var nodeId in edge.NodeIds)
            {
                if (edge.Roles.TryGetValue(nodeId, out var role) &&
                    string.Equals(role, "container", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index.TryAdd(nodeId, edge.EdgeType);
            }
        }

        return index;
    }

    /// <summary>
    /// Builds a containment map from definition hyperedges: maps each non-container
    /// child parser node ID to its container parser node ID, using the "container"
    /// role defined in the hyperedge. This replaces scope-based heuristics with
    /// structural information directly from the parser's hyperedge output.
    /// </summary>
    private static Dictionary<string, string> BuildContainmentMap(HypergraphData data)
    {
        var map = new Dictionary<string, string>();
        foreach (var edge in data.Hyperedges.Where(e => DefinitionEdgeTypes.Contains(e.EdgeType)))
        {
            string? containerId = null;
            var childIds = new List<string>();

            foreach (var nodeId in edge.NodeIds)
            {
                if (edge.Roles.TryGetValue(nodeId, out var role) &&
                    string.Equals(role, "container", StringComparison.OrdinalIgnoreCase))
                {
                    containerId = nodeId;
                }
                else
                {
                    childIds.Add(nodeId);
                }
            }

            if (containerId is not null)
            {
                foreach (var childId in childIds)
                {
                    map.TryAdd(childId, containerId);
                }
            }
        }

        return map;
    }

    // ── Import processing ───────────────────────────────────────────────

    private static void ProcessImportEdge(
        Hyperedge edge,
        HypergraphData data,
        Dictionary<string, HypergraphNode> memoryNodes,
        List<Hyperedge> edges,
        Dictionary<string, string> fileToModuleId,
        HashSet<string> inRepoNamespaces,
        HashSet<string> externalNodeIds)
    {
        string? importedName = null;
        string? importerFilePath = null;

        // Pass 1: look for role-tagged nodes
        foreach (var nodeId in edge.NodeIds)
        {
            if (!data.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            if (edge.Roles.TryGetValue(nodeId, out var role))
            {
                if (role is "imported_namespace" or "imported_module" or "dot_source_file")
                {
                    importedName = node.Name;
                    if (importerFilePath is null)
                    {
                        importerFilePath = node.FilePath;
                    }

                    continue;
                }
            }

            if (!string.IsNullOrEmpty(node.FilePath))
            {
                importerFilePath = node.FilePath;
            }
        }

        // Pass 2: fallback to namespace-typed nodes
        if (importedName is null)
        {
            foreach (var nodeId in edge.NodeIds)
            {
                if (data.Nodes.TryGetValue(nodeId, out var node) &&
                    string.Equals(node.NodeType, "namespace", StringComparison.OrdinalIgnoreCase))
                {
                    importedName = node.Name;
                    if (importerFilePath is null)
                    {
                        importerFilePath = node.FilePath;
                    }

                    break;
                }
            }
        }

        // Pass 3: fallback to any node with a name
        if (importedName is null)
        {
            foreach (var nodeId in edge.NodeIds)
            {
                if (data.Nodes.TryGetValue(nodeId, out var node) &&
                    !string.IsNullOrEmpty(node.Name))
                {
                    importedName = node.Name;
                    if (importerFilePath is null)
                    {
                        importerFilePath = node.FilePath;
                    }

                    break;
                }
            }
        }

        // Pass 4: last resort — use edge metadata or edge ID
        if (importedName is null)
        {
            if (edge.Metadata.TryGetValue("ImportedName", out var metaName) && metaName is string mn)
            {
                importedName = mn;
            }
            else
            {
                // Create a synthetic name so the import is never silently lost
                importedName = "unresolved:" + edge.Id;
            }
        }

        // Resolve importer file path from any edge node
        if (importerFilePath is null)
        {
            foreach (var nodeId in edge.NodeIds)
            {
                if (data.Nodes.TryGetValue(nodeId, out var n) && !string.IsNullOrEmpty(n.FilePath))
                {
                    importerFilePath = n.FilePath;
                    break;
                }
            }
        }

        // If we truly have no importer file, we cannot create the edge
        if (importerFilePath is null)
        {
            return;
        }

        string? importerModuleId = null;
        fileToModuleId.TryGetValue(importerFilePath, out importerModuleId);

        if (importerModuleId is null)
        {
            foreach (var nodeId in edge.NodeIds)
            {
                if (data.Nodes.TryGetValue(nodeId, out var n) &&
                    fileToModuleId.TryGetValue(n.FilePath, out var modId))
                {
                    importerModuleId = modId;
                    break;
                }
            }
        }

        // Try constructing module ID from file path as fallback
        if (importerModuleId is null)
        {
            var normalizedFile = NormalizePath(importerFilePath);
            var candidateModuleId = "/" + normalizedFile;
            if (memoryNodes.ContainsKey(candidateModuleId))
            {
                importerModuleId = candidateModuleId;
            }
        }

        if (importerModuleId is null)
        {
            return;
        }

        var originalEdgeType = edge.EdgeType.ToString();

        // Try in-repo resolution first
        string? targetId = null;

        // DotSource edges import file paths, not namespaces.
        // Resolve relative to the importing file's directory.
        if (edge.EdgeType == HyperedgeType.DotSource && importedName is not null)
        {
            targetId = ResolveDotSourceTarget(importedName, importerFilePath,
                memoryNodes, fileToModuleId, data);
        }

        if (targetId is null && importedName is not null && IsInRepoImport(importedName, inRepoNamespaces))
        {
            targetId = FindInRepoTarget(importedName, memoryNodes);
        }

        if (targetId is not null)
        {
            edges.Add(CreateDependsOnEdge(importerModuleId, targetId, originalEdgeType));
        }
        else if (importedName is not null)
        {
            // External or unresolved in-repo → always create boundary node + edge
            var externalId = BuildExternalNodeId(importedName);
            if (!memoryNodes.ContainsKey(externalId))
            {
                var extNode = CreateExternalNode(externalId, importedName);
                memoryNodes[externalId] = extNode;
                externalNodeIds.Add(externalId);
            }

            edges.Add(CreateDependsOnEdge(importerModuleId, externalId, originalEdgeType));
        }
    }

    /// <summary>
    /// Builds an external node ID in the format ext:{package}:{qualifiedName}.
    /// For namespace-level imports (e.g. "using Microsoft.CodeAnalysis;"),
    /// the package is the full namespace and qualifiedName is "*".
    /// </summary>
    internal static string BuildExternalNodeId(string importedName)
    {
        return "ext:" + importedName + ":*";
    }

    /// <summary>
    /// Resolves a DotSource import (file path) to an in-repo module node ID.
    /// Uses multiple resolution strategies:
    ///   1. Exact match in memoryNodes by resolved path
    ///   2. Case-insensitive match via fileToModuleId
    ///   3. Case-insensitive match across all project source files
    /// Returns null if the target cannot be resolved to any in-repo module.
    /// </summary>
    private static string? ResolveDotSourceTarget(
        string importedName,
        string importerFilePath,
        Dictionary<string, HypergraphNode> memoryNodes,
        Dictionary<string, string> fileToModuleId,
        HypergraphData data)
    {
        var importerDir = Path.GetDirectoryName(importerFilePath)?.Replace('\\', '/') ?? "";
        var combined = string.IsNullOrEmpty(importerDir)
            ? importedName
            : importerDir + "/" + importedName;
        var resolvedPath = ResolveDotSegments(combined);
        var candidateDotModuleId = "/" + resolvedPath;

        // 1. Direct match in memoryNodes
        if (memoryNodes.ContainsKey(candidateDotModuleId))
        {
            return candidateDotModuleId;
        }

        // 2. Case-insensitive match via fileToModuleId
        if (fileToModuleId.TryGetValue(resolvedPath, out var fmId))
        {
            return fmId;
        }

        // 3. Search all project source files for a case-insensitive path match
        foreach (var project in data.Metadata.Projects)
        {
            foreach (var srcFile in project.SourceFiles)
            {
                if (string.Equals(NormalizePath(srcFile), resolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    var moduleId = "/" + NormalizePath(srcFile);
                    if (memoryNodes.ContainsKey(moduleId))
                    {
                        return moduleId;
                    }
                }
            }
        }

        // 4. Case-insensitive fallback scanning all Module-type memoryNodes
        foreach (var (id, node) in memoryNodes)
        {
            if (string.Equals(node.NodeType, "Module", StringComparison.Ordinal) &&
                string.Equals(id, candidateDotModuleId, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    // ── Resolution helpers ──────────────────────────────────────────────

    private static bool IsInRepoImport(string importedName, HashSet<string> inRepoNamespaces)
    {
        if (inRepoNamespaces.Contains(importedName))
        {
            return true;
        }

        foreach (var ns in inRepoNamespaces)
        {
            if (importedName.StartsWith(ns + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindInRepoTarget(
        string importedName,
        Dictionary<string, HypergraphNode> memoryNodes)
    {
        foreach (var (id, node) in memoryNodes)
        {
            if (string.Equals(node.NodeType, "Package", StringComparison.Ordinal) &&
                string.Equals(node.Name, importedName, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        string? bestPkg = null;
        int bestLen = -1;
        foreach (var (id, node) in memoryNodes)
        {
            if (string.Equals(node.NodeType, "Package", StringComparison.Ordinal) &&
                importedName.StartsWith(node.Name + ".", StringComparison.OrdinalIgnoreCase) &&
                node.Name.Length > bestLen)
            {
                bestLen = node.Name.Length;
                bestPkg = id;
            }
        }

        if (bestPkg is not null)
        {
            return bestPkg;
        }

        foreach (var (id, node) in memoryNodes)
        {
            if (string.Equals(node.NodeType, "Module", StringComparison.Ordinal) &&
                string.Equals(node.Name, importedName, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    private static string ResolveModuleParent(
        string filePath,
        Dictionary<string, string> fileToProjectPath,
        Dictionary<string, string> projectToPackageId,
        List<ProjectMetadata> projects)
    {
        if (fileToProjectPath.TryGetValue(filePath, out var projPath) &&
            projectToPackageId.TryGetValue(projPath, out var pkgId))
        {
            return pkgId;
        }

        var normalizedFile = NormalizePath(filePath);
        string? bestMatch = null;
        int bestLen = -1;

        foreach (var project in projects)
        {
            var projectDir = NormalizePath(Path.GetDirectoryName(project.ProjectPath) ?? "");
            if (normalizedFile.StartsWith(projectDir + "/", StringComparison.OrdinalIgnoreCase) &&
                projectDir.Length > bestLen)
            {
                bestLen = projectDir.Length;
                if (projectToPackageId.TryGetValue(project.ProjectPath, out var id))
                {
                    bestMatch = id;
                }
            }
        }

        return bestMatch ?? "/";
    }

    private static string EnsureModuleNode(
        string filePath,
        Dictionary<string, HypergraphNode> memoryNodes,
        Dictionary<string, string> fileToModuleId,
        List<Hyperedge> edges,
        VersionRange version,
        Dictionary<string, string> fileContents,
        Func<string, string?> sourceFileReader)
    {
        if (fileToModuleId.TryGetValue(filePath, out var existingId))
        {
            return existingId;
        }

        var normalizedFile = NormalizePath(filePath);
        var moduleId = "/" + normalizedFile;

        if (!memoryNodes.ContainsKey(moduleId))
        {
            var lang = DetermineLanguage(filePath);
            var moduleNode = CreateStructuralNode(
                moduleId, Path.GetFileName(filePath), "Module", lang, filePath, version);

            // Read file content for ContentHash
            if (!fileContents.TryGetValue(filePath, out var content))
            {
                content = sourceFileReader(filePath);
                if (content is not null)
                {
                    fileContents[filePath] = content;
                }
            }

            if (content is not null)
            {
                moduleNode.Metadata[MemoryMetadataKeys.ContentHash] = ComputeContentHash(content);
            }

            memoryNodes[moduleId] = moduleNode;
            edges.Add(CreateContainsEdge("/", moduleId, "Containment"));
        }

        fileToModuleId[filePath] = moduleId;
        return moduleId;
    }

    // ── Qualified name helpers ──────────────────────────────────────────

    /// <summary>
    /// Builds a qualified name for use in memory IDs.
    /// Types: just the type name (e.g. "HypergraphNode") — file path provides unique context.
    /// Members: ContainingType.MemberName (e.g. "HypergraphNode.GetEmbeddingText").
    /// Falls back to just Name when Scope is empty.
    /// </summary>
    internal static string BuildQualifiedName(HypergraphNode node)
    {
        // Types: ID uses just the type name (file path already scopes it)
        if (TypeNodeTypes.Contains(node.NodeType))
        {
            return node.Name;
        }

        // Members with no scope: just the member name
        if (string.IsNullOrEmpty(node.Scope))
        {
            return node.Name;
        }

        // Members: extract containing type from scope
        var filePrefix = Path.GetFileNameWithoutExtension(node.FilePath);
        string scopeWithoutFile;

        if (node.Scope.StartsWith(filePrefix + ".", StringComparison.Ordinal))
        {
            scopeWithoutFile = node.Scope[(filePrefix.Length + 1)..];
        }
        else if (string.Equals(node.Scope, filePrefix, StringComparison.Ordinal))
        {
            return node.Name;
        }
        else
        {
            scopeWithoutFile = node.Scope;
        }

        if (string.IsNullOrEmpty(scopeWithoutFile))
        {
            return node.Name;
        }

        // Last segment of scope is the containing type name
        var lastDot = scopeWithoutFile.LastIndexOf('.');
        var containingType = lastDot >= 0 ? scopeWithoutFile[(lastDot + 1)..] : scopeWithoutFile;

        return containingType + "." + node.Name;
    }

    /// <summary>
    /// Extracts the namespace from a node's Scope property.
    /// For types, the Scope is typically "{FileName}.{Namespace}".
    /// For members, the Scope is typically "{FileName}.{Namespace}.{ContainingType}".
    /// Returns null when no namespace can be determined.
    /// </summary>
    internal static string? ExtractNamespace(HypergraphNode node)
    {
        if (string.IsNullOrEmpty(node.Scope))
        {
            return null;
        }

        var filePrefix = Path.GetFileNameWithoutExtension(node.FilePath);
        string scopeWithoutFile;

        if (node.Scope.StartsWith(filePrefix + ".", StringComparison.Ordinal))
        {
            scopeWithoutFile = node.Scope[(filePrefix.Length + 1)..];
        }
        else if (string.Equals(node.Scope, filePrefix, StringComparison.Ordinal))
        {
            return null;
        }
        else
        {
            scopeWithoutFile = node.Scope;
        }

        if (string.IsNullOrEmpty(scopeWithoutFile))
        {
            return null;
        }

        // For types, the remaining scope IS the namespace
        if (TypeNodeTypes.Contains(node.NodeType))
        {
            return scopeWithoutFile;
        }

        // For members, the namespace is everything except the last segment (the containing type)
        var lastDot = scopeWithoutFile.LastIndexOf('.');
        return lastDot > 0 ? scopeWithoutFile[..lastDot] : scopeWithoutFile;
    }

    private static string? FindParentType(
        HypergraphNode memberNode,
        List<HypergraphNode> types,
        string filePath,
        Dictionary<string, HypergraphNode> memoryNodes)
    {
        var memberQualified = BuildQualifiedName(memberNode);
        var normalizedFile = NormalizePath(filePath);

        string? bestMatch = null;
        int bestLen = -1;

        foreach (var typeNode in types)
        {
            var typeQualified = BuildQualifiedName(typeNode);
            var prefix = typeQualified + ".";

            if (memberQualified.StartsWith(prefix, StringComparison.Ordinal) &&
                typeQualified.Length > bestLen)
            {
                bestLen = typeQualified.Length;
                bestMatch = "/" + normalizedFile + "::" + typeQualified;
            }
        }

        return bestMatch is not null && memoryNodes.ContainsKey(bestMatch)
            ? bestMatch
            : null;
    }

    // ── Mapping helpers ─────────────────────────────────────────────────

    private static string MapNodeKind(string parserNodeType)
    {
        return parserNodeType.ToLowerInvariant() switch
        {
            "class" => "Class",
            "interface" => "Interface",
            "struct" => "Struct",
            "record" => "Record",
            "enum" => "Enum",
            "method" => "Method",
            "property" => "Property",
            "constructor" => "Constructor",
            "function" => "Function",
            "field" => "Field",
            _ => parserNodeType
        };
    }

    // ── Utility ─────────────────────────────────────────────────────────

    internal static string NormalizePath(string path)
    {
        return path
            .Replace('\\', '/')
            .TrimStart('/');
    }

    /// <summary>
    /// Resolves '.' and '..' segments in a forward-slash-separated path.
    /// </summary>
    internal static string ResolveDotSegments(string path)
    {
        var normalized = NormalizePath(path);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var seg in segments)
        {
            if (seg == ".")
            {
                continue;
            }

            if (seg == ".." && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(seg);
        }

        return string.Join("/", stack);
    }

    /// <summary>
    /// Topologically sorts types so that container types appear before their nested types.
    /// Uses the containment map to determine nesting relationships.
    /// </summary>
    private static List<HypergraphNode> TopologicalSortTypes(
        List<HypergraphNode> types,
        Dictionary<string, string> containmentMap)
    {
        var typeIds = new HashSet<string>(types.Select(t => t.Id));
        var sorted = new List<HypergraphNode>();
        var remaining = new List<HypergraphNode>(types);
        var processed = new HashSet<string>();

        while (remaining.Count > 0)
        {
            var batch = remaining
                .Where(t =>
                {
                    if (!containmentMap.TryGetValue(t.Id, out var containerId))
                    {
                        return true;
                    }

                    if (!typeIds.Contains(containerId))
                    {
                        return true;
                    }

                    return processed.Contains(containerId);
                })
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList();

            if (batch.Count == 0)
            {
                sorted.AddRange(remaining);
                break;
            }

            sorted.AddRange(batch);
            foreach (var t in batch)
            {
                processed.Add(t.Id);
                remaining.Remove(t);
            }
        }

        return sorted;
    }

    private static string DetermineLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ps1" or ".psm1" or ".psd1" => "powershell",
            _ => ""
        };
    }

    private static string? GetSourceText(HypergraphNode node)
    {
        if (node.Metadata.TryGetValue("SourceCode", out var code) &&
            code is string codeStr && !string.IsNullOrEmpty(codeStr))
        {
            return codeStr;
        }

        return null;
    }

    /// <summary>
    /// Extracts source text from full file content by line range.
    /// Returns null if content is null or line range is invalid.
    /// </summary>
    internal static string? ExtractSourceByLineRange(string? fullContent, int lineStart, int lineEnd)
    {
        if (fullContent is null || lineStart <= 0 || lineEnd < lineStart)
        {
            return null;
        }

        var lines = fullContent.Split('\n');
        if (lineStart > lines.Length)
        {
            return null;
        }

        var endLine = Math.Min(lineEnd, lines.Length);
        var extracted = lines.Skip(lineStart - 1).Take(endLine - lineStart + 1);
        return string.Join("\n", extracted);
    }

    internal static string ComputeContentHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
