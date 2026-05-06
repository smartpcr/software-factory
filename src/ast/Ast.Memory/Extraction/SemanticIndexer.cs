// -----------------------------------------------------------------------
// <copyright file="SemanticIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Storage;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Result of a <see cref="SemanticIndexer"/> indexing run.
/// </summary>
public class SemanticIndexResult
{
    /// <summary>Number of nodes that received embeddings.</summary>
    public int EmbeddingsGenerated { get; set; }

    /// <summary>Number of SIMILAR_TO edges created.</summary>
    public int SimilarToEdgesCreated { get; set; }

    /// <summary>Number of vectors actually stored in the vector store.</summary>
    public int VectorsStored { get; set; }

    /// <summary>Number of nodes persisted via graph store.</summary>
    public int NodesPersisted { get; set; }

    /// <summary>Number of edges persisted via graph store.</summary>
    public int EdgesPersisted { get; set; }

    /// <summary>Whether embedding generation was skipped due to unavailability.</summary>
    public bool Skipped { get; set; }

    /// <summary>The vector store backend that was used.</summary>
    public string? VectorBackend { get; set; }
}

/// <summary>
/// Semantic Indexer — Stage 2.3 of the extraction pipeline.
/// Batches nodes through <see cref="IEmbeddingGenerator.GenerateBatchEmbeddingsAsync"/>,
/// stores embedding vectors in <c>Metadata["Embedding"]</c>, then follows a
/// metadata-first dual-storage model: persists nodes durably to the authoritative
/// graph store via <see cref="IMemoryGraphStore.UpsertNodeAsync"/>, computes pairwise
/// cosine similarity, creates <c>SIMILAR_TO</c> edges where similarity exceeds
/// <see cref="MemoryOptions.SimilarityThreshold"/>, persists those edges via
/// <see cref="IMemoryGraphStore"/>, and finally stores vectors in the secondary
/// vector store. This ordering guarantees no orphan vector rows.
/// Vector storage is selected via <see cref="MemoryOptions.Backend"/>:
/// <c>Neo4j</c> → Qdrant (injected <see cref="IVectorStore"/>),
/// <c>Sqlite</c> → <see cref="SqliteVssVectorStore"/> (sqlite-vss with HNSW acceleration
/// when available, brute-force cosine fallback otherwise, on the same SQLite database
/// used by SqliteGraphStore, per architecture.md §3.2).
/// An injected <see cref="IVectorStore"/> always takes priority regardless of backend setting.
/// External nodes (NodeType "External" or "ExternalService") are excluded from
/// embedding generation as they represent out-of-repo dependencies.
/// </summary>
public class SemanticIndexer : IDisposable
{
    private readonly IEmbeddingGenerator? embeddingGenerator;
    private readonly IMemoryGraphStore? graphStore;
    private readonly IVectorStore? vectorStore;
    private IVectorStore? ownedVectorStore;
    private readonly MemoryOptions options;
    private readonly ILogger<SemanticIndexer> logger;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticIndexer"/> class.
    /// </summary>
    /// <param name="embeddingGenerator">
    /// Embedding generator (nullable — when null or unavailable, embedding generation
    /// is skipped gracefully with a warning log).
    /// </param>
    /// <param name="graphStore">
    /// Memory graph store for durably persisting nodes and edges (nullable — when null,
    /// nodes are only mutated in-memory and edges are only returned, not persisted).
    /// When provided, persistence failures propagate as exceptions (critical-write posture).
    /// </param>
    /// <param name="vectorStore">
    /// Vector store for persisting embeddings (nullable). An injected <see cref="IVectorStore"/>
    /// always takes priority. When null and <see cref="MemoryOptions.Backend"/> is
    /// <see cref="MemoryBackend.Neo4j"/>, a <see cref="QdrantVectorStore"/> is created from
    /// <see cref="MemoryOptions.QdrantEndpoint"/> if configured. When
    /// <see cref="MemoryBackend.Sqlite"/>, a <see cref="SqliteVssVectorStore"/> is created
    /// on the same database specified by <see cref="MemoryOptions.SqlitePath"/>
    /// (sqlite-vss with HNSW acceleration when available, brute-force cosine fallback).
    /// </param>
    /// <param name="options">Memory configuration options.</param>
    /// <param name="logger">Logger.</param>
    public SemanticIndexer(
        IEmbeddingGenerator? embeddingGenerator,
        IMemoryGraphStore? graphStore,
        IVectorStore? vectorStore,
        IOptions<MemoryOptions> options,
        ILogger<SemanticIndexer> logger)
    {
        this.embeddingGenerator = embeddingGenerator;
        this.graphStore = graphStore;
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.vectorStore = this.ResolveVectorStore(vectorStore);
    }

