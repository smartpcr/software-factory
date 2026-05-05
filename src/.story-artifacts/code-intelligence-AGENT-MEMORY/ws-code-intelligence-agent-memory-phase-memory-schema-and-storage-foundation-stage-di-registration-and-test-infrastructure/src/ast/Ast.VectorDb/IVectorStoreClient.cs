// -----------------------------------------------------------------------
// <copyright file="IVectorStoreClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

/// <summary>
/// Abstraction over a vector store for embedding storage, retrieval,
/// and similarity search. Designed for composition into graph stores.
/// </summary>
public interface IVectorStoreClient : IAsyncDisposable
{
    /// <summary>
    /// Ensures the target collection exists with the specified vector dimension.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task InitializeAsync(int dimension, CancellationToken ct = default);

    /// <summary>
    /// Upserts an embedding vector for the given node ID.
    /// </summary>
    Task UpsertVectorAsync(string nodeId, float[] embedding, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the embedding for a single node ID. Returns null if not found.
    /// </summary>
    Task<float[]?> GetVectorAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Searches for the most similar embeddings and returns (nodeId, score) pairs.
    /// </summary>
    Task<IReadOnlyList<(string NodeId, double Score)>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        CancellationToken ct = default);
}
