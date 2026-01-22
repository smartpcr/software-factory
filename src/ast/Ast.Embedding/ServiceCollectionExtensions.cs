// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

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
}