    /// <summary>
    /// Processes a set of nodes: generates embeddings, persists nodes with embeddings
    /// to the authoritative graph store, creates SIMILAR_TO edges, persists edges to the
    /// graph store, and finally stores vectors in the secondary vector store.
    /// Metadata-first ordering: graph persistence is authoritative; vector storage is
    /// a derived projection that only runs after graph persistence succeeds.
    /// External nodes (NodeType "External" or "ExternalService") are excluded from
    /// embedding generation as they represent out-of-repo dependencies.
    /// </summary>
    /// <param name="nodes">The nodes to index. Their <c>Metadata</c> will be mutated in-place.</param>
    /// <param name="version">
    /// Temporal version for graph persistence. When null, graph node and edge persistence
    /// are skipped but embedding generation and vector storage still proceed.
    /// Callers should pass a real <see cref="VersionRange"/> derived from the current commit
    /// for full durability.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The indexing result and list of SIMILAR_TO edges created.</returns>
    public async Task<(SemanticIndexResult Result, IReadOnlyList<Hyperedge> Edges)> IndexAsync(
        IReadOnlyList<HypergraphNode> nodes,
        VersionRange? version = null,
        CancellationToken cancellationToken = default)
    {
        var result = new SemanticIndexResult();

        if (nodes == null || nodes.Count == 0)
        {
            this.logger.LogDebug("No nodes to index");
            return (result, Array.Empty<Hyperedge>());
        }

        // Filter out external nodes — they represent out-of-repo dependencies
        // and should not receive embeddings
        var embeddableNodes = nodes.Where(n => IsEmbeddableNode(n)).ToList();
        if (embeddableNodes.Count < nodes.Count)
        {
            this.logger.LogDebug(
                "Filtered {Excluded} external nodes from {Total} total nodes",
                nodes.Count - embeddableNodes.Count, nodes.Count);
        }

        if (embeddableNodes.Count == 0)
        {
            this.logger.LogDebug("No embeddable nodes after filtering");
            return (result, Array.Empty<Hyperedge>());
        }

        // Check embedding generator availability (graceful degradation)
        if (this.embeddingGenerator == null)
        {
            this.logger.LogWarning(
                "IEmbeddingGenerator is not available; skipping embedding generation for {NodeCount} nodes",
                embeddableNodes.Count);
            result.Skipped = true;
            return (result, Array.Empty<Hyperedge>());
        }

        bool isAvailable;
        try
        {
            isAvailable = await this.embeddingGenerator.IsAvailableAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "IEmbeddingGenerator availability check failed; skipping embedding generation for {NodeCount} nodes",
                embeddableNodes.Count);
            result.Skipped = true;
            return (result, Array.Empty<Hyperedge>());
        }

        if (!isAvailable)
        {
            this.logger.LogWarning(
                "IEmbeddingGenerator reports unavailable; skipping embedding generation for {NodeCount} nodes",
                embeddableNodes.Count);
            result.Skipped = true;
            return (result, Array.Empty<Hyperedge>());
        }

        // Generate embeddings in batches (with graceful degradation on failure)
        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await this.GenerateEmbeddingsAsync(embeddableNodes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(
                ex,
                "GenerateBatchEmbeddingsAsync failed; skipping embedding generation for {NodeCount} nodes",
                embeddableNodes.Count);
            result.Skipped = true;
            return (result, Array.Empty<Hyperedge>());
        }

