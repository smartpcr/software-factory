// -----------------------------------------------------------------------
// <copyright file="ExtractionPipeline.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Csharp;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.PowerShell;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates the extraction pipeline: parse → StructureBuilder → CallChainAnalyzer → SemanticIndexer.
/// Per architecture §5.4. Implements <see cref="IExtractionPipeline"/>.
/// Persists extracted nodes/edges to <see cref="IMemoryGraphStore"/> when provided.
/// </summary>
public class ExtractionPipeline : IExtractionPipeline
{
    private readonly IStructureBuilder structureBuilder;
    private readonly ICallChainAnalyzer callChainAnalyzer;
    private readonly ISemanticIndexer semanticIndexer;
    private readonly FactNormalizer factNormalizer;
    private readonly IMemoryGraphStore? graphStore;
    private readonly ILogger<ExtractionPipeline> logger;

    public ExtractionPipeline(
        IStructureBuilder structureBuilder,
        ICallChainAnalyzer callChainAnalyzer,
        ISemanticIndexer semanticIndexer,
        FactNormalizer factNormalizer,
        IMemoryGraphStore? graphStore,
        ILogger<ExtractionPipeline> logger)
    {
        this.structureBuilder = structureBuilder;
        this.callChainAnalyzer = callChainAnalyzer;
        this.semanticIndexer = semanticIndexer;
        this.factNormalizer = factNormalizer;
        this.graphStore = graphStore;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractAsync(
        string repoRoot,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        this.logger.LogInformation("Starting full extraction from {RepoRoot}", repoRoot);

        // Step 1: Parse source files into HypergraphData
        var parseErrors = new List<string>();
        var filesSkipped = 0;
        HypergraphData data;

        try
        {
            data = this.ParseRepository(repoRoot, options, parseErrors, ref filesSkipped);
        }
        catch (DirectoryNotFoundException ex)
        {
            this.logger.LogError(ex, "Repository root not found: {RepoRoot}", repoRoot);
            return new ExtractionResult
            {
                Errors = new List<string> { $"Repository root not found: {ex.Message}" },
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            this.logger.LogError(ex, "Access denied: {RepoRoot}", repoRoot);
            return new ExtractionResult
            {
                Errors = new List<string> { $"Access denied: {ex.Message}" },
            };
        }

        // Step 1.5: Detect cross-language type references from PowerShell to .NET types
        var crossLangErrors = this.SynthesizeCrossLanguageTypeReferences(data, repoRoot);
        parseErrors.AddRange(crossLangErrors);

        // Step 2: Normalize — assign canonical IDs and deduplicate
        var normResult = this.factNormalizer.Normalize(data, repoRoot);
        this.logger.LogInformation(
            "Normalization: {Nodes} nodes, {Merged} merged, {Mappings} remapped",
            normResult.NodesNormalized, normResult.DuplicatesMerged, normResult.IdMappings.Count);

        // Step 3: Sequential pipeline — StructureBuilder → CallChainAnalyzer → SemanticIndexer
        // Per architecture §3.1: skip downstream stages and record errors on failure
        var allErrors = new List<string>(parseErrors);
        var totalNodes = data.Nodes.Count;
        var totalEdges = data.Hyperedges.Count;
        var totalEmbeddings = 0;

        // Stage 1: StructureBuilder
        StageResult structureResult;
        try
        {
            structureResult = await this.structureBuilder.BuildAsync(data, repoRoot, options, ct)
                .ConfigureAwait(false);
            totalNodes += structureResult.NodesCreated;
            totalEdges += structureResult.EdgesCreated;
            allErrors.AddRange(structureResult.Errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "StructureBuilder failed, skipping downstream stages");
            allErrors.Add($"StructureBuilder failed: {ex.Message}");
            return this.BuildResult(totalNodes, totalEdges, totalEmbeddings, filesSkipped, allErrors);
        }

        // Stage 2: CallChainAnalyzer
        StageResult callChainResult;
        try
        {
            callChainResult = await this.callChainAnalyzer.AnalyzeAsync(data, repoRoot, options, ct)
                .ConfigureAwait(false);
            totalEdges += callChainResult.EdgesCreated;
            allErrors.AddRange(callChainResult.Errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "CallChainAnalyzer failed, skipping downstream stages");
            allErrors.Add($"CallChainAnalyzer failed: {ex.Message}");
            return this.BuildResult(totalNodes, totalEdges, totalEmbeddings, filesSkipped, allErrors);
        }

        // Stage 3: SemanticIndexer
        StageResult semanticResult;
        try
        {
            semanticResult = await this.semanticIndexer.IndexAsync(data, repoRoot, options, ct)
                .ConfigureAwait(false);
            totalEmbeddings += semanticResult.EmbeddingsGenerated;
            totalEdges += semanticResult.EdgesCreated;
            allErrors.AddRange(semanticResult.Errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "SemanticIndexer failed");
            allErrors.Add($"SemanticIndexer failed: {ex.Message}");
        }

        // Step 4: Post-stage normalization — ensure ALL edges (including those added
        // by StructureBuilder, CallChainAnalyzer, SemanticIndexer) have required metadata
        this.StampEdgeMetadata(data);

        // Step 5: Persist to graph store if available — writes are critical per architecture §5.1
        if (this.graphStore != null)
        {
            var version = VersionRange.FromOptions(options);
            await this.graphStore.UpsertNodesAsync(data.Nodes.Values, version, ct).ConfigureAwait(false);
            await this.graphStore.UpsertEdgesAsync(data.Hyperedges, ct).ConfigureAwait(false);
            await this.graphStore.LogAuditAsync(
                "extraction-pipeline",
                "ExtractAsync",
                new { NodesCount = data.Nodes.Count, EdgesCount = data.Hyperedges.Count },
                ct).ConfigureAwait(false);
        }

        return this.BuildResult(totalNodes, totalEdges, totalEmbeddings, filesSkipped, allErrors);
    }

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractFilesAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        ExtractionOptions options,
        CancellationToken ct = default)
    {
        this.logger.LogInformation(
            "Starting incremental extraction for {Count} files", relativePaths.Count);

        if (relativePaths.Count == 0)
        {
            return ExtractionResult.Empty;
        }

        // Retire stale nodes/edges from graph store for changed files BEFORE parsing new content
        var retirementErrors = new List<string>();
        if (this.graphStore != null)
        {
            var commitIndex = options.CommitIndex ?? 0;
            foreach (var relPath in relativePaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var staleNodes = await this.graphStore
                        .GetNodesByFilePathAsync(relPath, ct).ConfigureAwait(false);
                    var staleNodeIds = staleNodes.Select(n => n.Id).ToList();

                    // Retire edges where at least one endpoint belongs to a changed file
                    if (staleNodeIds.Count > 0)
                    {
                        var staleEdges = await this.graphStore
                            .GetEdgesForNodesAsync(staleNodeIds, ct).ConfigureAwait(false);
                        foreach (var edge in staleEdges)
                        {
                            await this.graphStore.RetireEdgeAsync(edge.Id, commitIndex, ct)
                                .ConfigureAwait(false);
                        }
                    }

                    foreach (var nodeId in staleNodeIds)
                    {
                        await this.graphStore.RetireNodeAsync(nodeId, commitIndex, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Failed to retire stale data for {Path}", relPath);
                    retirementErrors.Add($"Retirement failed for {relPath}: {ex.Message}");
                }
            }
        }

        // Parse only the specified changed files into a fresh HypergraphData
        var parseErrors = new List<string>();
        var filesSkipped = 0;
        var data = new HypergraphData();

        foreach (var relPath in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                this.logger.LogWarning("File not found, skipping: {Path}", relPath);
                parseErrors.Add($"File not found: {relPath}");
                filesSkipped++;
                continue;
            }

            try
            {
                this.ParseSingleFile(fullPath, relPath, data, options);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to parse {Path}", relPath);
                parseErrors.Add($"Parse error in {relPath}: {ex.Message}");
                filesSkipped++;
            }
        }

        // Detect cross-language type references from PowerShell
        var crossLangErrors = this.SynthesizeCrossLanguageTypeReferences(data, repoRoot);
        parseErrors.AddRange(crossLangErrors);

        // Normalize freshly parsed data (canonical IDs, dedup within changed set)
        // NormalizeFiles does NOT remove the fresh nodes — just normalizes them
        var normResult = this.factNormalizer.NormalizeFiles(data, repoRoot, relativePaths);

        // Stamp CommitSha on nodes if provided
        if (!string.IsNullOrEmpty(options.CommitSha))
        {
            this.StampCommitSha(data, options);
        }

        // Run pipeline stages sequentially (skip downstream on failure, record errors)
        var allErrors = new List<string>(parseErrors);
        allErrors.AddRange(retirementErrors);
        var totalNodes = data.Nodes.Count;
        var totalEdges = data.Hyperedges.Count;
        var totalEmbeddings = 0;

        // Stage 1: StructureBuilder
        try
        {
            var structureResult = await this.structureBuilder.BuildAsync(data, repoRoot, options, ct)
                .ConfigureAwait(false);
            totalNodes += structureResult.NodesCreated;
            totalEdges += structureResult.EdgesCreated;
            allErrors.AddRange(structureResult.Errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "StructureBuilder failed in incremental mode");
            allErrors.Add($"StructureBuilder failed: {ex.Message}");
            return this.BuildResult(totalNodes, totalEdges, totalEmbeddings, filesSkipped, allErrors);
        }

        // Stage 2: CallChainAnalyzer
        try
        {
            var callChainResult = await this.callChainAnalyzer.AnalyzeAsync(data, repoRoot, options, ct)
                .ConfigureAwait(false);
            totalEdges += callChainResult.EdgesCreated;
            allErrors.AddRange(callChainResult.Errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "CallChainAnalyzer failed in incremental mode");
            allErrors.Add($"CallChainAnalyzer failed: {ex.Message}");
            return this.BuildResult(totalNodes, totalEdges, totalEmbeddings, filesSkipped, allErrors);
        }

        // Stage 3: SemanticIndexer
        try
        {
            var semanticResult = await this.semanticIndexer.IndexAsync(data, repoRoot, options, ct)
                .ConfigureAwait(false);
            totalEmbeddings += semanticResult.EmbeddingsGenerated;
            totalEdges += semanticResult.EdgesCreated;
            allErrors.AddRange(semanticResult.Errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "SemanticIndexer failed in incremental mode");
            allErrors.Add($"SemanticIndexer failed: {ex.Message}");
        }

        // Post-stage normalization — stamp MemoryLayer on all edges
        this.StampEdgeMetadata(data);

        // Persist to graph store — writes are critical per architecture §5.1
        if (this.graphStore != null)
        {
            var version = VersionRange.FromOptions(options);
            await this.graphStore.UpsertNodesAsync(data.Nodes.Values, version, ct).ConfigureAwait(false);
            await this.graphStore.UpsertEdgesAsync(data.Hyperedges, ct).ConfigureAwait(false);
            await this.graphStore.LogAuditAsync(
                "extraction-pipeline",
                "ExtractFilesAsync",
                new { ChangedFiles = relativePaths.Count, NodesCount = data.Nodes.Count },
                ct).ConfigureAwait(false);
        }

        return this.BuildResult(totalNodes, totalEdges, totalEmbeddings, filesSkipped, allErrors);
    }

    private HypergraphData ParseRepository(
        string repoRoot,
        ExtractionOptions options,
        List<string> errors,
        ref int filesSkipped)
    {
        var data = new HypergraphData();
        var scopeRoot = repoRoot;
        if (!string.IsNullOrEmpty(options.ScopeSubtree))
        {
            scopeRoot = Path.Combine(repoRoot, options.ScopeSubtree.Replace('/', Path.DirectorySeparatorChar));
        }

        if (!Directory.Exists(scopeRoot))
        {
            throw new DirectoryNotFoundException($"Scope root not found: {scopeRoot}");
        }

        // Collect source files by language
        var sourceFiles = new List<(string fullPath, string relPath, string language)>();

        if (options.Languages.Any(l => l.Equals("C#", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var file in EnumerateFiles(scopeRoot, "*.cs"))
            {
                var relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

                // Skip test projects unless explicitly included
                if (!options.IncludeTestProjects && IsTestFilePath(relPath))
                {
                    continue;
                }

                // Skip obj/bin directories
                if (IsGeneratedPath(relPath))
                {
                    continue;
                }

                sourceFiles.Add((file, relPath, "C#"));
            }
        }

        if (options.Languages.Any(l => l.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)))
        {
            var psExtensions = new[] { "*.ps1", "*.psm1", "*.psd1" };
            foreach (var ext in psExtensions)
            {
                foreach (var file in EnumerateFiles(scopeRoot, ext))
                {
                    var relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    if (!options.IncludeTestProjects && IsTestFilePath(relPath))
                    {
                        continue;
                    }

                    if (IsGeneratedPath(relPath))
                    {
                        continue;
                    }

                    sourceFiles.Add((file, relPath, "PowerShell"));
                }
            }
        }

        // Parse each file
        foreach (var (fullPath, relPath, language) in sourceFiles)
        {
            try
            {
                this.ParseSingleFile(fullPath, relPath, data, options);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to parse {Path}", relPath);
                errors.Add($"Parse error in {relPath}: {ex.Message}");
                filesSkipped++;
            }
        }

        return data;
    }

    private void ParseSingleFile(
        string fullPath,
        string relPath,
        HypergraphData data,
        ExtractionOptions options)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        // Create file node (NodeType "Module" per architecture node kinds)
        var fileId = CanonicalId.ForFile(relPath);
        if (!data.Nodes.ContainsKey(fileId))
        {
            data.Nodes[fileId] = new HypergraphNode
            {
                Id = fileId,
                Name = Path.GetFileName(fullPath),
                NodeType = "Module",
                Language = ext == ".cs" ? "C#" : "PowerShell",
                FilePath = relPath,
            };
        }

        if (ext == ".cs")
        {
            var source = File.ReadAllText(fullPath);
            var extractor = new CSharpHyperedgeExtractor(relPath);
            var result = extractor.Extract(source);
            var fileData = new HypergraphData { Nodes = result.Nodes, Hyperedges = result.Hyperedges };

            this.MergeData(data, fileData);
        }
        else if (ext is ".ps1" or ".psm1" or ".psd1")
        {
            var source = File.ReadAllText(fullPath);
            var extractor = new PowerShellHyperedgeExtractor(relPath);
            var result = extractor.Extract(source);
            var fileData = new HypergraphData { Nodes = result.Nodes, Hyperedges = result.Hyperedges };

            this.MergeData(data, fileData);
        }
    }

