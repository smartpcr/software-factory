// -----------------------------------------------------------------------
// <copyright file="LspModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

using System.Text.Json.Serialization;

/// <summary>
/// Represents a position in a text document.
/// </summary>
public record Position(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

/// <summary>
/// Represents a range in a text document.
/// </summary>
public record Range(
    [property: JsonPropertyName("start")] Position Start,
    [property: JsonPropertyName("end")] Position End);

/// <summary>
/// Represents a location in a text document.
/// </summary>
public record Location(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range);

/// <summary>
/// Text document identifier.
/// </summary>
public record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

/// <summary>
/// Text document position parameters.
/// </summary>
public record TextDocumentPositionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position);

/// <summary>
/// Reference parameters.
/// </summary>
public record ReferenceParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("context")] ReferenceContext Context);

/// <summary>
/// Reference context.
/// </summary>
public record ReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

/// <summary>
/// Represents hover information.
/// </summary>
public class HoverResult
{
    /// <summary>
    /// The hover contents.
    /// </summary>
    [JsonPropertyName("contents")]
    public MarkupContent? Contents { get; set; }

    /// <summary>
    /// An optional range.
    /// </summary>
    [JsonPropertyName("range")]
    public Range? Range { get; set; }
}

/// <summary>
/// Markup content.
/// </summary>
public class MarkupContent
{
    /// <summary>
    /// The kind of markup (plaintext or markdown).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "plaintext";

    /// <summary>
    /// The actual content.
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Symbol kind enumeration.
/// </summary>
public enum SymbolKind
{
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26
}

/// <summary>
/// Document symbol representing a symbol in a document.
/// </summary>
public class DocumentSymbol
{
    /// <summary>
    /// The name of this symbol.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// More detail for this symbol, e.g. the signature of a function.
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>
    /// The kind of this symbol.
    /// </summary>
    [JsonPropertyName("kind")]
    public SymbolKind Kind { get; set; }

    /// <summary>
    /// The range enclosing this symbol.
    /// </summary>
    [JsonPropertyName("range")]
    public Range Range { get; set; } = null!;

    /// <summary>
    /// The range that should be selected when this symbol is selected.
    /// </summary>
    [JsonPropertyName("selectionRange")]
    public Range SelectionRange { get; set; } = null!;

    /// <summary>
    /// Children of this symbol (for nested symbols).
    /// </summary>
    [JsonPropertyName("children")]
    public List<DocumentSymbol>? Children { get; set; }
}

/// <summary>
/// Workspace symbol.
/// </summary>
public class WorkspaceSymbol
{
    /// <summary>
    /// The name of this symbol.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The kind of this symbol.
    /// </summary>
    [JsonPropertyName("kind")]
    public SymbolKind Kind { get; set; }

    /// <summary>
    /// The location of this symbol.
    /// </summary>
    [JsonPropertyName("location")]
    public Location Location { get; set; } = null!;

    /// <summary>
    /// Container name (e.g., class name for methods).
    /// </summary>
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }
}

/// <summary>
/// Diagnostic severity.
/// </summary>
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}

/// <summary>
/// Represents a diagnostic, such as a compiler error or warning.
/// </summary>
public class Diagnostic
{
    /// <summary>
    /// The range at which the message applies.
    /// </summary>
    [JsonPropertyName("range")]
    public Range Range { get; set; } = null!;

    /// <summary>
    /// The diagnostic's severity.
    /// </summary>
    [JsonPropertyName("severity")]
    public DiagnosticSeverity? Severity { get; set; }

    /// <summary>
    /// The diagnostic's code.
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>
    /// The source of this diagnostic (e.g., 'csharp', 'typescript').
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// The diagnostic's message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Publish diagnostics parameters.
/// </summary>
public class PublishDiagnosticsParams
{
    /// <summary>
    /// The URI for which diagnostics are reported.
    /// </summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// The list of diagnostics.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

/// <summary>
/// Call hierarchy item.
/// </summary>
public class CallHierarchyItem
{
    /// <summary>
    /// The name of this item.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The kind of this item.
    /// </summary>
    [JsonPropertyName("kind")]
    public SymbolKind Kind { get; set; }

