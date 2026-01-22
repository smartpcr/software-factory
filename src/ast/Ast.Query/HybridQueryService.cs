// -----------------------------------------------------------------------
// <copyright file="HybridQueryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query;

using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service that combines graph and vector search for hybrid queries.
/// </summary>
public class HybridQueryService
{
    private readonly IGraphStore graphStore;
    private readonly IVectorStore? vectorStore;
    private readonly IEmbeddingGenerator? embeddingGenerator;
    private readonly ILogger<HybridQueryService> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HybridQueryService"/> class.
    /// </summary>
    public HybridQueryService(
        IGraphStore graphStore,
        ILogger<HybridQueryService> logger,
        IVectorStore? vectorStore = null,
        IEmbeddingGenerator? embeddingGenerator = null)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.vectorStore = vectorStore;
        this.embeddingGenerator = embeddingGenerator;
    }

    /// <summary>
    /// Gets whether semantic search is available.
    /// </summary>
    public bool SemanticSearchAvailable => this.vectorStore != null && this.embeddingGenerator != null;

    /// <summary>
    /// Creates a new query builder.
    /// </summary>
    /// <returns>A code query builder.</returns>
    public ICodeQuery Query() => new CodeQueryBuilder(this.graphStore, this.logger as ILogger<CodeQueryBuilder> ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeQueryBuilder>.Instance, this.vectorStore);

    /// <summary>
    /// Performs a hybrid search combining semantic and graph results.
    /// </summary>
    /// <param name="searchText">The text to search for.</param>
    /// <param name="options">Query options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Combined search results.</returns>
    public async Task<CodeQueryResult> HybridSearchAsync(
        string searchText,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchText);

        options ??= new QueryOptions();
        var result = new CodeQueryResult();

        // Perform graph search
        if (options.UseGraphSearch)
        {
            this.logger.LogDebug("Performing graph search for: {SearchText}", searchText);
            var graphResults = await this.SearchGraphAsync(searchText, options, cancellationToken);
            result.Nodes.AddRange(graphResults);
        }

        // Perform semantic search
        if (options.UseSemanticSearch && this.SemanticSearchAvailable)
        {
            this.logger.LogDebug("Performing semantic search for: {SearchText}", searchText);
            var semanticResults = await this.SearchSemanticAsync(searchText, options, cancellationToken);
            result.SemanticMatches.AddRange(semanticResults);
        }

        // Merge and deduplicate
        var seenIds = new HashSet<string>(result.Nodes.Select(n => n.Id));
        foreach (var scored in result.SemanticMatches)
        {
            if (!seenIds.Contains(scored.Node.Id))
            {
                result.Nodes.Add(scored.Node);
                seenIds.Add(scored.Node.Id);
            }
        }

        // Include related nodes if requested
        if (options.IncludeRelated)
        {
            var relatedNodes = new List<HypergraphNode>();
            foreach (var node in result.Nodes.Take(10)) // Limit expansion
            {
                var callees = await this.graphStore.GetCalleesAsync(node.Id, 1, cancellationToken);
                relatedNodes.AddRange(callees.Select(c => c.Node).Where(n => !seenIds.Contains(n.Id)));
            }
            result.Nodes.AddRange(relatedNodes.Take(options.Limit));
        }

        return result;
    }

    /// <summary>
    /// Finds code similar to the given node.
    /// </summary>
    /// <param name="nodeId">The reference node ID.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Similar nodes with scores.</returns>
    public async Task<IReadOnlyList<ScoredNode>> FindSimilarAsync(
        string nodeId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var node = await this.graphStore.GetNodeAsync(nodeId, cancellationToken);
        if (node == null)
        {
            return Array.Empty<ScoredNode>();
        }

        if (!this.SemanticSearchAvailable)
        {
            // Fallback to graph-based similarity
            return await this.FindSimilarByGraphAsync(node, limit, cancellationToken);
        }

        // Use vector search
        var embedding = await this.embeddingGenerator!.GenerateEmbeddingAsync(node.GetEmbeddingText(), cancellationToken);

        // Search vector store
        // Note: This assumes we have a collection with node embeddings
        var searchResults = await this.vectorStore!.SearchAsync(
            "code_nodes",
            embedding,
            limit + 1, // +1 to exclude self
            cancellationToken: cancellationToken);

        var results = new List<ScoredNode>();
        foreach (var result in searchResults)
        {
            if (result.Id == nodeId) continue; // Skip self

            var similarNode = await this.graphStore.GetNodeAsync(result.Id, cancellationToken);
            if (similarNode != null)
            {
                results.Add(new ScoredNode
                {
                    Node = similarNode,
                    Score = result.Score
                });
            }
        }

        return results.Take(limit).ToList();
    }

    /// <summary>
    /// Finds code that answers a natural language question.
    /// </summary>
    /// <param name="question">The natural language question.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Relevant code with explanations.</returns>
    public async Task<IReadOnlyList<ScoredNode>> AnswerQuestionAsync(
        string question,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (!this.SemanticSearchAvailable)
        {
            this.logger.LogWarning("Semantic search not available, falling back to keyword search");
            var graphResults = await this.SearchGraphAsync(question, new QueryOptions { Limit = limit }, cancellationToken);
            return graphResults.Select(n => new ScoredNode { Node = n, Score = 1.0f }).ToList();
        }

        var embedding = await this.embeddingGenerator!.GenerateEmbeddingAsync(question, cancellationToken);

        var searchResults = await this.vectorStore!.SearchAsync(
            "code_nodes",
            embedding,
            limit,
            cancellationToken: cancellationToken);

        var results = new List<ScoredNode>();
        foreach (var result in searchResults)
        {
            var node = await this.graphStore.GetNodeAsync(result.Id, cancellationToken);
            if (node != null)
            {
                results.Add(new ScoredNode
                {
                    Node = node,
                    Score = result.Score
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Explores the codebase starting from a node.
    /// </summary>
    /// <param name="startNodeId">Starting node ID.</param>
    /// <param name="depth">Exploration depth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Explored nodes organized by depth.</returns>
    public async Task<Dictionary<int, List<HypergraphNode>>> ExploreAsync(
        string startNodeId,
        int depth = 3,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, List<HypergraphNode>>();
        var visited = new HashSet<string>();

        var startNode = await this.graphStore.GetNodeAsync(startNodeId, cancellationToken);
        if (startNode == null)
        {
            return result;
        }

        result[0] = new List<HypergraphNode> { startNode };
        visited.Add(startNodeId);

        for (int currentDepth = 1; currentDepth <= depth; currentDepth++)
        {
            result[currentDepth] = new List<HypergraphNode>();

            foreach (var node in result[currentDepth - 1])
            {
                var callees = await this.graphStore.GetCalleesAsync(node.Id, 1, cancellationToken);
                var callers = await this.graphStore.GetCallersAsync(node.Id, 1, cancellationToken);

                var neighbors = callees.Concat(callers)
                    .Select(n => n.Node)
                    .Where(n => !visited.Contains(n.Id));

                foreach (var neighbor in neighbors)
                {
                    visited.Add(neighbor.Id);
                    result[currentDepth].Add(neighbor);
                }
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<HypergraphNode>> SearchGraphAsync(
        string searchText,
        QueryOptions options,
        CancellationToken cancellationToken)
    {
        // Extract potential identifiers from search text
        var words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToList();

        var allResults = new List<HypergraphNode>();

        foreach (var word in words.Take(5)) // Limit search terms
        {
            var filter = new NodeFilter { NamePattern = $"*{word}*" };
            var nodes = await this.graphStore.GetNodesAsync(filter, options.Limit, cancellationToken);
            allResults.AddRange(nodes);
        }

        // Deduplicate by ID
        return allResults
            .GroupBy(n => n.Id)
            .Select(g => g.First())
            .Take(options.Limit)
            .ToList();
    }

    private async Task<IReadOnlyList<ScoredNode>> SearchSemanticAsync(
        string searchText,
        QueryOptions options,
        CancellationToken cancellationToken)
    {
        if (!this.SemanticSearchAvailable)
        {
            return Array.Empty<ScoredNode>();
        }

        var embedding = await this.embeddingGenerator!.GenerateEmbeddingAsync(searchText, cancellationToken);

        var searchResults = await this.vectorStore!.SearchAsync(
            "code_nodes",
            embedding,
            options.Limit,
            filter: null,
            cancellationToken: cancellationToken);

        // Filter by minimum score
        searchResults = searchResults.Where(r => r.Score >= options.MinScore).ToList();

        var results = new List<ScoredNode>();
        foreach (var result in searchResults)
        {
            var node = await this.graphStore.GetNodeAsync(result.Id, cancellationToken);
            if (node != null)
            {
                results.Add(new ScoredNode
                {
                    Node = node,
                    Score = result.Score
                });
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<ScoredNode>> FindSimilarByGraphAsync(
        HypergraphNode node,
        int limit,
        CancellationToken cancellationToken)
    {
        // Find nodes with same type and similar structure
        var filter = new NodeFilter
        {
            NodeTypes = new[] { node.NodeType },
            Language = node.Language
        };

        var candidates = await this.graphStore.GetNodesAsync(filter, limit * 10, cancellationToken);

        // Score by structural similarity
        var scored = candidates
            .Where(c => c.Id != node.Id)
            .Select(c => new ScoredNode
            {
                Node = c,
                Score = CalculateStructuralSimilarity(node, c)
            })
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();

        return scored;
    }

    private static float CalculateStructuralSimilarity(HypergraphNode a, HypergraphNode b)
    {
        var score = 0f;

        // Same node type
        if (a.NodeType == b.NodeType) score += 0.3f;

        // Same language
        if (a.Language == b.Language) score += 0.2f;

        // Similar scope
        if (a.Scope == b.Scope) score += 0.2f;

        // Overlapping modifiers
        var commonModifiers = a.Modifiers.Intersect(b.Modifiers).Count();
        var totalModifiers = a.Modifiers.Union(b.Modifiers).Count();
        if (totalModifiers > 0)
        {
            score += 0.3f * commonModifiers / totalModifiers;
        }

        return score;
    }
}
