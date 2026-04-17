// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapper.PowerShellFamily.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Mapper partials for PowerShell and PowerShellTask runtime kinds.
/// </summary>
public static partial class EngineDtoMapper
{
    /// <summary>
    /// Maps a PowerShellScript engine definition to DTO.
    /// </summary>
    public static NodeDto MapPowerShell(PowerShellScriptNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.ScriptPath = source.ScriptPath;
        dto.RequiredModules = source.RequiredModules;
        dto.ModulePaths = source.ModulePaths;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to PowerShellScript engine definition.
    /// </summary>
    public static PowerShellScriptNodeDefinition MapPowerShellFromDto(NodeDto dto)
    {
        var target = new PowerShellScriptNodeDefinition
        {
            ScriptPath = dto.ScriptPath ?? string.Empty,
            RequiredModules = dto.RequiredModules,
            ModulePaths = dto.ModulePaths,
        };
        MapBaseFromDto(dto, target);
        return target;
    }

    /// <summary>
    /// Maps a PowerShellTask engine definition to DTO.
    /// </summary>
    public static NodeDto MapPowerShellTask(PowerShellTaskNodeDefinition source)
    {
        var dto = new NodeDto();
        MapBaseToDto(source, dto);
        dto.ScriptPath = source.ScriptPath;
        dto.ScriptContent = source.ScriptContent;
        dto.RequiredModules = source.RequiredModules;
        dto.ModulePaths = source.ModulePaths;
        return dto;
    }

    /// <summary>
    /// Maps a DTO to PowerShellTask engine definition.
    /// </summary>
    public static PowerShellTaskNodeDefinition MapPowerShellTaskFromDto(NodeDto dto)
    {
        var target = new PowerShellTaskNodeDefinition
        {
            ScriptPath = dto.ScriptPath,
            ScriptContent = dto.ScriptContent,
            RequiredModules = dto.RequiredModules,
            ModulePaths = dto.ModulePaths,
        };
        MapBaseFromDto(dto, target);
        return target;
    }
}
