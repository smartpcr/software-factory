// -----------------------------------------------------------------------
// <copyright file="IVectorStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

/// <summary>
/// Interface for vector database operations.
/// </summary>
public interface IVectorStore : IDisposable
{
    /// <summary>
    /// Gets the name of the vector store implementation.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Creates a collection for storing vectors.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="vectorSize">The dimension of vectors to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateCollectionAsync(
        string collectionName,
        int vectorSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a collection exists.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the collection exists.</returns>
    Task<bool> CollectionExistsAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteCollectionAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts vectors into the collection.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="points">The points to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertAsync(
        string collectionName,
        IEnumerable<VectorPoint> points,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for similar vectors.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="queryVector">The query vector.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="filter">Optional filter conditions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results ordered by similarity.</returns>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        int limit = 10,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a point by its ID.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="id">The point ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The point if found, null otherwise.</returns>
    Task<VectorPoint?> GetAsync(
        string collectionName,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes points by their IDs.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="ids">The point IDs to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(
        string collectionName,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of points in a collection.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of points.</returns>
    Task<long> CountAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the vector store is available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the store is available.</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}