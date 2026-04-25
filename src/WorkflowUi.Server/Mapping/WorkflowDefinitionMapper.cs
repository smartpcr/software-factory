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
    /// <param name="json">Workflow JSON.</param>
    /// <param name="workflowsRoot">Optional root directory for resolving subflow file paths from workflow IDs.</param>
    public static WorkflowDefinition MapFromJson(string json, string? workflowsRoot = null)
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

        // Parse default variables
        if (root.TryGetProperty("defaultVariables", out var varsEl) && varsEl.ValueKind == JsonValueKind.Object)
        {
            wf.DefaultVariables = new Dictionary<string, object>();
            foreach (var prop in varsEl.EnumerateObject())
            {
                wf.DefaultVariables[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? (object)i
                        : prop.Value.TryGetInt64(out var l) ? (object)l
                        : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText(),
                };
            }
        }

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

        // Resolve subflow file paths from workflowId when workflowFilePath is empty
        if (!string.IsNullOrEmpty(workflowsRoot))
        {
            ResolveSubflowPaths(wf, root, workflowsRoot);
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
                WebHookMethod = GetString(el, config, "webHookMethod") ?? "POST",
                WebHookRoute = GetString(el, config, "webHookRoute"),
                BodyJsonSchema = GetString(el, config, "bodyJsonSchema"),
            },
            RuntimeType.Setter => new SetterNodeDefinition
            {
                VariableMappings = GetStringDictionary(el, config, "variableMappings"),
                VariableOps = GetStringDictionary(el, config, "variableOps"),
            },
            RuntimeType.Assert => new AssertNodeDefinition
            {
                Condition = GetString(el, config, "condition") ?? "",
                Message = GetString(el, config, "message"),
            },
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
                Mode = GetEnum<AINodeMode>(el, config, "mode"),
                Backend = GetEnum<AIBackend>(el, config, "backend"),
                SkillName = GetString(el, config, "skillName"),
                SubAgentName = GetString(el, config, "subAgentName"),
                Model = GetEnum<AIModelId>(el, config, "model"),
                UserPrompt = GetString(el, config, "userPrompt"),
                SystemPrompt = GetString(el, config, "systemPrompt"),
                InputMapping = GetString(el, config, "inputMapping") ?? "input",
                OutputKey = GetString(el, config, "outputKey") ?? "result",
                Confidence = GetInt(el, config, "confidence", 70),
                MaxTokens = GetInt(el, config, "maxTokens", 4096),
                TimeoutMinutes = GetInt(el, config, "timeoutMinutes", 5),
                WorkingDirectory = GetString(el, config, "workingDirectory"),
                InputBindings = GetStringDictionary(el, config, "inputBindings") ?? [],
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
            RuntimeType.Group => CreateGroupNodeDefinition(el, config),
            _ => null,
        };

        return def;
    }

    private static GroupNodeDefinition CreateGroupNodeDefinition(JsonElement el, JsonElement config)
    {
        var def = new GroupNodeDefinition();

        // Parse childNodes — check top-level first, then configuration
        JsonElement childArr = default;
        var hasChildren = el.TryGetProperty("childNodes", out childArr) && childArr.ValueKind == JsonValueKind.Array;
        if (!hasChildren && config.ValueKind == JsonValueKind.Object)
        {
            hasChildren = config.TryGetProperty("childNodes", out childArr) && childArr.ValueKind == JsonValueKind.Array;
        }

        if (hasChildren)
        {
            def.ChildNodes = [];
            foreach (var child in childArr.EnumerateArray())
            {
                var childRtStr = child.TryGetProperty("runtimeType", out var crt) ? crt.GetString() : null;
                if (string.IsNullOrEmpty(childRtStr)) continue;
                if (!Enum.TryParse<RuntimeType>(childRtStr, true, out var childRt)) continue;
                var childDef = CreateNodeDefinition(childRt, child);
                if (childDef != null)
                {
                    childDef.NodeId = child.TryGetProperty("nodeId", out var cid) ? cid.GetString() ?? "" : "";
                    childDef.NodeName = child.TryGetProperty("nodeName", out var cnm) ? cnm.GetString() ?? "" : "";
                    def.ChildNodes.Add(childDef);
                }
            }
        }

        // Parse childConnections
        JsonElement connArr = default;
        var hasConns = el.TryGetProperty("childConnections", out connArr) && connArr.ValueKind == JsonValueKind.Array;
        if (!hasConns && config.ValueKind == JsonValueKind.Object)
        {
            hasConns = config.TryGetProperty("childConnections", out connArr) && connArr.ValueKind == JsonValueKind.Array;
        }

        if (hasConns)
        {
            def.ChildConnections = [];
            foreach (var connEl in connArr.EnumerateArray())
            {
                def.ChildConnections.Add(MapConnection(connEl));
            }
        }

        var mode = GetString(el, config, "executionMode");
        if (!string.IsNullOrEmpty(mode) && Enum.TryParse<ExecutionMode>(mode, true, out var em))
        {
            def.ExecutionMode = em;
        }

        // Parse scopedVariables (stored as JSON string in configuration)
        var scopedVarsStr = GetString(el, config, "scopedVariables");
        if (!string.IsNullOrEmpty(scopedVarsStr))
        {
            try
            {
                var varsArr = System.Text.Json.JsonSerializer.Deserialize<List<ScopedVariable>>(scopedVarsStr);
                if (varsArr?.Count > 0)
                {
                    def.ScopedVariables = varsArr;
                }
            }
            catch { /* ignore malformed JSON */ }
        }

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
        if (string.IsNullOrEmpty(str))
        {
            return default;
        }

        try
        {
            return ExecutionEngine.Enums.EnumMemberHelper.ParseEnumMember<T>(str);
        }
        catch
        {
            return default;
        }
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

    internal static Dictionary<string, string>? GetStringDictionary(JsonElement el, JsonElement config, string name)
    {
        JsonElement obj = default;
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out obj) && obj.ValueKind == JsonValueKind.Object)
        {
            return obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }

        if (el.TryGetProperty(name, out obj) && obj.ValueKind == JsonValueKind.Object)
        {
            return obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
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

    /// <summary>
    /// Post-process subflow nodes: resolve workflowId → workflowFilePath
    /// when the file path is empty but a workflowId is set in the node config.
    /// </summary>
    private static void ResolveSubflowPaths(WorkflowDefinition wf, JsonElement root, string workflowsRoot)
    {
        if (!root.TryGetProperty("nodes", out var nodesEl)) return;
        if (!Directory.Exists(workflowsRoot)) return;

        // Build a lookup: workflowId → file content (scan once for all subflow nodes)
        Dictionary<string, string>? idToContent = null;

        foreach (var nodeEl in nodesEl.EnumerateArray())
        {
            var rtStr = nodeEl.TryGetProperty("runtimeType", out var rt) ? rt.GetString() : null;
            if (!string.Equals(rtStr, "Subflow", StringComparison.OrdinalIgnoreCase)) continue;

            var nodeId = nodeEl.TryGetProperty("nodeId", out var nid) ? nid.GetString() : null;
            if (string.IsNullOrEmpty(nodeId)) continue;

            var subflowDef = wf.Nodes.OfType<SubflowNodeDefinition>()
                .FirstOrDefault(n => n.NodeId == nodeId);
            if (subflowDef == null) continue;
            // Skip if already resolved
            if (subflowDef.WorkflowDefinition != null || !string.IsNullOrWhiteSpace(subflowDef.WorkflowFilePath)) continue;

            JsonElement config = default;
            _ = nodeEl.TryGetProperty("configuration", out config) || nodeEl.TryGetProperty("config", out config);
            var workflowId = GetString(nodeEl, config, "workflowId");

            if (string.IsNullOrEmpty(workflowId)) continue;

            // Lazy-build the ID → content index on first subflow node
            if (idToContent == null)
            {
                idToContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(workflowsRoot, "*.json", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var text = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.TryGetProperty("workflowId", out var wid))
                        {
                            var id = wid.GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                idToContent[id] = text;
                            }
                        }
                    }
                    catch
                    {
                        // Skip unparseable files
                    }
                }
            }

            // Pre-map the child workflow so SubflowNode gets a fully resolved WorkflowDefinition
            if (idToContent.TryGetValue(workflowId, out var childJson))
            {
                try
                {
                    subflowDef.WorkflowDefinition = MapFromJson(childJson, workflowsRoot);
                    subflowDef.SkipValidation = true;
                }
                catch
                {
                    // Fall back to file path if mapping fails
                }
            }
        }
    }
}