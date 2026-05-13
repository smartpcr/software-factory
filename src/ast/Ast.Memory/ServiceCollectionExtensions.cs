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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration for the Ast.Memory consolidation services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGraphWriteLock"/> (singleton) and
    /// <see cref="ConsolidationService"/> (singleton) into the DI container.
    /// Binds <see cref="MemoryOptions"/> from the "AgentMemory" configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMemoryEvolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MemoryOptions>(configuration.GetSection("AgentMemory"));
        services.AddSingleton<IGraphWriteLock, GraphWriteLock>();
        services.AddSingleton<ConsolidationService>();
        services.AddHttpClient("OllamaConsolidation");
        return services;
    }

    /// <summary>
    /// Registers consolidation services without configuration binding (for testing).
    /// Assumes <see cref="MemoryOptions"/> is already registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMemoryEvolution(this IServiceCollection services)
    {
        services.AddSingleton<IGraphWriteLock, GraphWriteLock>();
        services.AddSingleton<ConsolidationService>();
        services.AddHttpClient("OllamaConsolidation");
        return services;
    }
}