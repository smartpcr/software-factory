// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.CompositionFamily.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;

/// <summary>
/// Mapper partials for Subflow, Timer, Group runtime kinds.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps a Subflow engine definition to DTO.
    /// </summary>
    public static NodeDto MapSubflow(SubflowNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.WorkflowFilePath = source.WorkflowFilePath;
        dto.WorkflowDefinition = source.WorkflowDefinition != null
            ? MapWorkflowToDto(source.WorkflowDefinition)
            : null;
        dto.InputMappings = source.InputMappings;
        dto.OutputMappings = source.OutputMappings;
        dto.Timeout = ToIsoDuration(source.Timeout);
        dto.SkipValidation = source.SkipValidation;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to Subflow engine definition.
    /// </summary>
    public static SubflowNodeDefinition MapSubflowFromDto(NodeDto dto)
    {
        var target = new SubflowNodeDefinition
        {
            WorkflowFilePath = dto.WorkflowFilePath!,
            WorkflowDefinition = dto.WorkflowDefinition != null
                ? MapWorkflowFromDto(dto.WorkflowDefinition)
                : null,
            InputMappings = dto.InputMappings ?? [],
            OutputMappings = dto.OutputMappings ?? [],
            Timeout = dto.Timeout != null ? FromIsoDuration(dto.Timeout) : TimeSpan.FromMinutes(5),
            SkipValidation = dto.SkipValidation ?? false,
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a Timer engine definition to DTO.
    /// </summary>
    public static NodeDto MapTimer(TimerNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.Schedule = source.Schedule;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to Timer engine definition.
    /// </summary>
    public static TimerNodeDefinition MapTimerFromDto(NodeDto dto)
    {
        var target = new TimerNodeDefinition
        {
            Schedule = dto.Schedule ?? string.Empty,
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a Group engine definition to DTO.
    /// </summary>
    public static NodeDto MapGroup(GroupNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.ExecutionMode = source.ExecutionMode.ToString();
        dto.ChildNodes = source.ChildNodes?.Select(MapToDto).ToList();
        dto.ChildConnections = source.ChildConnections?.Select(ConnectionMapper.MapToDto).ToList();
        return dto;
    }

    /// <summary>
    /// Maps a DTO to Group engine definition.
    /// </summary>
    public static GroupNodeDefinition MapGroupFromDto(NodeDto dto)
    {
        var target = new GroupNodeDefinition
        {
            ExecutionMode = Enum.Parse<ExecutionMode>(dto.ExecutionMode ?? "Sequential"),
            ChildNodes = dto.ChildNodes?.Select(MapFromDto).ToList(),
            ChildConnections = dto.ChildConnections?.Select(ConnectionMapper.MapFromDto).ToList(),
        };
        MapBaseFromDto(dto, target);
        return target;
    }
}
