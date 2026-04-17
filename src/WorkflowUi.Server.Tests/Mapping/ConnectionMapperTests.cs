// -----------------------------------------------------------------------
// <copyright file="ConnectionMapperTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System.Collections.Generic;
using ExecutionEngine.Enums;
using ExecutionEngine.Workflow;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for ConnectionMapper round-trip behavior.
/// </summary>
public class ConnectionMapperTests
{
    [Fact]
    public void Connection_roundtrips_all_fields()
    {
        var source = new NodeConnection
        {
            SourceNodeId = "src-1",
            TargetNodeId = "tgt-1",
            TriggerMessageType = MessageType.Fail,
            Condition = "retries > 0",
            IsEnabled = false,
            Priority = 5,
            SourcePort = "error",
            TargetPort = "input",
            Metadata = new Dictionary<string, object> { ["note"] = "retry path" },
        };

        var dto = ConnectionMapper.MapToDto(source);
        Assert.Equal("src-1", dto.SourceNodeId);
        Assert.Equal("tgt-1", dto.TargetNodeId);
        Assert.Equal("Fail", dto.TriggerMessageType);
        Assert.Equal("retries > 0", dto.Condition);
        Assert.False(dto.IsEnabled);
        Assert.Equal(5, dto.Priority);
        Assert.Equal("error", dto.SourcePort);
        Assert.Equal("input", dto.TargetPort);

        var rt = ConnectionMapper.MapFromDto(dto);
        Assert.Equal(MessageType.Fail, rt.TriggerMessageType);
        Assert.Equal("retries > 0", rt.Condition);
        Assert.False(rt.IsEnabled);
        Assert.Equal(5, rt.Priority);
        Assert.Equal("error", rt.SourcePort);
        Assert.Equal("input", rt.TargetPort);
    }

    [Theory]
    [InlineData("Complete")]
    [InlineData("Fail")]
    [InlineData("Progress")]
    [InlineData("Next")]
    [InlineData("Cancel")]
    public void Connection_roundtrips_all_message_types(string messageType)
    {
        var source = new NodeConnection
        {
            SourceNodeId = "a",
            TargetNodeId = "b",
            TriggerMessageType = System.Enum.Parse<MessageType>(messageType),
        };

        var dto = ConnectionMapper.MapToDto(source);
        Assert.Equal(messageType, dto.TriggerMessageType);

        var rt = ConnectionMapper.MapFromDto(dto);
        Assert.Equal(source.TriggerMessageType, rt.TriggerMessageType);
    }
}
