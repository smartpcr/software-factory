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
public class LspServerManager : IAsyncDisposable
{
    private readonly LspConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LspServerManager> _logger;
    private readonly ConcurrentDictionary<string, ILspClient> _clients = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Creates a new LSP server manager.
    /// </summary>
    /// <param name="options">LSP configuration options.</param>
    /// <param name="loggerFactory">Logger factory for creating loggers.</param>
    public LspServerManager(
        IOptions<LspConfiguration> options,
        ILoggerFactory loggerFactory)
    {
        _config = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LspServerManager>();
    }

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

        if (_clients.TryGetValue(key, out var existingClient) && existingClient.State == LspServerState.Running)
        {
            return existingClient;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_clients.TryGetValue(key, out existingClient) && existingClient.State == LspServerState.Running)
            {
                return existingClient;
            }

            // Check if language is enabled
            if (!IsLanguageEnabled(language))
            {
                throw new InvalidOperationException($"LSP for {language} is not enabled in configuration");
            }

            // Dispose existing client if it exists but is not running
            if (existingClient != null)
            {
                await existingClient.DisposeAsync();
                _clients.TryRemove(key, out _);
            }

            // Create new client
            var client = new LspClient(
                language,
                _config,
                _loggerFactory.CreateLogger<LspClient>());

            _logger.LogInformation("Initializing {Language} LSP client for workspace: {Path}", language, workspacePath);

            await client.InitializeAsync(workspacePath, cancellationToken);
            _clients[key] = client;

            return client;
        }
        finally
        {
            _initLock.Release();
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
        return _clients.TryGetValue(key, out var client) && client.State == LspServerState.Running
            ? client
            : null;
    }

    /// <summary>
    /// Gets all active clients.
    /// </summary>
    /// <returns>Collection of active clients.</returns>
    public IReadOnlyCollection<ILspClient> GetActiveClients()
    {
        return _clients.Values
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

        if (_clients.TryRemove(key, out var client))
        {
            _logger.LogInformation("Shutting down {Language} LSP client for workspace: {Path}", language, workspacePath);
            await client.ShutdownAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Shuts down all clients.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ShutdownAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Shutting down all LSP clients ({Count} active)", _clients.Count);

        var tasks = _clients.Values.Select(async client =>
        {
            try
            {
                await client.ShutdownAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error shutting down LSP client");
            }
        });

        await Task.WhenAll(tasks);
        _clients.Clear();
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
        await ShutdownAllAsync();
        _initLock.Dispose();
    }

    private bool IsLanguageEnabled(LspLanguage language)
    {
        return language switch
        {
            LspLanguage.CSharp => _config.Enabled && _config.CSharp.Enabled,
            LspLanguage.PowerShell => _config.Enabled && _config.PowerShell.Enabled,
            _ => false
        };
    }

    private static string GetClientKey(LspLanguage language, string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath).ToLowerInvariant();
        return $"{language}:{normalizedPath}";
    }
}
