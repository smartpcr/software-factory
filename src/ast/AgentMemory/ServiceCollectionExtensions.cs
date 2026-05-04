// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering agent memory services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds agent memory services with the in-memory store.
    /// The store is registered as a singleton (shared across the process lifetime),
    /// while services are transient (safe to create per-scope).
    /// </summary>
    public static IServiceCollection AddAgentMemory(this IServiceCollection services)
    {
        // Register the in-memory store as singleton for all three interfaces
        services.AddSingleton<InMemoryAgentMemoryStore>();
        services.AddSingleton<IAgentMemoryStore>(sp => sp.GetRequiredService<InMemoryAgentMemoryStore>());
        services.AddSingleton<ITieredExpertiseStore>(sp => sp.GetRequiredService<InMemoryAgentMemoryStore>());
        services.AddSingleton<ICallChainStore>(sp => sp.GetRequiredService<InMemoryAgentMemoryStore>());

        // Register services as transient
        services.AddTransient<IEpisodeRecorder, EpisodeRecorder>();
        services.AddTransient<IMemoryEvolver, MemoryEvolver>();
        services.AddTransient<IContextBuilder>(sp =>
        {
            var memoryStore = sp.GetRequiredService<IAgentMemoryStore>();
            var graphAdapter = sp.GetService<IGraphStoreAdapter>();
            var callChainStore = sp.GetService<ICallChainStore>();
            return new ContextBuilder(memoryStore, graphAdapter, callChainStore);
        });

        return services;
    }

    /// <summary>
    /// Adds agent memory services with file-system persistence.
    /// Memory is stored in JSON files at the specified path and survives process restarts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="storagePath">Directory path where memory files will be stored.</param>
    public static IServiceCollection AddAgentMemoryWithFileStorage(
        this IServiceCollection services,
        string storagePath)
    {
        var store = new FileSystemAgentMemoryStore(storagePath);
        services.AddSingleton<IAgentMemoryStore>(store);
        services.AddSingleton<ITieredExpertiseStore>(store);
        services.AddSingleton<ICallChainStore>(store);

        services.AddTransient<IEpisodeRecorder, EpisodeRecorder>();
        services.AddTransient<IMemoryEvolver, MemoryEvolver>();
        services.AddTransient<IContextBuilder>(sp =>
        {
            var memoryStore = sp.GetRequiredService<IAgentMemoryStore>();
            var graphAdapter = sp.GetService<IGraphStoreAdapter>();
            var callChainStore = sp.GetService<ICallChainStore>();
            return new ContextBuilder(memoryStore, graphAdapter, callChainStore);
        });

        return services;
    }

    /// <summary>
    /// Adds agent memory services with a custom <see cref="IAgentMemoryStore"/> implementation.
    /// Use this overload to provide a persistent store (e.g., database-backed).
    /// </summary>
    public static IServiceCollection AddAgentMemory<TStore>(this IServiceCollection services)
        where TStore : class, IAgentMemoryStore
    {
        services.AddSingleton<IAgentMemoryStore, TStore>();

        // If the store also implements optional interfaces, register those too
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IAgentMemoryStore>();
            return store as ITieredExpertiseStore
                ?? throw new InvalidOperationException(
                    $"{typeof(TStore).Name} does not implement ITieredExpertiseStore.");
        });

        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IAgentMemoryStore>();
            return store as ICallChainStore
                ?? throw new InvalidOperationException(
                    $"{typeof(TStore).Name} does not implement ICallChainStore.");
        });

        services.AddTransient<IEpisodeRecorder, EpisodeRecorder>();
        services.AddTransient<IMemoryEvolver, MemoryEvolver>();
        services.AddTransient<IContextBuilder>(sp =>
        {
            var memoryStore = sp.GetRequiredService<IAgentMemoryStore>();
            var graphAdapter = sp.GetService<IGraphStoreAdapter>();
            var callChainStore = sp.GetService<ICallChainStore>();
            return new ContextBuilder(memoryStore, graphAdapter, callChainStore);
        });

        return services;
    }
}