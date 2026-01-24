// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering vector store services with DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Qdrant vector store services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure vector store options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddQdrantVectorStore(
        this IServiceCollection services,
        Action<VectorStoreOptions>? configure = null)
    {
        var options = new VectorStoreOptions();
        configure?.Invoke(options);

        services.Configure<VectorStoreOptions>(opt =>
        {
            opt.Host = options.Host;
            opt.HttpPort = options.HttpPort;
            opt.GrpcPort = options.GrpcPort;
            opt.UseGrpc = options.UseGrpc;
            opt.UseTls = options.UseTls;
            opt.ApiKey = options.ApiKey;
            opt.NodesCollection = options.NodesCollection;
            opt.EdgesCollection = options.EdgesCollection;
            opt.DefaultVectorSize = options.DefaultVectorSize;
            opt.Distance = options.Distance;
            opt.TimeoutSeconds = options.TimeoutSeconds;
        });

        services.TryAddSingleton<IVectorStore, QdrantVectorStore>();
        services.TryAddSingleton<CodeVectorService>();

        return services;
    }

    /// <summary>
    /// Adds Qdrant vector store with default local configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="host">The Qdrant host.</param>
    /// <param name="grpcPort">The gRPC port.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLocalQdrant(
        this IServiceCollection services,
        string host = "localhost",
        int grpcPort = 6334)
    {
        return services.AddQdrantVectorStore(options =>
        {
            options.Host = host;
            options.GrpcPort = grpcPort;
            options.UseGrpc = true;
            options.UseTls = false;
        });
    }

    /// <summary>
    /// Adds Qdrant vector store with cloud configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="host">The Qdrant cloud host.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="grpcPort">The gRPC port.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddQdrantCloud(
        this IServiceCollection services,
        string host,
        string apiKey,
        int grpcPort = 6334)
    {
        return services.AddQdrantVectorStore(options =>
        {
            options.Host = host;
            options.GrpcPort = grpcPort;
            options.UseGrpc = true;
            options.UseTls = true;
            options.ApiKey = apiKey;
        });
    }
}