// -----------------------------------------------------------------------
// <copyright file="AssemblyLoadTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using Xunit;

/// <summary>
/// Smoke tests that verify the ExecutionEngine assembly is reachable and loads correctly.
/// </summary>
public class AssemblyLoadTests
{
    [Fact]
    public void EngineAssembly_IsReachable_Loads()
    {
        var asm = typeof(WorkflowDefinition).Assembly;
        Assert.NotNull(asm);
        Assert.Contains("ExecutionEngine", asm.FullName!);
    }

    [Fact]
    public void EngineAssembly_Version_HasNonEmptyString()
    {
        var asm = typeof(WorkflowDefinition).Assembly;
        var version = asm.GetName().Version?.ToString();
        Assert.False(string.IsNullOrEmpty(version));
    }

    [Fact]
    public void Loader_TypeExists()
    {
        var loaderType = typeof(WorkflowLoader);
        Assert.NotNull(loaderType);
        Assert.Equal("WorkflowLoader", loaderType.Name);
    }

    [Fact]
    public void Serializer_TypeExists()
    {
        var serializerType = typeof(WorkflowSerializer);
        Assert.NotNull(serializerType);
        Assert.Equal("WorkflowSerializer", serializerType.Name);
    }

    [Fact]
    public void RuntimeTypeEnum_Has13Members()
    {
        var members = Enum.GetValues<RuntimeType>();
        Assert.Equal(13, members.Length);
    }

    [Fact]
    public void NodeDefinitionBase_IsAbstract()
    {
        Assert.True(typeof(NodeDefinition).IsAbstract);
    }
}
