// -----------------------------------------------------------------------
// <copyright file="DefaultSemanticIndexer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Production <see cref="ISemanticIndexer"/> implementation that generates
/// embeddings for nodes via <see cref="IEmbeddingGenerator"/> and upserts
/// resulting vectors into <see cref="IVectorStore"/> ("memory_embeddings"
/// collection). After embedding, computes pairwise cosine similarity and
/// creates SIMILAR_TO edges where cosine ≥ <see cref="MemoryOptions.SimilarityThreshold"/>.
///
/// Per architecture.md §3.1, §5.4 lines 1514-1517:
///   SemanticIndexer → IEmbeddingGenerator.GenerateBatchEmbeddingsAsync
///                   → IVectorStore.UpsertAsync("memory_embeddings")
///                   → Recalculate SIMILAR_TO edges for changed nodes
///
/// Returns the number of SIMILAR_TO edges created or updated, which is tracked
/// in <see cref="SyncResult.SemanticEdgesCreated"/>.
/// </summary>
public class DefaultSemanticIndexer : ISemanticIndexer
{
    private const string EmbeddingsCollection = "memory_embeddings";

    private readonly IMemoryGraphStore graphStore;
    private readonly IEmbeddingGenerator embeddingGenerator;
    private readonly IVectorStore vectorStore;
    private readonly MemoryOptions options;
    private readonly ILogger<DefaultSemanticIndexer> logger;

    public DefaultSemanticIndexer(
        IMemoryGraphStore graphStore,
        IEmbeddingGenerator embeddingGenerator,
        IVectorStore vectorStore,
        IOptions<MemoryOptions> options,
        ILogger<DefaultSemanticIndexer> logger)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this.vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> IndexNodesAsync(
        IReadOnlyList<HypergraphNode> nodes,
        CancellationToken ct = default)
    {
        if (nodes.Count == 0)
        {
            return 0;
        }

        int similarToEdgesCreated = 0;
        var changedNodeIds = new HashSet<string>(nodes.Select(n => n.Id));

        // (1) Retire existing SIMILAR_TO edges connected to changed nodes.
        //     When a node's content changes, its old similarity relationships
        //     are stale and must be replaced with fresh embeddings.
        var existingSimilarToEdges = await this.graphStore.GetEdgesBySemanticRelationAsync(
            "SIMILAR_TO", limit: 10_000, ct: ct).ConfigureAwait(false);

        foreach (var edge in existingSimilarToEdges)
        {
            if (edge.NodeIds.Any(changedNodeIds.Contains) && !IsRetired(edge))
            {
                await this.graphStore.RetireEdgeAsync(edge.Id, GetSinceIndex(nodes[0]), ct).ConfigureAwait(false);
            }
        }

        // (2) Generate embeddings via IEmbeddingGenerator per architecture.md §3.1.
        //     Batch all node texts and generate embeddings in one call. If the
        //     embedding service is unavailable, skip embedding and SIMILAR_TO edges
        //     but do NOT fail the sync (graceful degradation per §8).
        bool embeddingAvailable;
        try
        {
            embeddingAvailable = await this.embeddingGenerator.IsAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "IEmbeddingGenerator availability check failed — skipping embeddings");
            embeddingAvailable = false;
        }

        if (embeddingAvailable)
        {
            await this.GenerateAndStoreEmbeddingsAsync(nodes, ct).ConfigureAwait(false);
        }
        else
        {
            // Mark nodes as pending when embedding generator is unavailable
            foreach (var node in nodes)
            {
                if (GetEmbedding(node) is null)
                {
                    node.Metadata[MemoryMetadataKeys.EmbeddingStatus] = "pending";
                }
            }

            this.logger.LogInformation(
                "IEmbeddingGenerator unavailable — skipping embedding generation for {Count} nodes",
                nodes.Count);
        }

        // (3) Compute pairwise similarity between changed nodes and all active nodes.
        //     Uses the freshly generated embeddings (stored in Metadata["Embedding"]).
        //     When IVectorStore is available, also uses ANN search for efficiency.
        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            var embedding = GetEmbedding(node);
            if (embedding is null)
            {
                continue;
            }

