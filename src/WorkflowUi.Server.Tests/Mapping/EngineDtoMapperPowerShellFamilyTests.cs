// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapperPowerShellFamilyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System.Collections.Generic;
using ExecutionEngine.Nodes.Definitions;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for PowerShell and PowerShellTask mapper partials.
/// </summary>
public class EngineDtoMapperPowerShellFamilyTests
{
    [Fact]
    public void PowerShell_roundtrips_with_modules()
    {
        var source = new PowerShellScriptNodeDefinition
        {
            NodeId = "ps-1",
            NodeName = "PS Script",
            ScriptPath = "scripts/deploy.ps1",
            RequiredModules = ["Az", "PSDesiredStateConfiguration"],
            ModulePaths = new Dictionary<string, string> { ["Az"] = "modules/Az" },
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("PowerShell", dto.RuntimeType);
        Assert.Equal("scripts/deploy.ps1", dto.ScriptPath);
        Assert.Equal(2, dto.RequiredModules!.Count);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var ps = Assert.IsType<PowerShellScriptNodeDefinition>(rt);
        Assert.Equal("scripts/deploy.ps1", ps.ScriptPath);
        Assert.Equal(2, ps.RequiredModules!.Count);
        Assert.Single(ps.ModulePaths!);
    }

    [Fact]
    public void PowerShellTask_inline_roundtrips()
    {
        var source = new PowerShellTaskNodeDefinition
        {
            NodeId = "pst-1",
            NodeName = "PS Task",
            ScriptContent = "Write-Host 'Hello'",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("PowerShellTask", dto.RuntimeType);
        Assert.Equal("Write-Host 'Hello'", dto.ScriptContent);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var psTask = Assert.IsType<PowerShellTaskNodeDefinition>(rt);
        Assert.Equal("Write-Host 'Hello'", psTask.ScriptContent);
    }

    [Fact]
    public void PowerShellTask_path_roundtrips()
    {
        var source = new PowerShellTaskNodeDefinition
        {
            NodeId = "pst-path",
            NodeName = "PS Path Task",
            ScriptPath = "scripts/task.ps1",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("scripts/task.ps1", dto.ScriptPath);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var psTask = Assert.IsType<PowerShellTaskNodeDefinition>(rt);
        Assert.Equal("scripts/task.ps1", psTask.ScriptPath);
    }
}
