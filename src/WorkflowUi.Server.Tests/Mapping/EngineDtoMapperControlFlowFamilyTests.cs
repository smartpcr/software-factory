// -----------------------------------------------------------------------
// <copyright file="EngineDtoMapperControlFlowFamilyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System.Collections.Generic;
using ExecutionEngine.Nodes.Definitions;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for IfElse, Switch, ForEach, While mapper partials.
/// </summary>
public class EngineDtoMapperControlFlowFamilyTests
{
    [Fact]
    public void IfElse_roundtrips_condition()
    {
        var source = new IfElseNodeDefinition
        {
            NodeId = "if-1",
            NodeName = "If Test",
            Condition = "x > 5",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("IfElse", dto.RuntimeType);
        Assert.Equal("x > 5", dto.Condition);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var ifElse = Assert.IsType<IfElseNodeDefinition>(rt);
        Assert.Equal("x > 5", ifElse.Condition);
    }

    [Fact]
    public void Switch_roundtrips_expression_and_cases()
    {
        var source = new SwitchNodeDefinition
        {
            NodeId = "sw-1",
            NodeName = "Switch Test",
            Expression = "status",
            Cases = new Dictionary<string, string>
            {
                ["active"] = "handle-active",
                ["inactive"] = "handle-inactive",
                ["default"] = "handle-default",
            },
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("Switch", dto.RuntimeType);
        Assert.Equal("status", dto.Expression);
        Assert.Equal(3, dto.Cases!.Count);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var sw = Assert.IsType<SwitchNodeDefinition>(rt);
        Assert.Equal("status", sw.Expression);
        Assert.Equal(3, sw.Cases.Count);
    }

    [Fact]
    public void ForEach_roundtrips_with_default_item_variable()
    {
        var source = new ForEachNodeDefinition
        {
            NodeId = "fe-1",
            NodeName = "ForEach Test",
            CollectionExpression = "items",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("ForEach", dto.RuntimeType);
        Assert.Equal("items", dto.CollectionExpression);
        Assert.Equal("item", dto.ItemVariableName);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var fe = Assert.IsType<ForEachNodeDefinition>(rt);
        Assert.Equal("items", fe.CollectionExpression);
        Assert.Equal("item", fe.ItemVariableName);
    }

    [Fact]
    public void While_roundtrips_with_default_max_iterations()
    {
        var source = new WhileNodeDefinition
        {
            NodeId = "wh-1",
            NodeName = "While Test",
            ConditionExpression = "count < 10",
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal("While", dto.RuntimeType);
        Assert.Equal("count < 10", dto.ConditionExpression);
        Assert.Equal(1000, dto.MaxIterations);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var wh = Assert.IsType<WhileNodeDefinition>(rt);
        Assert.Equal("count < 10", wh.ConditionExpression);
        Assert.Equal(1000, wh.MaxIterations);
    }

    [Fact]
    public void While_roundtrips_custom_max_iterations()
    {
        var source = new WhileNodeDefinition
        {
            NodeId = "wh-2",
            NodeName = "While Custom",
            ConditionExpression = "running",
            MaxIterations = 50,
        };

        var dto = EngineDtoMapper.MapToDto(source);
        Assert.Equal(50, dto.MaxIterations);

        var rt = EngineDtoMapper.MapFromDto(dto);
        var wh = Assert.IsType<WhileNodeDefinition>(rt);
        Assert.Equal(50, wh.MaxIterations);
    }
}
