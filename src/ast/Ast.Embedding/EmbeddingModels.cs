// -----------------------------------------------------------------------
// <copyright file="EmbeddingModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

/// <summary>
/// Represents an embedding result with associated metadata.
/// </summary>
public class EmbeddingResult
{
    /// <summary>
    /// Gets or sets the unique identifier for this embedding.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the original text that was embedded.
    /// </summary>
    public required string Text { get; set; }

    /// <summary>
    /// Gets or sets the embedding vector.
    /// </summary>
    public required float[] Vector { get; set; }

    /// <summary>
    /// Gets or sets the model used to generate this embedding.
    /// </summary>
    public required string Model { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this embedding was generated.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets optional metadata associated with this embedding.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Represents a code element with its embedding.
/// </summary>
public class CodeEmbedding
{
    /// <summary>
    /// Gets or sets the node ID from the hypergraph.
    /// </summary>
    public required string NodeId { get; set; }

    /// <summary>
    /// Gets or sets the file path where the code element is defined.
    /// </summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// Gets or sets the type of code element (class, method, function, etc.).
    /// </summary>
    public required string ElementType { get; set; }

    /// <summary>
    /// Gets or sets the name of the code element.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the source code or signature that was embedded.
    /// </summary>
    public required string SourceText { get; set; }

    /// <summary>
    /// Gets or sets the embedding vector.
    /// </summary>
    public required float[] Vector { get; set; }

    /// <summary>
    /// Gets or sets the start line number in the source file.
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// Gets or sets the end line number in the source file.
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// Gets or sets the programming language.
    /// </summary>
    public required string Language { get; set; }
}

/// <summary>
/// Configuration for embedding generation that extends the base configuration.
/// </summary>
public class EmbeddingGeneratorOptions
{
    /// <summary>
    /// Gets or sets the provider type (LLama, Http, OpenAI).
    /// </summary>
    public EmbeddingProvider Provider { get; set; } = EmbeddingProvider.Http;

    /// <summary>
    /// Gets or sets the path to the local GGUF model file (for LLama provider).
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Gets or sets the API endpoint (for Http provider).
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Gets or sets the model name.
    /// </summary>
    public string ModelName { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Gets or sets the embedding dimensions.
    /// </summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>
    /// Gets or sets the batch size for embedding generation.
    /// </summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets the request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the number of GPU layers to offload (for LLama provider).
    /// -1 means all layers on GPU.
    /// </summary>
    public int GpuLayerCount { get; set; } = -1;

    /// <summary>
    /// Gets or sets the context size (for LLama provider).
    /// </summary>
    public int ContextSize { get; set; } = 2048;

    /// <summary>
    /// Gets or sets whether to use memory mapping (for LLama provider).
    /// </summary>
    public bool UseMemoryMap { get; set; } = true;

    /// <summary>
    /// Gets or sets the API key (for providers that require authentication).
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Embedding provider types.
/// </summary>
public enum EmbeddingProvider
{
    /// <summary>
    /// Local LLamaSharp-based embedding generation.
    /// </summary>
    LLama,

    /// <summary>
    /// HTTP API-based embedding (Ollama, LM Studio, etc.).
    /// </summary>
    Http,

    /// <summary>
    /// OpenAI-compatible API.
    /// </summary>
    OpenAI
}