        // Validate returned embedding count matches input node count
        if (embeddings.Count != embeddableNodes.Count)
        {
            this.logger.LogWarning(
                "Embedding count mismatch: expected {Expected}, got {Actual}; skipping indexing",
                embeddableNodes.Count, embeddings.Count);
            result.Skipped = true;
            return (result, Array.Empty<Hyperedge>());
        }

        // Validate vector dimensions are consistent
        var expectedDim = this.embeddingGenerator.Dimensions;
        for (var i = 0; i < embeddings.Count; i++)
        {
            if (embeddings[i] == null || embeddings[i].Length != expectedDim)
            {
                this.logger.LogWarning(
                    "Vector dimension mismatch at index {Index}: expected {Expected}, got {Actual}; skipping indexing",
                    i, expectedDim, embeddings[i]?.Length ?? 0);
                result.Skipped = true;
                return (result, Array.Empty<Hyperedge>());
            }
        }

        result.EmbeddingsGenerated = embeddings.Count;

        // Store embeddings in node metadata
        for (var i = 0; i < embeddableNodes.Count; i++)
        {
            embeddableNodes[i].Metadata[MemoryMetadataKeys.Embedding] = embeddings[i];
            embeddableNodes[i].Metadata[MemoryMetadataKeys.EmbeddingModel] = this.embeddingGenerator.ModelName;
        }

        this.logger.LogInformation(
            "Generated embeddings for {Count} nodes using model {Model} (dim={Dim})",
            embeddings.Count,
            this.embeddingGenerator.ModelName,
            this.embeddingGenerator.Dimensions);

        // Metadata-first dual-storage model (architecture.md §3.2):
        // 1. Persist nodes (with embeddings in Metadata) to the authoritative graph store
        // 2. Persist SIMILAR_TO edges to the authoritative graph store
        // 3. Store vectors in the secondary vector store (derived/projection)
        // This ordering guarantees no orphan vector rows: if graph persistence fails,
        // vector storage is never reached; if vector storage fails, the authoritative
        // graph already has the durable copy.

        // Step 1: Persist nodes (with embeddings) to graph store — requires a real VersionRange
        result.NodesPersisted = await this.PersistNodesToGraphStoreAsync(embeddableNodes, version, cancellationToken);

        // Step 2: Compute pairwise cosine similarity and create SIMILAR_TO edges
        var edges = this.ComputeSimilarToEdges(embeddableNodes, embeddings);
        result.SimilarToEdgesCreated = edges.Count;

        // Step 3: Persist edges to graph store — requires a real VersionRange
        result.EdgesPersisted = await this.PersistEdgesToGraphStoreAsync(edges, version, cancellationToken);

        // Step 4: Store vectors in vector store (secondary/derived storage)
        var (vectorsStored, vectorBackend) = await this.StoreVectorsAsync(embeddableNodes, embeddings, cancellationToken);
        result.VectorsStored = vectorsStored;
        result.VectorBackend = vectorBackend;

        this.logger.LogInformation(
            "Created {EdgeCount} SIMILAR_TO edges (threshold={Threshold})",
            edges.Count,
            this.options.SimilarityThreshold);