    /// <summary>
    /// More detail for this item, e.g. the signature of a function.
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>
    /// The resource identifier of this item.
    /// </summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// The range enclosing this symbol.
    /// </summary>
    [JsonPropertyName("range")]
    public Range Range { get; set; } = null!;

    /// <summary>
    /// The range that should be selected when this item is selected.
    /// </summary>
    [JsonPropertyName("selectionRange")]
    public Range SelectionRange { get; set; } = null!;

    /// <summary>
    /// Additional data for this item.
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// Incoming call hierarchy item.
/// </summary>
public class CallHierarchyIncomingCall
{
    /// <summary>
    /// The item that makes the call.
    /// </summary>
    [JsonPropertyName("from")]
    public CallHierarchyItem From { get; set; } = null!;

    /// <summary>
    /// The ranges at which the calls appear.
    /// </summary>
    [JsonPropertyName("fromRanges")]
    public List<Range> FromRanges { get; set; } = [];
}

/// <summary>
/// Outgoing call hierarchy item.
/// </summary>
public class CallHierarchyOutgoingCall
{
    /// <summary>
    /// The item being called.
    /// </summary>
    [JsonPropertyName("to")]
    public CallHierarchyItem To { get; set; } = null!;

    /// <summary>
    /// The range at which this item is called.
    /// </summary>
    [JsonPropertyName("fromRanges")]
    public List<Range> FromRanges { get; set; } = [];
}

/// <summary>
/// Initialize parameters.
/// </summary>
public class InitializeParams
{
    /// <summary>
    /// The process ID of the parent process.
    /// </summary>
    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    /// <summary>
    /// The root URI of the workspace.
    /// </summary>
    [JsonPropertyName("rootUri")]
    public string? RootUri { get; set; }

    /// <summary>
    /// The capabilities provided by the client.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public ClientCapabilities Capabilities { get; set; } = new();

    /// <summary>
    /// The workspace folders.
    /// </summary>
    [JsonPropertyName("workspaceFolders")]
    public List<WorkspaceFolder>? WorkspaceFolders { get; set; }
}

/// <summary>
/// Workspace folder.
/// </summary>
public class WorkspaceFolder
{
    /// <summary>
    /// The associated URI for this workspace folder.
    /// </summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// The name of the workspace folder.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Client capabilities.
/// </summary>
public class ClientCapabilities
{
    /// <summary>
    /// Text document capabilities.
    /// </summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentClientCapabilities? TextDocument { get; set; }

    /// <summary>
    /// Workspace capabilities.
    /// </summary>
    [JsonPropertyName("workspace")]
    public WorkspaceClientCapabilities? Workspace { get; set; }
}

/// <summary>
/// Text document client capabilities.
/// </summary>
public class TextDocumentClientCapabilities
{
    /// <summary>
    /// Synchronization capabilities.
    /// </summary>
    [JsonPropertyName("synchronization")]
    public SynchronizationCapabilities? Synchronization { get; set; }

    /// <summary>
    /// Hover capabilities.
    /// </summary>
    [JsonPropertyName("hover")]
    public HoverCapabilities? Hover { get; set; }

    /// <summary>
    /// Publish diagnostics capabilities.
    /// </summary>
    [JsonPropertyName("publishDiagnostics")]
    public PublishDiagnosticsCapabilities? PublishDiagnostics { get; set; }
}

/// <summary>
/// Synchronization capabilities.
/// </summary>
public class SynchronizationCapabilities
{
    [JsonPropertyName("didOpen")]
    public bool DidOpen { get; set; } = true;

    [JsonPropertyName("didChange")]
    public bool DidChange { get; set; } = true;

    [JsonPropertyName("didClose")]
    public bool DidClose { get; set; } = true;
}

/// <summary>
/// Hover capabilities.
/// </summary>
public class HoverCapabilities
{
    [JsonPropertyName("contentFormat")]
    public List<string>? ContentFormat { get; set; }
}

/// <summary>
/// Publish diagnostics capabilities.
/// </summary>
public class PublishDiagnosticsCapabilities
{
    [JsonPropertyName("versionSupport")]
    public bool VersionSupport { get; set; } = true;
}

/// <summary>
/// Workspace client capabilities.
/// </summary>
public class WorkspaceClientCapabilities
{
    [JsonPropertyName("configuration")]
    public bool Configuration { get; set; } = true;

