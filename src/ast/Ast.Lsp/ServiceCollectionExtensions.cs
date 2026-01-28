// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering LSP services with DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds LSP enrichment services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure LSP options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLspServices(
        this IServiceCollection services,
        Action<LspConfiguration>? configure = null)
    {
        var config = new LspConfiguration();
        configure?.Invoke(config);

        services.Configure<LspConfiguration>(opt =>
        {
            opt.Enabled = config.Enabled;
            opt.TimeoutMs = config.TimeoutMs;
            opt.MaxReferences = config.MaxReferences;
            opt.CollectDiagnostics = config.CollectDiagnostics;
            opt.CollectCallHierarchy = config.CollectCallHierarchy;
            opt.ServerIdleTimeoutSeconds = config.ServerIdleTimeoutSeconds;

            // C# configuration
            opt.CSharp.Enabled = config.CSharp.Enabled;
            opt.CSharp.ServerType = config.CSharp.ServerType;
            opt.CSharp.ServerPath = config.CSharp.ServerPath;
            opt.CSharp.SolutionPath = config.CSharp.SolutionPath;
            opt.CSharp.AdditionalArguments = config.CSharp.AdditionalArguments;

            // PowerShell configuration
            opt.PowerShell.Enabled = config.PowerShell.Enabled;
            opt.PowerShell.PowerShellPath = config.PowerShell.PowerShellPath;
            opt.PowerShell.EditorServicesPath = config.PowerShell.EditorServicesPath;
            opt.PowerShell.LogLevel = config.PowerShell.LogLevel;
            opt.PowerShell.AdditionalArguments = config.PowerShell.AdditionalArguments;
        });

        services.TryAddSingleton<LspServerManager>();
        services.TryAddSingleton<LspEnrichmentService>();

        return services;
    }

    /// <summary>
    /// Adds LSP services with C# support only.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serverType">The C# server type to use.</param>
    /// <param name="serverPath">Optional path to the server executable.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCSharpLsp(
        this IServiceCollection services,
        CSharpLspServerType serverType = CSharpLspServerType.CSharpLs,
        string? serverPath = null)
    {
        return services.AddLspServices(config =>
        {
            config.CSharp.Enabled = true;
            config.CSharp.ServerType = serverType;
            config.CSharp.ServerPath = serverPath;
            config.PowerShell.Enabled = false;
        });
    }

    /// <summary>
    /// Adds LSP services with PowerShell support only.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="powerShellPath">Optional path to PowerShell executable.</param>
    /// <param name="editorServicesPath">Optional path to PSES module.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPowerShellLsp(
        this IServiceCollection services,
        string? powerShellPath = null,
        string? editorServicesPath = null)
    {
        return services.AddLspServices(config =>
        {
            config.CSharp.Enabled = false;
            config.PowerShell.Enabled = true;
            config.PowerShell.PowerShellPath = powerShellPath;
            config.PowerShell.EditorServicesPath = editorServicesPath;
        });
    }

    /// <summary>
    /// Adds LSP services with both C# and PowerShell support.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAllLspServices(this IServiceCollection services)
    {
        return services.AddLspServices(config =>
        {
            config.CSharp.Enabled = true;
            config.PowerShell.Enabled = true;
        });
    }
}