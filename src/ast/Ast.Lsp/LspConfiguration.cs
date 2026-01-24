// -----------------------------------------------------------------------
// <copyright file="LspConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

/// <summary>
/// Configuration for LSP services.
/// </summary>
public class LspConfiguration
{
    /// <summary>
    /// Whether LSP enrichment is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// C# language server configuration.
    /// </summary>
    public CSharpLspConfiguration CSharp { get; set; } = new();

    /// <summary>
    /// PowerShell language server configuration.
    /// </summary>
    public PowerShellLspConfiguration PowerShell { get; set; } = new();

    /// <summary>
    /// Default timeout in milliseconds for LSP requests.
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Maximum number of references to retrieve per symbol.
    /// </summary>
    public int MaxReferences { get; set; } = 100;

    /// <summary>
    /// Whether to collect diagnostics during enrichment.
    /// </summary>
    public bool CollectDiagnostics { get; set; } = true;

    /// <summary>
    /// Whether to collect call hierarchy during enrichment.
    /// </summary>
    public bool CollectCallHierarchy { get; set; } = true;

    /// <summary>
    /// Server idle timeout in seconds before shutdown.
    /// </summary>
    public int ServerIdleTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// C# language server configuration.
/// </summary>
public class CSharpLspConfiguration
{
    /// <summary>
    /// Whether C# LSP is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Server type to use.
    /// </summary>
    public CSharpLspServerType ServerType { get; set; } = CSharpLspServerType.CSharpLs;

    /// <summary>
    /// Path to the language server executable.
    /// If null, will search in PATH.
    /// </summary>
    public string? ServerPath { get; set; }

    /// <summary>
    /// Additional command-line arguments for the server.
    /// </summary>
    public List<string> AdditionalArguments { get; set; } = [];

    /// <summary>
    /// Solution file to use. If null, will auto-detect.
    /// </summary>
    public string? SolutionPath { get; set; }
}

/// <summary>
/// C# language server types.
/// </summary>
public enum CSharpLspServerType
{
    /// <summary>
    /// csharp-ls - lightweight C# language server.
    /// </summary>
    CSharpLs,

    /// <summary>
    /// OmniSharp - full-featured C# language server.
    /// </summary>
    OmniSharp
}

/// <summary>
/// PowerShell language server configuration.
/// </summary>
public class PowerShellLspConfiguration
{
    /// <summary>
    /// Whether PowerShell LSP is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to PowerShell executable.
    /// If null, will use 'pwsh' or 'powershell' from PATH.
    /// </summary>
    public string? PowerShellPath { get; set; }

    /// <summary>
    /// Path to PowerShellEditorServices module.
    /// If null, will attempt to find in standard locations.
    /// </summary>
    public string? EditorServicesPath { get; set; }

    /// <summary>
    /// Additional command-line arguments for PSES.
    /// </summary>
    public List<string> AdditionalArguments { get; set; } = [];

    /// <summary>
    /// Log level for PSES (Diagnostic, Verbose, Normal, Warning, Error).
    /// </summary>
    public string LogLevel { get; set; } = "Normal";
}

/// <summary>
/// Supported languages for LSP enrichment.
/// </summary>
public enum LspLanguage
{
    /// <summary>
    /// C# language.
    /// </summary>
    CSharp,

    /// <summary>
    /// PowerShell language.
    /// </summary>
    PowerShell
}

/// <summary>
/// LSP server state.
/// </summary>
public enum LspServerState
{
    /// <summary>
    /// Server not started.
    /// </summary>
    NotStarted,

    /// <summary>
    /// Server is starting.
    /// </summary>
    Starting,

    /// <summary>
    /// Server is running and ready.
    /// </summary>
    Running,

    /// <summary>
    /// Server is stopping.
    /// </summary>
    Stopping,

    /// <summary>
    /// Server has stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Server failed to start or crashed.
    /// </summary>
    Failed
}

/// <summary>
/// Progress information for LSP enrichment.
/// </summary>
public class EnrichmentProgress(int processed, int total, string currentFile, string? currentItem = null)
{
    /// <summary>
    /// Number of items processed.
    /// </summary>
    public int Processed { get; init; } = processed;

    /// <summary>
    /// Total number of items to process.
    /// </summary>
    public int Total { get; init; } = total;

    /// <summary>
    /// Current file being processed.
    /// </summary>
    public string CurrentFile { get; init; } = currentFile;

    /// <summary>
    /// Current item being processed.
    /// </summary>
    public string? CurrentItem { get; init; } = currentItem;

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public double Percentage => this.Total > 0 ? (double)this.Processed / this.Total * 100 : 0;
}