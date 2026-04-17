// -----------------------------------------------------------------------
// <copyright file="EngineAssemblyVersionProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using ExecutionEngine.Workflow;

/// <summary>
/// Provides engine assembly version information via reflection.
/// Registered as a singleton to amortize reflection cost.
/// </summary>
public sealed class EngineAssemblyVersionProvider
{
    private readonly EngineVersionInfo versionInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineAssemblyVersionProvider"/> class.
    /// </summary>
    public EngineAssemblyVersionProvider()
    {
        var assembly = typeof(WorkflowDefinition).Assembly;
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var fileVersionAttr = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
            .OfType<System.Reflection.AssemblyFileVersionAttribute>()
            .FirstOrDefault();
        var fileVersion = fileVersionAttr?.Version ?? assemblyVersion;

        this.versionInfo = new EngineVersionInfo(assemblyVersion, fileVersion);
    }

    /// <summary>
    /// Gets the engine version information.
    /// </summary>
    public EngineVersionInfo GetVersion() => this.versionInfo;
}

/// <summary>
/// Engine version information.
/// </summary>
/// <param name="EngineAssemblyVersion">The assembly version.</param>
/// <param name="EngineFileVersion">The file version.</param>
public record EngineVersionInfo(string EngineAssemblyVersion, string EngineFileVersion);
