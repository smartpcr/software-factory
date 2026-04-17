// -----------------------------------------------------------------------
// <copyright file="NodeDtos.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System.Text.Json.Serialization;

/// <summary>
/// Base DTO for all node kinds, matching the TypeScript NodeDtoBase contract.
/// </summary>
public class NodeDto
{
    public string RuntimeType { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string JoinType { get; set; } = "Any";

    public string Priority { get; set; } = "Normal";

    public int MaxConcurrentExecutions { get; set; }

    public RetryPolicyDto? RetryPolicy { get; set; }

    public CircuitBreakerPolicyDto? CircuitBreakerPolicy { get; set; }

    public string? CompensationNodeId { get; set; }

    public string? FallbackNodeId { get; set; }

    public Dictionary<string, object>? Configuration { get; set; }

    // --- CSharp kind-specific ---
    public string? AssemblyPath { get; set; }

    public string? TypeName { get; set; }

    // --- CSharpScript kind-specific ---
    public string? ScriptPath { get; set; }

    public string? ScriptContent { get; set; }

    // --- CSharpTask kind-specific ---
    public string? ExecutorTypeName { get; set; }

    public string? ExecutorAssemblyPath { get; set; }

    // --- PowerShell kind-specific ---
    public List<string>? RequiredModules { get; set; }

    public Dictionary<string, string>? ModulePaths { get; set; }

    // --- IfElse kind-specific ---
    public string? Condition { get; set; }

    // --- Switch kind-specific ---
    public string? Expression { get; set; }

    public Dictionary<string, string>? Cases { get; set; }

    // --- ForEach kind-specific ---
    public string? CollectionExpression { get; set; }

    public string? ItemVariableName { get; set; }

    // --- While kind-specific ---
    public string? ConditionExpression { get; set; }

    public int? MaxIterations { get; set; }

    // --- Subflow kind-specific ---
    public string? WorkflowFilePath { get; set; }

    public WorkflowDefinitionDto? WorkflowDefinition { get; set; }

    public Dictionary<string, string>? InputMappings { get; set; }

    public Dictionary<string, string>? OutputMappings { get; set; }

    public string? Timeout { get; set; }

    public bool? SkipValidation { get; set; }

    // --- Timer kind-specific ---
    public string? Schedule { get; set; }

    // --- Container kind-specific ---
    public string? ExecutionMode { get; set; }

    public List<NodeDto>? ChildNodes { get; set; }

    public List<NodeConnectionDto>? ChildConnections { get; set; }
}

/// <summary>
/// Retry policy DTO matching the TypeScript RetryPolicyDto contract.
/// </summary>
public class RetryPolicyDto
{
    public string Strategy { get; set; } = "None";

    public int MaxAttempts { get; set; } = 3;

    public string InitialDelay { get; set; } = "PT1S";

    public string MaxDelay { get; set; } = "PT1M";

    public double Multiplier { get; set; } = 2.0;

    public List<string> RetryOn { get; set; } = [];

    public List<string> DoNotRetryOn { get; set; } = [];
}

/// <summary>
/// Circuit breaker policy DTO matching the TypeScript CircuitBreakerPolicyDto contract.
/// </summary>
public class CircuitBreakerPolicyDto
{
    public int FailureThreshold { get; set; } = 50;

    public int HalfOpenSuccesses { get; set; } = 3;

    public int MinimumThroughput { get; set; } = 10;

    public string OpenDuration { get; set; } = "PT30S";
}

/// <summary>
/// Connection DTO matching the TypeScript NodeConnectionDto contract.
/// </summary>
public class NodeConnectionDto
{
    public string SourceNodeId { get; set; } = string.Empty;

    public string TargetNodeId { get; set; } = string.Empty;

    public string TriggerMessageType { get; set; } = "Complete";

    public string? Condition { get; set; }

    public bool IsEnabled { get; set; } = true;

    public Dictionary<string, object>? Metadata { get; set; }

    public int Priority { get; set; }

    public string? SourcePort { get; set; }

    public string? TargetPort { get; set; }
}

/// <summary>
/// Workflow definition DTO matching the TypeScript WorkflowDefinitionDto contract.
/// </summary>
public class WorkflowDefinitionDto
{
    public string? WorkflowId { get; set; }

    public string? WorkflowName { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    public List<NodeDto>? Nodes { get; set; }

    public List<NodeConnectionDto>? Connections { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public string? EntryPointNodeId { get; set; }

    public int? MaxConcurrency { get; set; }

    public bool? AllowPause { get; set; }

    public int? TimeoutSeconds { get; set; }

    public Dictionary<string, string>? DefaultVariables { get; set; }
}

/// <summary>
/// Sidecar metadata DTO carrying UI-only state (positions, viewport).
/// </summary>
public class SidecarDto
{
    public Dictionary<string, NodeSidecarMeta>? Nodes { get; set; }

    public ViewportMeta? Viewport { get; set; }
}

/// <summary>
/// Per-node UI metadata (canvas position).
/// </summary>
public class NodeSidecarMeta
{
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>
/// Viewport UI metadata.
/// </summary>
public class ViewportMeta
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Zoom { get; set; } = 1.0;
}

/// <summary>
/// Result of merging a workflow DTO with sidecar UI metadata.
/// Carries both the engine workflow and positional data without
/// leaking UI state into engine-owned fields.
/// </summary>
public class MergedWorkflow
{
    public WorkflowDefinitionDto Workflow { get; set; } = new();

    public Dictionary<string, NodeSidecarMeta> NodePositions { get; set; } = [];

    public ViewportMeta? Viewport { get; set; }
}

/// <summary>
/// Response from the mapper round-trip endpoint.
/// Includes the round-tripped workflow and any diagnostics.
/// </summary>
public class MapperRoundTripResponse
{
    public WorkflowDefinitionDto Workflow { get; set; } = new();

    public List<string>? UnresolvedTypeNames { get; set; }
}
