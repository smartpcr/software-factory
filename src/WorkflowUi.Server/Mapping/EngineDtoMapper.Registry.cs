// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.Registry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;

/// <summary>
/// Registry and factory methods for dispatching by runtimeType.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps any engine NodeDefinition subclass to a DTO, dispatching by RuntimeType.
    /// </summary>
    public static NodeDto MapToDto(NodeDefinition source)
    {
        return source switch
        {
            NoopNodeDefinition noop => MapNoop(noop),
            CSharpNodeDefinition csharp => MapCSharp(csharp),
            CSharpScriptNodeDefinition csharpScript => MapCSharpScript(csharpScript),
            CSharpTaskNodeDefinition csharpTask => MapCSharpTask(csharpTask),
            PowerShellScriptNodeDefinition ps => MapPowerShell(ps),
            PowerShellTaskNodeDefinition psTask => MapPowerShellTask(psTask),
            IfElseNodeDefinition ifElse => MapIfElse(ifElse),
            SwitchNodeDefinition sw => MapSwitch(sw),
            ForEachNodeDefinition forEach => MapForEach(forEach),
            WhileNodeDefinition wh => MapWhile(wh),
            SubflowNodeDefinition subflow => MapSubflow(subflow),
            TimerNodeDefinition timer => MapTimer(timer),
            ContainerNodeDefinition container => MapContainer(container),
            _ => throw new ArgumentException($"Unsupported node definition type: {source.GetType().Name}"),
        };
    }

    /// <summary>
    /// Maps a DTO back to an engine NodeDefinition, dispatching by runtimeType string.
    /// </summary>
    public static NodeDefinition MapFromDto(NodeDto dto)
    {
        return dto.RuntimeType switch
        {
            "Noop" => MapNoopFromDto(dto),
            "CSharp" => MapCSharpFromDto(dto),
            "CSharpScript" => MapCSharpScriptFromDto(dto),
            "CSharpTask" => MapCSharpTaskFromDto(dto),
            "PowerShell" => MapPowerShellFromDto(dto),
            "PowerShellTask" => MapPowerShellTaskFromDto(dto),
            "IfElse" => MapIfElseFromDto(dto),
            "Switch" => MapSwitchFromDto(dto),
            "ForEach" => MapForEachFromDto(dto),
            "While" => MapWhileFromDto(dto),
            "Subflow" => MapSubflowFromDto(dto),
            "Timer" => MapTimerFromDto(dto),
            "Container" => MapContainerFromDto(dto),
            _ => throw new ArgumentException($"Unsupported runtime type: {dto.RuntimeType}"),
        };
    }

    /// <summary>
    /// Maps an engine WorkflowDefinition to a DTO.
    /// </summary>
    public static WorkflowDefinitionDto MapWorkflowToDto(WorkflowDefinition source)
    {
        return new WorkflowDefinitionDto
        {
            WorkflowId = source.WorkflowId,
            WorkflowName = source.WorkflowName,
            Description = source.Description,
            Version = source.Version,
            Nodes = source.Nodes?.Select(MapToDto).ToList(),
            Connections = source.Connections?.Select(ConnectionMapper.MapToDto).ToList(),
            Metadata = source.Metadata?.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString() ?? string.Empty),
            EntryPointNodeId = source.EntryPointNodeId,
            MaxConcurrency = source.MaxConcurrency,
            AllowPause = source.AllowPause,
            TimeoutSeconds = source.TimeoutSeconds,
            DefaultVariables = source.DefaultVariables?.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString() ?? string.Empty),
        };
    }

    /// <summary>
    /// Maps a DTO back to an engine WorkflowDefinition.
    /// </summary>
    public static WorkflowDefinition MapWorkflowFromDto(WorkflowDefinitionDto dto)
    {
        return MapWorkflowFromDto(dto, out _);
    }

    /// <summary>
    /// Maps a DTO back to an engine WorkflowDefinition with diagnostics
    /// for unresolvable retry type names.
    /// </summary>
    public static WorkflowDefinition MapWorkflowFromDto(WorkflowDefinitionDto dto, out List<string> unresolvedTypeNames)
    {
        diagnosticCollector = [];
        try
        {
            var result = new WorkflowDefinition
            {
                WorkflowId = dto.WorkflowId ?? string.Empty,
                WorkflowName = dto.WorkflowName ?? string.Empty,
                Description = dto.Description,
                Version = dto.Version ?? "1.0",
                Nodes = dto.Nodes?.Select(MapFromDto).ToList() ?? [],
                Connections = dto.Connections?.Select(ConnectionMapper.MapFromDto).ToList() ?? [],
                Metadata = dto.Metadata?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)kvp.Value) ?? [],
                EntryPointNodeId = dto.EntryPointNodeId,
                MaxConcurrency = dto.MaxConcurrency ?? 0,
                AllowPause = dto.AllowPause ?? true,
                TimeoutSeconds = dto.TimeoutSeconds ?? 0,
                DefaultVariables = dto.DefaultVariables?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)kvp.Value),
            };

            unresolvedTypeNames = diagnosticCollector;
            return result;
        }
        finally
        {
            diagnosticCollector = null;
        }
    }

    /// <summary>
    /// All supported runtime type names.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedRuntimeTypes =
    [
        "Noop", "CSharp", "CSharpScript", "CSharpTask",
        "PowerShell", "PowerShellTask",
        "IfElse", "ForEach", "While", "Switch",
        "Subflow", "Timer", "Container",
    ];
}
