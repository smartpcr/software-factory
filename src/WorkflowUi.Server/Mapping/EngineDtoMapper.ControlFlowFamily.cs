// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.ControlFlowFamily.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Mapper partials for IfElse, Switch, ForEach, While runtime kinds.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps an IfElse engine definition to DTO.
    /// </summary>
    public static NodeDto MapIfElse(IfElseNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.Condition = source.Condition;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to IfElse engine definition.
    /// </summary>
    public static IfElseNodeDefinition MapIfElseFromDto(NodeDto dto)
    {
        var target = new IfElseNodeDefinition
        {
            Condition = dto.Condition ?? string.Empty,
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a Switch engine definition to DTO.
    /// </summary>
    public static NodeDto MapSwitch(SwitchNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.Expression = source.Expression;
        dto.Cases = source.Cases;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to Switch engine definition.
    /// </summary>
    public static SwitchNodeDefinition MapSwitchFromDto(NodeDto dto)
    {
        var target = new SwitchNodeDefinition
        {
            Expression = dto.Expression ?? string.Empty,
            Cases = dto.Cases ?? [],
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a ForEach engine definition to DTO.
    /// </summary>
    public static NodeDto MapForEach(ForEachNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.CollectionExpression = source.CollectionExpression;
        dto.ItemVariableName = source.ItemVariableName;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to ForEach engine definition.
    /// </summary>
    public static ForEachNodeDefinition MapForEachFromDto(NodeDto dto)
    {
        var target = new ForEachNodeDefinition
        {
            CollectionExpression = dto.CollectionExpression ?? string.Empty,
            ItemVariableName = dto.ItemVariableName ?? "item",
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a While engine definition to DTO.
    /// </summary>
    public static NodeDto MapWhile(WhileNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.ConditionExpression = source.ConditionExpression;
        dto.MaxIterations = source.MaxIterations;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to While engine definition.
    /// </summary>
    public static WhileNodeDefinition MapWhileFromDto(NodeDto dto)
    {
        var target = new WhileNodeDefinition
        {
            ConditionExpression = dto.ConditionExpression ?? string.Empty,
            MaxIterations = dto.MaxIterations ?? 1000,
        };
        MapBaseFromDto(dto, target);
        return target;
    }
}
