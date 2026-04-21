// -----------------------------------------------------------------------
// <copyright file="WorkflowDefinitionMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System.Text.Json;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;

/// <summary>
/// Maps the UI's workflow JSON format to the engine's typed WorkflowDefinition.
/// Handles the configuration → typed property mapping.
/// </summary>
public static class WorkflowDefinitionMapper
{
    /// <summary>
    /// Parses a workflow JSON string into a typed <see cref="WorkflowDefinition"/>.
    /// </summary>
    public static WorkflowDefinition MapFromJson(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var wf = new WorkflowDefinition
        {
            WorkflowId = GetStringDirect(root, "workflowId") ?? "",
            WorkflowName = GetStringDirect(root, "workflowName") ?? "",
            Description = GetStringDirect(root, "description"),
            Version = GetStringDirect(root, "version") ?? "1.0",
            EntryPointNodeId = GetStringDirect(root, "entryPointNodeId"),
            MaxConcurrency = GetIntDirect(root, "maxConcurrency", 0),
            AllowPause = GetBoolDirect(root, "allowPause", true),
            TimeoutSeconds = GetIntDirect(root, "timeoutSeconds", 0),
        };

        if (root.TryGetProperty("nodes", out var nodesEl))
        {
            foreach (var nodeEl in nodesEl.EnumerateArray())
            {
                var nodeDef = MapNodeDefinition(nodeEl);
                if (nodeDef != null)
                {
                    wf.Nodes.Add(nodeDef);
                }
            }
        }

        if (root.TryGetProperty("connections", out var connsEl))
        {
            foreach (var connEl in connsEl.EnumerateArray())
            {
                wf.Connections.Add(MapConnection(connEl));
            }
        }

        return wf;
    }

    /// <summary>
    /// Creates a typed <see cref="NodeDefinition"/> from a JSON element using the RuntimeType discriminator.
    /// Shared by both batch validation and workflow mapping.
    /// </summary>
    public static NodeDefinition? CreateNodeDefinition(RuntimeType runtimeType, JsonElement el)
    {
        JsonElement config = default;
        _ = el.TryGetProperty("configuration", out config) || el.TryGetProperty("config", out config);

        NodeDefinition? def = runtimeType switch
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
                SkipValidation = true,
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
            RuntimeType.Parallel => new ParallelNodeDefinition
            {
                JoinMode = GetString(el, config, "joinMode") ?? "all",
                Timeout = GetInt(el, config, "timeout", 30000),
                Branches = GetStringList(el, config, "branches") ?? ["branch_1", "branch_2"],
            },
            RuntimeType.Container => new ContainerNodeDefinition(),
            _ => null,
        };

        return def;
    }

    /// <summary>
    /// Extracts a string from a config object first, then falls back to the element.
    /// </summary>
    internal static string? GetString(JsonElement el, JsonElement config, string name)
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

    /// <summary>
    /// Extracts an integer from a config object first, then falls back to the element.
    /// </summary>
    internal static int GetInt(JsonElement el, JsonElement config, string name, int defaultValue)
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

    /// <summary>
    /// Extracts an enum from a config object first, then falls back to the element.
    /// </summary>
    internal static T GetEnum<T>(JsonElement el, JsonElement config, string name) where T : struct, Enum
    {
        var str = GetString(el, config, name);
        return Enum.TryParse<T>(str, true, out var result) ? result : default;
    }

    /// <summary>
    /// Extracts a string list from a config object first, then falls back to the element.
    /// Handles JSON arrays. Ignores .ToString() serialized values like "System.Collections...".
    /// </summary>
    internal static List<string>? GetStringList(JsonElement el, JsonElement config, string name)
    {
        JsonElement arr = default;
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return arr.EnumerateArray().Select(v => v.GetString() ?? "").ToList();
        }

        if (el.TryGetProperty(name, out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return arr.EnumerateArray().Select(v => v.GetString() ?? "").ToList();
        }

        return null;
    }

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

    private static NodeDefinition? MapNodeDefinition(JsonElement nodeEl)
    {
        var nodeId = nodeEl.TryGetProperty("nodeId", out var nid) ? nid.GetString() : null;
        var runtimeTypeStr = nodeEl.TryGetProperty("runtimeType", out var rt) ? rt.GetString() : null;

        // Resolve legacy aliases
        if (!string.IsNullOrEmpty(runtimeTypeStr) && LegacyAliases.TryGetValue(runtimeTypeStr, out var canonical))
        {
            runtimeTypeStr = canonical;
        }

        if (string.IsNullOrEmpty(runtimeTypeStr) || !Enum.TryParse<RuntimeType>(runtimeTypeStr, true, out var runtimeType))
        {
            return null;
        }

        var nodeDef = CreateNodeDefinition(runtimeType, nodeEl);
        if (nodeDef == null)
        {
            return null;
        }

        nodeDef.NodeId = nodeId ?? "";

        if (nodeEl.TryGetProperty("nodeName", out var nn))
        {
            nodeDef.NodeName = nn.GetString() ?? "";
        }

        return nodeDef;
    }

    private static NodeConnection MapConnection(JsonElement connEl)
    {
        var conn = new NodeConnection
        {
            SourceNodeId = GetStringDirect(connEl, "sourceNodeId") ?? "",
            TargetNodeId = GetStringDirect(connEl, "targetNodeId") ?? "",
            Condition = GetStringDirect(connEl, "condition"),
            IsEnabled = GetBoolDirect(connEl, "isEnabled", true),
            Priority = GetIntDirect(connEl, "priority", 0),
            SourcePort = GetStringDirect(connEl, "sourcePort"),
            TargetPort = GetStringDirect(connEl, "targetPort"),
        };

        var triggerStr = GetStringDirect(connEl, "triggerMessageType");
        if (!string.IsNullOrEmpty(triggerStr) && Enum.TryParse<MessageType>(triggerStr, true, out var msgType))
        {
            conn.TriggerMessageType = msgType;
        }

        return conn;
    }

    private static string? GetStringDirect(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static int GetIntDirect(JsonElement el, string name, int defaultValue)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : defaultValue;
    }

    private static bool GetBoolDirect(JsonElement el, string name, bool defaultValue)
    {
        if (el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }

        return defaultValue;
    }
}