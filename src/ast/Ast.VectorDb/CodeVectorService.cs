// -----------------------------------------------------------------------
// <copyright file="CodeVectorService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

using Ast.Core;
using Ast.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// High-level service for storing and searching code embeddings.
/// </summary>
public sealed class CodeVectorService : IDisposable
{
    private readonly IVectorStore vectorStore;
    private readonly IEmbeddingGenerator embeddingGenerator;
    private readonly ILogger<CodeVectorService> logger;
    private readonly VectorStoreOptions options;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeVectorService"/> class.
    /// </summary>
    /// <param name="vectorStore">The vector store.</param>
    /// <param name="embeddingGenerator">The embedding generator.</param>
    /// <param name="options">The vector store options.</param>
    /// <param name="logger">The logger.</param>
    public CodeVectorService(
        IVectorStore vectorStore,
        IEmbeddingGenerator embeddingGenerator,
        IOptions<VectorStoreOptions> options,
        ILogger<CodeVectorService> logger)
    {
        this.vectorStore = vectorStore;
        this.embeddingGenerator = embeddingGenerator;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>
    /// Initializes the collections for storing code data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        // Create nodes collection
        if (!await this.vectorStore.CollectionExistsAsync(this.options.NodesCollection, cancellationToken))
        {
            await this.vectorStore.CreateCollectionAsync(
                this.options.NodesCollection,
                this.embeddingGenerator.Dimensions,
                cancellationToken);
            this.logger.LogInformation("Created nodes collection: {Collection}", this.options.NodesCollection);
        }

        // Create edges collection
        if (!await this.vectorStore.CollectionExistsAsync(this.options.EdgesCollection, cancellationToken))
        {
            await this.vectorStore.CreateCollectionAsync(
                this.options.EdgesCollection,
                this.embeddingGenerator.Dimensions,
                cancellationToken);
            this.logger.LogInformation("Created edges collection: {Collection}", this.options.EdgesCollection);
        }
    }

    /// <summary>
    /// Indexes a hypergraph's nodes into the vector database.
    /// </summary>
    /// <param name="data">The hypergraph data.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of nodes indexed.</returns>
    public async Task<int> IndexNodesAsync(
        HypergraphData data,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var nodes = data.Nodes.Values.ToList();
        if (nodes.Count == 0)
        {
            return 0;
        }

        this.logger.LogInformation("Indexing {Count} nodes", nodes.Count);

        var batchSize = 100;
        var indexed = 0;

        foreach (var batch in nodes.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Generate embeddings for batch
            var texts = batch.Select(n => n.GetEmbeddingText()).ToList();
            var embeddings = await this.embeddingGenerator.GenerateBatchEmbeddingsAsync(texts, cancellationToken);

            // Create vector points
            var points = batch.Zip(embeddings, (node, vector) => new VectorPoint
            {
                Id = node.Id,
                Vector = vector,
                Payload = new Dictionary<string, object>
                {
                    ["name"] = node.Name,
                    ["node_type"] = node.NodeType,
                    ["language"] = node.Language,
                    ["file_path"] = node.FilePath,
                    ["scope"] = node.Scope,
                    ["line_start"] = node.LineStart,
                    ["line_end"] = node.LineEnd,
                    ["modifiers"] = node.Modifiers
                }
            }).ToList();

            // Upsert to vector store
            await this.vectorStore.UpsertAsync(this.options.NodesCollection, points, cancellationToken);

            indexed += batch.Length;
            progress?.Report(new IndexingProgress
            {
                TotalItems = nodes.Count,
                IndexedItems = indexed,
                CurrentBatch = batch.Length
            });

            this.logger.LogDebug("Indexed {Indexed}/{Total} nodes", indexed, nodes.Count);
        }

        this.logger.LogInformation("Completed indexing {Count} nodes", indexed);
        return indexed;
    }

