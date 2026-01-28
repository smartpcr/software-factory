// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering graph database services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Neo4j graph store services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure graph store options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNeo4jGraphStore(
        this IServiceCollection services,
        Action<GraphStoreOptions> configureOptions)
    {
        services.Configure(configureOptions);
        services.TryAddSingleton<IGraphStore, Neo4jGraphStore>();
        return services;
    }

    /// <summary>
    /// Adds Neo4j graph store services with default options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="uri">The Neo4j connection URI.</param>
    /// <param name="username">The Neo4j username.</param>
    /// <param name="password">The Neo4j password.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNeo4jGraphStore(
        this IServiceCollection services,
        string uri,
        string username,
        string password)
    {
        return services.AddNeo4jGraphStore(options =>
        {
            options.Uri = uri;
            options.Username = username;
            options.Password = password;
        });
    }
}