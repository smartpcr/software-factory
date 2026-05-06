// -----------------------------------------------------------------------
// <copyright file="McpServer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Mcp;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// MCP (Model Context Protocol) server that handles JSON-RPC 2.0 requests
/// for memory tools. Supports <c>initialize</c>, <c>tools/list</c>, and <c>tools/call</c>.
/// External agents connect via <see cref="IMcpTransport"/> to invoke memory tools.
/// </summary>
public sealed class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly MemoryMcpToolRegistration toolRegistration;
    private readonly MemoryMcpHandler handler;
    private readonly ILogger<McpServer> logger;

    public McpServer(
        MemoryMcpToolRegistration toolRegistration,
        MemoryMcpHandler handler,
        ILogger<McpServer> logger)
    {
        this.toolRegistration = toolRegistration;
        this.handler = handler;
        this.logger = logger;
    }

    /// <summary>
    /// Runs the MCP server using the given transport until cancellation.
    /// Reads JSON-RPC requests, dispatches them, and writes responses.
    /// </summary>
    public async Task RunAsync(IMcpTransport transport, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var request = await transport.ReadRequestAsync(ct).ConfigureAwait(false);
            if (request == null)
            {
                break;
            }

            var response = await this.ProcessRequestAsync(request, ct).ConfigureAwait(false);
            await transport.WriteResponseAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the MCP server over standard I/O streams (for external agent connections).
    /// </summary>
    public async Task RunStdioAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        var transport = new StdioMcpTransport(input, output);
        await this.RunAsync(transport, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes a single JSON-RPC 2.0 request and returns the response.
    /// This is the core dispatch method used for both transport and in-process testing.
    /// </summary>
    public async Task<string> ProcessRequestAsync(string jsonRequest, CancellationToken ct = default)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonRequest);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var idProp) ? idProp : (JsonElement?)null;
            var method = root.TryGetProperty("method", out var methodProp)
                ? methodProp.GetString() ?? string.Empty
                : string.Empty;

            return method switch
            {
                "initialize" => BuildResponse(id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "ast-memory-mcp", version = "1.0.0" }
                }),
                "tools/list" => this.HandleToolsList(id),
                "tools/call" => await this.HandleToolsCall(root, id, ct).ConfigureAwait(false),
                _ => BuildError(id, -32601, $"Method not found: {method}")
            };
        }
        catch (JsonException ex)
        {
            this.logger.LogError(ex, "Invalid JSON-RPC request");
            return BuildError(null, -32700, "Parse error: " + ex.Message);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Internal error processing MCP request");
            return BuildError(null, -32603, "Internal error: " + ex.Message);
        }
    }

    private string HandleToolsList(JsonElement? id)
    {
        var toolDefs = new List<object>();
        foreach (var tool in this.toolRegistration.Tools)
        {
            toolDefs.Add(new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.InputSchema.ToSerializable()
            });
        }

        return BuildResponse(id, new { tools = toolDefs });
    }

    private async Task<string> HandleToolsCall(JsonElement root, JsonElement? id, CancellationToken ct)
    {
        if (!root.TryGetProperty("params", out var paramsProp))
        {
            return BuildError(id, -32602, "Missing 'params' in tools/call request");
        }

        if (!paramsProp.TryGetProperty("name", out var nameProp))
        {
            return BuildError(id, -32602, "Missing 'name' in tools/call params");
        }

        var toolName = nameProp.GetString() ?? string.Empty;

        // Validate tool exists
        var toolDef = this.toolRegistration.GetTool(toolName);
        if (toolDef == null)
        {
            return BuildError(id, -32602, $"Unknown tool: {toolName}");
        }

        var arguments = paramsProp.TryGetProperty("arguments", out var argsProp)
            ? argsProp
            : JsonSerializer.Deserialize<JsonElement>("{}");

        var result = await this.handler.HandleAsync(toolName, arguments, ct).ConfigureAwait(false);

        // Parse to see if it's an error
        var resultDoc = JsonDocument.Parse(result);
        if (resultDoc.RootElement.TryGetProperty("error", out _))
        {
            return BuildResponse(id, new
            {
                content = new[]
                {
                    new { type = "text", text = result }
                },
                isError = true
            });
        }

        return BuildResponse(id, new
        {
            content = new[]
            {
                new { type = "text", text = result }
            }
        });
    }

    private static string BuildResponse(JsonElement? id, object result)
    {
        var responseObj = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };

        if (id.HasValue)
        {
            responseObj["id"] = id.Value.ValueKind == JsonValueKind.Number
                ? id.Value.GetInt64()
                : (object)(id.Value.GetString() ?? "null");
        }

        return JsonSerializer.Serialize(responseObj, JsonOptions);
    }

    private static string BuildError(JsonElement? id, int code, string message)
    {
        var responseObj = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new { code, message }
        };

        if (id.HasValue)
        {
            responseObj["id"] = id.Value.ValueKind == JsonValueKind.Number
                ? id.Value.GetInt64()
                : (object)(id.Value.GetString() ?? "null");
        }

        return JsonSerializer.Serialize(responseObj, JsonOptions);
    }

    /// <summary>
    /// Simple stdio transport for MCP over standard I/O streams.
    /// Uses newline-delimited JSON-RPC messages.
    /// </summary>
    private sealed class StdioMcpTransport : IMcpTransport
    {
        private readonly StreamReader reader;
        private readonly StreamWriter writer;

        public StdioMcpTransport(Stream input, Stream output)
        {
            this.reader = new StreamReader(input);
            this.writer = new StreamWriter(output) { AutoFlush = true };
        }

        public async Task<string?> ReadRequestAsync(CancellationToken ct = default)
        {
            return await this.reader.ReadLineAsync(ct).ConfigureAwait(false);
        }

        public async Task WriteResponseAsync(string response, CancellationToken ct = default)
        {
            await this.writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
        }
    }
}