    /// <summary>
    /// Indexes a hypergraph's edges into the vector database.
    /// </summary>
    /// <param name="data">The hypergraph data.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of edges indexed.</returns>
    public async Task<int> IndexEdgesAsync(
        HypergraphData data,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var edges = data.Hyperedges;
        if (edges.Count == 0)
        {
            return 0;
        }

        this.logger.LogInformation("Indexing {Count} edges", edges.Count);

        var batchSize = 100;
        var indexed = 0;

        foreach (var batch in edges.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Generate embeddings for batch
            var texts = batch.Select(e => e.GetEmbeddingText()).ToList();
            var embeddings = await this.embeddingGenerator.GenerateBatchEmbeddingsAsync(texts, cancellationToken);

            // Create vector points
            var points = batch.Zip(embeddings, (edge, vector) => new VectorPoint
            {
                Id = edge.Id,
                Vector = vector,
                Payload = new Dictionary<string, object>
                {
                    ["edge_type"] = edge.EdgeType.ToString(),
                    ["cardinality"] = edge.Cardinality,
                    ["is_cross_language"] = edge.IsCrossLanguage,
                    ["node_ids"] = edge.NodeIds,
                    ["languages"] = edge.Languages,
                    ["weight"] = edge.Weight
                }
            }).ToList();

            // Upsert to vector store
            await this.vectorStore.UpsertAsync(this.options.EdgesCollection, points, cancellationToken);

            indexed += batch.Length;
            progress?.Report(new IndexingProgress
            {
                TotalItems = edges.Count,
                IndexedItems = indexed,
                CurrentBatch = batch.Length
            });

            this.logger.LogDebug("Indexed {Indexed}/{Total} edges", indexed, edges.Count);
        }

        this.logger.LogInformation("Completed indexing {Count} edges", indexed);
        return indexed;
    }

    /// <summary>
    /// Searches for similar code nodes using natural language or code.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="filter">Optional filter conditions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results with similarity scores.</returns>
    public async Task<IReadOnlyList<CodeSearchResult>> SearchNodesAsync(
        string query,
        int limit = 10,
        CodeSearchFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        // Generate query embedding
        var queryVector = await this.embeddingGenerator.GenerateEmbeddingAsync(query, cancellationToken);

        // Build filter
        var vectorFilter = filter != null ? BuildVectorFilter(filter) : null;

        // Search
        var results = await this.vectorStore.SearchAsync(
            this.options.NodesCollection,
            queryVector,
            limit,
            vectorFilter,
            cancellationToken);

        return results.Select(r => new CodeSearchResult
        {
            Id = r.Id,
            Score = r.Score,
            Name = r.Payload.GetValueOrDefault("name")?.ToString() ?? string.Empty,
            NodeType = r.Payload.GetValueOrDefault("node_type")?.ToString() ?? string.Empty,
            Language = r.Payload.GetValueOrDefault("language")?.ToString() ?? string.Empty,
            FilePath = r.Payload.GetValueOrDefault("file_path")?.ToString() ?? string.Empty,
            LineStart = r.Payload.GetValueOrDefault("line_start") is long l ? (int)l : 0,
            LineEnd = r.Payload.GetValueOrDefault("line_end") is long le ? (int)le : 0
        }).ToList();
    }

    /// <summary>
    /// Searches for similar hyperedges.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="edgeTypes">Optional edge types to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results with similarity scores.</returns>
    public async Task<IReadOnlyList<EdgeSearchResult>> SearchEdgesAsync(
        string query,
        int limit = 10,
        IEnumerable<HyperedgeType>? edgeTypes = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        // Generate query embedding
        var queryVector = await this.embeddingGenerator.GenerateEmbeddingAsync(query, cancellationToken);

        // Build filter
        VectorFilter? vectorFilter = null;
        if (edgeTypes != null)
        {
            var typeStrings = edgeTypes.Select(t => t.ToString()).ToList();
            if (typeStrings.Count > 0)
            {
                vectorFilter = new VectorFilter
                {
                    Must = new List<FilterCondition>
                    {
                        new FilterCondition
                        {
                            Field = "edge_type",
                            Operator = FilterOperator.In,
                            Value = typeStrings
                        }
                    }
                };
            }
        }

        // Search
        var results = await this.vectorStore.SearchAsync(
            this.options.EdgesCollection,
            queryVector,
            limit,
            vectorFilter,
            cancellationToken);

        return results.Select(r => new EdgeSearchResult
        {
            Id = r.Id,
            Score = r.Score,
            EdgeType = Enum.TryParse<HyperedgeType>(
                r.Payload.GetValueOrDefault("edge_type")?.ToString(),
                out var et) ? et : HyperedgeType.DataFlow,
            Cardinality = r.Payload.GetValueOrDefault("cardinality") is long c ? (int)c : 0,
            IsCrossLanguage = r.Payload.GetValueOrDefault("is_cross_language") is bool b && b,
            NodeIds = r.Payload.GetValueOrDefault("node_ids") as IList<object> is { } list
                ? list.Select(x => x.ToString()!).ToList()
                : new List<string>()
        }).ToList();
    }

    /// <summary>
    /// Gets statistics about the indexed data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Index statistics.</returns>
    public async Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var nodeCount = await this.vectorStore.CountAsync(this.options.NodesCollection, cancellationToken);
        var edgeCount = await this.vectorStore.CountAsync(this.options.EdgesCollection, cancellationToken);