    [JsonPropertyName("didChangeWatchedFiles")]
    public DidChangeWatchedFilesCapabilities? DidChangeWatchedFiles { get; set; }
}

/// <summary>
/// Did change watched files capabilities.
/// </summary>
public class DidChangeWatchedFilesCapabilities
{
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; set; } = true;
}

/// <summary>
/// Initialize result.
/// </summary>
public class InitializeResult
{
    /// <summary>
    /// The capabilities the server provides.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; set; } = new();
}

/// <summary>
/// Server capabilities.
/// </summary>
public class ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public object? TextDocumentSync { get; set; }

    [JsonPropertyName("hoverProvider")]
    public bool HoverProvider { get; set; }

    [JsonPropertyName("definitionProvider")]
    public bool DefinitionProvider { get; set; }

    [JsonPropertyName("referencesProvider")]
    public bool ReferencesProvider { get; set; }

    [JsonPropertyName("documentSymbolProvider")]
    public bool DocumentSymbolProvider { get; set; }

    [JsonPropertyName("workspaceSymbolProvider")]
    public bool WorkspaceSymbolProvider { get; set; }

    [JsonPropertyName("callHierarchyProvider")]
    public bool CallHierarchyProvider { get; set; }
}

/// <summary>
/// Text document item for didOpen notification.
/// </summary>
public class TextDocumentItem
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("languageId")]
    public string LanguageId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Did open text document parameters.
/// </summary>
public class DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentItem TextDocument { get; set; } = null!;
}

/// <summary>
/// Did close text document parameters.
/// </summary>
public class DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;
}

/// <summary>
/// Document symbol parameters.
/// </summary>
public class DocumentSymbolParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;
}

/// <summary>
/// Workspace symbol parameters.
/// </summary>
public class WorkspaceSymbolParams
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
}

/// <summary>
/// Call hierarchy prepare parameters.
/// </summary>
public class CallHierarchyPrepareParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonPropertyName("position")]
    public Position Position { get; set; } = null!;
}

/// <summary>
/// Call hierarchy incoming calls parameters.
/// </summary>
public class CallHierarchyIncomingCallsParams
{
    [JsonPropertyName("item")]
    public CallHierarchyItem Item { get; set; } = null!;
}

/// <summary>
/// Call hierarchy outgoing calls parameters.
/// </summary>
public class CallHierarchyOutgoingCallsParams
{
    [JsonPropertyName("item")]
    public CallHierarchyItem Item { get; set; } = null!;
}

/// <summary>
/// LSP enrichment data extracted for a code symbol.
/// </summary>
public class LspEnrichmentData
{
    /// <summary>
    /// Type signature from hover information.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>
    /// Return type extracted from signature.
    /// </summary>
    public string? ReturnType { get; set; }

    /// <summary>
    /// Parameter information.
    /// </summary>
    public List<ParameterInfo> Parameters { get; set; } = [];

    /// <summary>
    /// Documentation/summary from hover.
    /// </summary>
    public string? Documentation { get; set; }

    /// <summary>
    /// Definition locations.
    /// </summary>
    public List<Location> Definitions { get; set; } = [];

    /// <summary>
    /// Reference locations (usages).
    /// </summary>
    public List<Location> References { get; set; } = [];

    /// <summary>
    /// Incoming calls (who calls this).
    /// </summary>
    public List<CallHierarchyIncomingCall> IncomingCalls { get; set; } = [];

    /// <summary>
    /// Outgoing calls (what this calls).
    /// </summary>
    public List<CallHierarchyOutgoingCall> OutgoingCalls { get; set; } = [];

    /// <summary>
    /// Diagnostics for this symbol.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

/// <summary>
/// Parameter information.
/// </summary>
public class ParameterInfo
{
    /// <summary>
    /// Parameter name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Parameter type.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Whether the parameter has a default value.
    /// </summary>
    public bool HasDefault { get; set; }

    /// <summary>
    /// Default value if available.
    /// </summary>
    public string? DefaultValue { get; set; }
}
