// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Schema;
using Ast.Memory.Storage;
using Ast.VectorDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Extension methods for registering the agent memory graph in the DI container.
/// Per architecture.md §7.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the memory graph subsystem — <see cref="IMemoryGraphStore"/>,
    /// <see cref="IGraphStore"/>, and <see cref="AnnotationKeyRegistry"/> —
    /// based on <see cref="MemoryOptions.Backend"/>.
    /// <para>
    /// <b>SQLite backend:</b> Creates a <see cref="SqliteGraphStore"/> using
    /// <see cref="MemoryOptions.SqlitePath"/> (defaults to <c>memory_graph.db</c>).
    /// </para>
    /// <para>
    /// <b>Neo4j backend:</b> Creates a <see cref="Neo4jMemoryGraphStore"/> from
    /// <see cref="MemoryOptions.Neo4jUri"/>, <see cref="MemoryOptions.Neo4jUser"/>,
    /// and <see cref="MemoryOptions.Neo4jPassword"/>. Optionally composes a
    /// <see cref="IVectorStoreClient"/> if <see cref="MemoryOptions.QdrantEndpoint"/>
    /// is set.
    /// </para>
    /// <para>
    /// Callers must invoke <c>InitializeAsync</c> on the resolved store before use.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMemoryGraph(
        this IServiceCollection services,
        Action<MemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddSingleton<AnnotationKeyRegistry>();

        services.AddSingleton<IMemoryGraphStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            return opts.Backend switch
            {
                MemoryBackend.Sqlite => CreateSqliteStore(opts),
                MemoryBackend.Neo4j => CreateNeo4jStore(sp, opts),
                _ => throw new NotSupportedException(
                    $"Memory backend '{opts.Backend}' is not supported. " +
                    $"Supported values: {string.Join(", ", Enum.GetNames<MemoryBackend>())}.")
            };
        });

        // Map IGraphStore to the same singleton instance
        services.AddSingleton<IGraphStore>(sp => sp.GetRequiredService<IMemoryGraphStore>());

        return services;
    }

    private static SqliteGraphStore CreateSqliteStore(MemoryOptions opts)
    {
        var path = opts.SqlitePath ?? "memory_graph.db";
        var connectionString = $"Data Source={path}";
        return new SqliteGraphStore(connectionString);
    }

    private static Neo4jMemoryGraphStore CreateNeo4jStore(IServiceProvider sp, MemoryOptions opts)
    {
        if (string.IsNullOrEmpty(opts.Neo4jUri))
        {
            throw new InvalidOperationException(
                "MemoryOptions.Neo4jUri is required when Backend is Neo4j.");
        }

        if (string.IsNullOrEmpty(opts.Neo4jUser))
        {
            throw new InvalidOperationException(
                "MemoryOptions.Neo4jUser is required when Backend is Neo4j.");
        }

        if (string.IsNullOrEmpty(opts.Neo4jPassword))
        {
            throw new InvalidOperationException(
                "MemoryOptions.Neo4jPassword is required when Backend is Neo4j.");
        }

        var graphStore = new Neo4jGraphStore(opts.Neo4jUri, opts.Neo4jUser, opts.Neo4jPassword);

        IVectorStoreClient? vectorStore = null;
        if (!string.IsNullOrEmpty(opts.QdrantEndpoint))
        {
            vectorStore = new QdrantVectorStore(opts.QdrantEndpoint);
        }

        var logger = sp.GetService<ILogger<Neo4jMemoryGraphStore>>();

        return new Neo4jMemoryGraphStore(graphStore, vectorStore, opts, logger);
    }
}