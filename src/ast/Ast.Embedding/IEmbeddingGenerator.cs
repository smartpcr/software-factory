// -----------------------------------------------------------------------
// <copyright file="IEmbeddingGenerator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

/// <summary>
/// Interface for generating vector embeddings from text.
/// </summary>
public interface IEmbeddingGenerator : IDisposable
{
    /// <summary>
    /// Gets the dimension of the embedding vectors produced by this generator.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Gets the name of the model being used.
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// Generates an embedding for a single text input.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector.</returns>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple text inputs in batch.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vectors in the same order as inputs.</returns>
    Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the embedding service is available and ready.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the service is ready.</returns>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
