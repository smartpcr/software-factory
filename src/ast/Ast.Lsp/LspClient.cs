// -----------------------------------------------------------------------
// <copyright file="LspClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

/// <summary>
/// LSP client implementation using JSON-RPC over stdio.
/// </summary>
public class LspClient : ILspClient
{
    private readonly ILogger<LspClient> _logger;
    private readonly LspConfiguration _config;
    private readonly ConcurrentDictionary<string, List<Diagnostic>> _diagnostics = new();
    private readonly ConcurrentDictionary<string, int> _documentVersions = new();

    private Process? _serverProcess;
    private JsonRpc? _rpc;
    private LspServerState _state = LspServerState.NotStarted;
    private string? _rootPath;
    private ServerCapabilities? _capabilities;

    /// <inheritdoc />
    public LspLanguage Language { get; }

    /// <inheritdoc />
    public LspServerState State => _state;

    /// <inheritdoc />
    public string? RootPath => _rootPath;

    /// <inheritdoc />
    public ServerCapabilities? Capabilities => _capabilities;

    /// <inheritdoc />
    public event EventHandler<DiagnosticsEventArgs>? DiagnosticsReceived;

    /// <summary>
    /// Creates a new LSP client for the specified language.
    /// </summary>
    /// <param name="language">The language to support.</param>
    /// <param name="config">LSP configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public LspClient(LspLanguage language, LspConfiguration config, ILogger<LspClient> logger)
    {
        Language = language;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (_state == LspServerState.Running)
        {
            _logger.LogWarning("LSP client already initialized for {RootPath}", _rootPath);
            return;
        }

        _state = LspServerState.Starting;
        _rootPath = rootPath;

        try
        {
            // Start the language server process
            var startInfo = CreateProcessStartInfo(rootPath);
            _serverProcess = new Process { StartInfo = startInfo };

            _serverProcess.Start();
            _logger.LogInformation("Started {Language} LSP server (PID: {Pid})", Language, _serverProcess.Id);

            // Create JSON-RPC connection over stdio
            _rpc = new JsonRpc(
                _serverProcess.StandardInput.BaseStream,
                _serverProcess.StandardOutput.BaseStream);

            // Register notification handlers
            _rpc.AddLocalRpcMethod("textDocument/publishDiagnostics", new Action<PublishDiagnosticsParams>(OnPublishDiagnostics));

            _rpc.StartListening();

            // Send initialize request
            var initParams = CreateInitializeParams(rootPath);
            var result = await _rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize",
                initParams,
                cancellationToken);

            _capabilities = result.Capabilities;

            // Send initialized notification
            await _rpc.NotifyAsync("initialized");

            _state = LspServerState.Running;
            _logger.LogInformation(
                "{Language} LSP server initialized. Hover: {Hover}, Definition: {Def}, References: {Refs}, CallHierarchy: {Call}",
                Language,
                _capabilities.HoverProvider,
                _capabilities.DefinitionProvider,
                _capabilities.ReferencesProvider,
                _capabilities.CallHierarchyProvider);
        }
        catch (Exception ex)
        {
            _state = LspServerState.Failed;
            _logger.LogError(ex, "Failed to initialize {Language} LSP server", Language);
            throw new LspException($"Failed to initialize {Language} LSP server", ex);
        }
    }

    /// <inheritdoc />
    public async Task OpenDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var uri = PathToUri(filePath);
        var version = _documentVersions.AddOrUpdate(uri, 1, (_, v) => v + 1);

        var languageId = Language switch
        {
            LspLanguage.CSharp => "csharp",
            LspLanguage.PowerShell => "powershell",
            _ => "plaintext"
        };

        var param = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = languageId,
                Version = version,
                Text = content
            }
        };

        await _rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", param);
        _logger.LogDebug("Opened document: {Uri}", uri);
    }

    /// <inheritdoc />
    public async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var uri = PathToUri(filePath);
        var param = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        };

        await _rpc!.NotifyWithParameterObjectAsync("textDocument/didClose", param);
        _documentVersions.TryRemove(uri, out _);
        _diagnostics.TryRemove(uri, out _);
        _logger.LogDebug("Closed document: {Uri}", uri);
    }

    /// <inheritdoc />
    public async Task<HoverResult?> GetHoverAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.HoverProvider != true)
        {
            _logger.LogDebug("Hover not supported by server");
            return null;
        }

        var param = new TextDocumentPositionParams(
            new TextDocumentIdentifier(PathToUri(filePath)),
            new Position(line, character));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var result = await _rpc!.InvokeWithParameterObjectAsync<HoverResult?>(
                "textDocument/hover",
                param,
                timeoutCts.Token);

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Hover request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hover request failed for {File}:{Line}:{Char}", filePath, line, character);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Location>> GetDefinitionsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.DefinitionProvider != true)
        {
            _logger.LogDebug("Definition not supported by server");
            return [];
        }

        var param = new TextDocumentPositionParams(
            new TextDocumentIdentifier(PathToUri(filePath)),
            new Position(line, character));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var result = await _rpc!.InvokeWithParameterObjectAsync<JsonElement>(
                "textDocument/definition",
                param,
                timeoutCts.Token);

            return ParseLocationOrLocations(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Definition request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Definition request failed for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Location>> GetReferencesAsync(
        string filePath,
        int line,
        int character,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.ReferencesProvider != true)
        {
            _logger.LogDebug("References not supported by server");
            return [];
        }

        var param = new ReferenceParams(
            new TextDocumentIdentifier(PathToUri(filePath)),
            new Position(line, character),
            new ReferenceContext(includeDeclaration));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var result = await _rpc!.InvokeWithParameterObjectAsync<List<Location>?>(
                "textDocument/references",
                param,
                timeoutCts.Token);

            var locations = result ?? [];

            // Limit results
            if (locations.Count > _config.MaxReferences)
            {
                locations = locations.Take(_config.MaxReferences).ToList();
            }

            return locations;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("References request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "References request failed for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.DocumentSymbolProvider != true)
        {
            _logger.LogDebug("Document symbols not supported by server");
            return [];
        }

        var param = new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier(PathToUri(filePath))
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var result = await _rpc!.InvokeWithParameterObjectAsync<List<DocumentSymbol>?>(
                "textDocument/documentSymbol",
                param,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Document symbols request timed out for {File}", filePath);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Document symbols request failed for {File}", filePath);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.WorkspaceSymbolProvider != true)
        {
            _logger.LogDebug("Workspace symbols not supported by server");
            return [];
        }

        var param = new WorkspaceSymbolParams { Query = query };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var result = await _rpc!.InvokeWithParameterObjectAsync<List<WorkspaceSymbol>?>(
                "workspace/symbol",
                param,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Workspace symbols request timed out for query: {Query}", query);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace symbols request failed for query: {Query}", query);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CallHierarchyIncomingCall>> GetIncomingCallsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.CallHierarchyProvider != true)
        {
            _logger.LogDebug("Call hierarchy not supported by server");
            return [];
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            // First, prepare the call hierarchy item
            var prepareParam = new CallHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier(PathToUri(filePath)),
                Position = new Position(line, character)
            };

            var items = await _rpc!.InvokeWithParameterObjectAsync<List<CallHierarchyItem>?>(
                "textDocument/prepareCallHierarchy",
                prepareParam,
                timeoutCts.Token);

            if (items is null || items.Count == 0)
            {
                return [];
            }

            // Then get incoming calls for the first item
            var incomingParam = new CallHierarchyIncomingCallsParams { Item = items[0] };
            var result = await _rpc.InvokeWithParameterObjectAsync<List<CallHierarchyIncomingCall>?>(
                "callHierarchy/incomingCalls",
                incomingParam,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Incoming calls request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incoming calls request failed for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CallHierarchyOutgoingCall>> GetOutgoingCallsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_capabilities?.CallHierarchyProvider != true)
        {
            _logger.LogDebug("Call hierarchy not supported by server");
            return [];
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            // First, prepare the call hierarchy item
            var prepareParam = new CallHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier(PathToUri(filePath)),
                Position = new Position(line, character)
            };

            var items = await _rpc!.InvokeWithParameterObjectAsync<List<CallHierarchyItem>?>(
                "textDocument/prepareCallHierarchy",
                prepareParam,
                timeoutCts.Token);

            if (items is null || items.Count == 0)
            {
                return [];
            }

            // Then get outgoing calls for the first item
            var outgoingParam = new CallHierarchyOutgoingCallsParams { Item = items[0] };
            var result = await _rpc.InvokeWithParameterObjectAsync<List<CallHierarchyOutgoingCall>?>(
                "callHierarchy/outgoingCalls",
                outgoingParam,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Outgoing calls request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outgoing calls request failed for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Diagnostic> GetDiagnostics(string filePath)
    {
        var uri = PathToUri(filePath);
        return _diagnostics.TryGetValue(uri, out var diagnostics)
            ? diagnostics
            : [];
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> GetAllDiagnostics()
    {
        return _diagnostics.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<Diagnostic>)kvp.Value);
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_state != LspServerState.Running)
        {
            return;
        }

        _state = LspServerState.Stopping;

        try
        {
            if (_rpc != null)
            {
                // Send shutdown request
                await _rpc.InvokeAsync("shutdown", cancellationToken);

                // Send exit notification
                await _rpc.NotifyAsync("exit");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during LSP shutdown");
        }
        finally
        {
            await DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _state = LspServerState.Stopped;

        _rpc?.Dispose();
        _rpc = null;

        if (_serverProcess != null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                {
                    _serverProcess.Kill();
                    await _serverProcess.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error killing LSP server process");
            }

            _serverProcess.Dispose();
            _serverProcess = null;
        }

        _diagnostics.Clear();
        _documentVersions.Clear();
    }

    private ProcessStartInfo CreateProcessStartInfo(string rootPath)
    {
        return Language switch
        {
            LspLanguage.CSharp => CreateCSharpServerStartInfo(rootPath),
            LspLanguage.PowerShell => CreatePowerShellServerStartInfo(rootPath),
            _ => throw new NotSupportedException($"Language {Language} is not supported")
        };
    }

    private ProcessStartInfo CreateCSharpServerStartInfo(string rootPath)
    {
        var csConfig = _config.CSharp;

        string fileName;
        string arguments;

        if (csConfig.ServerType == CSharpLspServerType.CSharpLs)
        {
            fileName = csConfig.ServerPath ?? "csharp-ls";
            arguments = string.Join(" ", csConfig.AdditionalArguments);
        }
        else // OmniSharp
        {
            fileName = csConfig.ServerPath ?? "omnisharp";
            var args = new List<string> { "-lsp" };

            if (!string.IsNullOrEmpty(csConfig.SolutionPath))
            {
                args.Add($"-s \"{csConfig.SolutionPath}\"");
            }

            args.AddRange(csConfig.AdditionalArguments);
            arguments = string.Join(" ", args);
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = rootPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private ProcessStartInfo CreatePowerShellServerStartInfo(string rootPath)
    {
        var psConfig = _config.PowerShell;
        var pwsh = psConfig.PowerShellPath ?? "pwsh";

        // Build the command to start PSES
        var editorServicesModule = psConfig.EditorServicesPath ?? "PowerShellEditorServices";
        var startCommand = $"Import-Module '{editorServicesModule}'; " +
                          $"Start-EditorServices -HostName 'AstLsp' " +
                          $"-HostProfileId 'AstLsp' " +
                          $"-HostVersion '1.0.0' " +
                          $"-LogPath '{Path.Combine(Path.GetTempPath(), "pses.log")}' " +
                          $"-LogLevel '{psConfig.LogLevel}' " +
                          $"-Stdio " +
                          string.Join(" ", psConfig.AdditionalArguments);

        return new ProcessStartInfo
        {
            FileName = pwsh,
            Arguments = $"-NoLogo -NoProfile -Command \"{startCommand}\"",
            WorkingDirectory = rootPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private InitializeParams CreateInitializeParams(string rootPath)
    {
        var rootUri = PathToUri(rootPath);

        return new InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = rootUri,
            Capabilities = new ClientCapabilities
            {
                TextDocument = new TextDocumentClientCapabilities
                {
                    Synchronization = new SynchronizationCapabilities
                    {
                        DidOpen = true,
                        DidChange = true,
                        DidClose = true
                    },
                    Hover = new HoverCapabilities
                    {
                        ContentFormat = ["markdown", "plaintext"]
                    },
                    PublishDiagnostics = new PublishDiagnosticsCapabilities
                    {
                        VersionSupport = true
                    }
                },
                Workspace = new WorkspaceClientCapabilities
                {
                    Configuration = true,
                    DidChangeWatchedFiles = new DidChangeWatchedFilesCapabilities
                    {
                        DynamicRegistration = true
                    }
                }
            },
            WorkspaceFolders =
            [
                new WorkspaceFolder
                {
                    Uri = rootUri,
                    Name = Path.GetFileName(rootPath)
                }
            ]
        };
    }

    private void OnPublishDiagnostics(PublishDiagnosticsParams param)
    {
        _diagnostics[param.Uri] = param.Diagnostics;

        DiagnosticsReceived?.Invoke(this, new DiagnosticsEventArgs
        {
            Uri = param.Uri,
            Diagnostics = param.Diagnostics
        });

        _logger.LogDebug("Received {Count} diagnostics for {Uri}", param.Diagnostics.Count, param.Uri);
    }

    private void EnsureInitialized()
    {
        if (_state != LspServerState.Running)
        {
            throw new InvalidOperationException($"LSP client is not running (state: {_state}). Call InitializeAsync first.");
        }
    }

    private static string PathToUri(string path)
    {
        // Normalize path and convert to URI
        path = Path.GetFullPath(path);

        // Handle Windows drive letters
        if (Path.DirectorySeparatorChar == '\\')
        {
            path = "/" + path.Replace('\\', '/');
        }

        return "file://" + path;
    }

    private static IReadOnlyList<Location> ParseLocationOrLocations(JsonElement element)
    {
        // LSP definition can return Location, Location[], or LocationLink[]
        if (element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var locations = new List<Location>();
            foreach (var item in element.EnumerateArray())
            {
                var location = ParseSingleLocation(item);
                if (location != null)
                {
                    locations.Add(location);
                }
            }
            return locations;
        }

        var singleLocation = ParseSingleLocation(element);
        return singleLocation != null ? [singleLocation] : [];
    }

    private static Location? ParseSingleLocation(JsonElement element)
    {
        try
        {
            // Try as Location
            if (element.TryGetProperty("uri", out var uriProp) &&
                element.TryGetProperty("range", out var rangeProp))
            {
                return JsonSerializer.Deserialize<Location>(element.GetRawText());
            }

            // Try as LocationLink (use targetUri and targetRange)
            if (element.TryGetProperty("targetUri", out var targetUri) &&
                element.TryGetProperty("targetRange", out var targetRange))
            {
                var uri = targetUri.GetString() ?? string.Empty;
                var range = JsonSerializer.Deserialize<Range>(targetRange.GetRawText());
                if (range != null)
                {
                    return new Location(uri, range);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Exception for LSP-related errors.
/// </summary>
public class LspException : Exception
{
    public LspException(string message) : base(message) { }
    public LspException(string message, Exception innerException) : base(message, innerException) { }
}
