// -----------------------------------------------------------------------
// <copyright file="CodeQueryBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query;

using Ast.Core;
using Ast.GraphDb;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implementation of the fluent code query builder.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CodeQueryBuilder"/> class.
/// </remarks>
public class CodeQueryBuilder(
    IGraphStore graphStore,
    ILogger<CodeQueryBuilder> logger,
    IVectorStore? vectorStore = null) : ICodeQuery
{
    private readonly IGraphStore graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
    private readonly IVectorStore? vectorStore = vectorStore;
    private readonly ILogger<CodeQueryBuilder> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public INodeQuery Nodes() => new NodeQueryBuilder(this.graphStore, this.logger);

    /// <inheritdoc/>
    public ISingleNodeQuery Node(string nodeId) => new SingleNodeQueryBuilder(this.graphStore, this.vectorStore, this.logger, nodeId, byName: false);

    /// <inheritdoc/>
    public ISingleNodeQuery NodeByName(string name) => new SingleNodeQueryBuilder(this.graphStore, this.vectorStore, this.logger, name, byName: true);

    /// <inheritdoc/>
    public ISemanticQuery Search(string searchText)
    {
        if (this.vectorStore == null)
        {
            throw new InvalidOperationException("Vector store not configured. Cannot perform semantic search.");
        }
        return new SemanticQueryBuilder(this.graphStore, this.vectorStore, this.logger, searchText);
    }

    /// <inheritdoc/>
    public IPathQuery PathsBetween(string fromNodeId, string toNodeId) =>
        new PathQueryBuilder(this.graphStore, this.logger, fromNodeId, toNodeId, byName: false);

    /// <inheritdoc/>
    public IPathQuery PathsBetweenNames(string fromName, string toName) =>
        new PathQueryBuilder(this.graphStore, this.logger, fromName, toName, byName: true);
}

/// <summary>
/// Node query builder implementation.
/// </summary>
internal class NodeQueryBuilder(IGraphStore graphStore, ILogger logger) : INodeQuery
{
    private readonly IGraphStore graphStore = graphStore;
    private readonly ILogger logger = logger;
    private readonly NodeFilter filter = new();
    private int limit = 100;
    private int skip = 0;
    private readonly List<string> nodeTypes = [];

    public INodeQuery OfLanguage(string language)
    {
        this.filter.Language = language;
        return this;
    }

    public INodeQuery OfType(string nodeType)
    {
        this.nodeTypes.Add(nodeType);
        return this;
    }

    public INodeQuery OfTypes(params string[] nodeTypes)
    {
        this.nodeTypes.AddRange(nodeTypes);
        return this;
    }

    public INodeQuery InFile(string filePath)
    {
        this.filter.FilePath = filePath;
        return this;
    }

    public INodeQuery InFiles(string filePattern)
    {
        this.filter.FilePath = filePattern;
        return this;
    }

    public INodeQuery WithName(string namePattern)
    {
        this.filter.NamePattern = namePattern;
        return this;
    }

    public INodeQuery InScope(string scope)
    {
        this.filter.Scope = scope;
        return this;
    }

    public INodeQuery WithModifier(string modifier)
    {
        // Note: This would require extending NodeFilter
        return this;
    }

    public INodeQuery Classes() => this.OfType(NodeLabels.Class);
    public INodeQuery Interfaces() => this.OfType(NodeLabels.Interface);
    public INodeQuery Methods() => this.OfType(NodeLabels.Method);
    public INodeQuery Properties() => this.OfType(NodeLabels.Property);
    public INodeQuery Functions() => this.OfType(NodeLabels.Function);

    public INodeQuery Take(int count)
    {
        this.limit = count;
        return this;
    }

    public INodeQuery Skip(int count)
    {
        this.skip = count;
        return this;
    }

    public async Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await this.ToListAsync(cancellationToken);
        return new CodeQueryResult { Nodes = [.. nodes] };
    }

    public async Task<IReadOnlyList<HypergraphNode>> ToListAsync(CancellationToken cancellationToken = default)
    {
        if (this.nodeTypes.Count > 0)
        {
            this.filter.NodeTypes = this.nodeTypes;
        }

        var nodes = await this.graphStore.GetNodesAsync(this.filter, this.limit + this.skip, cancellationToken);

        if (this.skip > 0)
        {
            return nodes.Skip(this.skip).Take(this.limit).ToList();
        }

        return nodes;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await this.ToListAsync(cancellationToken);
        return nodes.Count;
    }
}

