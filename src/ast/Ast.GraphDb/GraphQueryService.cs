// -----------------------------------------------------------------------
// <copyright file="GraphQueryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Ast.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// High-level service for querying code graphs.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="GraphQueryService"/> class.
/// </remarks>
/// <param name="graphStore">The graph store.</param>
/// <param name="logger">The logger.</param>
public class GraphQueryService(IGraphStore graphStore, ILogger<GraphQueryService> logger)
{
    private readonly IGraphStore graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
    private readonly ILogger<GraphQueryService> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Indexes a code hypergraph into the graph store.
    /// </summary>
    /// <param name="hypergraph">The hypergraph data to index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of nodes and edges indexed.</returns>
    public async Task<(int NodesIndexed, int EdgesIndexed)> IndexHypergraphAsync(
        HypergraphData hypergraph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hypergraph);

        this.logger.LogInformation(
            "Indexing hypergraph with {NodeCount} nodes and {EdgeCount} edges",
            hypergraph.Nodes.Count,
            hypergraph.Hyperedges.Count);

        var nodesIndexed = await this.graphStore.CreateNodesAsync(hypergraph.Nodes.Values, cancellationToken);
        var edgesIndexed = await this.graphStore.CreateEdgesAsync(hypergraph.Hyperedges, cancellationToken);

        this.logger.LogInformation(
            "Indexed {NodesIndexed} nodes and {EdgesIndexed} edges",
            nodesIndexed,
            edgesIndexed);

