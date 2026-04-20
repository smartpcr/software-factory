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

/// <summary>
/// Validates node definitions using the engine's built-in validation logic.
/// </summary>
public static class NodeValidationEndpoints
{
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
                    var runtimeTypeStr = nodeEl.TryGetProperty("runtimeType", out var rt) ? rt.GetString() : null;

                    if (string.IsNullOrEmpty(runtimeTypeStr))
                    {
                        allEntries.Add(new { nodeId, field = "runtimeType", message = "RuntimeType is required", severity = "error" });
                        continue;
                    }

                    if (!Enum.TryParse<RuntimeType>(runtimeTypeStr, true, out var runtimeType))
                    {
                        allEntries.Add(new { nodeId, field = "runtimeType", message = $"Unknown RuntimeType: {runtimeTypeStr}", severity = "error" });
                        continue;
                    }

                    var nodeDef = CreateNodeDefinition(runtimeType, nodeEl);
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

    private static NodeDefinition? CreateNodeDefinition(RuntimeType runtimeType, JsonElement el)
    {
        // Extract config/configuration properties
        JsonElement config = default;
        var hasConfig = el.TryGetProperty("configuration", out config) || el.TryGetProperty("config", out config);

        NodeDefinition def = runtimeType switch
        {
            RuntimeType.Start => new StartNodeDefinition
            {
                TriggerType = GetEnum<TriggerType>(el, config, "triggerType"),
                Schedule = GetString(el, config, "schedule"),
                EventName = GetString(el, config, "eventName"),
            },
            RuntimeType.Noop => new NoopNodeDefinition(),
            RuntimeType.IfElse => new IfElseNodeDefinition { Condition = GetString(el, config, "condition") ?? "" },
            RuntimeType.ForEach => new ForEachNodeDefinition
            {
                CollectionExpression = GetString(el, config, "collectionExpression") ?? "",
                ItemVariableName = GetString(el, config, "itemVariableName") ?? "item",
            },
            RuntimeType.While => new WhileNodeDefinition
            {
                ConditionExpression = GetString(el, config, "conditionExpression") ?? "",
                MaxIterations = GetInt(el, config, "maxIterations", 1000),
            },
            RuntimeType.Switch => new SwitchNodeDefinition
            {
                Expression = GetString(el, config, "expression") ?? "",
            },
            RuntimeType.Timer => new TimerNodeDefinition { Schedule = GetString(el, config, "schedule") ?? "" },
            RuntimeType.Subflow => new SubflowNodeDefinition
            {
                WorkflowFilePath = GetString(el, config, "workflowFilePath") ?? "",
                SkipValidation = true, // Don't validate subflow file existence from UI
            },
            RuntimeType.CSharpScript => new CSharpScriptNodeDefinition
            {
                ScriptPath = GetString(el, config, "scriptPath"),
                ScriptContent = GetString(el, config, "scriptContent"),
            },
            RuntimeType.CSharpTask => new CSharpTaskNodeDefinition
            {
                ScriptContent = GetString(el, config, "scriptContent"),
                ExecutorTypeName = GetString(el, config, "executorTypeName"),
                ExecutorAssemblyPath = GetString(el, config, "executorAssemblyPath"),
            },
            RuntimeType.PowerShell => new PowerShellScriptNodeDefinition
            {
                ScriptPath = GetString(el, config, "scriptPath"),
            },
            RuntimeType.PowerShellTask => new PowerShellTaskNodeDefinition
            {
                ScriptPath = GetString(el, config, "scriptPath"),
                ScriptContent = GetString(el, config, "scriptContent"),
            },
            RuntimeType.HttpRequest => new HttpRequestNodeDefinition
            {
                Url = GetString(el, config, "url") ?? "",
                Method = GetString(el, config, "method") ?? "GET",
            },
            RuntimeType.Log => new LogNodeDefinition
            {
                Level = GetEnum<LogLevel>(el, config, "level"),
                MessageTemplate = GetString(el, config, "messageTemplate") ?? GetString(el, config, "message") ?? "",
            },
            RuntimeType.Delay => new DelayNodeDefinition
            {
                Duration = GetInt(el, config, "duration", 1000),
                Unit = GetString(el, config, "unit") ?? "ms",
            },
            RuntimeType.AI => new AINodeDefinition
            {
                Mode = GetString(el, config, "mode") ?? "skill",
                SkillId = GetString(el, config, "skillId"),
                SubagentId = GetString(el, config, "subagentId"),
                Model = GetString(el, config, "model") ?? "claude-opus-4-7",
            },
            RuntimeType.Action => new ActionNodeDefinition
            {
                ActionType = GetString(el, config, "actionType") ?? "function",
                Handler = GetString(el, config, "handler") ?? "",
            },
            RuntimeType.Trigger => new TriggerNodeDefinition
            {
                Route = GetString(el, config, "route") ?? "/webhook",
                Method = GetString(el, config, "method") ?? "POST",
            },
            RuntimeType.End => new EndNodeDefinition
            {
                Status = GetString(el, config, "status") ?? "success",
            },
            RuntimeType.Parallel => new ParallelNodeDefinition(),
            RuntimeType.Container => new ContainerNodeDefinition(),
            _ => null,
        };

        return def;
    }

    private static string? GetString(JsonElement el, JsonElement config, string name)
    {
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        if (el.TryGetProperty(name, out var v2) && v2.ValueKind == JsonValueKind.String)
        {
            return v2.GetString();
        }

        return null;
    }

    private static int GetInt(JsonElement el, JsonElement config, string name, int defaultValue)
    {
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            return v.GetInt32();
        }

        if (el.TryGetProperty(name, out var v2) && v2.ValueKind == JsonValueKind.Number)
        {
            return v2.GetInt32();
        }

        return defaultValue;
    }

    private static T GetEnum<T>(JsonElement el, JsonElement config, string name) where T : struct, Enum
    {
        var str = GetString(el, config, name);
        return Enum.TryParse<T>(str, true, out var result) ? result : default;
    }
}
