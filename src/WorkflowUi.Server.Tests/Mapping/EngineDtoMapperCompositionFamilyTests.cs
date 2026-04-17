// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapperCompositionFamilyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System;
using System.Collections.Generic;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for Subflow, Timer, Container mapper partials.
/// </summary>
public class EngineDtoMapperCompositionFamilyTests
{
    [Fact]
    public void Subflow_file_roundtrips()
    {
        var source = new SubflowNodeDefinition
        {
            NodeId = "sub-file",
            NodeName = "Subflow File",
            WorkflowFilePath = "workflows/child.yaml",
            InputMappings = new Dictionary<string, string> { ["param1"] = "value1" },
            OutputMappings = new Dictionary<string, string> { ["result"] = "output1" },
            Timeout = TimeSpan.FromMinutes(10),
            SkipValidation = true,
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("Subflow", dto.RuntimeType);
        Assert.Equal("workflows/child.yaml", dto.WorkflowFilePath);
        Assert.Null(dto.WorkflowDefinition);
        Assert.True(dto.SkipValidation);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var sub = Assert.IsType<SubflowNodeDefinition>(rt);
        Assert.Equal("workflows/child.yaml", sub.WorkflowFilePath);
        Assert.Equal(TimeSpan.FromMinutes(10), sub.Timeout);
        Assert.True(sub.SkipValidation);
    }

    [Fact]
    public void Subflow_inline_roundtrips()
    {
        var inlineDef = new WorkflowDefinition
        {
            WorkflowId = "inline-wf",
            WorkflowName = "Inline",
            Nodes = [new NoopNodeDefinition { NodeId = "inner-noop", NodeName = "Inner" }],
        };

        var source = new SubflowNodeDefinition
        {
            NodeId = "sub-inline",
            NodeName = "Subflow Inline",
            WorkflowDefinition = inlineDef,
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.NotNull(dto.WorkflowDefinition);
        Assert.Equal("inline-wf", dto.WorkflowDefinition!.WorkflowId);
        Assert.Single(dto.WorkflowDefinition.Nodes!);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var sub = Assert.IsType<SubflowNodeDefinition>(rt);
        Assert.NotNull(sub.WorkflowDefinition);
        Assert.Equal("inline-wf", sub.WorkflowDefinition!.WorkflowId);
    }

    [Fact]
    public void Timer_roundtrips_schedule()
    {
        var source = new TimerNodeDefinition
        {
            NodeId = "timer-1",
            NodeName = "Cron Timer",
            Schedule = "0 */5 * * *",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("Timer", dto.RuntimeType);
        Assert.Equal("0 */5 * * *", dto.Schedule);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var timer = Assert.IsType<TimerNodeDefinition>(rt);
        Assert.Equal("0 */5 * * *", timer.Schedule);
    }

    [Fact]
    public void Container_parallel_roundtrips()
    {
        var source = new ContainerNodeDefinition
        {
            NodeId = "container-1",
            NodeName = "Parallel Container",
            ExecutionMode = ExecutionMode.Parallel,
            ChildNodes =
            [
                new NoopNodeDefinition { NodeId = "child-a", NodeName = "A" },
                new NoopNodeDefinition { NodeId = "child-b", NodeName = "B" },
            ],
            ChildConnections =
            [
                new NodeConnection
                {
                    SourceNodeId = "child-a",
                    TargetNodeId = "child-b",
                    TriggerMessageType = MessageType.Complete,
                },
            ],
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("Container", dto.RuntimeType);
        Assert.Equal("Parallel", dto.ExecutionMode);
        Assert.Equal(2, dto.ChildNodes!.Count);
        Assert.Single(dto.ChildConnections!);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var container = Assert.IsType<ContainerNodeDefinition>(rt);
        Assert.Equal(ExecutionMode.Parallel, container.ExecutionMode);
        Assert.Equal(2, container.ChildNodes!.Count);
        Assert.Single(container.ChildConnections!);
    }
}