/// <summary>
/// Single node query builder implementation.
/// </summary>
internal class SingleNodeQueryBuilder(
    IGraphStore graphStore,
    IVectorStore? vectorStore,
    ILogger logger,
    string identifier,
    bool byName) : ISingleNodeQuery
{
    private readonly IGraphStore graphStore = graphStore;
    private readonly IVectorStore? vectorStore = vectorStore;
    private readonly ILogger logger = logger;
    private readonly string identifier = identifier;
    private readonly bool byName = byName;

    public async Task<HypergraphNode?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (this.byName)
        {
            var nodes = await this.graphStore.GetNodesAsync(
                new NodeFilter { NamePattern = this.identifier },
                limit: 1,
                cancellationToken);
            return nodes.FirstOrDefault();
        }

        return await this.graphStore.GetNodeAsync(this.identifier, cancellationToken);
    }

    private async Task<string?> ResolveNodeIdAsync(CancellationToken cancellationToken)
    {
        if (!this.byName)
        {
            return this.identifier;
        }

        var node = await this.GetAsync(cancellationToken);
        return node?.Id;
    }

    public ITraversalQuery Callers() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Incoming, HyperedgeType.MethodInvocation);
    public ITraversalQuery Callees() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Outgoing, HyperedgeType.MethodInvocation);
    public ITraversalQuery Dependents() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Incoming, null);
    public ITraversalQuery Dependencies() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Outgoing, null);
    public ITraversalQuery Implementations() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Incoming, HyperedgeType.Implements);
    public ITraversalQuery DerivedClasses() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Incoming, HyperedgeType.Extends);
    public ITraversalQuery BaseTypes() => new TraversalQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName, TraversalDirection.Outgoing, HyperedgeType.Extends);
    public IImpactQuery Impact() => new ImpactQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName);
    public IEdgeQuery Edges() => new EdgeQueryBuilder(this.graphStore, this.logger, this.identifier, this.byName);
}

/// <summary>
/// Traversal query builder implementation.
/// </summary>
internal class TraversalQueryBuilder : ITraversalQuery
{
    private readonly IGraphStore graphStore;
    private readonly ILogger logger;
    private readonly string identifier;
    private readonly bool byName;
    private readonly TraversalDirection direction;
    private readonly List<HyperedgeType> edgeTypes = [];
    private int depth = 1;
    private string? language;
    private bool includeCrossLanguage;
    private int limit = 100;

    public TraversalQueryBuilder(
        IGraphStore graphStore,
        ILogger logger,
        string identifier,
        bool byName,
        TraversalDirection direction,
        HyperedgeType? defaultEdgeType)
    {
        this.graphStore = graphStore;
        this.logger = logger;
        this.identifier = identifier;
        this.byName = byName;
        this.direction = direction;
        if (defaultEdgeType.HasValue)
        {
            this.edgeTypes.Add(defaultEdgeType.Value);
        }
    }

    public ITraversalQuery WithDepth(int depth)
    {
        this.depth = depth;
        return this;
    }

    public ITraversalQuery ViaEdgeType(HyperedgeType edgeType)
    {
        this.edgeTypes.Clear();
        this.edgeTypes.Add(edgeType);
        return this;
    }

    public ITraversalQuery ViaEdgeTypes(params HyperedgeType[] edgeTypes)
    {
        this.edgeTypes.Clear();
        this.edgeTypes.AddRange(edgeTypes);
        return this;
    }

    public ITraversalQuery OfLanguage(string language)
    {
        this.language = language;
        return this;
    }

    public ITraversalQuery IncludeCrossLanguage()
    {
        this.includeCrossLanguage = true;
        return this;
    }

    public ITraversalQuery Take(int count)
    {
        this.limit = count;
        return this;
    }

    public async Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var nodesWithDepth = await this.ToListAsync(cancellationToken);
        return new CodeQueryResult
        {
            Nodes = [.. nodesWithDepth.Select(n => n.Node)]
        };
    }

    public async Task<IReadOnlyList<NodeWithDepth>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var nodeId = await this.ResolveNodeIdAsync(cancellationToken);
        if (nodeId == null)
        {
            return Array.Empty<NodeWithDepth>();
        }

        IReadOnlyList<NodeWithDepth> results = this.direction switch
        {
            TraversalDirection.Incoming => await this.graphStore.GetCallersAsync(nodeId, this.depth, cancellationToken),
            TraversalDirection.Outgoing => await this.graphStore.GetCalleesAsync(nodeId, this.depth, cancellationToken),
            _ => (await this.graphStore.GetCallersAsync(nodeId, this.depth, cancellationToken))
                .Concat(await this.graphStore.GetCalleesAsync(nodeId, this.depth, cancellationToken))
                .ToList()
        };

        // Apply filters
        if (this.edgeTypes.Count > 0)
        {
            results = results.Where(r => this.edgeTypes.Contains(r.RelationshipType)).ToList();
        }

        if (!string.IsNullOrEmpty(this.language))
        {
            results = results.Where(r => r.Node.Language == this.language).ToList();
        }

        if (!this.includeCrossLanguage)
        {
            var sourceNode = await this.graphStore.GetNodeAsync(nodeId, cancellationToken);
            if (sourceNode != null)
            {
                results = results.Where(r => r.Node.Language == sourceNode.Language).ToList();
            }
        }

        return results.Take(this.limit).ToList();
    }

    private async Task<string?> ResolveNodeIdAsync(CancellationToken cancellationToken)
    {
        if (!this.byName)
        {
            return this.identifier;
        }

        var nodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { NamePattern = this.identifier },
            limit: 1,
            cancellationToken);
        return nodes.FirstOrDefault()?.Id;
    }
}