        return (result, edges);
    }

    /// <summary>
    /// Persists nodes (with updated embedding metadata) to the graph store.
    /// Uses <see cref="IMemoryGraphStore.UpsertNodeAsync"/> for durable storage.
    /// Returns 0 if graph store is unavailable or <paramref name="version"/> is null.
    /// Failures propagate as exceptions (critical-write posture per architecture.md).
    /// </summary>
    private async Task<int> PersistNodesToGraphStoreAsync(
        IReadOnlyList<HypergraphNode> nodes,
        VersionRange? version,
        CancellationToken cancellationToken)
    {
        if (this.graphStore == null)
        {
            this.logger.LogDebug("No IMemoryGraphStore available; skipping node persistence");
            return 0;
        }

        if (version == null)
        {
            this.logger.LogDebug(
                "No VersionRange provided; skipping graph node persistence for {Count} nodes. " +
                "Pass a real VersionRange derived from the current commit to enable persistence",
                nodes.Count);
            return 0;
        }

        var persisted = 0;

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.graphStore.UpsertNodeAsync(node, version, cancellationToken);
            persisted++;
        }

        this.logger.LogDebug("Persisted {Count} nodes to graph store", persisted);
        return persisted;
    }

    /// <summary>
    /// Persists SIMILAR_TO edges to the graph store via <see cref="IMemoryGraphStore"/>.
    /// Returns 0 if graph store is unavailable or <paramref name="version"/> is null.
    /// Failures propagate as exceptions (critical-write posture per architecture.md).
    /// </summary>
    private async Task<int> PersistEdgesToGraphStoreAsync(
        IReadOnlyList<Hyperedge> edges,
        VersionRange? version,
        CancellationToken cancellationToken)
    {
        if (this.graphStore == null || edges.Count == 0 || version == null)
        {
            return 0;
        }

        var persisted = 0;

        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.graphStore.CreateEdgeAsync(edge, cancellationToken);
            persisted++;
        }

        this.logger.LogDebug("Persisted {Count} SIMILAR_TO edges to graph store", persisted);
        return persisted;
    }

    /// <summary>
    /// Generates embeddings for the given nodes using batch processing.
    /// </summary>
    private async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<HypergraphNode> nodes,
        CancellationToken cancellationToken)
    {
        // Prefer enhanced text when LSP-enriched, otherwise use basic text
        var texts = nodes.Select(n => n.IsLspEnriched
            ? n.GetEnhancedEmbeddingText()
            : n.GetEmbeddingText()).ToList();

        var batchSize = this.options.EmbeddingBatchSize;
        if (batchSize <= 0)
        {
            batchSize = 32;
        }

        if (texts.Count <= batchSize)
        {
            return await this.embeddingGenerator!.GenerateBatchEmbeddingsAsync(texts, cancellationToken);
        }

        // Process in batches to avoid overwhelming the embedding service
        var allEmbeddings = new List<float[]>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = texts.Skip(offset).Take(batchSize);
            var batchEmbeddings = await this.embeddingGenerator!.GenerateBatchEmbeddingsAsync(
                batch, cancellationToken);
            allEmbeddings.AddRange(batchEmbeddings);

            this.logger.LogDebug(
                "Embedded batch {Offset}-{End} of {Total} nodes",
                offset,
                Math.Min(offset + batchSize, texts.Count),
                texts.Count);
        }

        return allEmbeddings;
    }

    /// <summary>
    /// Stores embedding vectors in the resolved vector store.
    /// Returns the number of vectors stored and the backend name.
    /// When the store is not configured or availability check fails, returns (0, null)
    /// gracefully. Once the store is confirmed available, create/upsert failures
    /// propagate as exceptions (critical-write posture per architecture.md).
    /// </summary>
    private async Task<(int Count, string? Backend)> StoreVectorsAsync(
        IReadOnlyList<HypergraphNode> nodes,
        IReadOnlyList<float[]> embeddings,
        CancellationToken cancellationToken)
    {
        if (this.vectorStore == null)
        {
            this.logger.LogDebug("No vector store resolved; skipping vector persistence");
            return (0, null);
        }

        bool storeAvailable;
        try
        {
            storeAvailable = await this.vectorStore.IsAvailableAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Vector store availability check failed; skipping vector persistence");
            return (0, null);
        }

        if (!storeAvailable)
        {
            this.logger.LogWarning("Vector store is not available; skipping vector persistence");
            return (0, null);
        }

        var collectionName = this.options.VectorCollectionName;
        var vectorSize = this.embeddingGenerator!.Dimensions;

        // Critical-write: create/upsert failures propagate as exceptions
        var exists = await this.vectorStore.CollectionExistsAsync(collectionName, cancellationToken);
        if (!exists)
        {
            await this.vectorStore.CreateCollectionAsync(collectionName, vectorSize, cancellationToken);
            this.logger.LogInformation(
                "Created vector collection '{Collection}' with dimension {Dim}",
                collectionName,
                vectorSize);
        }

        // Build vector points with node_id, MemoryLayer, and NodeType per architecture.md §3.2.2 line 343.
        // The node_id payload field is critical for Qdrant ID resolution: QdrantVectorStore maps
        // string IDs to deterministic UUID v5 on upsert and returns UUIDs on search, so consumers
        // must use payload["node_id"] to resolve the original HypergraphNode.Id.
        var points = new List<VectorPoint>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var memoryLayer = nodes[i].Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var layerVal)
                ? layerVal?.ToString() ?? "semantic"
                : "semantic";

            points.Add(new VectorPoint
            {
                Id = nodes[i].Id,
                Vector = embeddings[i],
                Payload = new Dictionary<string, object>
                {
                    ["node_id"] = nodes[i].Id,
                    [MemoryMetadataKeys.MemoryLayer] = memoryLayer,
                    ["NodeType"] = nodes[i].NodeType,
                    ["name"] = nodes[i].Name,
                    ["file_path"] = nodes[i].FilePath,
                    ["language"] = nodes[i].Language
                }
            });
        }

        await this.vectorStore.UpsertAsync(collectionName, points, cancellationToken);
        this.logger.LogDebug("Stored {Count} vectors in collection '{Collection}'", points.Count, collectionName);

        return (points.Count, this.vectorStore.Name);
    }

    /// <summary>
    /// Computes pairwise cosine similarity among nodes and creates SIMILAR_TO edges
    /// for pairs exceeding the configured threshold.
    /// Upper-triangular iteration (i &lt; j) to avoid self-edges and symmetric duplicates.
    /// </summary>
    internal IReadOnlyList<Hyperedge> ComputeSimilarToEdges(
        IReadOnlyList<HypergraphNode> nodes,
        IReadOnlyList<float[]> embeddings)
    {
        var edges = new List<Hyperedge>();
        var threshold = this.options.SimilarityThreshold;

        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var similarity = CosineSimilarity(embeddings[i], embeddings[j]);

                if (similarity >= threshold)
                {
                    var nodeAId = nodes[i].Id;
                    var nodeBId = nodes[j].Id;

                    // Deterministic edge ID: sort IDs alphabetically to guarantee uniqueness
                    var (first, second) = string.CompareOrdinal(nodeAId, nodeBId) <= 0
                        ? (nodeAId, nodeBId)
                        : (nodeBId, nodeAId);

                    var edge = new Hyperedge
                    {
                        Id = $"similar:{first}:{second}",
                        EdgeType = HyperedgeType.MemoryRelation,
                        NodeIds = [nodeAId, nodeBId],
                        Roles = new Dictionary<string, string>
                        {
                            [nodeAId] = "node_a",
                            [nodeBId] = "node_b"
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            [MemoryMetadataKeys.SemanticRelation] = "SIMILAR_TO",
                            [MemoryMetadataKeys.MemoryLayer] = "semantic",
                            [MemoryMetadataKeys.SimilarityScore] = similarity
                        }
                    };

                    edges.Add(edge);
                }
            }
        }

        return edges;
    }

    /// <summary>
    /// Computes cosine similarity between two float vectors.
    /// </summary>
    internal static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors must have the same dimension");
        }

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return magnitude == 0 ? 0 : dotProduct / magnitude;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.ownedVectorStore?.Dispose();
    }

    /// <summary>
    /// Resolves the appropriate vector store based on <see cref="MemoryOptions.Backend"/>.
    /// For <see cref="MemoryBackend.Neo4j"/>, uses the injected <see cref="IVectorStore"/>
    /// or creates a <see cref="QdrantVectorStore"/> from <see cref="MemoryOptions.QdrantEndpoint"/>.
    /// For <see cref="MemoryBackend.Sqlite"/>, creates a <see cref="SqliteVssVectorStore"/>
    /// on the database specified by <see cref="MemoryOptions.SqlitePath"/> — the same
    /// database used by SqliteGraphStore per architecture.md §3.2.
    /// An injected <see cref="IVectorStore"/> always takes priority regardless of backend.
    /// </summary>
    private IVectorStore? ResolveVectorStore(IVectorStore? injectedStore)
    {
        // Injected store always takes priority (allows overriding for any backend)
        if (injectedStore != null)
        {
            this.logger.LogInformation(
                "Backend={Backend}: using injected IVectorStore ({StoreName}) for vector storage",
                this.options.Backend, injectedStore.Name);
            return injectedStore;
        }

        switch (this.options.Backend)
        {
            case MemoryBackend.Neo4j:
                // Create QdrantVectorStore from endpoint configuration
                if (!string.IsNullOrWhiteSpace(this.options.QdrantEndpoint))
                {
                    return this.CreateQdrantVectorStore(this.options.QdrantEndpoint);
                }

                this.logger.LogWarning(
                    "Backend=Neo4j but no IVectorStore injected and QdrantEndpoint not configured; " +
                    "vector storage will be unavailable");
                return null;

            case MemoryBackend.Sqlite:
                return this.CreateSqliteLocalStore();

            default:
                this.logger.LogWarning(
                    "Unknown Backend={Backend}; vector storage unavailable",
                    this.options.Backend);
                return null;
        }
    }

    /// <summary>
    /// Creates a <see cref="QdrantVectorStore"/> from the configured endpoint URI.
    /// Parses the endpoint as a URI to extract host, port, and TLS settings.
    /// </summary>
    private IVectorStore CreateQdrantVectorStore(string endpoint)
    {
        Uri uri;
        try
        {
            uri = new Uri(endpoint);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException(
                $"Invalid QdrantEndpoint '{endpoint}'. Expected a valid URI " +
                "(e.g., 'http://localhost:6334' or 'https://qdrant.example.com:6334').",
                ex);
        }

        var qdrantOptions = new VectorStoreOptions
        {
            Host = uri.Host,
            GrpcPort = uri.Port > 0 ? uri.Port : 6334,
            UseGrpc = true,
            UseTls = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
        };

        var qdrantLogger = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory
            .Instance.CreateLogger<QdrantVectorStore>();
        var store = new QdrantVectorStore(
            Microsoft.Extensions.Options.Options.Create(qdrantOptions),
            qdrantLogger);

        this.ownedVectorStore = store;
        this.logger.LogInformation(
            "Backend=Neo4j: created QdrantVectorStore at {Host}:{Port} (TLS={Tls})",
            qdrantOptions.Host, qdrantOptions.GrpcPort, qdrantOptions.UseTls);
        return store;
    }

    /// <summary>
    /// Creates the local vector store for Sqlite backend.
    /// Uses <see cref="SqliteVssVectorStore"/> per architecture.md §3.2: attempts
    /// to load the <c>sqlite-vss</c> extension for HNSW-accelerated ANN search;
    /// falls back to brute-force cosine similarity in managed code when the native
    /// extension is unavailable. Operates on the same SQLite database specified by
    /// <see cref="MemoryOptions.SqlitePath"/>.
    /// Returns null when SqlitePath is not configured.
    /// </summary>
    private IVectorStore? CreateSqliteLocalStore()
    {
        var dbPath = this.options.SqlitePath;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            this.logger.LogWarning(
                "Backend=Sqlite but SqlitePath is not configured; local vector storage is disabled. " +
                "Set MemoryOptions.SqlitePath to the same database used by SqliteGraphStore");
            return null;
        }

        var store = new SqliteVssVectorStore(dbPath, this.logger);
        this.ownedVectorStore = store;
        this.logger.LogInformation(
            "Backend=Sqlite: created SqliteVssVectorStore at '{Path}'", dbPath);
        return store;
    }

    /// <summary>
    /// Determines whether a node should receive an embedding.
    /// External nodes (out-of-repo dependencies) are excluded.
    /// Both "External" (boundary nodes created by the pipeline) and
    /// "ExternalService" (legacy/alternate naming) are filtered out.
    /// </summary>
    internal static bool IsEmbeddableNode(HypergraphNode node)
    {
        var nodeType = node.NodeType;
        return !string.Equals(nodeType, "External", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(nodeType, "ExternalService", StringComparison.OrdinalIgnoreCase);
    }
}