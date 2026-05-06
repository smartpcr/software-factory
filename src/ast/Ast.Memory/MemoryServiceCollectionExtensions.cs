// -----------------------------------------------------------------------
// <copyright file="MemoryServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods to register memory subsystem services in a DI container.
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers all memory-layer services using the default in-memory graph store.
    /// </summary>
    public static IServiceCollection AddMemoryServices(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryGraphStore, InMemoryMemoryGraphStore>();
        return services.AddCoreMemoryServices();
    }

    /// <summary>
    /// Registers all memory-layer services with a custom store path for the in-memory store.
    /// </summary>
    public static IServiceCollection AddMemoryServices(this IServiceCollection services, string storePath)
    {
        services.AddSingleton<IMemoryGraphStore>(_ => new InMemoryMemoryGraphStore(storePath));
        return services.AddCoreMemoryServices();
    }

    /// <summary>
    /// Registers all memory-layer services using the graph store backend
    /// selected by <see cref="MemoryOptions.Backend"/>.
    /// Note: Stage 6.2 uses a JSON-backed file store for all backends to keep
    /// the CLI self-contained. SQLite/Neo4j backends are planned for future stages.
    /// </summary>
    public static IServiceCollection AddMemoryServices(this IServiceCollection services, MemoryOptions options)
    {
        // Stage 6.2: JSON file-backed store for all backends (self-contained CLI).
        // Future stages will add SqliteGraphStore and Neo4jMemoryGraphStore implementations.
        var storePath = options.Backend switch
        {
            MemoryBackend.Neo4j => options.Neo4jUri ?? "memory-graph.json",
            _ => options.SqlitePath ?? "memory-graph.json",
        };
        services.AddSingleton<IMemoryGraphStore>(_ => new InMemoryMemoryGraphStore(storePath));

        return services.AddCoreMemoryServices();
    }

    private static IServiceCollection AddCoreMemoryServices(this IServiceCollection services)
    {
        services.AddSingleton<IExtractionPipeline, DefaultExtractionPipeline>();
        services.AddSingleton<IMemoryRetrievalEngine, DefaultMemoryRetrievalEngine>();
        services.AddSingleton<IIncrementalSync>(sp =>
            new DefaultIncrementalSync(
                sp.GetRequiredService<IMemoryGraphStore>(),
                sp.GetRequiredService<IExtractionPipeline>()));
        services.AddSingleton<IEvolutionEngine>(sp =>
            new DefaultEvolutionEngine(
                sp.GetRequiredService<IMemoryGraphStore>(),
                sp.GetService<Ast.Embedding.IEmbeddingGenerator>()));
        return services;
    }
}