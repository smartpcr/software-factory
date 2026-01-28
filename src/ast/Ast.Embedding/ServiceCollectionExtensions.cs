// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

using Ast.Lsp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering embedding services with DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds embedding services to the service collection using HTTP provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure embedding options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEmbeddingServices(
        this IServiceCollection services,
        Action<EmbeddingGeneratorOptions>? configure = null)
    {
        var options = new EmbeddingGeneratorOptions();
        configure?.Invoke(options);

        services.Configure<EmbeddingGeneratorOptions>(opt =>
        {
            opt.Provider = options.Provider;
            opt.ModelPath = options.ModelPath;
            opt.Endpoint = options.Endpoint;
            opt.ModelName = options.ModelName;
            opt.Dimensions = options.Dimensions;
            opt.BatchSize = options.BatchSize;
            opt.TimeoutSeconds = options.TimeoutSeconds;
            opt.GpuLayerCount = options.GpuLayerCount;
            opt.ContextSize = options.ContextSize;
            opt.UseMemoryMap = options.UseMemoryMap;
            opt.ApiKey = options.ApiKey;
        });

        services.TryAddSingleton<EmbeddingGeneratorFactory>();

        // Register the appropriate generator based on provider
        services.TryAddSingleton<IEmbeddingGenerator>(sp =>
        {
            var factory = sp.GetRequiredService<EmbeddingGeneratorFactory>();
            return factory.Create(options);
        });

        services.TryAddSingleton<CodeEmbeddingService>();

        return services;
    }

    /// <summary>
    /// Adds HTTP-based embedding services (Ollama, LM Studio).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The API endpoint.</param>
    /// <param name="modelName">The model name.</param>
    /// <param name="dimensions">The embedding dimensions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpEmbedding(
        this IServiceCollection services,
        string endpoint = "http://localhost:11434",
        string modelName = "nomic-embed-text",
        int dimensions = 768)
    {
        return services.AddEmbeddingServices(options =>
        {
            options.Provider = EmbeddingProvider.Http;
            options.Endpoint = endpoint;
            options.ModelName = modelName;
            options.Dimensions = dimensions;
        });
    }

    /// <summary>
    /// Adds LLamaSharp-based embedding services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="modelPath">Path to the GGUF model file.</param>
    /// <param name="dimensions">The embedding dimensions.</param>
    /// <param name="gpuLayers">Number of GPU layers (-1 for all).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLLamaEmbedding(
        this IServiceCollection services,
        string modelPath,
        int dimensions = 768,
        int gpuLayers = -1)
    {
        return services.AddEmbeddingServices(options =>
        {
            options.Provider = EmbeddingProvider.LLama;
            options.ModelPath = modelPath;
            options.Dimensions = dimensions;
            options.GpuLayerCount = gpuLayers;
        });
    }

    /// <summary>
    /// Adds OpenAI-compatible embedding services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The API endpoint.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="modelName">The model name.</param>
    /// <param name="dimensions">The embedding dimensions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOpenAIEmbedding(
        this IServiceCollection services,
        string endpoint,
        string apiKey,
        string modelName = "text-embedding-3-small",
        int dimensions = 1536)
    {
        return services.AddEmbeddingServices(options =>
        {
            options.Provider = EmbeddingProvider.OpenAI;
            options.Endpoint = endpoint;
            options.ApiKey = apiKey;
            options.ModelName = modelName;
            options.Dimensions = dimensions;
        });
    }

    /// <summary>
    /// Adds the enriched embedding pipeline with LSP integration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureEmbedding">Action to configure embedding options.</param>
    /// <param name="configureLsp">Action to configure LSP options.</param>
    /// <param name="configurePipeline">Action to configure pipeline options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEnrichedEmbeddingPipeline(
        this IServiceCollection services,
        Action<EmbeddingGeneratorOptions>? configureEmbedding = null,
        Action<LspConfiguration>? configureLsp = null,
        Action<EnrichedEmbeddingOptions>? configurePipeline = null)
    {
        // Add embedding services
        services.AddEmbeddingServices(configureEmbedding);

        // Add LSP services
        services.AddLspServices(configureLsp);

        // Configure pipeline options
        var pipelineOptions = new EnrichedEmbeddingOptions();
        configurePipeline?.Invoke(pipelineOptions);

        services.Configure<EnrichedEmbeddingOptions>(opt =>
        {
            opt.EnableLspEnrichment = pipelineOptions.EnableLspEnrichment;
            opt.BatchSize = pipelineOptions.BatchSize;
            opt.UseParallelProcessing = pipelineOptions.UseParallelProcessing;
            opt.MaxDegreeOfParallelism = pipelineOptions.MaxDegreeOfParallelism;
            opt.ContinueOnEnrichmentErrors = pipelineOptions.ContinueOnEnrichmentErrors;
            opt.EnableCaching = pipelineOptions.EnableCaching;
            opt.CacheDirectory = pipelineOptions.CacheDirectory;
        });

        // Register the pipeline
        services.TryAddSingleton<EnrichedEmbeddingPipeline>();

        return services;
    }

    /// <summary>
    /// Adds the enriched embedding pipeline with HTTP embedding and C# LSP.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="embeddingEndpoint">The embedding API endpoint.</param>
    /// <param name="embeddingModel">The embedding model name.</param>
    /// <param name="csharpLspServerType">The C# LSP server type.</param>
    /// <param name="csharpLspServerPath">Optional path to the C# LSP server.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCSharpEnrichedEmbedding(
        this IServiceCollection services,
        string embeddingEndpoint = "http://localhost:11434",
        string embeddingModel = "nomic-embed-text",
        CSharpLspServerType csharpLspServerType = CSharpLspServerType.CSharpLs,
        string? csharpLspServerPath = null)
    {
        return services.AddEnrichedEmbeddingPipeline(
            embedding =>
            {
                embedding.Provider = EmbeddingProvider.Http;
                embedding.Endpoint = embeddingEndpoint;
                embedding.ModelName = embeddingModel;
            },
            lsp =>
            {
                lsp.Enabled = true;
                lsp.CSharp.Enabled = true;
                lsp.CSharp.ServerType = csharpLspServerType;
                if (csharpLspServerPath != null)
                {
                    lsp.CSharp.ServerPath = csharpLspServerPath;
                }
                lsp.PowerShell.Enabled = false;
            });
    }

    /// <summary>
    /// Adds the enriched embedding pipeline with HTTP embedding and PowerShell LSP.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="embeddingEndpoint">The embedding API endpoint.</param>
    /// <param name="embeddingModel">The embedding model name.</param>
    /// <param name="powerShellPath">Optional path to the PowerShell executable.</param>
    /// <param name="editorServicesPath">Optional path to PowerShellEditorServices.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPowerShellEnrichedEmbedding(
        this IServiceCollection services,
        string embeddingEndpoint = "http://localhost:11434",
        string embeddingModel = "nomic-embed-text",
        string? powerShellPath = null,
        string? editorServicesPath = null)
    {
        return services.AddEnrichedEmbeddingPipeline(
            embedding =>
            {
                embedding.Provider = EmbeddingProvider.Http;
                embedding.Endpoint = embeddingEndpoint;
                embedding.ModelName = embeddingModel;
            },
            lsp =>
            {
                lsp.Enabled = true;
                lsp.CSharp.Enabled = false;
                lsp.PowerShell.Enabled = true;
                if (powerShellPath != null)
                {
                    lsp.PowerShell.PowerShellPath = powerShellPath;
                }
                if (editorServicesPath != null)
                {
                    lsp.PowerShell.EditorServicesPath = editorServicesPath;
                }
            });
    }

    /// <summary>
    /// Adds the enriched embedding pipeline with HTTP embedding and both C# and PowerShell LSP.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="embeddingEndpoint">The embedding API endpoint.</param>
    /// <param name="embeddingModel">The embedding model name.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFullEnrichedEmbedding(
        this IServiceCollection services,
        string embeddingEndpoint = "http://localhost:11434",
        string embeddingModel = "nomic-embed-text")
    {
        return services.AddEnrichedEmbeddingPipeline(
            embedding =>
            {
                embedding.Provider = EmbeddingProvider.Http;
                embedding.Endpoint = embeddingEndpoint;
                embedding.ModelName = embeddingModel;
            },
            lsp =>
            {
                lsp.Enabled = true;
                lsp.CSharp.Enabled = true;
                lsp.PowerShell.Enabled = true;
            });
    }

    /// <summary>
    /// Adds embedding services without LSP enrichment (embedding only).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureEmbedding">Action to configure embedding options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEmbeddingOnlyPipeline(
        this IServiceCollection services,
        Action<EmbeddingGeneratorOptions>? configureEmbedding = null)
    {
        // Add embedding services
        services.AddEmbeddingServices(configureEmbedding);

        // Configure pipeline with LSP disabled
        services.Configure<EnrichedEmbeddingOptions>(opt =>
        {
            opt.EnableLspEnrichment = false;
        });

        // Register the pipeline without LSP service
        services.TryAddSingleton<EnrichedEmbeddingPipeline>();

        return services;
    }
}