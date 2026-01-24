// -----------------------------------------------------------------------
// <copyright file="LLamaEmbeddingGenerator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Embedding generator using local LLamaSharp models.
/// </summary>
public sealed class LLamaEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly ILogger<LLamaEmbeddingGenerator> logger;
    private readonly EmbeddingGeneratorOptions options;
    private readonly LLamaWeights model;
    private readonly LLamaEmbedder embedder;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LLamaEmbeddingGenerator"/> class.
    /// </summary>
    /// <param name="options">The embedding options.</param>
    /// <param name="logger">The logger.</param>
    public LLamaEmbeddingGenerator(
        IOptions<EmbeddingGeneratorOptions> options,
        ILogger<LLamaEmbeddingGenerator> logger)
    {
        this.options = options.Value;
        this.logger = logger;

        if (string.IsNullOrEmpty(this.options.ModelPath))
        {
            throw new ArgumentException("ModelPath must be specified for LLama provider", nameof(options));
        }

        if (!File.Exists(this.options.ModelPath))
        {
            throw new FileNotFoundException($"Model file not found: {this.options.ModelPath}");
        }

        this.logger.LogInformation(
            "Loading LLamaSharp model from {ModelPath} with {GpuLayers} GPU layers",
            this.options.ModelPath,
            this.options.GpuLayerCount);

        var parameters = new ModelParams(this.options.ModelPath)
        {
            ContextSize = (uint)this.options.ContextSize,
            GpuLayerCount = this.options.GpuLayerCount,
            UseMemorymap = this.options.UseMemoryMap,
            Embeddings = true
        };

        this.model = LLamaWeights.LoadFromFile(parameters);
        this.embedder = new LLamaEmbedder(this.model, parameters);

        this.logger.LogInformation("LLamaSharp model loaded successfully");
    }

    /// <inheritdoc/>
    public int Dimensions => this.options.Dimensions;

    /// <inheritdoc/>
    public string ModelName => Path.GetFileNameWithoutExtension(this.options.ModelPath) ?? "unknown";

    /// <inheritdoc/>
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[this.Dimensions];
        }

        try
        {
            // LLamaSharp's GetEmbeddings returns a list of embeddings (one per input)
            var embeddingsList = await Task.Run(() => this.embedder.GetEmbeddings(text), cancellationToken);

            if (embeddingsList == null || embeddingsList.Count == 0)
            {
                this.logger.LogWarning("Empty embedding returned for text of length {Length}", text.Length);
                return new float[this.Dimensions];
            }

            // Get the first embedding (we only passed one text)
            var embedding = embeddingsList[0];

            // Log if dimensions don't match
            if (embedding.Length != this.Dimensions)
            {
                this.logger.LogDebug(
                    "Embedding dimension mismatch: expected {Expected}, got {Actual}",
                    this.Dimensions,
                    embedding.Length);
            }

            return embedding;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error generating embedding for text of length {Length}", text.Length);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var textList = texts.ToList();
        var results = new List<float[]>(textList.Count);

        // Process in batches
        var batches = textList
            .Select((text, index) => (text, index))
            .Chunk(this.options.BatchSize);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchTasks = batch.Select(item =>
                this.GenerateEmbeddingAsync(item.text, cancellationToken));

            var batchResults = await Task.WhenAll(batchTasks);
            results.AddRange(batchResults);

            this.logger.LogDebug(
                "Processed batch of {BatchSize} embeddings, total: {Total}",
                batch.Length,
                results.Count);
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!this.disposed && this.embedder != null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.embedder?.Dispose();
        this.model?.Dispose();
        this.logger.LogInformation("LLamaEmbeddingGenerator disposed");
    }
}