            // Query nodes in the same memory layer for similarity comparison
            var layer = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml)
                ? ml?.ToString() : null;

            if (layer is null)
            {
                continue;
            }

            var candidates = await this.graphStore.GetNodesByLayerAsync(
                layer, limit: 1_000, ct: ct).ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                if (candidate.Id == node.Id)
                {
                    continue;
                }

                var candidateEmbedding = GetEmbedding(candidate);

                // Fallback: retrieve embedding from IVectorStore when not in memory.
                // This is essential for production where candidate nodes were embedded
                // in a previous sync session and their embeddings are persisted in the
                // vector store but not loaded into in-memory node metadata.
                if (candidateEmbedding is null && embeddingAvailable)
                {
                    candidateEmbedding = await this.TryGetEmbeddingFromVectorStoreAsync(
                        candidate.Id, ct).ConfigureAwait(false);
                    if (candidateEmbedding is not null)
                    {
                        candidate.Metadata[MemoryMetadataKeys.Embedding] = candidateEmbedding;
                    }
                }

                if (candidateEmbedding is null)
                {
                    continue;
                }

                var similarity = CosineSimilarity(embedding, candidateEmbedding);
                if (similarity >= this.options.SimilarityThreshold)
                {
                    // Extract SinceCommit/SinceIndex from the source node to stamp
                    // VersionRange metadata on the SIMILAR_TO edge. This ensures
                    // semantic edges carry temporal provenance like structural edges.
                    var sinceCommit = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sc)
                        ? sc?.ToString() : null;
                    var sinceIndex = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si)
                        && si is int idx ? (int?)idx : null;

                    var edgeId = $"similar:{node.Id}:{candidate.Id}";
                    var edge = new Hyperedge
                    {
                        Id = edgeId,
                        EdgeType = HyperedgeType.MemoryRelation,
                        NodeIds = new List<string> { node.Id, candidate.Id },
                        Roles = new Dictionary<string, string>
                        {
                            [node.Id] = "source",
                            [candidate.Id] = "target",
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            [MemoryMetadataKeys.SemanticRelation] = "SIMILAR_TO",
                            [MemoryMetadataKeys.SimilarityScore] = similarity,
                            [MemoryMetadataKeys.MemoryLayer] = "semantic",
                            [MemoryMetadataKeys.SinceCommit] = sinceCommit,
                            [MemoryMetadataKeys.SinceIndex] = sinceIndex,
                        },
                    };

                    await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
                    similarToEdgesCreated++;
                }
            }
        }

        this.logger.LogDebug(
            "SemanticIndexer: processed {NodeCount} nodes, created {EdgeCount} SIMILAR_TO edges",
            nodes.Count, similarToEdgesCreated);

        return similarToEdgesCreated;
    }

    /// <summary>
    /// Generates embeddings for nodes via IEmbeddingGenerator.GenerateBatchEmbeddingsAsync,
    /// stores them in Metadata["Embedding"], persists updated metadata back to the graph
    /// store via UpsertNodeAsync, and upserts VectorPoint records into the IVectorStore
    /// "memory_embeddings" collection per architecture.md §3.2.2.
    /// </summary>
    private async Task GenerateAndStoreEmbeddingsAsync(
        IReadOnlyList<HypergraphNode> nodes,
        CancellationToken ct)
    {
        // Build text representations for embedding: use SourceCode if available,
        // otherwise use QualifiedName + NodeType as a fallback representation.
        var texts = new List<string>(nodes.Count);
        foreach (var node in nodes)
        {
            string text;
            if (node.Metadata.TryGetValue(MemoryMetadataKeys.SourceCode, out var src) && src is string code && code.Length > 0)
            {
                text = code;
            }
            else
            {
                var qName = node.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn)
                    ? qn?.ToString() ?? node.Name : node.Name;
                text = $"{node.NodeType} {qName}";
            }

            texts.Add(text);
        }

        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await this.embeddingGenerator.GenerateBatchEmbeddingsAsync(texts, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Batch embedding generation failed for {Count} nodes — marking as pending", nodes.Count);
            foreach (var node in nodes)
            {
                node.Metadata[MemoryMetadataKeys.EmbeddingStatus] = "pending";
            }

            return;
        }

        // Store embeddings in node metadata and prepare VectorPoints.
        // Per architecture.md §3.2.2 dual-storage model: embeddings are persisted
        // to BOTH IVectorStore (for ANN search) AND the graph store (for metadata
        // queries). We use CreateNodeAsync (raw overwrite) rather than UpsertNodeAsync
        // to avoid closing the node's active VersionRange — the node already carries
        // correct SinceIndex/SinceCommit from IncrementalSync's earlier UpsertNodeAsync.
        var vectorPoints = new List<VectorPoint>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var embedding = embeddings[i];

            node.Metadata[MemoryMetadataKeys.Embedding] = embedding;
            node.Metadata[MemoryMetadataKeys.EmbeddingModel] = this.embeddingGenerator.ModelName;
            node.Metadata[MemoryMetadataKeys.EmbeddingStatus] = "generated";

            // Durably persist embedding metadata back to the graph store (§3.2.2).
            // CreateNodeAsync performs a raw key overwrite without temporal version
            // manipulation, so the node's existing VersionRange metadata is preserved
            // intact. This is essential for production stores where in-memory mutations
            // to the node object would not survive process restarts.
            try
            {
                await this.graphStore.CreateNodeAsync(node, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger.LogWarning(ex,
                    "Failed to persist embedding metadata to graph store for node {NodeId} — metadata is in-memory only",
                    node.Id);
            }

            vectorPoints.Add(new VectorPoint
            {
                Id = node.Id,
                Vector = embedding,
                Payload = new Dictionary<string, object>
                {
                    ["MemoryLayer"] = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) ? ml?.ToString() ?? "structural" : "structural",
                    ["NodeType"] = node.NodeType ?? "Unknown",
                    ["FilePath"] = node.FilePath ?? string.Empty,
                },
            });
        }

        // Upsert into IVectorStore (§3.2.2 dual-storage pattern)
        try
        {
            if (!await this.vectorStore.CollectionExistsAsync(EmbeddingsCollection, ct).ConfigureAwait(false))
            {
                await this.vectorStore.CreateCollectionAsync(
                    EmbeddingsCollection, this.embeddingGenerator.Dimensions, ct).ConfigureAwait(false);
            }

            await this.vectorStore.UpsertAsync(EmbeddingsCollection, vectorPoints, ct).ConfigureAwait(false);
            this.logger.LogDebug(
                "Upserted {Count} embeddings into IVectorStore collection '{Collection}'",
                vectorPoints.Count, EmbeddingsCollection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Vector store upsert is non-fatal — embeddings are already in node metadata.
            // They can be synced to the vector store later.
            this.logger.LogWarning(ex, "IVectorStore upsert failed for {Count} points — embeddings stored in metadata only", vectorPoints.Count);
        }
    }

    /// <summary>
    /// Retrieves a previously stored embedding from <see cref="IVectorStore"/> by node ID.
    /// Returns null on any failure (collection missing, point not found, store unavailable).
    /// This enables similarity recalculation against nodes whose embeddings were persisted
    /// in a previous sync session but are not loaded in the current in-memory graph snapshot.
    /// </summary>
    private async Task<float[]?> TryGetEmbeddingFromVectorStoreAsync(string nodeId, CancellationToken ct)
    {
        try
        {
            if (!await this.vectorStore.CollectionExistsAsync(EmbeddingsCollection, ct).ConfigureAwait(false))
            {
                return null;
            }

            var point = await this.vectorStore.GetAsync(EmbeddingsCollection, nodeId, ct).ConfigureAwait(false);
            return point?.Vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogDebug(
                ex, "Failed to retrieve embedding for node {NodeId} from IVectorStore — skipping similarity", nodeId);
            return null;
        }
    }

    private static float[]? GetEmbedding(HypergraphNode node)
    {
        if (node.Metadata.TryGetValue(MemoryMetadataKeys.Embedding, out var emb) && emb is float[] arr)
        {
            return arr;
        }

        return null;
    }

    private static int GetSinceIndex(HypergraphNode node)
    {
        if (node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var idx) && idx is int i)
        {
            return i;
        }

        return 0;
    }

    private static bool IsRetired(Hyperedge edge)
    {
        return edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var val) && val is not null;
    }

    /// <summary>
    /// Computes cosine similarity between two embedding vectors.
    /// Returns a value in [-1, 1] where 1 means identical direction.
    /// </summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator < 1e-10 ? 0.0 : dot / denominator;
    }
}