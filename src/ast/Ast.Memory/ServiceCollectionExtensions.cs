// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI registration for the Ast.Memory subsystem including
/// IncrementalSync, CommitRegistry, and GitDiffDriver.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers IncrementalSync and its dependencies.
    /// Callers must register <see cref="IMemoryGraphStore"/>,
    /// <see cref="IExtractionPipeline"/>, <see cref="IEvolutionEngine"/>,
    /// <c>IEmbeddingGenerator</c> (from Ast.Embedding),
    /// and <c>IVectorStore</c> (from Ast.VectorDb) in their own DI setup.
    /// <see cref="ICommitRegistry"/> is registered as <see cref="InMemoryCommitRegistry"/>
    /// by default; callers should replace it with a durable implementation
    /// backed by the <c>commits</c> table (§3.2.3) for production.
    /// Registers <see cref="DefaultSemanticIndexer"/> as the production
    /// <see cref="ISemanticIndexer"/> implementation that generates embeddings
    /// via <c>IEmbeddingGenerator</c>, stores vectors in <c>IVectorStore</c>,
    /// and manages SIMILAR_TO edges per architecture.md §3.1, §5.4.
    /// </summary>
    public static IServiceCollection AddIncrementalSync(this IServiceCollection services)
    {
        // Ensure MemoryOptions is available via IOptions<MemoryOptions>.
        // Configure with defaults; callers can override via services.Configure<MemoryOptions>().
        services.AddOptions<MemoryOptions>();
        services.TryAddSingleton<ICommitRegistry, InMemoryCommitRegistry>();
        services.AddSingleton<GitDiffDriver>();
        services.AddSingleton<ISemanticIndexer, DefaultSemanticIndexer>();
        services.AddSingleton<IIncrementalSync, IncrementalSync>();
        return services;
    }

    /// <summary>
    /// Registers IncrementalSync with custom <see cref="MemoryOptions"/>.
    /// </summary>
    public static IServiceCollection AddIncrementalSync(
        this IServiceCollection services,
        Action<MemoryOptions> configureOptions)
    {
        services.Configure(configureOptions);
        return services.AddIncrementalSync();
    }
}