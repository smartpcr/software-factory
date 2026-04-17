// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapperRegistryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for the mapper registry dispatch and workflow-level mapping.
/// </summary>
public class EngineDtoMapperRegistryTests
{
    [Fact]
    public void SupportedRuntimeTypes_has_13_entries()
    {
        Assert.Equal(13, EngineDtoMapper.SupportedRuntimeTypes.Count);
    }

    [Fact]
    public void SupportedRuntimeTypes_matches_engine_enum()
    {
        var engineTypes = Enum.GetNames<RuntimeType>().OrderBy(n => n).ToList();
        var mapperTypes = EngineDtoMapper.SupportedRuntimeTypes.OrderBy(n => n).ToList();
        Assert.Equal(engineTypes, mapperTypes);
    }

    [Fact]
    public void MapWorkflowToDto_and_back_roundtrips()
    {
        var source = new WorkflowDefinition
        {
            WorkflowId = "wf-1",
            WorkflowName = "Test Workflow",
            Description = "A test workflow",
            Version = "2.0",
            MaxConcurrency = 10,
            AllowPause = false,
            TimeoutSeconds = 300,
            EntryPointNodeId = "start",
            Metadata = new Dictionary<string, object> { ["env"] = "production" },
            DefaultVariables = new Dictionary<string, object> { ["region"] = "us-east" },
            Nodes =
            [
                new NoopNodeDefinition { NodeId = "start", NodeName = "Start" },
                new NoopNodeDefinition { NodeId = "end", NodeName = "End" },
            ],
            Connections =
            [
                new NodeConnection
                {
                    SourceNodeId = "start",
                    TargetNodeId = "end",
                    TriggerMessageType = MessageType.Complete,
                },
            ],
        };

        var dto = EngineDtoMapper.MapWorkflowToDto(source);
        Assert.Equal("wf-1", dto.WorkflowId);
        Assert.Equal("Test Workflow", dto.WorkflowName);
        Assert.Equal(2, dto.Nodes!.Count);
        Assert.Single(dto.Connections!);
        Assert.Equal(10, dto.MaxConcurrency);
        Assert.False(dto.AllowPause);
        Assert.Equal(300, dto.TimeoutSeconds);

        var rt = EngineDtoMapper.MapWorkflowFromDto(dto);
        Assert.Equal("wf-1", rt.WorkflowId);
        Assert.Equal("Test Workflow", rt.WorkflowName);
        Assert.Equal(2, rt.Nodes.Count);
        Assert.Single(rt.Connections);
        Assert.Equal(10, rt.MaxConcurrency);
        Assert.False(rt.AllowPause);
        Assert.Equal(300, rt.TimeoutSeconds);
    }

    [Fact]
    public void MapperDiagnostics_summarizes_workflow()
    {
        var dto = new WorkflowDefinitionDto
        {
            WorkflowId = "diag-test",
            Nodes =
            [
                new NodeDto { RuntimeType = "Noop", NodeId = "a" },
                new NodeDto { RuntimeType = "Noop", NodeId = "b" },
                new NodeDto { RuntimeType = "CSharp", NodeId = "c" },
            ],
            Connections =
            [
                new WorkflowUi.Server.Mapping.NodeConnectionDto { SourceNodeId = "a", TargetNodeId = "b" },
            ],
        };

        var summary = MapperDiagnostics.Summarize(dto);
        Assert.Equal("diag-test", summary.WorkflowId);
        Assert.Equal(3, summary.NodeCount);
        Assert.Equal(1, summary.ConnectionCount);
        Assert.Equal(2, summary.NodesByKind["Noop"]);
        Assert.Equal(1, summary.NodesByKind["CSharp"]);
    }

    [Fact]
    public void MapperDiagnostics_finds_unrecognized_types()
    {
        var dto = new WorkflowDefinitionDto
        {
            Nodes =
            [
                new NodeDto { RuntimeType = "Noop", NodeId = "a" },
                new NodeDto { RuntimeType = "CustomWidget", NodeId = "b" },
            ],
        };

        var unrecognized = MapperDiagnostics.FindUnrecognizedRuntimeTypes(dto);
        Assert.Single(unrecognized);
        Assert.Contains("CustomWidget", unrecognized);
    }
}
