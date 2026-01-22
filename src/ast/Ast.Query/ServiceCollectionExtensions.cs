// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering query services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds code query services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCodeQuery(this IServiceCollection services)
    {
        services.TryAddSingleton<ICodeQuery, CodeQueryBuilder>();
        services.TryAddSingleton<HybridQueryService>();
        return services;
    }
}
