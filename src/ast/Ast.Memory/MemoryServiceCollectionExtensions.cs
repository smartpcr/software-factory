// -----------------------------------------------------------------------
// <copyright file="MemoryServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Extension methods to register Agent Memory services in the DI container.
/// Wires up the <see cref="IIncrementalSync"/> with file-system watcher as
/// a hosted service, <see cref="IExtractionPipeline"/> (Roslyn-based),
/// <see cref="IFileSyncStateStore"/> (SQLite-backed), and
/// <see cref="IMemoryGraphStore"/> (SQLite-backed by default per operator
/// decision). See architecture.md §5.5.
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Agent Memory incremental sync services including:
    /// <list type="bullet">
    ///   <item><see cref="IExtractionPipeline"/> → <see cref="RoslynExtractionPipeline"/></item>
    ///   <item><see cref="IFileSyncStateStore"/> → <see cref="SqliteFileSyncStateStore"/></item>
    ///   <item><see cref="IMemoryGraphStore"/> → <see cref="SqliteMemoryGraphStore"/> (default)</item>
    ///   <item><see cref="IIncrementalSync"/> → <see cref="IncrementalSync"/></item>
    ///   <item><see cref="FileWatcherHostedService"/> (hosted service)</item>
    /// </list>
    /// Callers may register a custom <see cref="IMemoryGraphStore"/> before calling
    /// this method to override the default SQLite store.
    /// </summary>
    public static IServiceCollection AddAgentMemorySync(
        this IServiceCollection services,
        Action<MemoryOptions>? configureOptions = null)
    {
        // Ensure options infrastructure is available
        services.AddOptions<MemoryOptions>();
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        // Ensure logging is resolvable (ILogger<T> needed by IncrementalSync, RoslynExtractionPipeline)
        services.AddLogging();

        services.AddSingleton<IExtractionPipeline, RoslynExtractionPipeline>();

        // SQLite-backed sync state store by default (operator decision: persists
        // ContentHash across restarts). Falls back to in-memory if no SQLite path.
        services.AddSingleton<IFileSyncStateStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryOptions>>().Value;
            var dbPath = opts.SqlitePath;
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                dbPath = Path.Combine(
                    opts.RepoRoot ?? Path.GetTempPath(),
                    ".agent-memory",
                    "sync_state.db");
            }

            return new SqliteFileSyncStateStore(dbPath);
        });

        // SQLite-backed graph store by default (operator decision: retains graph
        // state across restarts). Register one concrete instance exposed through
        // both IMemoryGraphStore and IGraphStore interfaces.
        // TryAdd respects any previously registered custom store.
        services.TryAddSingleton<SqliteMemoryGraphStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryOptions>>().Value;
            var dbPath = opts.SqlitePath;
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                dbPath = Path.Combine(
                    opts.RepoRoot ?? Path.GetTempPath(),
                    ".agent-memory",
                    "memory_graph.db");
            }
            else
            {
                // Use a sibling file for the graph store if a sync state path was given
                var dir = Path.GetDirectoryName(dbPath) ?? Path.GetTempPath();
                dbPath = Path.Combine(dir, "memory_graph.db");
            }

            return new SqliteMemoryGraphStore(dbPath);
        });
        services.TryAddSingleton<IMemoryGraphStore>(sp => sp.GetRequiredService<SqliteMemoryGraphStore>());
        services.TryAddSingleton<IGraphStore>(sp => sp.GetRequiredService<SqliteMemoryGraphStore>());

        services.AddSingleton<IIncrementalSync, IncrementalSync>();

        // Register NullSemanticReIndexer as fallback ONLY if no real implementation
        // has been registered. Consumers can register a real ISemanticReIndexer
        // BEFORE calling AddAgentMemorySync and TryAdd will not override it.
        services.TryAddSingleton<ISemanticReIndexer, NullSemanticReIndexer>();

        services.AddHostedService<FileWatcherHostedService>();

        return services;
    }
}