/// <summary>
/// Impact query builder implementation.
/// </summary>
internal class ImpactQueryBuilder(IGraphStore graphStore, ILogger logger, string identifier, bool byName) : IImpactQuery
{
    private readonly IGraphStore graphStore = graphStore;
    private readonly ILogger logger = logger;
    private readonly string identifier = identifier;
    private readonly bool byName = byName;
    private int depth = 3;
    private bool directOnly;

    public IImpactQuery WithDepth(int depth)
    {
        this.depth = depth;
        return this;
    }

    public IImpactQuery DirectOnly()
    {
        this.directOnly = true;
        this.depth = 1;
        return this;
    }

    public async Task<ImpactAnalysis> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var nodeId = await this.ResolveNodeIdAsync(cancellationToken) ?? throw new InvalidOperationException($"Node not found: {this.identifier}");
        var impact = await this.graphStore.AnalyzeImpactAsync(nodeId, this.depth, cancellationToken);

        if (this.directOnly)
        {
            impact.IndirectlyAffected.Clear();
        }

        return impact;
    }

    private async Task<string?> ResolveNodeIdAsync(CancellationToken cancellationToken)
    {
        if (!this.byName)
        {
            return this.identifier;
        }

        var nodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { NamePattern = this.identifier },
            limit: 1,
            cancellationToken);
        return nodes.FirstOrDefault()?.Id;
    }
}

/// <summary>
/// Edge query builder implementation.
/// </summary>
internal class EdgeQueryBuilder(IGraphStore graphStore, ILogger logger, string identifier, bool byName) : IEdgeQuery
{
    private readonly IGraphStore graphStore = graphStore;
    private readonly ILogger logger = logger;
    private readonly string identifier = identifier;
    private readonly bool byName = byName;
    private readonly List<HyperedgeType> edgeTypes = [];
    private TraversalDirection? direction;

    public IEdgeQuery OfType(HyperedgeType edgeType)
    {
        this.edgeTypes.Add(edgeType);
        return this;
    }

    public IEdgeQuery OfTypes(params HyperedgeType[] edgeTypes)
    {
        this.edgeTypes.AddRange(edgeTypes);
        return this;
    }

    public IEdgeQuery Incoming()
    {
        this.direction = TraversalDirection.Incoming;
        return this;
    }

    public IEdgeQuery Outgoing()
    {
        this.direction = TraversalDirection.Outgoing;
        return this;
    }

    public async Task<IReadOnlyList<Hyperedge>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var nodeId = await this.ResolveNodeIdAsync(cancellationToken);
        if (nodeId == null)
        {
            return Array.Empty<Hyperedge>();
        }

        var edges = await this.graphStore.GetEdgesForNodeAsync(
            nodeId,
            this.edgeTypes.Count > 0 ? this.edgeTypes : null,
            cancellationToken);

        // TODO: Filter by direction if needed (requires edge direction info)
        return edges;
    }

    private async Task<string?> ResolveNodeIdAsync(CancellationToken cancellationToken)
    {
        if (!this.byName)
        {
            return this.identifier;
        }

        var nodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { NamePattern = this.identifier },
            limit: 1,
            cancellationToken);
        return nodes.FirstOrDefault()?.Id;
    }
}

