// -----------------------------------------------------------------------
// <copyright file="LspDetectionStrategy.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Strategies;

using System.Diagnostics;
using System.Text.Json;
using CodeIntelligence.Models;

/// <summary>
/// Detection strategy using Language Server Protocol for cross-reference analysis.
/// </summary>
public sealed class LspDetectionStrategy : IDetectionStrategy
{
    private readonly LspClientOptions options;
    private Process? lspProcess;
    private StreamWriter? writer;
    private StreamReader? reader;
    private int requestId;
    private bool initialized;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LspDetectionStrategy"/> class.
    /// </summary>
    public LspDetectionStrategy()
        : this(new LspClientOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LspDetectionStrategy"/> class.
    /// </summary>
    /// <param name="options">The LSP client options.</param>
    public LspDetectionStrategy(LspClientOptions options)
    {
        this.options = options;
    }

    /// <inheritdoc/>
    public string Name => "lsp";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedLanguages => ["csharp", "cs", "typescript", "ts", "javascript", "js"];

    /// <inheritdoc/>
    public bool CanHandle(DetectionPattern pattern)
    {
        return pattern.Strategy.Equals("lsp", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DetectedRelationship>> DetectAsync(
        DetectionRule rule,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var relationships = new List<DetectedRelationship>();

        if (!this.initialized && !await this.InitializeAsync(cancellationToken))
        {
            return relationships;
        }

        foreach (var filePath in filePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var pattern in rule.Detection.Where(p => this.CanHandle(p)))
            {
                var detected = await this.ApplyPatternAsync(rule, pattern, filePath, cancellationToken);
                relationships.AddRange(detected);
            }
        }

        return relationships;
    }

    /// <summary>
    /// Initializes the LSP server connection.
    /// </summary>
    private async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(this.options.ServerCommand))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = this.options.ServerCommand,
                Arguments = this.options.ServerArguments ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            this.lspProcess = Process.Start(startInfo);
            if (this.lspProcess == null)
            {
                return false;
            }

            this.writer = this.lspProcess.StandardInput;
            this.reader = this.lspProcess.StandardOutput;

            // Send initialize request
            var initParams = new
            {
                processId = Environment.ProcessId,
                rootUri = this.options.RootUri,
                capabilities = new
                {
                    textDocument = new
                    {
                        definition = new { dynamicRegistration = false },
                        references = new { dynamicRegistration = false },
                        hover = new { dynamicRegistration = false }
                    }
                }
            };

            var response = await this.SendRequestAsync("initialize", initParams, cancellationToken);
            if (response != null)
            {
                await this.SendNotificationAsync("initialized", new { }, cancellationToken);
                this.initialized = true;
                return true;
            }
        }
        catch (Exception)
        {
            // LSP server failed to start
        }

