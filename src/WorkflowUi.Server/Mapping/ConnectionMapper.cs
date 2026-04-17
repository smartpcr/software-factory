// -----------------------------------------------------------------------
// <copyright file="ConnectionMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System;
using ExecutionEngine.Enums;
using ExecutionEngine.Workflow;

/// <summary>
/// Bidirectional mapper for NodeConnection ↔ NodeConnectionDto.
/// </summary>
public static class ConnectionMapper
{
    /// <summary>
    /// Maps an engine NodeConnection to a DTO.
    /// </summary>
    public static NodeConnectionDto MapToDto(NodeConnection source)
    {
        return new NodeConnectionDto
        {
            SourceNodeId = source.SourceNodeId,
            TargetNodeId = source.TargetNodeId,
            TriggerMessageType = source.TriggerMessageType.ToString(),
            Condition = source.Condition,
            IsEnabled = source.IsEnabled,
            Metadata = source.Metadata,
            Priority = source.Priority,
            SourcePort = source.SourcePort,
            TargetPort = source.TargetPort,
        };
    }

    /// <summary>
    /// Maps a DTO back to an engine NodeConnection.
    /// </summary>
    public static NodeConnection MapFromDto(NodeConnectionDto dto)
    {
        return new NodeConnection
        {
            SourceNodeId = dto.SourceNodeId,
            TargetNodeId = dto.TargetNodeId,
            TriggerMessageType = Enum.Parse<MessageType>(dto.TriggerMessageType),
            Condition = dto.Condition,
            IsEnabled = dto.IsEnabled,
            Metadata = dto.Metadata,
            Priority = dto.Priority,
            SourcePort = dto.SourcePort,
            TargetPort = dto.TargetPort,
        };
    }
}
