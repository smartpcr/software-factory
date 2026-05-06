// -----------------------------------------------------------------------
// <copyright file="MemoryMcpToolRegistration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Mcp;

using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Registers the 5 MCP tools for the memory graph per architecture.md §3.8.
/// Tools are registered with <see cref="McpServer"/> for JSON-RPC transport
/// and can also be enumerated directly for programmatic access.
/// </summary>
public sealed class MemoryMcpToolRegistration
{
    private readonly List<McpToolDefinition> tools;

    public MemoryMcpToolRegistration()
    {
        this.tools = new List<McpToolDefinition>
        {
            new McpToolDefinition(
                name: "memory_query",
                description: "Hybrid context retrieval with configurable mode and budget",
                inputSchema: new McpInputSchema(
                    type: "object",
                    properties: new Dictionary<string, McpPropertySchema>
                    {
                        ["queryText"] = new("string", "Free-text query for context retrieval"),
                        ["seedNodeIds"] = new("array", "Seed node IDs for structural/call-chain retrieval")
                        {
                            Items = new McpItemSchema("string")
                        },
                        ["mode"] = new("string", "Retrieval mode: StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall, Hybrid")
                        {
                            Enum = new[] { "StructuralDrill", "CallTrace", "SemanticSearch", "ExpertiseRecall", "Hybrid" }
                        },
                        ["tokenBudget"] = new("integer", "Maximum token budget for context (default 8000, max 32000)"),
                        ["atCommitIndex"] = new("integer", "Temporal commit index for point-in-time queries")
                    },
                    required: new List<string>())),

            new McpToolDefinition(
                name: "memory_status",
                description: "Graph statistics (node/edge counts by layer)",
                inputSchema: new McpInputSchema(
                    type: "object",
                    properties: new Dictionary<string, McpPropertySchema>(),
                    required: new List<string>())),

            new McpToolDefinition(
                name: "memory_record_expertise",
                description: "Write an expertise entry (title, body, kind, target)",
                inputSchema: new McpInputSchema(
                    type: "object",
                    properties: new Dictionary<string, McpPropertySchema>
                    {
                        ["targetNodeId"] = new("string", "Code node ID this expertise applies to"),
                        ["kind"] = new("string", "Expertise kind: Pattern, Pitfall, Preference, Insight")
                        {
                            Enum = new[] { "Pattern", "Pitfall", "Preference", "Insight" }
                        },
                        ["title"] = new("string", "Headline for the expertise entry"),
                        ["body"] = new("string", "Detailed description of the expertise"),
                        ["sourceAgent"] = new("string", "Identifier of the agent authoring this entry"),
                        ["scope"] = new("string", "Scope: 'exact' (default) or 'subtree'")
                        {
                            Enum = new[] { "exact", "subtree" }
                        },
                        ["evidence"] = new("array", "Supporting citations (optional)")
                        {
                            Items = new McpItemSchema("object")
                            {
                                Properties = new Dictionary<string, McpPropertySchema>
                                {
                                    ["nodeId"] = new("string", "Referenced code node ID"),
                                    ["commitSha"] = new("string", "Commit SHA where the evidence was observed"),
                                    ["snippet"] = new("string", "Code snippet excerpt")
                                },
                                Required = new[] { "nodeId" }
                            }
                        }
                    },
                    required: new List<string> { "targetNodeId", "kind", "title", "body", "sourceAgent" })),

            new McpToolDefinition(
                name: "memory_sync",
                description: "Trigger incremental sync",
                inputSchema: new McpInputSchema(
                    type: "object",
                    properties: new Dictionary<string, McpPropertySchema>
                    {
                        ["repoRoot"] = new("string", "Repository root path"),
                        ["fromCommit"] = new("string", "Starting commit SHA"),
                        ["toCommit"] = new("string", "Ending commit SHA")
                    },
                    required: new List<string> { "repoRoot", "fromCommit", "toCommit" })),

            new McpToolDefinition(
                name: "memory_consolidate",
                description: "Run consolidation (admin)",
                inputSchema: new McpInputSchema(
                    type: "object",
                    properties: new Dictionary<string, McpPropertySchema>
                    {
                        ["similarityThreshold"] = new("number", "Cosine similarity threshold for merge candidates (default 0.85)"),
                        ["maxMergesPerRun"] = new("integer", "Maximum merge operations per run (default 50)")
                    },
                    required: new List<string>()))
        };
    }

