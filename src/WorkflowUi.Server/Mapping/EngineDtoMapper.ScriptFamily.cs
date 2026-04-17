// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.ScriptFamily.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Mapper partials for CSharpScript and CSharpTask runtime kinds.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps a CSharpScript engine definition to DTO.
    /// </summary>
    public static NodeDto MapCSharpScript(CSharpScriptNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.ScriptPath = source.ScriptPath;
        dto.ScriptContent = source.ScriptContent;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to CSharpScript engine definition.
    /// </summary>
    public static CSharpScriptNodeDefinition MapCSharpScriptFromDto(NodeDto dto)
    {
        var target = new CSharpScriptNodeDefinition
        {
            ScriptPath = dto.ScriptPath ?? string.Empty,
            ScriptContent = dto.ScriptContent ?? string.Empty,
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a CSharpTask engine definition to DTO.
    /// </summary>
    public static NodeDto MapCSharpTask(CSharpTaskNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.ScriptContent = source.ScriptContent;
        dto.ExecutorTypeName = source.ExecutorTypeName;
        dto.ExecutorAssemblyPath = source.ExecutorAssemblyPath;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to CSharpTask engine definition.
    /// </summary>
    public static CSharpTaskNodeDefinition MapCSharpTaskFromDto(NodeDto dto)
    {
        var target = new CSharpTaskNodeDefinition
        {
            ScriptContent = dto.ScriptContent,
            ExecutorTypeName = dto.ExecutorTypeName,
            ExecutorAssemblyPath = dto.ExecutorAssemblyPath,
        };
        MapBaseFromDto(dto, target);
        return target;
    }
}
