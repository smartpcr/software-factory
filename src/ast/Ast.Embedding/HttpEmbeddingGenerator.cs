// -----------------------------------------------------------------------
// <copyright file="HttpEmbeddingGenerator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Embedding generator using HTTP API (Ollama, LM Studio, or OpenAI-compatible endpoints).
/// </summary>
public sealed class HttpEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly ILogger<HttpEmbeddingGenerator> logger;
    private readonly EmbeddingGeneratorOptions options;
    private readonly HttpClient httpClient;
    private readonly JsonSerializerOptions jsonOptions;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpEmbeddingGenerator"/> class.
    /// </summary>
    /// <param name="options">The embedding options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="httpClient">Optional HTTP client (for testing).</param>
    public HttpEmbeddingGenerator(
        IOptions<EmbeddingGeneratorOptions> options,
        ILogger<HttpEmbeddingGenerator> logger,
        HttpClient? httpClient = null)
    {
        this.options = options.Value;
        this.logger = logger;
        this.httpClient = httpClient ?? new HttpClient();
        this.httpClient.Timeout = TimeSpan.FromSeconds(this.options.TimeoutSeconds);

        if (!string.IsNullOrEmpty(this.options.ApiKey))
        {
            this.httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {this.options.ApiKey}");
        }

        this.jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        this.logger.LogInformation(
            "HttpEmbeddingGenerator initialized with endpoint {Endpoint}, model {Model}",
            this.options.Endpoint,
            this.options.ModelName);
    }

    /// <inheritdoc/>
    public int Dimensions => this.options.Dimensions;

    /// <inheritdoc/>
    public string ModelName => this.options.ModelName;

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
            return this.options.Provider switch
            {
                EmbeddingProvider.OpenAI => await this.GenerateOpenAIEmbeddingAsync(text, cancellationToken),
                _ => await this.GenerateOllamaEmbeddingAsync(text, cancellationToken)
            };
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

        // Process in batches with concurrency control
        var batches = textList.Chunk(this.options.BatchSize);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Run batch in parallel with limited concurrency
            var batchTasks = batch.Select(text => this.GenerateEmbeddingAsync(text, cancellationToken));
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
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (this.disposed)
        {
            return false;
        }

        try
        {
            // Try to get a simple embedding to check availability
            var response = await this.httpClient.GetAsync(
                new Uri(new Uri(this.options.Endpoint), "/api/tags"),
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Embedding service not available at {Endpoint}", this.options.Endpoint);
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.httpClient?.Dispose();
        this.logger.LogInformation("HttpEmbeddingGenerator disposed");
    }

    private async Task<float[]> GenerateOllamaEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var request = new OllamaEmbeddingRequest
        {
            Model = this.options.ModelName,
            Prompt = text
        };

        var endpoint = new Uri(new Uri(this.options.Endpoint), "/api/embeddings");
        var response = await this.httpClient.PostAsJsonAsync(endpoint, request, this.jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(this.jsonOptions, cancellationToken);

        if (result?.Embedding == null || result.Embedding.Length == 0)
        {
            this.logger.LogWarning("Empty embedding returned from Ollama for text of length {Length}", text.Length);
            return new float[this.Dimensions];
        }

        return result.Embedding;
    }

    private async Task<float[]> GenerateOpenAIEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var request = new OpenAIEmbeddingRequest
        {
            Model = this.options.ModelName,
            Input = text
        };

        var endpoint = new Uri(new Uri(this.options.Endpoint), "/v1/embeddings");
        var response = await this.httpClient.PostAsJsonAsync(endpoint, request, this.jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>(this.jsonOptions, cancellationToken);

        if (result?.Data == null || result.Data.Count == 0 || result.Data[0].Embedding == null)
        {
            this.logger.LogWarning("Empty embedding returned from OpenAI for text of length {Length}", text.Length);
            return new float[this.Dimensions];
        }

        return result.Data[0].Embedding;
    }

    #region API Models

    private sealed class OllamaEmbeddingRequest
    {
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
    }

    private sealed class OllamaEmbeddingResponse
    {
        public float[] Embedding { get; set; } = [];
    }

    private sealed class OpenAIEmbeddingRequest
    {
        public string Model { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
    }

    private sealed class OpenAIEmbeddingResponse
    {
        public List<OpenAIEmbeddingData> Data { get; set; } = [];
    }

    private sealed class OpenAIEmbeddingData
    {
        public float[] Embedding { get; set; } = [];
        public int Index { get; set; }
    }

    #endregion
}