    /// <summary>
    /// Gets the registered MCP tools.
    /// </summary>
    public IReadOnlyList<McpToolDefinition> Tools => this.tools;

    /// <summary>
    /// Gets a specific tool by name, or null if not found.
    /// </summary>
    public McpToolDefinition? GetTool(string name)
    {
        for (var i = 0; i < this.tools.Count; i++)
        {
            if (this.tools[i].Name == name)
            {
                return this.tools[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Returns all tool names.
    /// </summary>
    public IReadOnlyList<string> GetToolNames()
    {
        var names = new List<string>(this.tools.Count);
        for (var i = 0; i < this.tools.Count; i++)
        {
            names.Add(this.tools[i].Name);
        }

        return names;
    }
}

/// <summary>
/// Represents an MCP tool definition.
/// </summary>
public sealed class McpToolDefinition
{
    public McpToolDefinition(string name, string description, McpInputSchema inputSchema)
    {
        this.Name = name;
        this.Description = description;
        this.InputSchema = inputSchema;
    }

    public string Name { get; }
    public string Description { get; }
    public McpInputSchema InputSchema { get; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            name = this.Name,
            description = this.Description,
            inputSchema = this.InputSchema.ToSerializable()
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// MCP input schema following JSON Schema conventions.
/// </summary>
public sealed class McpInputSchema
{
    public McpInputSchema(
        string type,
        Dictionary<string, McpPropertySchema> properties,
        List<string> required)
    {
        this.Type = type;
        this.Properties = properties;
        this.Required = required;
    }

    public string Type { get; }
    public Dictionary<string, McpPropertySchema> Properties { get; }
    public List<string> Required { get; }

    public object ToSerializable()
    {
        var props = new Dictionary<string, object>();
        foreach (var kvp in this.Properties)
        {
            var propDict = new Dictionary<string, object>
            {
                ["type"] = kvp.Value.Type,
                ["description"] = kvp.Value.Description
            };

            if (kvp.Value.Enum is { Count: > 0 })
            {
                propDict["enum"] = kvp.Value.Enum;
            }

            if (kvp.Value.Items != null)
            {
                var itemsDict = new Dictionary<string, object> { ["type"] = kvp.Value.Items.Type };
                if (kvp.Value.Items.Properties is { Count: > 0 })
                {
                    var itemProps = new Dictionary<string, object>();
                    foreach (var ip in kvp.Value.Items.Properties)
                    {
                        itemProps[ip.Key] = new { type = ip.Value.Type, description = ip.Value.Description };
                    }

                    itemsDict["properties"] = itemProps;
                }

                if (kvp.Value.Items.Required is { Count: > 0 })
                {
                    itemsDict["required"] = kvp.Value.Items.Required;
                }

                propDict["items"] = itemsDict;
            }

            props[kvp.Key] = propDict;
        }

        return new
        {
            type = this.Type,
            properties = props,
            required = this.Required
        };
    }
}

/// <summary>
/// Schema for a single MCP tool property, supporting JSON Schema conventions
/// including array item schemas and enum restrictions.
/// </summary>
public sealed class McpPropertySchema
{
    public McpPropertySchema(string type, string description)
    {
        this.Type = type;
        this.Description = description;
    }

    public string Type { get; }
    public string Description { get; }

    /// <summary>
    /// For array types: the schema of each item in the array.
    /// </summary>
    public McpItemSchema? Items { get; init; }

    /// <summary>
    /// Allowed enum values, if any.
    /// </summary>
    public IReadOnlyList<string>? Enum { get; init; }
}

/// <summary>
/// Schema for items within an array property.
/// </summary>
public sealed class McpItemSchema
{
    public McpItemSchema(string type)
    {
        this.Type = type;
    }

    public string Type { get; }

    /// <summary>
    /// For object-type items: property schemas.
    /// </summary>
    public Dictionary<string, McpPropertySchema>? Properties { get; init; }

    /// <summary>
    /// Required properties within the item object.
    /// </summary>
    public IReadOnlyList<string>? Required { get; init; }
}