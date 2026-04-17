// -----------------------------------------------------------------------
// <copyright file="EngineServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using ExecutionEngine.Workflow;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkflowUi.Server.Endpoints;

/// <summary>
/// Registers ExecutionEngine services in the DI container.
/// </summary>
public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds execution engine services to the service collection.
    /// Uses TryAdd to ensure idempotency.
    /// </summary>
    public static IServiceCollection AddExecutionEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<WorkflowSerializer>();
        services.TryAddScoped<WorkflowLoader>(sp =>
            new WorkflowLoader(sp));
        services.TryAddSingleton<IWorkflowExecutorFactory, PlaceholderWorkflowExecutorFactory>();
        services.TryAddSingleton<EngineAssemblyVersionProvider>();

        var logger = LoggerFactory.Create(b => b.AddConsole())
            .CreateLogger("WorkflowUi.Server.Hosting.Di");
        logger.LogInformation(
            "Registered WorkflowLoader (scoped), WorkflowSerializer (singleton), IWorkflowExecutorFactory (singleton placeholder)");

        return services;
    }
}
