// -----------------------------------------------------------------------
// <copyright file="ILspClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

/// <summary>
/// Interface for Language Server Protocol client operations.
/// </summary>
public interface ILspClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the language this client handles.
    /// </summary>
    LspLanguage Language { get; }

    /// <summary>
    /// Gets the current state of the LSP server.
    /// </summary>
    LspServerState State { get; }

    /// <summary>
    /// Gets the root path of the workspace.
    /// </summary>
    string? RootPath { get; }

    /// <summary>
    /// Gets the server capabilities after initialization.
    /// </summary>
    ServerCapabilities? Capabilities { get; }

    /// <summary>
    /// Initializes the LSP server for the given workspace root.
    /// </summary>
    /// <param name="rootPath">The root path of the workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the initialization.</returns>
    Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies the server that a document was opened.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="content">The file content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OpenDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies the server that a document was closed.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets hover information for a position in a document.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="line">Zero-based line number.</param>
    /// <param name="character">Zero-based character position.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hover result or null if not available.</returns>
    Task<HoverResult?> GetHoverAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets definition locations for a symbol.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="line">Zero-based line number.</param>
    /// <param name="character">Zero-based character position.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of definition locations.</returns>
    Task<IReadOnlyList<Location>> GetDefinitionsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets reference locations for a symbol.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="line">Zero-based line number.</param>
    /// <param name="character">Zero-based character position.</param>
    /// <param name="includeDeclaration">Whether to include the declaration in results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of reference locations.</returns>
    Task<IReadOnlyList<Location>> GetReferencesAsync(
        string filePath,
        int line,
        int character,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets document symbols for a file.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of document symbols.</returns>
    Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for symbols in the workspace.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching symbols.</returns>
    Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets incoming calls (callers) for a symbol.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="line">Zero-based line number.</param>
    /// <param name="character">Zero-based character position.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of incoming calls.</returns>
    Task<IReadOnlyList<CallHierarchyIncomingCall>> GetIncomingCallsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets outgoing calls (callees) for a symbol.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="line">Zero-based line number.</param>
    /// <param name="character">Zero-based character position.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of outgoing calls.</returns>
    Task<IReadOnlyList<CallHierarchyOutgoingCall>> GetOutgoingCallsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets diagnostics for a file.
    /// The server publishes diagnostics asynchronously.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <returns>List of diagnostics for the file.</returns>
    IReadOnlyList<Diagnostic> GetDiagnostics(string filePath);

    /// <summary>
    /// Gets all diagnostics for all opened files.
    /// </summary>
    /// <returns>Dictionary mapping file URIs to their diagnostics.</returns>
    IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> GetAllDiagnostics();

    /// <summary>
    /// Event raised when diagnostics are published.
    /// </summary>
    event EventHandler<DiagnosticsEventArgs>? DiagnosticsReceived;

    /// <summary>
    /// Shuts down the LSP server gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Event arguments for diagnostics received.
/// </summary>
public class DiagnosticsEventArgs : EventArgs
{
    /// <summary>
    /// The file URI.
    /// </summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>
    /// The diagnostics.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}