        return (nodesIndexed, edgesIndexed);
    }

    /// <summary>
    /// Finds all classes that implement a given interface.
    /// </summary>
    /// <param name="interfaceName">The interface name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Implementing classes.</returns>
    public async Task<IReadOnlyList<HypergraphNode>> FindImplementationsAsync(
        string interfaceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(interfaceName);

        // Find the interface node
        var interfaces = await this.graphStore.GetNodesAsync(
            new NodeFilter
            {
                NodeTypes = new[] { NodeLabels.Interface },
                NamePattern = interfaceName
            },
            cancellationToken: cancellationToken);

        if (interfaces.Count == 0)
        {
            this.logger.LogWarning("Interface not found: {InterfaceName}", interfaceName);
            return Array.Empty<HypergraphNode>();
        }

        var implementations = new List<HypergraphNode>();
        foreach (var iface in interfaces)
        {
            var callers = await this.graphStore.GetCallersAsync(iface.Id, depth: 1, cancellationToken);
            implementations.AddRange(
                callers
                    .Where(c => c.RelationshipType == HyperedgeType.Implements)
                    .Select(c => c.Node));
        }

        return implementations;
    }

    /// <summary>
    /// Finds all classes that extend a given class.
    /// </summary>
    /// <param name="className">The base class name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Derived classes.</returns>
    public async Task<IReadOnlyList<HypergraphNode>> FindDerivedClassesAsync(
        string className,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);

        var classes = await this.graphStore.GetNodesAsync(
            new NodeFilter
            {
                NodeTypes = new[] { NodeLabels.Class },
                NamePattern = className
            },
            cancellationToken: cancellationToken);

        if (classes.Count == 0)
        {
            this.logger.LogWarning("Class not found: {ClassName}", className);
            return Array.Empty<HypergraphNode>();
        }

        var derivedClasses = new List<HypergraphNode>();
        foreach (var baseClass in classes)
        {
            var callers = await this.graphStore.GetCallersAsync(baseClass.Id, depth: 1, cancellationToken);
            derivedClasses.AddRange(
                callers
                    .Where(c => c.RelationshipType == HyperedgeType.Extends)
                    .Select(c => c.Node));
        }

        return derivedClasses;
    }

    /// <summary>
    /// Finds the call chain between two methods.
    /// </summary>
    /// <param name="sourceMethodName">The source method name.</param>
    /// <param name="targetMethodName">The target method name.</param>
    /// <param name="maxDepth">Maximum depth to search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paths from source to target.</returns>
    public async Task<IReadOnlyList<GraphPath>> FindCallChainAsync(
        string sourceMethodName,
        string targetMethodName,
        int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceMethodName);
        ArgumentException.ThrowIfNullOrEmpty(targetMethodName);

        var sourceMethods = await this.graphStore.GetNodesAsync(
            new NodeFilter
            {
                NodeTypes = new[] { NodeLabels.Method, NodeLabels.Function },
                NamePattern = sourceMethodName
            },
            cancellationToken: cancellationToken);

        var targetMethods = await this.graphStore.GetNodesAsync(
            new NodeFilter
            {
                NodeTypes = new[] { NodeLabels.Method, NodeLabels.Function },
                NamePattern = targetMethodName
            },
            cancellationToken: cancellationToken);

        if (sourceMethods.Count == 0 || targetMethods.Count == 0)
        {
            this.logger.LogWarning(
                "Source or target method not found: {Source} -> {Target}",
                sourceMethodName,
                targetMethodName);
            return Array.Empty<GraphPath>();
        }

        var allPaths = new List<GraphPath>();
        foreach (var source in sourceMethods)
        {
            foreach (var target in targetMethods)
            {
                var paths = await this.graphStore.FindPathsAsync(
                    source.Id,
                    target.Id,
                    maxDepth,
                    cancellationToken);
                allPaths.AddRange(paths);
            }
        }

        return allPaths;
    }

    /// <summary>
    /// Gets all methods called by a given method (direct and transitive).
    /// </summary>
    /// <param name="methodName">The method name.</param>
    /// <param name="depth">How deep to traverse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Called methods with their depth.</returns>
    public async Task<IReadOnlyList<NodeWithDepth>> GetMethodDependenciesAsync(
        string methodName,
        int depth = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodName);

        var methods = await this.graphStore.GetNodesAsync(
            new NodeFilter
            {
                NodeTypes = new[] { NodeLabels.Method, NodeLabels.Function },
                NamePattern = methodName
            },
            cancellationToken: cancellationToken);

        if (methods.Count == 0)
        {
            this.logger.LogWarning("Method not found: {MethodName}", methodName);
            return Array.Empty<NodeWithDepth>();
        }

        var allDependencies = new List<NodeWithDepth>();
        foreach (var method in methods)
        {
            var callees = await this.graphStore.GetCalleesAsync(method.Id, depth, cancellationToken);
            allDependencies.AddRange(callees);
        }

        return allDependencies;
    }

    /// <summary>
    /// Gets all methods that call a given method (direct and transitive callers).
    /// </summary>
    /// <param name="methodName">The method name.</param>
    /// <param name="depth">How deep to traverse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Calling methods with their depth.</returns>
    public async Task<IReadOnlyList<NodeWithDepth>> GetMethodCallersAsync(
        string methodName,
        int depth = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodName);

        var methods = await this.graphStore.GetNodesAsync(
            new NodeFilter
            {
                NodeTypes = new[] { NodeLabels.Method, NodeLabels.Function },
                NamePattern = methodName
            },
            cancellationToken: cancellationToken);

        if (methods.Count == 0)
        {
            this.logger.LogWarning("Method not found: {MethodName}", methodName);
            return Array.Empty<NodeWithDepth>();
        }

        var allCallers = new List<NodeWithDepth>();
        foreach (var method in methods)
        {
            var callers = await this.graphStore.GetCallersAsync(method.Id, depth, cancellationToken);
            allCallers.AddRange(callers);
        }

        return allCallers;
    }

    /// <summary>
    /// Analyzes the impact of changing a code element.
    /// </summary>
    /// <param name="elementName">The name of the element to analyze.</param>
    /// <param name="maxDepth">Maximum depth for impact analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Impact analysis result, or null if element not found.</returns>
    public async Task<ImpactAnalysis?> AnalyzeChangeImpactAsync(
        string elementName,
        int maxDepth = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(elementName);

        var nodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { NamePattern = elementName },
            limit: 1,
            cancellationToken: cancellationToken);

        if (nodes.Count == 0)
        {
            this.logger.LogWarning("Element not found: {ElementName}", elementName);
            return null;
        }

        return await this.graphStore.AnalyzeImpactAsync(nodes[0].Id, maxDepth, cancellationToken);
    }

    /// <summary>
    /// Gets nodes in a specific file.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nodes in the file.</returns>
    public Task<IReadOnlyList<HypergraphNode>> GetNodesInFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        return this.graphStore.GetNodesAsync(
            new NodeFilter { FilePath = filePath },
            limit: 1000,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets nodes by type.
    /// </summary>
    /// <param name="nodeType">The node type (e.g., "Class", "Method").</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nodes of the specified type.</returns>
    public Task<IReadOnlyList<HypergraphNode>> GetNodesByTypeAsync(
        string nodeType,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeType);

        return this.graphStore.GetNodesAsync(
            new NodeFilter { NodeTypes = new[] { nodeType } },
            limit: limit,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets cross-language relationships.
    /// </summary>
    /// <param name="sourceLanguage">The source language.</param>
    /// <param name="targetLanguage">The target language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nodes that have cross-language relationships.</returns>
    public async Task<IReadOnlyList<(HypergraphNode Source, HypergraphNode Target, HyperedgeType EdgeType)>> GetCrossLanguageRelationshipsAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceLanguage);
        ArgumentException.ThrowIfNullOrEmpty(targetLanguage);

        var sourceNodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { Language = sourceLanguage },
            limit: 1000,
            cancellationToken: cancellationToken);

        var results = new List<(HypergraphNode, HypergraphNode, HyperedgeType)>();

        foreach (var sourceNode in sourceNodes)
        {
            var edges = await this.graphStore.GetEdgesForNodeAsync(sourceNode.Id, cancellationToken: cancellationToken);
            foreach (var edge in edges)
            {
                // Find target nodes in the other language (skip the source node itself)
                foreach (var targetId in edge.NodeIds.Where(id => id != sourceNode.Id))
                {
                    var targetNode = await this.graphStore.GetNodeAsync(targetId, cancellationToken);
                    if (targetNode != null && targetNode.Language == targetLanguage)
                    {
                        results.Add((sourceNode, targetNode, edge.EdgeType));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Gets graph statistics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph statistics.</returns>
    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return this.graphStore.GetStatisticsAsync(cancellationToken);
    }

    /// <summary>
    /// Ensures indexes are created for optimal performance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Creating graph indexes");
        await this.graphStore.CreateIndexesAsync(cancellationToken);
        this.logger.LogInformation("Graph indexes created");
    }

    /// <summary>
    /// Clears all data from the graph store.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        this.logger.LogWarning("Clearing all data from graph store");
        await this.graphStore.ClearAsync(cancellationToken);
        this.logger.LogInformation("Graph store cleared");
    }
}