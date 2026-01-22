// -----------------------------------------------------------------------
// <copyright file="CodeEmbeddingService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

using Ast.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service for generating embeddings from code hypergraph data.
/// </summary>
public sealed class CodeEmbeddingService : IDisposable
{
    private readonly IEmbeddingGenerator generator;
    private readonly ILogger<CodeEmbeddingService> logger;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeEmbeddingService"/> class.
    /// </summary>
    /// <param name="generator">The embedding generator to use.</param>
    /// <param name="logger">The logger.</param>
    public CodeEmbeddingService(
        IEmbeddingGenerator generator,
        ILogger<CodeEmbeddingService> logger)
    {
        this.generator = generator;
        this.logger = logger;
    }

    /// <summary>
    /// Gets the embedding generator being used.
    /// </summary>
    public IEmbeddingGenerator Generator => this.generator;

    /// <summary>
    /// Generates embeddings for all nodes in a hypergraph.
    /// </summary>
    /// <param name="data">The hypergraph data.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of code embeddings.</returns>
    public async Task<IReadOnlyList<CodeEmbedding>> GenerateNodeEmbeddingsAsync(
        HypergraphData data,
        IProgress<EmbeddingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var nodes = data.Nodes.Values.ToList();
        var results = new List<CodeEmbedding>(nodes.Count);
        var processedCount = 0;

        this.logger.LogInformation("Generating embeddings for {NodeCount} nodes", nodes.Count);

        // Prepare texts for batch processing
        var nodeTexts = nodes.Select(n => n.GetEmbeddingText()).ToList();

        // Generate embeddings in batches
        var embeddings = await this.generator.GenerateBatchEmbeddingsAsync(nodeTexts, cancellationToken);

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var embedding = embeddings[i];

            results.Add(new CodeEmbedding
            {
                NodeId = node.Id,
                FilePath = node.FilePath,
                ElementType = node.NodeType,
                Name = node.Name,
                SourceText = nodeTexts[i],
                Vector = embedding,
                StartLine = node.LineStart,
                EndLine = node.LineEnd,
                Language = node.Language
            });

            processedCount++;

            if (processedCount % 100 == 0)
            {
                progress?.Report(new EmbeddingProgress
                {
                    TotalItems = nodes.Count,
                    ProcessedItems = processedCount,
                    CurrentItem = node.Name
                });

                this.logger.LogDebug(
                    "Generated embeddings for {Processed}/{Total} nodes",
                    processedCount,
                    nodes.Count);
            }
        }

        this.logger.LogInformation("Completed generating embeddings for {NodeCount} nodes", results.Count);
        return results;
    }

    /// <summary>
    /// Generates embeddings for all hyperedges in a hypergraph.
    /// </summary>
    /// <param name="data">The hypergraph data.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of embedding results for edges.</returns>
    public async Task<IReadOnlyList<EmbeddingResult>> GenerateEdgeEmbeddingsAsync(
        HypergraphData data,
        IProgress<EmbeddingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var edges = data.Hyperedges;
        var results = new List<EmbeddingResult>(edges.Count);
        var processedCount = 0;

        this.logger.LogInformation("Generating embeddings for {EdgeCount} hyperedges", edges.Count);

        // Prepare texts for batch processing
        var edgeTexts = edges.Select(e => e.GetEmbeddingText()).ToList();

        // Generate embeddings in batches
        var embeddings = await this.generator.GenerateBatchEmbeddingsAsync(edgeTexts, cancellationToken);

        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            var embedding = embeddings[i];

            results.Add(new EmbeddingResult
            {
                Id = edge.Id,
                Text = edgeTexts[i],
                Vector = embedding,
                Model = this.generator.ModelName,
                Metadata = new Dictionary<string, object>
                {
                    ["edge_type"] = edge.EdgeType.ToString(),
                    ["cardinality"] = edge.Cardinality,
                    ["is_cross_language"] = edge.IsCrossLanguage
                }
            });

            processedCount++;

            if (processedCount % 100 == 0)
            {
                progress?.Report(new EmbeddingProgress
                {
                    TotalItems = edges.Count,
                    ProcessedItems = processedCount,
                    CurrentItem = edge.EdgeType.ToString()
                });
            }
        }

        this.logger.LogInformation("Completed generating embeddings for {EdgeCount} hyperedges", results.Count);
        return results;
    }

    /// <summary>
    /// Generates an embedding for a code query string.
    /// </summary>
    /// <param name="query">The query string (natural language or code snippet).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query embedding.</returns>
    public async Task<float[]> GenerateQueryEmbeddingAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        return await this.generator.GenerateEmbeddingAsync(query, cancellationToken);
    }

    /// <summary>
    /// Calculates the cosine similarity between two embedding vectors.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>Cosine similarity between 0 and 1.</returns>
    public static float CosineSimilarity(float[] a, float[] b)
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

    /// <summary>
    /// Finds the most similar embeddings to a query.
    /// </summary>
    /// <param name="queryVector">The query embedding vector.</param>
    /// <param name="candidates">Candidate embeddings to search.</param>
    /// <param name="topK">Number of results to return.</param>
    /// <returns>Top K similar embeddings with their similarity scores.</returns>
    public static IReadOnlyList<(CodeEmbedding Embedding, float Similarity)> FindSimilar(
        float[] queryVector,
        IEnumerable<CodeEmbedding> candidates,
        int topK = 10)
    {
        return candidates
            .Select(e => (Embedding: e, Similarity: CosineSimilarity(queryVector, e.Vector)))
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .ToList();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.generator?.Dispose();
        this.logger.LogInformation("CodeEmbeddingService disposed");
    }
}

/// <summary>
/// Progress information for embedding generation.
/// </summary>
public class EmbeddingProgress
{
    /// <summary>
    /// Gets or sets the total number of items to process.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the number of items processed so far.
    /// </summary>
    public int ProcessedItems { get; set; }

    /// <summary>
    /// Gets or sets the name of the current item being processed.
    /// </summary>
    public string CurrentItem { get; set; } = string.Empty;

    /// <summary>
    /// Gets the completion percentage.
    /// </summary>
    public double PercentComplete => TotalItems == 0 ? 0 : (double)ProcessedItems / TotalItems * 100;
}