        return new IndexStatistics
        {
            NodesCollection = this.options.NodesCollection,
            EdgesCollection = this.options.EdgesCollection,
            NodeCount = nodeCount,
            EdgeCount = edgeCount,
            VectorDimensions = this.embeddingGenerator.Dimensions,
            EmbeddingModel = this.embeddingGenerator.ModelName
        };
    }

    /// <summary>
    /// Clears all indexed data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.logger.LogWarning("Clearing all indexed data");

        if (await this.vectorStore.CollectionExistsAsync(this.options.NodesCollection, cancellationToken))
        {
            await this.vectorStore.DeleteCollectionAsync(this.options.NodesCollection, cancellationToken);
        }

        if (await this.vectorStore.CollectionExistsAsync(this.options.EdgesCollection, cancellationToken))
        {
            await this.vectorStore.DeleteCollectionAsync(this.options.EdgesCollection, cancellationToken);
        }

        // Recreate empty collections
        await this.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.vectorStore?.Dispose();
        this.embeddingGenerator?.Dispose();
        this.logger.LogInformation("CodeVectorService disposed");
    }

    private static VectorFilter BuildVectorFilter(CodeSearchFilter filter)
    {
        var result = new VectorFilter();

        if (!string.IsNullOrEmpty(filter.Language))
        {
            result.Must.Add(new FilterCondition
            {
                Field = "language",
                Operator = FilterOperator.Equals,
                Value = filter.Language
            });
        }

        if (filter.NodeTypes?.Any() == true)
        {
            result.Must.Add(new FilterCondition
            {
                Field = "node_type",
                Operator = FilterOperator.In,
                Value = filter.NodeTypes.ToList()
            });
        }

        if (!string.IsNullOrEmpty(filter.FilePath))
        {
            result.Must.Add(new FilterCondition
            {
                Field = "file_path",
                Operator = FilterOperator.Contains,
                Value = filter.FilePath
            });
        }

        return result;
    }
}

/// <summary>
/// Progress information for indexing operations.
/// </summary>
public class IndexingProgress
{
    /// <summary>Gets or sets the total items to index.</summary>
    public int TotalItems { get; set; }

    /// <summary>Gets or sets the items indexed so far.</summary>
    public int IndexedItems { get; set; }

    /// <summary>Gets or sets the current batch size.</summary>
    public int CurrentBatch { get; set; }

    /// <summary>Gets the completion percentage.</summary>
    public double PercentComplete => TotalItems == 0 ? 0 : (double)IndexedItems / TotalItems * 100;
}

/// <summary>
/// Filter options for code search.
/// </summary>
public class CodeSearchFilter
{
    /// <summary>Gets or sets the language to filter by.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the node types to filter by.</summary>
    public IEnumerable<string>? NodeTypes { get; set; }

    /// <summary>Gets or sets the file path pattern to filter by.</summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// A code search result.
/// </summary>
public class CodeSearchResult
{
    /// <summary>Gets or sets the node ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the similarity score.</summary>
    public float Score { get; set; }

    /// <summary>Gets or sets the node name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the node type.</summary>
    public required string NodeType { get; set; }

    /// <summary>Gets or sets the language.</summary>
    public required string Language { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public required string FilePath { get; set; }

    /// <summary>Gets or sets the start line.</summary>
    public int LineStart { get; set; }

    /// <summary>Gets or sets the end line.</summary>
    public int LineEnd { get; set; }
}

/// <summary>
/// An edge search result.
/// </summary>
public class EdgeSearchResult
{
    /// <summary>Gets or sets the edge ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the similarity score.</summary>
    public float Score { get; set; }

    /// <summary>Gets or sets the edge type.</summary>
    public HyperedgeType EdgeType { get; set; }

    /// <summary>Gets or sets the cardinality.</summary>
    public int Cardinality { get; set; }

    /// <summary>Gets or sets whether this is cross-language.</summary>
    public bool IsCrossLanguage { get; set; }

    /// <summary>Gets or sets the connected node IDs.</summary>
    public List<string> NodeIds { get; set; } = new();
}

/// <summary>
/// Index statistics.
/// </summary>
public class IndexStatistics
{
    /// <summary>Gets or sets the nodes collection name.</summary>
    public required string NodesCollection { get; set; }

    /// <summary>Gets or sets the edges collection name.</summary>
    public required string EdgesCollection { get; set; }

    /// <summary>Gets or sets the node count.</summary>
    public long NodeCount { get; set; }

    /// <summary>Gets or sets the edge count.</summary>
    public long EdgeCount { get; set; }

    /// <summary>Gets or sets the vector dimensions.</summary>
    public int VectorDimensions { get; set; }

    /// <summary>Gets or sets the embedding model name.</summary>
    public required string EmbeddingModel { get; set; }
}
