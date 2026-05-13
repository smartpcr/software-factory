// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.GraphDb;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

/// <summary>
/// DI registration for the Ast.Memory subsystem.
/// Registers <see cref="IMemoryGraphStore"/>, <see cref="ILlmClient"/>,
/// and <see cref="IEvolutionEngine"/> with their production implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the memory graph subsystem with Neo4j backing store.
    /// This is the production (team / CI) wiring per architecture.md §3.2.1.
    /// Registers <see cref="IGraphStore"/> (via <see cref="Neo4jGraphStore"/>)
    /// and wraps it with <see cref="Neo4jMemoryGraphStore"/> for memory-specific operations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureStore">Configure Neo4j connection options.</param>
    /// <param name="configureMemory">Configure memory options (LLM endpoint, thresholds).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMemoryGraph(
        this IServiceCollection services,
        Action<Neo4jMemoryStoreOptions> configureStore,
        Action<MemoryOptions>? configureMemory = null)
    {
        services.Configure(configureStore);

        if (configureMemory is not null)
        {
            services.Configure(configureMemory);
        }

        // Neo4j driver — singleton, thread-safe
        services.TryAddSingleton<IDriver>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Neo4jMemoryStoreOptions>>().Value;
            return GraphDatabase.Driver(
                opts.Uri,
                AuthTokens.Basic(opts.Username, opts.Password));
        });

        // Register canonical IGraphStore for delegation (shared driver)
        services.TryAddSingleton<IGraphStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Neo4jMemoryStoreOptions>>().Value;
            var graphOpts = Microsoft.Extensions.Options.Options.Create(new GraphStoreOptions
            {
                Uri = opts.Uri,
                Username = opts.Username,
                Password = opts.Password,
                Database = opts.Database
            });
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Neo4jGraphStore>();
            return new Neo4jGraphStore(graphOpts, logger);
        });

        // Production stores and engines
        services.TryAddSingleton<IMemoryGraphStore, Neo4jMemoryGraphStore>();
        services.TryAddTransient<IEvolutionEngine, EvolutionEngine>();

        // LLM client — uses a dedicated HttpClient
        services.TryAddSingleton<ILlmClient>(sp =>
        {
            var memoryOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryOptions>>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OllamaLlmClient>();
            var httpClient = new HttpClient();
            return new OllamaLlmClient(httpClient, memoryOptions, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds the memory graph subsystem with a custom <see cref="IMemoryGraphStore"/>
    /// implementation (for local/SQLite mode or testing with a real store).
    /// </summary>
    /// <typeparam name="TStore">The store implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureMemory">Configure memory options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMemoryGraph<TStore>(
        this IServiceCollection services,
        Action<MemoryOptions>? configureMemory = null)
        where TStore : class, IMemoryGraphStore
    {
        if (configureMemory is not null)
        {
            services.Configure(configureMemory);
        }

        services.TryAddSingleton<IMemoryGraphStore, TStore>();
        services.TryAddTransient<IEvolutionEngine, EvolutionEngine>();

        services.TryAddSingleton<ILlmClient>(sp =>
        {
            var memoryOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryOptions>>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<OllamaLlmClient>();
            var httpClient = new HttpClient();
            return new OllamaLlmClient(httpClient, memoryOptions, logger);
        });

        return services;
    }
}