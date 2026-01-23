// -----------------------------------------------------------------------
// <copyright file="LspEnrichmentService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp;

using System.Text.RegularExpressions;
using Ast.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Service for enriching hypergraph nodes with LSP semantic information.
/// </summary>
public class LspEnrichmentService : IAsyncDisposable
{
    private readonly LspServerManager _serverManager;
    private readonly LspConfiguration _config;
    private readonly ILogger<LspEnrichmentService> _logger;

    /// <summary>
    /// Creates a new LSP enrichment service.
    /// </summary>
    /// <param name="serverManager">The LSP server manager.</param>
    /// <param name="options">LSP configuration options.</param>
    /// <param name="logger">Logger instance.</param>
    public LspEnrichmentService(
        LspServerManager serverManager,
        IOptions<LspConfiguration> options,
        ILogger<LspEnrichmentService> logger)
    {
        _serverManager = serverManager;
        _config = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Enriches a single hypergraph node with LSP semantic information.
    /// </summary>
    /// <param name="node">The node to enrich.</param>
    /// <param name="workspacePath">The workspace root path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enriched node.</returns>
    public async Task<HypergraphNode> EnrichNodeAsync(
        HypergraphNode node,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return node;
        }

        var language = LspServerManager.GetLanguageForFile(node.FilePath);
        if (language is null)
        {
            return node;
        }

        try
        {
            var client = await _serverManager.GetOrCreateClientAsync(language.Value, workspacePath, cancellationToken);

            // Read file content and open document
            var content = await File.ReadAllTextAsync(node.FilePath, cancellationToken);
            await client.OpenDocumentAsync(node.FilePath, content, cancellationToken);

            try
            {
                var enrichmentData = await GetEnrichmentDataAsync(client, node, cancellationToken);
                return ApplyEnrichmentData(node, enrichmentData);
            }
            finally
            {
                await client.CloseDocumentAsync(node.FilePath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich node {NodeId} from {File}", node.Id, node.FilePath);
            return node;
        }
    }

    /// <summary>
    /// Enriches a batch of hypergraph nodes with LSP semantic information.
    /// </summary>
    /// <param name="nodes">The nodes to enrich.</param>
    /// <param name="workspacePath">The workspace root path.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enriched nodes.</returns>
    public async Task<IReadOnlyList<HypergraphNode>> EnrichBatchAsync(
        IReadOnlyList<HypergraphNode> nodes,
        string workspacePath,
        IProgress<EnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || nodes.Count == 0)
        {
            return nodes;
        }

        var results = new List<HypergraphNode>(nodes.Count);
        var processed = 0;

        // Group nodes by file to minimize document open/close operations
        var nodesByFile = nodes.GroupBy(n => n.FilePath).ToList();

        foreach (var fileGroup in nodesByFile)
        {
            var filePath = fileGroup.Key;
            var fileNodes = fileGroup.ToList();

            var language = LspServerManager.GetLanguageForFile(filePath);
            if (language is null)
            {
                results.AddRange(fileNodes);
                processed += fileNodes.Count;
                continue;
            }

            try
            {
                var client = await _serverManager.GetOrCreateClientAsync(
                    language.Value, workspacePath, cancellationToken);

                // Read and open the file once
                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                await client.OpenDocumentAsync(filePath, content, cancellationToken);

                try
                {
                    // Wait a bit for diagnostics to be processed
                    await Task.Delay(100, cancellationToken);

                    foreach (var node in fileNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var enrichmentData = await GetEnrichmentDataAsync(client, node, cancellationToken);
                            var enrichedNode = ApplyEnrichmentData(node, enrichmentData);
                            results.Add(enrichedNode);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to enrich node {NodeId}", node.Id);
                            results.Add(node);
                        }

                        processed++;
                        progress?.Report(new EnrichmentProgress(processed, nodes.Count, filePath, node.Name));
                    }
                }
                finally
                {
                    await client.CloseDocumentAsync(filePath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process file {File}", filePath);
                results.AddRange(fileNodes);
                processed += fileNodes.Count;
            }
        }

        _logger.LogInformation("Enriched {Enriched}/{Total} nodes", results.Count, nodes.Count);
        return results;
    }

    /// <summary>
    /// Enriches all nodes in a hypergraph.
    /// </summary>
    /// <param name="hypergraph">The hypergraph to enrich.</param>
    /// <param name="workspacePath">The workspace root path.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enriched hypergraph.</returns>
    public async Task<HypergraphData> EnrichHypergraphAsync(
        HypergraphData hypergraph,
        string workspacePath,
        IProgress<EnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
        {
            return hypergraph;
        }

        var nodes = hypergraph.Nodes.Values.ToList();
        var enrichedNodes = await EnrichBatchAsync(nodes, workspacePath, progress, cancellationToken);

        var enrichedNodeDict = enrichedNodes.ToDictionary(n => n.Id);

        return new HypergraphData
        {
            Nodes = enrichedNodeDict,
            Hyperedges = hypergraph.Hyperedges,
            Metadata = hypergraph.Metadata
        };
    }

    /// <summary>
    /// Gets enrichment data for a specific node.
    /// </summary>
    private async Task<LspEnrichmentData> GetEnrichmentDataAsync(
        ILspClient client,
        HypergraphNode node,
        CancellationToken cancellationToken)
    {
        var data = new LspEnrichmentData();

        // Use the start of the node as the position
        var line = node.LineStart - 1; // Convert to 0-based
        var character = 0;

        // Try to get a better character position from the node name in metadata
        if (node.Metadata.TryGetValue("NameColumn", out var colObj) && colObj is int col)
        {
            character = col;
        }

        // Get hover information (contains type signature and documentation)
        var hover = await client.GetHoverAsync(node.FilePath, line, character, cancellationToken);
        if (hover?.Contents != null)
        {
            ParseHoverContent(hover.Contents, data);
        }

        // Get references
        var references = await client.GetReferencesAsync(
            node.FilePath, line, character, includeDeclaration: false, cancellationToken);
        data.References = references.ToList();

        // Get call hierarchy if enabled and applicable
        if (_config.CollectCallHierarchy && IsCallableNode(node))
        {
            var incomingCalls = await client.GetIncomingCallsAsync(
                node.FilePath, line, character, cancellationToken);
            data.IncomingCalls = incomingCalls.ToList();

            var outgoingCalls = await client.GetOutgoingCallsAsync(
                node.FilePath, line, character, cancellationToken);
            data.OutgoingCalls = outgoingCalls.ToList();
        }

        // Get diagnostics
        if (_config.CollectDiagnostics)
        {
            var diagnostics = client.GetDiagnostics(node.FilePath);
            data.Diagnostics = diagnostics
                .Where(d => d.Range.Start.Line >= node.LineStart - 1 &&
                           d.Range.End.Line <= node.LineEnd - 1)
                .ToList();
        }

        return data;
    }

    /// <summary>
    /// Parses hover content to extract signature and documentation.
    /// </summary>
    private static void ParseHoverContent(MarkupContent content, LspEnrichmentData data)
    {
        var value = content.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Handle markdown content
        if (content.Kind == "markdown")
        {
            // Extract code blocks (usually contain signatures)
            var codeBlockMatch = Regex.Match(value, @"```(?:csharp|cs|powershell|ps1)?\s*\n(.*?)\n```", RegexOptions.Singleline);
            if (codeBlockMatch.Success)
            {
                data.Signature = codeBlockMatch.Groups[1].Value.Trim();
                ParseSignature(data.Signature, data);
            }

            // Extract documentation (text outside code blocks)
            var docText = Regex.Replace(value, @"```.*?```", "", RegexOptions.Singleline).Trim();
            if (!string.IsNullOrWhiteSpace(docText))
            {
                // Remove markdown headers and excessive whitespace
                docText = Regex.Replace(docText, @"^#+\s*", "", RegexOptions.Multiline);
                docText = Regex.Replace(docText, @"\n{3,}", "\n\n");
                data.Documentation = docText.Trim();
            }
        }
        else
        {
            // Plain text - try to extract signature
            var lines = value.Split('\n');
            if (lines.Length > 0)
            {
                data.Signature = lines[0].Trim();
                ParseSignature(data.Signature, data);

                if (lines.Length > 1)
                {
                    data.Documentation = string.Join("\n", lines.Skip(1)).Trim();
                }
            }
        }
    }

    /// <summary>
    /// Parses a type signature to extract return type and parameters.
    /// </summary>
    private static void ParseSignature(string signature, LspEnrichmentData data)
    {
        // Try to match C# method signature pattern
        var methodMatch = Regex.Match(signature,
            @"(?:(?<modifiers>[\w\s]+)\s+)?(?<returnType>[\w<>\[\],\s\?]+)\s+(?<name>\w+)\s*\((?<params>.*?)\)",
            RegexOptions.Singleline);

        if (methodMatch.Success)
        {
            data.ReturnType = methodMatch.Groups["returnType"].Value.Trim();
            var paramsStr = methodMatch.Groups["params"].Value;

            if (!string.IsNullOrWhiteSpace(paramsStr))
            {
                data.Parameters = ParseParameters(paramsStr);
            }

            return;
        }

        // Try to match property/field signature
        var propMatch = Regex.Match(signature, @"(?<type>[\w<>\[\],\s\?]+)\s+(?<name>\w+)\s*\{");
        if (propMatch.Success)
        {
            data.ReturnType = propMatch.Groups["type"].Value.Trim();
        }
    }

    /// <summary>
    /// Parses parameter list from a method signature.
    /// </summary>
    private static List<ParameterInfo> ParseParameters(string paramsStr)
    {
        var parameters = new List<ParameterInfo>();

        // Split by comma, but be careful with generic types
        var paramParts = SplitParameters(paramsStr);

        foreach (var part in paramParts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var param = new ParameterInfo();

            // Check for default value
            var defaultMatch = Regex.Match(trimmed, @"(.+?)\s*=\s*(.+)$");
            if (defaultMatch.Success)
            {
                trimmed = defaultMatch.Groups[1].Value.Trim();
                param.HasDefault = true;
                param.DefaultValue = defaultMatch.Groups[2].Value.Trim();
            }

            // Split type and name
            var lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                param.Type = trimmed[..lastSpace].Trim();
                param.Name = trimmed[(lastSpace + 1)..].Trim();
            }
            else
            {
                param.Name = trimmed;
            }

            parameters.Add(param);
        }

        return parameters;
    }

    /// <summary>
    /// Splits parameter string handling nested generics.
    /// </summary>
    private static List<string> SplitParameters(string paramsStr)
    {
        var result = new List<string>();
        var current = "";
        var depth = 0;

        foreach (var ch in paramsStr)
        {
            switch (ch)
            {
                case '<':
                case '[':
                case '(':
                    depth++;
                    current += ch;
                    break;
                case '>':
                case ']':
                case ')':
                    depth--;
                    current += ch;
                    break;
                case ',' when depth == 0:
                    result.Add(current);
                    current = "";
                    break;
                default:
                    current += ch;
                    break;
            }
        }

        if (!string.IsNullOrEmpty(current))
        {
            result.Add(current);
        }

        return result;
    }

    /// <summary>
    /// Applies enrichment data to a node.
    /// </summary>
    private HypergraphNode ApplyEnrichmentData(HypergraphNode node, LspEnrichmentData data)
    {
        var metadata = new Dictionary<string, object?>(node.Metadata);

        if (!string.IsNullOrEmpty(data.Signature))
        {
            metadata["Signature"] = data.Signature;
        }

        if (!string.IsNullOrEmpty(data.ReturnType))
        {
            metadata["ReturnType"] = data.ReturnType;
        }

        if (data.Parameters.Count > 0)
        {
            metadata["Parameters"] = string.Join(", ",
                data.Parameters.Select(p => $"{p.Name}: {p.Type}"));
            metadata["ParameterCount"] = data.Parameters.Count;
        }

        if (!string.IsNullOrEmpty(data.Documentation))
        {
            metadata["Documentation"] = data.Documentation;
        }

        if (data.References.Count > 0)
        {
            metadata["ReferenceCount"] = data.References.Count;
            metadata["CalledBy"] = string.Join(", ",
                data.References
                    .Select(r => $"{Path.GetFileName(UriToPath(r.Uri))}:{r.Range.Start.Line + 1}")
                    .Take(20));
        }

        if (data.IncomingCalls.Count > 0)
        {
            metadata["IncomingCallCount"] = data.IncomingCalls.Count;
            metadata["Callers"] = string.Join(", ",
                data.IncomingCalls.Select(c => c.From.Name).Take(20));
        }

        if (data.OutgoingCalls.Count > 0)
        {
            metadata["OutgoingCallCount"] = data.OutgoingCalls.Count;
            metadata["Callees"] = string.Join(", ",
                data.OutgoingCalls.Select(c => c.To.Name).Take(20));
        }

        if (data.Diagnostics.Count > 0)
        {
            var errors = data.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var warnings = data.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

            if (errors.Count > 0)
            {
                metadata["ErrorCount"] = errors.Count;
                metadata["Errors"] = string.Join("; ", errors.Select(e => e.Message).Take(5));
            }

            if (warnings.Count > 0)
            {
                metadata["WarningCount"] = warnings.Count;
                metadata["Warnings"] = string.Join("; ", warnings.Select(w => w.Message).Take(5));
            }
        }

        metadata["LspEnriched"] = true;
        metadata["LspEnrichedAt"] = DateTime.UtcNow;

        return new HypergraphNode
        {
            Id = node.Id,
            Name = node.Name,
            NodeType = node.NodeType,
            Language = node.Language,
            FilePath = node.FilePath,
            Scope = node.Scope,
            LineStart = node.LineStart,
            LineEnd = node.LineEnd,
            Modifiers = node.Modifiers,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Determines if a node represents a callable (method/function).
    /// </summary>
    private static bool IsCallableNode(HypergraphNode node)
    {
        return node.NodeType is "Method" or "Function" or "Constructor" or "Property";
    }

    /// <summary>
    /// Converts a file URI to a path.
    /// </summary>
    private static string UriToPath(string uri)
    {
        if (uri.StartsWith("file://"))
        {
            var path = uri["file://".Length..];
            // Handle Windows paths (file:///C:/...)
            if (path.Length > 2 && path[0] == '/' && path[2] == ':')
            {
                path = path[1..];
            }
            return path.Replace('/', Path.DirectorySeparatorChar);
        }
        return uri;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _serverManager.DisposeAsync();
    }
}
