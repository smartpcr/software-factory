// -----------------------------------------------------------------------
// <copyright file="NodeValidationEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using WorkflowUi.Server.Mapping;

/// <summary>
/// Validates node definitions using the engine's built-in validation logic.
/// </summary>
public static class NodeValidationEndpoints
{
    /// <summary>
    /// Legacy UI type names that map to canonical RuntimeType values.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["http"] = "HttpRequest",
        ["if"] = "IfElse",
        ["transform"] = "Transform",
        ["foreach"] = "ForEach",
    };

    public static WebApplication MapNodeValidationEndpoints(this WebApplication app)
    {
        // Batch validate all nodes in a workflow
        app.MapPost("/api/nodes/validate-batch", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();

            try
            {
                var doc = JsonDocument.Parse(json);
                var allEntries = new List<object>();

                foreach (var nodeEl in doc.RootElement.EnumerateArray())
                {
                    var nodeId = nodeEl.TryGetProperty("nodeId", out var nid) ? nid.GetString() : "";
                    var nodeName = nodeEl.TryGetProperty("nodeName", out var nm) ? nm.GetString() : "";
                    var runtimeTypeStr = nodeEl.TryGetProperty("runtimeType", out var rt) ? rt.GetString() : null;

                    if (string.IsNullOrEmpty(runtimeTypeStr))
                    {
                        allEntries.Add(new { nodeId, nodeName, field = "runtimeType", message = "RuntimeType is required", severity = "error" });
                        continue;
                    }

                    // Resolve legacy aliases before parsing
                    if (LegacyAliases.TryGetValue(runtimeTypeStr, out var canonical))
                    {
                        runtimeTypeStr = canonical;
                    }

                    if (!Enum.TryParse<RuntimeType>(runtimeTypeStr, true, out var runtimeType))
                    {
                        allEntries.Add(new { nodeId, nodeName, field = "runtimeType", message = $"Unknown RuntimeType: {runtimeTypeStr}", severity = "error" });
                        continue;
                    }

                    var nodeDef = WorkflowDefinitionMapper.CreateNodeDefinition(runtimeType, nodeEl);
                    if (nodeDef == null)
                    {
                        continue;
                    }

                    nodeDef.NodeId = nodeId ?? "";
                    if (nodeEl.TryGetProperty("nodeName", out var nn))
                    {
                        nodeDef.NodeName = nn.GetString() ?? "";
                    }

                    var validationContext = new ValidationContext(nodeDef);
                    var results = nodeDef.Validate(validationContext)?.ToList() ?? [];
                    allEntries.AddRange(results.Select(r => new
                    {
                        nodeId,
                        nodeName,
                        field = r.MemberNames.FirstOrDefault() ?? "",
                        message = r.ErrorMessage ?? "Validation error",
                        severity = "error",
                    }));
                }

                return Results.Ok(allEntries);
            }
            catch (JsonException ex)
            {
                return Results.Ok(new[] { new { nodeId = "", field = "", message = $"Invalid JSON: {ex.Message}", severity = "error" } });
            }
        })
        .WithName("ValidateNodeBatch")
        .WithTags("Validation")
        .AllowAnonymous();

        return app;
    }


}
