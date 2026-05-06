// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory;

using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Extension methods for registering the agent memory graph in the DI container.
/// Per architecture.md §7.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the memory graph subsystem — <see cref="AnnotationKeyRegistry"/>,
    /// <see cref="ICentralityProvider"/>, and <see cref="IDecayService"/> —
    /// using the provided <see cref="MemoryOptions"/>.
    /// <para>
    /// Callers must register <see cref="IMemoryGraphStore"/> separately
    /// (e.g. via a storage-specific extension method) before resolving
    /// <see cref="IDecayService"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMemoryGraph(
        this IServiceCollection services,
        Action<MemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddSingleton<AnnotationKeyRegistry>();

        // Register decay subsystem with fallback centrality provider
        services.TryAddSingleton<ICentralityProvider, NullCentralityProvider>();
        services.Configure<DecayServiceOptions>(decayOpts =>
        {
            var memOpts = new MemoryOptions();
            configure(memOpts);
            decayOpts.DecayHalfLifeDays = memOpts.DecayHalfLifeDays;
            decayOpts.CentralityBoostFactor = memOpts.CentralityBoostFactor;
            decayOpts.ArchiveConfidenceFloor = memOpts.ArchiveConfidenceFloor;
            decayOpts.StaleDecayFactor = memOpts.StaleDecayFactor;
            decayOpts.MinorDecayFactor = memOpts.MinorDecayFactor;
        });
        services.AddSingleton<DecayService>();
        services.AddSingleton<IDecayService>(sp => sp.GetRequiredService<DecayService>());
        services.AddSingleton<IIncrementalSyncDecayHook>(sp => sp.GetRequiredService<DecayService>());

        return services;
    }
}