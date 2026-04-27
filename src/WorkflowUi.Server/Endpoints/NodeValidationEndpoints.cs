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

                // Pre-scan: build parentId→children map so Group nodes know their children
                var childrenByParent = new Dictionary<string, List<JsonElement>>();
                foreach (var nodeEl in doc.RootElement.EnumerateArray())
                {
                    if (nodeEl.TryGetProperty("parentId", out var pid) && pid.ValueKind == JsonValueKind.String)
                    {
                        var parentId = pid.GetString();
                        if (!string.IsNullOrEmpty(parentId))
                        {
                            if (!childrenByParent.ContainsKey(parentId))
                                childrenByParent[parentId] = [];
                            childrenByParent[parentId].Add(nodeEl);
                        }
                    }
                }

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

                    // For Group nodes, also resolve children from parentId relationships in the batch
                    if (runtimeType == RuntimeType.Group && nodeDef is GroupNodeDefinition gnd)
                    {
                        // If childNodes wasn't populated from the payload, resolve from parentId
                        if ((gnd.ChildNodes == null || gnd.ChildNodes.Count == 0) && childrenByParent.TryGetValue(nodeId ?? "", out var childEls))
                        {
                            gnd.ChildNodes = [];
                            foreach (var childEl in childEls)
                            {
                                var childRtStr = childEl.TryGetProperty("runtimeType", out var crt) ? crt.GetString() : null;
                                if (string.IsNullOrEmpty(childRtStr)) continue;
                                if (!Enum.TryParse<RuntimeType>(childRtStr, true, out var childRt)) continue;
                                var childDef = WorkflowDefinitionMapper.CreateNodeDefinition(childRt, childEl);
                                if (childDef != null)
                                {
                                    childDef.NodeId = childEl.TryGetProperty("nodeId", out var cid) ? cid.GetString() ?? "" : "";
                                    gnd.ChildNodes.Add(childDef);
                                }
                            }
                        }
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
