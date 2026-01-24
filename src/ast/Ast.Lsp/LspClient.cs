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
/// <remarks>
/// Creates a new LSP client for the specified language.
/// </remarks>
/// <param name="language">The language to support.</param>
/// <param name="config">LSP configuration.</param>
/// <param name="logger">Logger instance.</param>
public class LspClient(LspLanguage language, LspConfiguration config, ILogger<LspClient> logger) : ILspClient
{
    private readonly ILogger<LspClient> logger = logger;
    private readonly LspConfiguration config = config;
    private readonly ConcurrentDictionary<string, List<Diagnostic>> diagnostics = new();
    private readonly ConcurrentDictionary<string, int> documentVersions = new();

    private Process? serverProcess;
    private JsonRpc? rpc;
    private LspServerState state = LspServerState.NotStarted;
    private string? rootPath;
    private ServerCapabilities? capabilities;

    /// <inheritdoc />
    public LspLanguage Language { get; } = language;

    /// <inheritdoc />
    public LspServerState State => this.state;

    /// <inheritdoc />
    public string? RootPath => this.rootPath;

    /// <inheritdoc />
    public ServerCapabilities? Capabilities => this.capabilities;

    /// <inheritdoc />
    public event EventHandler<DiagnosticsEventArgs>? DiagnosticsReceived;

    /// <inheritdoc />
    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (this.state == LspServerState.Running)
        {
            this.logger.LogWarning("LSP client already initialized for {RootPath}", this.rootPath);
            return;
        }

        this.state = LspServerState.Starting;
        this.rootPath = rootPath;

        try
        {
            // Start the language server process
            var startInfo = this.CreateProcessStartInfo(rootPath);
            this.serverProcess = new Process { StartInfo = startInfo };

            this.serverProcess.Start();
            this.logger.LogInformation("Started {Language} LSP server (PID: {Pid})", this.Language, this.serverProcess.Id);

            // Create JSON-RPC connection over stdio
            this.rpc = new JsonRpc(
                this.serverProcess.StandardInput.BaseStream,
                this.serverProcess.StandardOutput.BaseStream);

            // Register notification handlers
            this.rpc.AddLocalRpcMethod("textDocument/publishDiagnostics", new Action<PublishDiagnosticsParams>(this.OnPublishDiagnostics));

            this.rpc.StartListening();

            // Send initialize request
            var initParams = CreateInitializeParams(rootPath);
            var result = await this.rpc.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize",
                initParams,
                cancellationToken);

            this.capabilities = result.Capabilities;

            // Send initialized notification
            await this.rpc.NotifyAsync("initialized");

