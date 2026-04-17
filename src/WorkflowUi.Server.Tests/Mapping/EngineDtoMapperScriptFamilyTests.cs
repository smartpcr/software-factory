// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapperScriptFamilyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using ExecutionEngine.Nodes.Definitions;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for CSharpScript and CSharpTask mapper partials.
/// </summary>
public class EngineDtoMapperScriptFamilyTests
{
    [Fact]
    public void CSharpScript_roundtrips_script_fields()
    {
        var source = new CSharpScriptNodeDefinition
        {
            NodeId = "script-1",
            NodeName = "Script Node",
            ScriptPath = "scripts/hello.csx",
            ScriptContent = "Console.WriteLine(\"Hello\");",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("CSharpScript", dto.RuntimeType);
        Assert.Equal("scripts/hello.csx", dto.ScriptPath);
        Assert.Equal("Console.WriteLine(\"Hello\");", dto.ScriptContent);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var csScript = Assert.IsType<CSharpScriptNodeDefinition>(rt);
        Assert.Equal("scripts/hello.csx", csScript.ScriptPath);
        Assert.Equal("Console.WriteLine(\"Hello\");", csScript.ScriptContent);
    }

    [Fact]
    public void CSharpTask_inline_roundtrips()
    {
        var source = new CSharpTaskNodeDefinition
        {
            NodeId = "task-inline",
            NodeName = "Inline Task",
            ScriptContent = "return 42;",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("CSharpTask", dto.RuntimeType);
        Assert.Equal("return 42;", dto.ScriptContent);
        Assert.Null(dto.ExecutorTypeName);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var csTask = Assert.IsType<CSharpTaskNodeDefinition>(rt);
        Assert.Equal("return 42;", csTask.ScriptContent);
        Assert.Null(csTask.ExecutorTypeName);
    }

    [Fact]
    public void CSharpTask_executor_roundtrips()
    {
        var source = new CSharpTaskNodeDefinition
        {
            NodeId = "task-executor",
            NodeName = "Executor Task",
            ExecutorTypeName = "MyApp.Tasks.MyTaskExecutor",
            ExecutorAssemblyPath = "bin/MyApp.Tasks.dll",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("MyApp.Tasks.MyTaskExecutor", dto.ExecutorTypeName);
        Assert.Equal("bin/MyApp.Tasks.dll", dto.ExecutorAssemblyPath);
        Assert.Null(dto.ScriptContent);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var csTask = Assert.IsType<CSharpTaskNodeDefinition>(rt);
        Assert.Equal("MyApp.Tasks.MyTaskExecutor", csTask.ExecutorTypeName);
        Assert.Equal("bin/MyApp.Tasks.dll", csTask.ExecutorAssemblyPath);
    }

    [Fact]
    public void CSharp_compiled_roundtrips()
    {
        var source = new CSharpNodeDefinition
        {
            NodeId = "csharp-compiled",
            NodeName = "Compiled",
            AssemblyPath = "bin/MyApp.dll",
            TypeName = "MyApp.Nodes.MyNode",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("CSharp", dto.RuntimeType);
        Assert.Equal("bin/MyApp.dll", dto.AssemblyPath);
        Assert.Equal("MyApp.Nodes.MyNode", dto.TypeName);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var csharp = Assert.IsType<CSharpNodeDefinition>(rt);
        Assert.Equal("bin/MyApp.dll", csharp.AssemblyPath);
        Assert.Equal("MyApp.Nodes.MyNode", csharp.TypeName);
    }
}
