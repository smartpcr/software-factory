// -----------------------------------------------------------------------
// <copyright file="MemoryMcpServiceExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Mcp;

using Ast.Memory.InMemory;
using Ast.Memory.Interfaces;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration extensions for memory MCP tools and server.
/// Registers tool definitions, handler, MCP server, and optionally
/// in-memory implementations for self-contained operation.
/// </summary>
public static class MemoryMcpServiceExtensions
{
    /// <summary>
    /// Registers the MCP tool registration, handler, and server.
    /// Callers must separately register implementations of <see cref="IMemoryRetrievalEngine"/>,
    /// <see cref="IMemoryGraphStore"/>, <see cref="IEvolutionEngine"/>, and <see cref="IIncrementalSync"/>.
    /// </summary>
    public static IServiceCollection AddMemoryMcpTools(this IServiceCollection services)
    {
        services.AddSingleton<MemoryMcpToolRegistration>();
        services.AddTransient<MemoryMcpHandler>();
        services.AddTransient<McpServer>();
        return services;
    }

    /// <summary>
    /// Registers in-memory implementations of all memory interfaces plus the MCP server.
    /// Suitable for testing and standalone operation without external dependencies.
    /// </summary>
    public static IServiceCollection AddMemoryMcpToolsWithInMemoryBackend(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryMemoryGraphStore>();
        services.AddSingleton<IMemoryGraphStore>(sp => sp.GetRequiredService<InMemoryMemoryGraphStore>());
        services.AddSingleton<IMemoryRetrievalEngine>(sp =>
            new InMemoryRetrievalEngine(sp.GetRequiredService<IMemoryGraphStore>()));
        services.AddSingleton<IEvolutionEngine>(sp =>
            new InMemoryEvolutionEngine(sp.GetRequiredService<IMemoryGraphStore>()));
        services.AddSingleton<IIncrementalSync, StubIncrementalSync>();
        services.AddMemoryMcpTools();
        return services;
    }

    /// <summary>
    /// Minimal stub for IIncrementalSync used with in-memory backend.
    /// </summary>
    private sealed class StubIncrementalSync : IIncrementalSync
    {
        public System.Threading.Tasks.Task<Models.SyncResult> ApplyDiffAsync(
            string repoRoot, string fromCommit, string toCommit,
            System.Threading.CancellationToken ct = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                new Models.SyncResult(0, 0, 0, 0, 0, 0, System.TimeSpan.Zero));
        }
    }
}
