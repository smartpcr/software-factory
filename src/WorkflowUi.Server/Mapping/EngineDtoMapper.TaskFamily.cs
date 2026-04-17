// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.TaskFamily.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Mapper partials for Noop and compiled CSharp runtime kinds.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps a Noop engine definition to DTO.
    /// </summary>
    public static NodeDto MapNoop(NoopNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        return dto;
    }

    /// <summary>
    /// Maps a DTO to Noop engine definition.
    /// </summary>
    public static NoopNodeDefinition MapNoopFromDto(NodeDto dto)
    {
        var target = new NoopNodeDefinition();
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a compiled CSharp engine definition to DTO.
    /// </summary>
    public static NodeDto MapCSharp(CSharpNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.AssemblyPath = source.AssemblyPath;
        dto.TypeName = source.TypeName;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to compiled CSharp engine definition.
    /// </summary>
    public static CSharpNodeDefinition MapCSharpFromDto(NodeDto dto)
    {
        var target = new CSharpNodeDefinition
        {
            AssemblyPath = dto.AssemblyPath,
            TypeName = dto.TypeName,
        };
        MapBaseFromDto(dto, target);
        return target;
    }
}