/// <summary>
/// Semantic query builder implementation.
/// </summary>
internal class SemanticQueryBuilder(
    IGraphStore graphStore,
    IVectorStore vectorStore,
    ILogger logger,
    string searchText) : ISemanticQuery
{
    private readonly IGraphStore graphStore = graphStore;
    private readonly IVectorStore vectorStore = vectorStore;
    private readonly ILogger logger = logger;
    private readonly string searchText = searchText;
    private string? language;
    private string? nodeType;
    private string? filePath;
    private float minScore = 0.5f;
    private int limit = 10;
    private int graphExpansionDepth = 0;

    public ISemanticQuery InLanguage(string language)
    {
        this.language = language;
        return this;
    }

    public ISemanticQuery OfType(string nodeType)
    {
        this.nodeType = nodeType;
        return this;
    }

    public ISemanticQuery InFile(string filePath)
    {
        this.filePath = filePath;
        return this;
    }

    public ISemanticQuery WithMinScore(float minScore)
    {
        this.minScore = minScore;
        return this;
    }

    public ISemanticQuery Take(int count)
    {
        this.limit = count;
        return this;
    }

    public ISemanticQuery WithGraphExpansion(int depth = 1)
    {
        this.graphExpansionDepth = depth;
        return this;
    }

    public async Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var scoredNodes = await this.ToListAsync(cancellationToken);
        var result = new CodeQueryResult
        {
            SemanticMatches = [.. scoredNodes]
        };

        // Graph expansion if requested
        if (this.graphExpansionDepth > 0)
        {
            var relatedNodes = new HashSet<HypergraphNode>();
            foreach (var scored in scoredNodes)
            {
                var callees = await this.graphStore.GetCalleesAsync(scored.Node.Id, this.graphExpansionDepth, cancellationToken);
                foreach (var callee in callees)
                {
                    relatedNodes.Add(callee.Node);
                }
            }
            result.Nodes = [.. relatedNodes];
        }

        return result;
    }

    public async Task<IReadOnlyList<ScoredNode>> ToListAsync(CancellationToken cancellationToken = default)
    {
        // Build filter
        var filter = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(this.language))
        {
            filter["language"] = this.language;
        }
        if (!string.IsNullOrEmpty(this.nodeType))
        {
            filter["nodeType"] = this.nodeType;
        }
        if (!string.IsNullOrEmpty(this.filePath))
        {
            filter["filePath"] = this.filePath;
        }

        // This is a simplified implementation
        // In reality, you'd need to:
        // 1. Generate embedding for search text
        // 2. Search vector store
        // 3. Map results back to graph nodes

        // For now, we'll use graph search as fallback
        var graphFilter = new NodeFilter
        {
            Language = this.language,
            NodeTypes = this.nodeType != null ? new[] { this.nodeType } : null,
            FilePath = this.filePath,
            NamePattern = this.searchText
        };

        var nodes = await this.graphStore.GetNodesAsync(graphFilter, this.limit, cancellationToken);

        return nodes.Select(n => new ScoredNode
        {
            Node = n,
            Score = 1.0f // Default score for graph matches
        }).ToList();
    }
}

/// <summary>
/// Path query builder implementation.
/// </summary>
internal class PathQueryBuilder(
    IGraphStore graphStore,
    ILogger logger,
    string fromIdentifier,
    string toIdentifier,
    bool byName) : IPathQuery
{
    private readonly IGraphStore graphStore = graphStore;
    private readonly ILogger logger = logger;
    private readonly string fromIdentifier = fromIdentifier;
    private readonly string toIdentifier = toIdentifier;
    private readonly bool byName = byName;
    private int maxLength = 5;
    private readonly List<HyperedgeType> edgeTypes = [];
    private int limit = 10;
    private bool shortestOnly = true;

    public IPathQuery WithMaxLength(int maxLength)
    {
        this.maxLength = maxLength;
        return this;
    }

    public IPathQuery ViaEdgeTypes(params HyperedgeType[] edgeTypes)
    {
        this.edgeTypes.AddRange(edgeTypes);
        return this;
    }

    public IPathQuery Take(int count)
    {
        this.limit = count;
        return this;
    }

    public IPathQuery ShortestOnly()
    {
        this.shortestOnly = true;
        return this;
    }

    public async Task<CodeQueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var paths = await this.ToListAsync(cancellationToken);
        return new CodeQueryResult { Paths = [.. paths] };
    }

    public async Task<IReadOnlyList<GraphPath>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var fromId = await this.ResolveNodeIdAsync(this.fromIdentifier, cancellationToken);
        var toId = await this.ResolveNodeIdAsync(this.toIdentifier, cancellationToken);

        if (fromId == null || toId == null)
        {
            return Array.Empty<GraphPath>();
        }

        var paths = await this.graphStore.FindPathsAsync(fromId, toId, this.maxLength, cancellationToken);

        // Apply filters
        if (this.edgeTypes.Count > 0)
        {
            paths = paths.Where(p => p.Edges.All(e => this.edgeTypes.Contains(e.EdgeType))).ToList();
        }

        if (this.shortestOnly && paths.Count > 0)
        {
            var minLength = paths.Min(p => p.Length);
            paths = paths.Where(p => p.Length == minLength).ToList();
        }

        return paths.Take(this.limit).ToList();
    }

    private async Task<string?> ResolveNodeIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!this.byName)
        {
            return identifier;
        }

        var nodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { NamePattern = identifier },
            limit: 1,
            cancellationToken);
        return nodes.FirstOrDefault()?.Id;
    }
}