            this.state = LspServerState.Running;
            this.logger.LogInformation(
                "{Language} LSP server initialized. Hover: {Hover}, Definition: {Def}, References: {Refs}, CallHierarchy: {Call}",
                this.Language,
                ServerCapabilities.IsCapabilityEnabled(this.capabilities.HoverProvider),
                ServerCapabilities.IsCapabilityEnabled(this.capabilities.DefinitionProvider),
                ServerCapabilities.IsCapabilityEnabled(this.capabilities.ReferencesProvider),
                ServerCapabilities.IsCapabilityEnabled(this.capabilities.CallHierarchyProvider));
        }
        catch (Exception ex)
        {
            this.state = LspServerState.Failed;
            this.logger.LogError(ex, "Failed to initialize {Language} LSP server", this.Language);
            throw new LspException($"Failed to initialize {this.Language} LSP server", ex);
        }
    }

    /// <inheritdoc />
    public async Task OpenDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        this.EnsureInitialized();

        var uri = PathToUri(filePath);
        var version = this.documentVersions.AddOrUpdate(uri, 1, (_, v) => v + 1);

        var languageId = this.Language switch
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

        await this.rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", param);
        this.logger.LogDebug("Opened document: {Uri}", uri);
    }

    /// <inheritdoc />
    public async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        this.EnsureInitialized();

        var uri = PathToUri(filePath);
        var param = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        };

        await this.rpc!.NotifyWithParameterObjectAsync("textDocument/didClose", param);
        this.documentVersions.TryRemove(uri, out _);
        this.diagnostics.TryRemove(uri, out _);
        this.logger.LogDebug("Closed document: {Uri}", uri);
    }

    /// <inheritdoc />
    public async Task<HoverResult?> GetHoverAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.HoverProvider))
        {
            this.logger.LogDebug("Hover not supported by server");
            return null;
        }

        var param = new TextDocumentPositionParams(
            new TextDocumentIdentifier(PathToUri(filePath)),
            new Position(line, character));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            var result = await this.rpc!.InvokeWithParameterObjectAsync<HoverResult?>(
                "textDocument/hover",
                param,
                timeoutCts.Token);

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("Hover request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return null;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Hover request failed for {File}:{Line}:{Char}", filePath, line, character);
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
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.DefinitionProvider))
        {
            this.logger.LogDebug("Definition not supported by server");
            return [];
        }

        var param = new TextDocumentPositionParams(
            new TextDocumentIdentifier(PathToUri(filePath)),
            new Position(line, character));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            var result = await this.rpc!.InvokeWithParameterObjectAsync<JsonElement>(
                "textDocument/definition",
                param,
                timeoutCts.Token);

            return ParseLocationOrLocations(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("Definition request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Definition request failed for {File}:{Line}:{Char}", filePath, line, character);
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
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.ReferencesProvider))
        {
            this.logger.LogDebug("References not supported by server");
            return [];
        }

        var param = new ReferenceParams(
            new TextDocumentIdentifier(PathToUri(filePath)),
            new Position(line, character),
            new ReferenceContext(includeDeclaration));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            var result = await this.rpc!.InvokeWithParameterObjectAsync<List<Location>?>(
                "textDocument/references",
                param,
                timeoutCts.Token);

            var locations = result ?? [];

            // Limit results
            if (locations.Count > this.config.MaxReferences)
            {
                locations = [.. locations.Take(this.config.MaxReferences)];
            }

            return locations;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("References request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "References request failed for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.DocumentSymbolProvider))
        {
            this.logger.LogDebug("Document symbols not supported by server");
            return [];
        }

        var param = new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier(PathToUri(filePath))
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            var result = await this.rpc!.InvokeWithParameterObjectAsync<List<DocumentSymbol>?>(
                "textDocument/documentSymbol",
                param,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("Document symbols request timed out for {File}", filePath);
            return [];
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Document symbols request failed for {File}", filePath);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.WorkspaceSymbolProvider))
        {
            this.logger.LogDebug("Workspace symbols not supported by server");
            return [];
        }

        var param = new WorkspaceSymbolParams { Query = query };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            var result = await this.rpc!.InvokeWithParameterObjectAsync<List<WorkspaceSymbol>?>(
                "workspace/symbol",
                param,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("Workspace symbols request timed out for query: {Query}", query);
            return [];
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Workspace symbols request failed for query: {Query}", query);
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
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.CallHierarchyProvider))
        {
            this.logger.LogDebug("Call hierarchy not supported by server");
            return [];
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            // First, prepare the call hierarchy item
            var prepareParam = new CallHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier(PathToUri(filePath)),
                Position = new Position(line, character)
            };

            var items = await this.rpc!.InvokeWithParameterObjectAsync<List<CallHierarchyItem>?>(
                "textDocument/prepareCallHierarchy",
                prepareParam,
                timeoutCts.Token);

            if (items is null || items.Count == 0)
            {
                return [];
            }

            // Then get incoming calls for the first item
            var incomingParam = new CallHierarchyIncomingCallsParams { Item = items[0] };
            var result = await this.rpc.InvokeWithParameterObjectAsync<List<CallHierarchyIncomingCall>?>(
                "callHierarchy/incomingCalls",
                incomingParam,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("Incoming calls request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Incoming calls request failed for {File}:{Line}:{Char}", filePath, line, character);
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
        this.EnsureInitialized();

        if (!ServerCapabilities.IsCapabilityEnabled(this.capabilities?.CallHierarchyProvider))
        {
            this.logger.LogDebug("Call hierarchy not supported by server");
            return [];
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.config.TimeoutMs);

            // First, prepare the call hierarchy item
            var prepareParam = new CallHierarchyPrepareParams
            {
                TextDocument = new TextDocumentIdentifier(PathToUri(filePath)),
                Position = new Position(line, character)
            };

            var items = await this.rpc!.InvokeWithParameterObjectAsync<List<CallHierarchyItem>?>(
                "textDocument/prepareCallHierarchy",
                prepareParam,
                timeoutCts.Token);

            if (items is null || items.Count == 0)
            {
                return [];
            }

            // Then get outgoing calls for the first item
            var outgoingParam = new CallHierarchyOutgoingCallsParams { Item = items[0] };
            var result = await this.rpc.InvokeWithParameterObjectAsync<List<CallHierarchyOutgoingCall>?>(
                "callHierarchy/outgoingCalls",
                outgoingParam,
                timeoutCts.Token);

            return result ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning("Outgoing calls request timed out for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Outgoing calls request failed for {File}:{Line}:{Char}", filePath, line, character);
            return [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Diagnostic> GetDiagnostics(string filePath)
    {
        var uri = PathToUri(filePath);
        return this.diagnostics.TryGetValue(uri, out var diagnostics)
            ? diagnostics
            : [];
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> GetAllDiagnostics()
    {
        return this.diagnostics.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<Diagnostic>)kvp.Value);
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (this.state != LspServerState.Running)
        {
            return;
        }

        this.state = LspServerState.Stopping;

        try
        {
            if (this.rpc != null)
            {
                // Send shutdown request
                await this.rpc.InvokeAsync("shutdown", cancellationToken);

                // Send exit notification
                await this.rpc.NotifyAsync("exit");
            }
        }
        catch (StreamJsonRpc.RemoteInvocationException ex)
        {
            // PowerShell Editor Services may return "Internal Error" during shutdown
            // which is expected behavior and can be safely ignored
            this.logger.LogDebug(ex, "LSP server returned error during shutdown (expected for some servers)");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Error during LSP shutdown");
        }
        finally
        {
            await this.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.state = LspServerState.Stopped;

        this.rpc?.Dispose();
        this.rpc = null;

        if (this.serverProcess != null)
        {
            try
            {
                if (!this.serverProcess.HasExited)
                {
                    this.serverProcess.Kill();
                    await this.serverProcess.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Error killing LSP server process");
            }

            this.serverProcess.Dispose();
            this.serverProcess = null;
        }

        this.diagnostics.Clear();
        this.documentVersions.Clear();
    }

    private ProcessStartInfo CreateProcessStartInfo(string rootPath)
    {
        return this.Language switch
        {
            LspLanguage.CSharp => this.CreateCSharpServerStartInfo(rootPath),
            LspLanguage.PowerShell => this.CreatePowerShellServerStartInfo(rootPath),
            _ => throw new NotSupportedException($"Language {this.Language} is not supported")
        };
    }

    private ProcessStartInfo CreateCSharpServerStartInfo(string rootPath)
    {
        var csConfig = this.config.CSharp;

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
        var psConfig = this.config.PowerShell;
        var pwsh = psConfig.PowerShellPath ?? "pwsh";

        // Build the command to start PSES
        var editorServicesModule = psConfig.EditorServicesPath ?? "it ";
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

    private static InitializeParams CreateInitializeParams(string rootPath)
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
        this.diagnostics[param.Uri] = param.Diagnostics;

        DiagnosticsReceived?.Invoke(this, new DiagnosticsEventArgs
        {
            Uri = param.Uri,
            Diagnostics = param.Diagnostics
        });

        this.logger.LogDebug("Received {Count} diagnostics for {Uri}", param.Diagnostics.Count, param.Uri);
    }

    private void EnsureInitialized()
    {
        if (this.state != LspServerState.Running)
        {
            throw new InvalidOperationException($"LSP client is not running (state: {this.state}). Call InitializeAsync first.");
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