        return false;
    }

    /// <summary>
    /// Applies a detection pattern using LSP queries.
    /// </summary>
    private async Task<IEnumerable<DetectedRelationship>> ApplyPatternAsync(
        DetectionRule rule,
        DetectionPattern detectionPattern,
        string filePath,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DetectedRelationship>();

        foreach (var pattern in detectionPattern.Patterns)
        {
            var lspMethod = this.GetLspMethod(pattern);
            if (string.IsNullOrEmpty(lspMethod))
            {
                continue;
            }

            // Open the document
            await this.OpenDocumentAsync(filePath, cancellationToken);

            try
            {
                var results = await this.ExecuteLspQueryAsync(lspMethod, filePath, pattern, cancellationToken);

                foreach (var result in results)
                {
                    var relationship = this.CreateRelationship(rule, result, filePath);
                    if (relationship != null)
                    {
                        relationships.Add(relationship);
                    }
                }
            }
            finally
            {
                // Close the document
                await this.CloseDocumentAsync(filePath, cancellationToken);
            }
        }

        return relationships;
    }

    private string? GetLspMethod(PatternDefinition pattern)
    {
        // Map pattern types to LSP methods
        return pattern.NodeType?.ToLowerInvariant() switch
        {
            "definition" or "gotodefinition" => "textDocument/definition",
            "references" or "findreferences" => "textDocument/references",
            "implementation" or "gotoimplement" => "textDocument/implementation",
            "typedefinition" => "textDocument/typeDefinition",
            "hover" => "textDocument/hover",
            "documentSymbol" or "symbols" => "textDocument/documentSymbol",
            "callhierarchy" => "textDocument/prepareCallHierarchy",
            _ => null
        };
    }

    private async Task OpenDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var uri = new Uri(filePath).ToString();

        var @params = new
        {
            textDocument = new
            {
                uri,
                languageId = this.GetLanguageId(filePath),
                version = 1,
                text = content
            }
        };

        await this.SendNotificationAsync("textDocument/didOpen", @params, cancellationToken);
    }

    private async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        var uri = new Uri(filePath).ToString();

        var @params = new
        {
            textDocument = new { uri }
        };

        await this.SendNotificationAsync("textDocument/didClose", @params, cancellationToken);
    }

    private string GetLanguageId(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" => "typescript",
            ".tsx" => "typescriptreact",
            ".js" => "javascript",
            ".jsx" => "javascriptreact",
            _ => "plaintext"
        };
    }

    private async Task<IEnumerable<LspResult>> ExecuteLspQueryAsync(
        string method,
        string filePath,
        PatternDefinition pattern,
        CancellationToken cancellationToken)
    {
        var results = new List<LspResult>();

        if (method == "textDocument/documentSymbol")
        {
            // Get all symbols in the document
            var uri = new Uri(filePath).ToString();
            var @params = new
            {
                textDocument = new { uri }
            };

            var response = await this.SendRequestAsync(method, @params, cancellationToken);
            if (response.HasValue)
            {
                results.AddRange(this.ParseSymbolResponse(response.Value, filePath));
            }
        }
        else
        {
            // For position-based queries, we need to scan the document
            // This is a simplified implementation - in practice, you'd parse
            // the AST first to find positions of interest
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var lines = content.Split('\n');

            // Find positions matching the pattern condition
            for (var line = 0; line < lines.Length; line++)
            {
                if (pattern.Condition != null &&
                    lines[line].Contains(pattern.Condition, StringComparison.OrdinalIgnoreCase))
                {
                    var column = lines[line].IndexOf(pattern.Condition, StringComparison.OrdinalIgnoreCase);

                    var uri = new Uri(filePath).ToString();
                    var @params = new
                    {
                        textDocument = new { uri },
                        position = new { line, character = column }
                    };

                    var response = await this.SendRequestAsync(method, @params, cancellationToken);
                    if (response.HasValue)
                    {
                        results.AddRange(this.ParseLocationResponse(response.Value, filePath, line, column));
                    }
                }
            }
        }

        return results;
    }

    private IEnumerable<LspResult> ParseSymbolResponse(JsonElement response, string filePath)
    {
        if (response.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var symbol in response.EnumerateArray())
        {
            if (symbol.TryGetProperty("name", out var nameProp) &&
                symbol.TryGetProperty("kind", out var kindProp))
            {
                var range = symbol.TryGetProperty("range", out var rangeProp)
                    ? rangeProp
                    : symbol.TryGetProperty("location", out var locProp) &&
                      locProp.TryGetProperty("range", out var locRange)
                        ? locRange
                        : default;

                var startLine = 0;
                var startChar = 0;
                if (range.ValueKind != JsonValueKind.Undefined &&
                    range.TryGetProperty("start", out var start))
                {
                    startLine = start.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    startChar = start.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
                }

                yield return new LspResult
                {
                    Name = nameProp.GetString() ?? string.Empty,
                    Kind = this.GetSymbolKindName(kindProp.GetInt32()),
                    FilePath = filePath,
                    Line = startLine,
                    Column = startChar
                };
            }
        }
    }

    private IEnumerable<LspResult> ParseLocationResponse(
        JsonElement response,
        string sourceFile,
        int sourceLine,
        int sourceColumn)
    {
        if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in response.EnumerateArray())
            {
                var result = this.ParseSingleLocation(location);
                if (result != null)
                {
                    result.SourceFile = sourceFile;
                    result.SourceLine = sourceLine;
                    result.SourceColumn = sourceColumn;
                    yield return result;
                }
            }
        }
        else if (response.ValueKind == JsonValueKind.Object)
        {
            var result = this.ParseSingleLocation(response);
            if (result != null)
            {
                result.SourceFile = sourceFile;
                result.SourceLine = sourceLine;
                result.SourceColumn = sourceColumn;
                yield return result;
            }
        }
    }

    private LspResult? ParseSingleLocation(JsonElement location)
    {
        if (!location.TryGetProperty("uri", out var uriProp))
        {
            return null;
        }

        var uri = uriProp.GetString();
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        var line = 0;
        var column = 0;

        if (location.TryGetProperty("range", out var range) &&
            range.TryGetProperty("start", out var start))
        {
            line = start.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
            column = start.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
        }

        return new LspResult
        {
            FilePath = Uri.UnescapeDataString(new Uri(uri).LocalPath),
            Line = line,
            Column = column,
            Kind = "Location"
        };
    }

    private string GetSymbolKindName(int kind)
    {
        return kind switch
        {
            1 => "File",
            2 => "Module",
            3 => "Namespace",
            4 => "Package",
            5 => "Class",
            6 => "Method",
            7 => "Property",
            8 => "Field",
            9 => "Constructor",
            10 => "Enum",
            11 => "Interface",
            12 => "Function",
            13 => "Variable",
            14 => "Constant",
            15 => "String",
            16 => "Number",
            17 => "Boolean",
            18 => "Array",
            19 => "Object",
            20 => "Key",
            21 => "Null",
            22 => "EnumMember",
            23 => "Struct",
            24 => "Event",
            25 => "Operator",
            26 => "TypeParameter",
            _ => "Unknown"
        };
    }

    private DetectedRelationship? CreateRelationship(
        DetectionRule rule,
        LspResult result,
        string sourceFilePath)
    {
        var sourceNode = new RelationshipNode
        {
            NodeType = rule.Output.SourceNodeType ?? rule.Output.NodeType ?? result.Kind,
            Name = result.Name ?? Path.GetFileName(result.FilePath),
            FullyQualifiedName = $"{result.FilePath}:{result.Line}:{result.Column}",
            FilePath = result.SourceFile ?? sourceFilePath,
            LineNumber = result.SourceLine ?? result.Line + 1,
            ColumnNumber = result.SourceColumn ?? result.Column + 1
        };

        RelationshipNode? targetNode = null;
        if (result.SourceFile != null)
        {
            targetNode = new RelationshipNode
            {
                NodeType = rule.Output.TargetNodeType ?? result.Kind,
                Name = result.Name ?? Path.GetFileName(result.FilePath),
                FullyQualifiedName = $"{result.FilePath}:{result.Line}:{result.Column}",
                FilePath = result.FilePath,
                LineNumber = result.Line + 1,
                ColumnNumber = result.Column + 1
            };
        }

        var evidence = new Evidence
        {
            Source = "lsp",
            Description = $"LSP {result.Kind} reference",
            Weight = rule.Confidence.BaseScore,
            FilePath = result.FilePath,
            LineNumber = result.Line + 1
        };

        var properties = new Dictionary<string, object>
        {
            ["kind"] = result.Kind,
            ["targetFile"] = result.FilePath
        };

        foreach (var prop in rule.Output.Properties)
        {
            if (prop.Value != null)
            {
                properties[prop.Name] = prop.Value;
            }
        }

        return new DetectedRelationship
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = rule.Category,
            RelationshipType = rule.RelationshipType,
            Source = sourceNode,
            Target = targetNode,
            Confidence = rule.Confidence.BaseScore,
            EvidenceChain = [evidence],
            DetectionStrategy = this.Name,
            Properties = properties
        };
    }

    private async Task<JsonElement?> SendRequestAsync(
        string method,
        object @params,
        CancellationToken cancellationToken)
    {
        if (this.writer == null || this.reader == null)
        {
            return null;
        }

        var id = Interlocked.Increment(ref this.requestId);
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        };

        var json = JsonSerializer.Serialize(request);
        var message = $"Content-Length: {json.Length}\r\n\r\n{json}";

        await this.writer.WriteAsync(message);
        await this.writer.FlushAsync();

        // Read response
        var response = await this.ReadResponseAsync(id, cancellationToken);
        return response;
    }

    private async Task SendNotificationAsync(
        string method,
        object @params,
        CancellationToken cancellationToken)
    {
        if (this.writer == null)
        {
            return;
        }

        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params
        };

        var json = JsonSerializer.Serialize(notification);
        var message = $"Content-Length: {json.Length}\r\n\r\n{json}";

        await this.writer.WriteAsync(message);
        await this.writer.FlushAsync();
    }

    private async Task<JsonElement?> ReadResponseAsync(int expectedId, CancellationToken cancellationToken)
    {
        if (this.reader == null)
        {
            return null;
        }

        try
        {
            // Read Content-Length header
            var headerLine = await this.reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(headerLine) || !headerLine.StartsWith("Content-Length:"))
            {
                return null;
            }

            var contentLength = int.Parse(headerLine.Split(':')[1].Trim());

            // Read empty line
            await this.reader.ReadLineAsync(cancellationToken);

            // Read content
            var buffer = new char[contentLength];
            await this.reader.ReadAsync(buffer, 0, contentLength);
            var content = new string(buffer);

            var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp) &&
                idProp.GetInt32() == expectedId &&
                root.TryGetProperty("result", out var result))
            {
                return result;
            }
        }
        catch (Exception)
        {
            // Failed to read response
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            if (this.initialized)
            {
                try
                {
                    this.SendNotificationAsync("exit", new { }, CancellationToken.None).Wait(1000);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            this.writer?.Dispose();
            this.reader?.Dispose();
            this.lspProcess?.Kill();
            this.lspProcess?.Dispose();

            this.disposed = true;
        }
    }

    private sealed class LspResult
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public int Column { get; set; }
        public string? SourceFile { get; set; }
        public int? SourceLine { get; set; }
        public int? SourceColumn { get; set; }
    }
}

/// <summary>
/// Options for configuring the LSP client.
/// </summary>
public record LspClientOptions
{
    /// <summary>
    /// Gets or sets the command to start the LSP server.
    /// </summary>
    public string? ServerCommand { get; init; }

    /// <summary>
    /// Gets or sets the arguments for the LSP server command.
    /// </summary>
    public string? ServerArguments { get; init; }

    /// <summary>
    /// Gets or sets the root URI for the workspace.
    /// </summary>
    public string? RootUri { get; init; }

    /// <summary>
    /// Gets or sets the timeout for LSP requests in milliseconds.
    /// </summary>
    public int RequestTimeoutMs { get; init; } = 30000;
}