    private void MergeData(HypergraphData target, HypergraphData source)
    {
        foreach (var kvp in source.Nodes)
        {
            target.Nodes.TryAdd(kvp.Key, kvp.Value);
        }

        target.Hyperedges.AddRange(source.Hyperedges);
    }

    /// <summary>
    /// Scans PowerShell source files for .NET type references (e.g., [MyApp.Services.PaymentService]::new())
    /// by reading the actual source text and matching qualified type patterns via regex.
    /// Synthesizes cross-language reference nodes + CALLS edges so FactNormalizer can merge
    /// them with C# class definitions by QualifiedName.
    /// Returns any errors encountered reading source files.
    /// </summary>
    private List<string> SynthesizeCrossLanguageTypeReferences(HypergraphData data, string repoRoot)
    {
        var errors = new List<string>();
        // Collect all C# class/interface definitions for matching
        var csharpDefinitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in data.Nodes)
        {
            var node = kvp.Value;
            if (!string.Equals(node.Language, "C#", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(node.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (node.NodeType.Equals("Class", StringComparison.OrdinalIgnoreCase)
                || node.NodeType.Equals("Interface", StringComparison.OrdinalIgnoreCase))
            {
                // Index by fully qualified name (scope.name) — use raw scope
                if (!string.IsNullOrEmpty(node.Scope))
                {
                    var fqn = $"{node.Scope}.{node.Name}";
                    csharpDefinitions.TryAdd(fqn, kvp.Key);

                    // Also index by normalized qualified name for cross-language matching.
                    // C# extractor may produce doubled scopes like "MyApp.Services.MyApp.Services.PaymentService",
                    // but PS type references use the actual namespace: "MyApp.Services.PaymentService".
                    var normalizedScope = NormalizeScopeForMatching(node.Scope, node.Name, node.FilePath);
                    if (!string.IsNullOrEmpty(normalizedScope))
                    {
                        var normalizedFqn = $"{normalizedScope}.{node.Name}";
                        csharpDefinitions.TryAdd(normalizedFqn, kvp.Key);
                    }
                }

                // Also index by short name
                csharpDefinitions.TryAdd(node.Name, kvp.Key);
            }
        }

        if (csharpDefinitions.Count == 0)
        {
            return errors;
        }

        // Find all PowerShell file nodes and read their source to find .NET type references
        var psFileNodes = data.Nodes.Values
            .Where(n => (string.Equals(n.NodeType, "File", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n.NodeType, "Module", StringComparison.OrdinalIgnoreCase))
                && (n.Language.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
                    || n.Language.Equals("powershell", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Regex patterns for .NET type references in PowerShell:
        // 1. Dotted type refs: [Namespace.Type] or [Namespace.Sub.Type]
        var dottedTypeRefPattern = new Regex(
            @"\[(\w+(?:\.\w+)+)\]",
            RegexOptions.Compiled);

        // 2. Simple type refs: [TypeName]::new() or [TypeName]::Method()
        //    Only match PascalCase names to avoid built-in types like [string], [int]
        var simpleTypeRefPattern = new Regex(
            @"\[([A-Z][a-zA-Z0-9]+)\]::(?:new|[A-Z])",
            RegexOptions.Compiled);

        // 3. New-Object calls: New-Object TypeName or New-Object Namespace.Type
        var newObjectPattern = new Regex(
            @"New-Object\s+(?:-TypeName\s+)?(\w+(?:\.\w+)*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Index PS function nodes by file for caller resolution
        var psFunctionsByFile = data.Nodes.Values
            .Where(n => n.NodeType.Equals("function", StringComparison.OrdinalIgnoreCase)
                || n.NodeType.Equals("Function", StringComparison.OrdinalIgnoreCase))
            .GroupBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var psFileNode in psFileNodes)
        {
            var relPath = psFileNode.FilePath;
            if (string.IsNullOrEmpty(relPath))
            {
                continue;
            }

            var fullPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            string sourceText;
            try
            {
                if (!File.Exists(fullPath))
                {
                    this.logger.LogDebug("PowerShell source file not found: {Path}", fullPath);
                    continue;
                }

                sourceText = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to read PowerShell source {Path} for cross-language analysis", fullPath);
                errors.Add($"Cross-language source read failed for {relPath}: {ex.Message}");
                continue;
            }

            var allTypeRefMatches = new List<(string typeName, int position)>();

            foreach (Match match in dottedTypeRefPattern.Matches(sourceText))
            {
                allTypeRefMatches.Add((match.Groups[1].Value, match.Index));
            }

            foreach (Match match in simpleTypeRefPattern.Matches(sourceText))
            {
                allTypeRefMatches.Add((match.Groups[1].Value, match.Index));
            }

            foreach (Match match in newObjectPattern.Matches(sourceText))
            {
                allTypeRefMatches.Add((match.Groups[1].Value, match.Index));
            }

            // Deduplicate matches by type name per file
            var seenTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (typeName, matchPosition) in allTypeRefMatches)
            {
                if (!seenTypeNames.Add(typeName))
                {
                    continue;
                }

                // Check against C# definitions
                if (!csharpDefinitions.TryGetValue(typeName, out var csNodeId))
                {
                    continue;
                }

                var shortName = typeName.Contains('.')
                    ? typeName[(typeName.LastIndexOf('.') + 1)..]
                    : typeName;
                var namespacePart = typeName.Contains('.')
                    ? typeName[..typeName.LastIndexOf('.')]
                    : string.Empty;

                // Create a PS reference node for this type reference
                var refNodeId = $"ps-ref:{relPath}:{typeName}";
                if (!data.Nodes.ContainsKey(refNodeId))
                {
                    // Determine the line number of this reference for function containment
                    var lineNumber = sourceText[..matchPosition].Count(c => c == '\n') + 1;

                    // Resolve the actual QualifiedName from the C# definition
                    // so FactNormalizer can merge them by QualifiedName.
                    // Use NormalizeScopeForMatching to handle the C# extractor's doubled
                    // scope stack (e.g., "PaymentService.MyApp.Services" → "MyApp.Services").
                    var resolvedQualifiedName = typeName;
                    if (data.Nodes.TryGetValue(csNodeId, out var csDefNode))
                    {
                        if (csDefNode.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn)
                            && qn is string qnStr && !string.IsNullOrEmpty(qnStr))
                        {
                            resolvedQualifiedName = qnStr;
                        }
                        else if (!string.IsNullOrEmpty(csDefNode.Scope))
                        {
                            var normalizedScope = NormalizeScopeForMatching(
                                csDefNode.Scope, csDefNode.Name, csDefNode.FilePath);
                            if (!string.IsNullOrEmpty(normalizedScope))
                            {
                                resolvedQualifiedName = $"{normalizedScope}.{csDefNode.Name}";
                            }
                            else
                            {
                                resolvedQualifiedName = csDefNode.Name;
                            }
                        }
                    }

                    var resolvedShortName = resolvedQualifiedName.Contains('.')
                        ? resolvedQualifiedName[(resolvedQualifiedName.LastIndexOf('.') + 1)..]
                        : resolvedQualifiedName;
                    var resolvedNamespace = resolvedQualifiedName.Contains('.')
                        ? resolvedQualifiedName[..resolvedQualifiedName.LastIndexOf('.')]
                        : string.Empty;

                    var refNode = new HypergraphNode
                    {
                        Id = refNodeId,
                        Name = resolvedShortName,
                        NodeType = "Class",
                        Language = "PowerShell",
                        FilePath = relPath,
                        Scope = resolvedNamespace,
                        LineStart = lineNumber,
                        LineEnd = lineNumber,
                    };
                    refNode.Metadata[MemoryMetadataKeys.QualifiedName] = resolvedQualifiedName;
                    data.Nodes[refNodeId] = refNode;

                    // Find the enclosing PS function by line containment
                    HypergraphNode? callerFunc = null;
                    if (psFunctionsByFile.TryGetValue(relPath, out var funcs))
                    {
                        callerFunc = funcs
                            .Where(f => f.LineStart <= lineNumber && f.LineEnd >= lineNumber)
                            .OrderBy(f => f.LineEnd - f.LineStart)
                            .FirstOrDefault();

                        // Fallback: first function in the file
                        callerFunc ??= funcs.FirstOrDefault();
                    }

                    if (callerFunc != null)
                    {
                        var edgeId = $"cross-lang:{callerFunc.Id}→{refNodeId}";
                        data.Hyperedges.Add(new Hyperedge
                        {
                            Id = edgeId,
                            EdgeType = HyperedgeType.CrossLanguageMethodCall,
                            NodeIds = new List<string> { callerFunc.Id, refNodeId },
                            Roles = new Dictionary<string, string>
                            {
                                { callerFunc.Id, "caller" },
                                { refNodeId, "callee" },
                            },
                            Languages = new List<string> { "PowerShell", "C#" },
                            Metadata = new Dictionary<string, object?>
                            {
                                { MemoryMetadataKeys.SemanticRelation, "CALLS" },
                                { MemoryMetadataKeys.MemoryLayer, "call_chain" },
                            },
                        });
                    }
                }
            }
        }

        return errors;
    }

    private ExtractionResult BuildResult(
        int nodesCreated,
        int edgesCreated,
        int embeddingsGenerated,
        int filesSkipped,
        List<string> errors)
    {
        return new ExtractionResult
        {
            NodesCreated = nodesCreated,
            EdgesCreated = edgesCreated,
            EmbeddingsGenerated = embeddingsGenerated,
            FilesSkipped = filesSkipped,
            Errors = errors.Where(e => !string.IsNullOrEmpty(e)).ToList(),
        };
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsTestFilePath(string relPath)
    {
        return relPath.Contains("Test", StringComparison.OrdinalIgnoreCase)
            && (relPath.Contains("UnitTest", StringComparison.OrdinalIgnoreCase)
                || relPath.Contains("IntegrationTest", StringComparison.OrdinalIgnoreCase)
                || relPath.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
                || relPath.Contains(".UnitTests", StringComparison.OrdinalIgnoreCase));
    }

    private void StampCommitSha(HypergraphData data, ExtractionOptions options)
    {
        if (string.IsNullOrEmpty(options.CommitSha))
        {
            return;
        }

        foreach (var node in data.Nodes.Values)
        {
            node.Metadata["CommitSha"] = options.CommitSha;
        }
    }

    private static bool IsGeneratedPath(string relPath)
    {
        return relPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relPath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
            || relPath.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stamps MemoryLayer, SemanticRelation, and OriginalEdgeType metadata on ALL edges.
    /// Called after all pipeline stages so that edges added by StructureBuilder,
    /// CallChainAnalyzer, and SemanticIndexer consistently carry the required metadata
    /// per architecture.md graph invariants.
    /// Also enforces binary edge invariant: only edges with exactly 2 distinct endpoints
    /// are retained. Raw parser hyperedges with >2 nodes are dropped.
    /// </summary>
    private void StampEdgeMetadata(HypergraphData data)
    {
        // Filter to only binary edges (exactly 2 distinct endpoints) per architecture invariant
        var binaryEdges = new List<Hyperedge>();
        foreach (var edge in data.Hyperedges)
        {
            var distinctEndpoints = edge.NodeIds.Distinct().Count();
            if (distinctEndpoints == 2)
            {
                binaryEdges.Add(edge);
            }
            else
            {
                this.logger.LogDebug(
                    "Dropping non-binary edge {EdgeId} ({EdgeType}) with {Count} distinct endpoints",
                    edge.Id, edge.EdgeType, distinctEndpoints);
            }
        }

        data.Hyperedges.Clear();
        data.Hyperedges.AddRange(binaryEdges);

        foreach (var edge in data.Hyperedges)
        {
            if (!edge.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
            {
                edge.Metadata[MemoryMetadataKeys.MemoryLayer] = FactNormalizer.InferMemoryLayer(edge.EdgeType);
            }

            if (!edge.Metadata.ContainsKey(MemoryMetadataKeys.SemanticRelation))
            {
                edge.Metadata[MemoryMetadataKeys.SemanticRelation] = FactNormalizer.InferSemanticRelation(edge.EdgeType);
            }

            if (!edge.Metadata.ContainsKey(MemoryMetadataKeys.OriginalEdgeType))
            {
                edge.Metadata[MemoryMetadataKeys.OriginalEdgeType] = edge.EdgeType.ToString();
            }
        }

        // Also stamp MemoryLayer on all nodes if not present
        foreach (var node in data.Nodes.Values)
        {
            if (!node.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
            {
                node.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
            }
        }
    }

    /// <summary>
    /// Normalizes a C# extractor scope for cross-language matching.
    /// The C# extractor scope stack may include the file stem and doubled namespace
    /// segments. This method strips the file stem and node name, then deduplicates
    /// repeated namespace segments to produce the actual .NET namespace.
    /// </summary>
    private static string NormalizeScopeForMatching(string scope, string nodeName, string filePath)
    {
        if (string.IsNullOrEmpty(scope))
        {
            return scope;
        }

        var parts = scope.Split('.');
        var fileStem = !string.IsNullOrEmpty(filePath)
            ? Path.GetFileNameWithoutExtension(filePath)
            : null;

        var cleanedParts = new List<string>();
        foreach (var part in parts)
        {
            if (fileStem != null && string.Equals(part, fileStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(part, nodeName, StringComparison.OrdinalIgnoreCase))
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
        for (var len = 1; len <= cleanedParts.Count / 2; len++)
        {
            if (cleanedParts.Count % len != 0)
            {
                continue;
            }

            var isRepeating = true;
            for (var i = len; i < cleanedParts.Count; i++)
            {
                if (!string.Equals(cleanedParts[i], cleanedParts[i % len], StringComparison.OrdinalIgnoreCase))
                {
                    isRepeating = false;
                    break;
                }
            }

            if (isRepeating)
            {
                return string.Join(".", cleanedParts.Take(len));
            }
        }

        return string.Join(".", cleanedParts);
    }
}