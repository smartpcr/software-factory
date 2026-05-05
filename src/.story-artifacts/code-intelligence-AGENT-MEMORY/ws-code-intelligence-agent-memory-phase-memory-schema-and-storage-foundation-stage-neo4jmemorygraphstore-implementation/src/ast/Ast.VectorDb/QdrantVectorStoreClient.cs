// -----------------------------------------------------------------------
// <copyright file="QdrantVectorStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

using System.Security.Cryptography;
using System.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;

/// <summary>
/// Qdrant-backed implementation of <see cref="IVectorStoreClient"/>.
/// Provides typed methods for embedding upsert, retrieval, and similarity search.
/// Designed for composition into <c>Neo4jMemoryGraphStore</c>.
/// </summary>
public sealed class QdrantVectorStore : IVectorStoreClient
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private bool _disposed;

    public QdrantVectorStore(string endpoint, string collectionName = "memory_embeddings")
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _client = new QdrantClient(endpoint);
        _collectionName = collectionName;
    }

    /// <summary>
    /// Constructor accepting a pre-built <see cref="QdrantClient"/> (for testing).
    /// </summary>
    public QdrantVectorStore(QdrantClient client, string collectionName = "memory_embeddings")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _collectionName = collectionName;
    }

    /// <summary>
    /// Ensures the target collection exists with the specified vector dimension.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public async Task InitializeAsync(int dimension, CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct).ConfigureAwait(false);
        if (collections.Any(c => c == _collectionName))
        {
            return;
        }

        await _client.CreateCollectionAsync(
            _collectionName,
            new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine },
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts an embedding vector for the given node ID.
    /// The node ID is deterministically hashed to a Qdrant point ID (GUID).
    /// </summary>
    public async Task UpsertVectorAsync(string nodeId, float[] embedding, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(embedding);

        var pointId = NodeIdToGuid(nodeId);
        var point = new PointStruct
        {
            Id = new PointId { Uuid = pointId.ToString() },
            Vectors = embedding,
            Payload =
            {
                ["node_id"] = nodeId,
            },
        };

        await _client.UpsertAsync(_collectionName, [point], cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the embedding for a single node ID. Returns null if not found.
    /// </summary>
    public async Task<float[]?> GetVectorAsync(string nodeId, CancellationToken ct = default)
    {
        var pointId = NodeIdToGuid(nodeId);
        var points = await _client.RetrieveAsync(
            _collectionName,
            [new PointId { Uuid = pointId.ToString() }],
            withVectors: true,
            cancellationToken: ct).ConfigureAwait(false);

        if (points.Count == 0)
        {
            return null;
        }

        return points[0].Vectors.Vector.Data.ToArray();
    }

    /// <summary>
    /// Searches for the most similar embeddings and returns (nodeId, score) pairs.
    /// </summary>
    public async Task<IReadOnlyList<(string NodeId, double Score)>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        CancellationToken ct = default)
    {
        var results = await _client.SearchAsync(
            _collectionName,
            queryEmbedding,
            limit: (ulong)topK,
            cancellationToken: ct).ConfigureAwait(false);

        return results
            .Where(r => r.Payload.ContainsKey("node_id"))
            .Select(r => (
                NodeId: r.Payload["node_id"].StringValue,
                Score: (double)r.Score))
            .ToList();
    }

    /// <summary>
    /// Deterministically hashes a node ID string to a GUID for use as a Qdrant point ID.
    /// </summary>
    internal static Guid NodeIdToGuid(string nodeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nodeId));
        return new Guid(hash[..16]);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
