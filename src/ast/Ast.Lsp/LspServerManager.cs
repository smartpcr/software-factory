// -----------------------------------------------------------------------
// <copyright file="LspServerManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Manages LSP server instances for multiple workspaces and languages.
/// </summary>
/// <remarks>
/// Creates a new LSP server manager.
/// </remarks>
/// <param name="options">LSP configuration options.</param>
/// <param name="loggerFactory">Logger factory for creating loggers.</param>
public class LspServerManager(
    IOptions<LspConfiguration> options,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly LspConfiguration config = options.Value;
    private readonly ILoggerFactory loggerFactory = loggerFactory;
    private readonly ILogger<LspServerManager> logger = loggerFactory.CreateLogger<LspServerManager>();
    private readonly ConcurrentDictionary<string, ILspClient> clients = new();
    private readonly SemaphoreSlim initLock = new(1, 1);

    /// <summary>
    /// Gets or creates an LSP client for the specified language and workspace.
    /// </summary>
    /// <param name="language">The language to support.</param>
    /// <param name="workspacePath">The root path of the workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An initialized LSP client.</returns>
    public async Task<ILspClient> GetOrCreateClientAsync(
        LspLanguage language,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var key = GetClientKey(language, workspacePath);

        if (this.clients.TryGetValue(key, out var existingClient) && existingClient.State == LspServerState.Running)
        {
            return existingClient;
        }

        await this.initLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (this.clients.TryGetValue(key, out existingClient) && existingClient.State == LspServerState.Running)
            {
                return existingClient;
            }

            // Check if language is enabled
            if (!this.IsLanguageEnabled(language))
            {
                throw new InvalidOperationException($"LSP for {language} is not enabled in configuration");
            }

            // Dispose existing client if it exists but is not running
            if (existingClient != null)
            {
                await existingClient.DisposeAsync();
                this.clients.TryRemove(key, out _);
            }

            // Create new client
            var client = new LspClient(
                language,
                this.config,
                this.loggerFactory.CreateLogger<LspClient>());

            this.logger.LogInformation("Initializing {Language} LSP client for workspace: {Path}", language, workspacePath);

            await client.InitializeAsync(workspacePath, cancellationToken);
            this.clients[key] = client;

            return client;
        }
        finally
        {
            this.initLock.Release();
        }
    }

    /// <summary>
    /// Gets an existing client if available.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <param name="workspacePath">The workspace path.</param>
    /// <returns>The client if exists and running, null otherwise.</returns>
    public ILspClient? GetClient(LspLanguage language, string workspacePath)
    {
        var key = GetClientKey(language, workspacePath);
        return this.clients.TryGetValue(key, out var client) && client.State == LspServerState.Running
            ? client
            : null;
    }

    /// <summary>
    /// Gets all active clients.
    /// </summary>
    /// <returns>Collection of active clients.</returns>
    public IReadOnlyCollection<ILspClient> GetActiveClients()
    {
        return this.clients.Values
            .Where(c => c.State == LspServerState.Running)
            .ToList();
    }

    /// <summary>
    /// Shuts down a specific client.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <param name="workspacePath">The workspace path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ShutdownClientAsync(
        LspLanguage language,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var key = GetClientKey(language, workspacePath);

        if (this.clients.TryRemove(key, out var client))
        {
            this.logger.LogInformation("Shutting down {Language} LSP client for workspace: {Path}", language, workspacePath);
            await client.ShutdownAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Shuts down all clients.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ShutdownAllAsync(CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Shutting down all LSP clients ({Count} active)", this.clients.Count);

        var tasks = this.clients.Values.Select(async client =>
        {
            try
            {
                await client.ShutdownAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Error shutting down LSP client");
            }
        });

        await Task.WhenAll(tasks);
        this.clients.Clear();
    }

    /// <summary>
    /// Determines the appropriate language from a file path.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <returns>The language, or null if not supported.</returns>
    public static LspLanguage? GetLanguageForFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".cs" => LspLanguage.CSharp,
            ".csx" => LspLanguage.CSharp,
            ".ps1" => LspLanguage.PowerShell,
            ".psm1" => LspLanguage.PowerShell,
            ".psd1" => LspLanguage.PowerShell,
            _ => null
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.ShutdownAllAsync();
        this.initLock.Dispose();
    }

    private bool IsLanguageEnabled(LspLanguage language)
    {
        return language switch
        {
            LspLanguage.CSharp => this.config.Enabled && this.config.CSharp.Enabled,
            LspLanguage.PowerShell => this.config.Enabled && this.config.PowerShell.Enabled,
            _ => false
        };
    }

    private static string GetClientKey(LspLanguage language, string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath).ToLowerInvariant();
        return $"{language}:{normalizedPath}";
    }
}