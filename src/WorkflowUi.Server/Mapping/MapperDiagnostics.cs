// -----------------------------------------------------------------------
// <copyright file="MapperDiagnostics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System.Collections.Generic;
using System.Linq;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Diagnostic helpers for mapper observability and tracing.
/// </summary>
public static class MapperDiagnostics
{
    /// <summary>
    /// Summarizes a mapped workflow for structured logging.
    /// </summary>
    public static MapperSummary Summarize(WorkflowDefinitionDto dto)
    {
        var nodesByKind = (dto.Nodes ?? [])
            .GroupBy(n => n.RuntimeType)
            .ToDictionary(g => g.Key, g => g.Count());

        return new MapperSummary
        {
            WorkflowId = dto.WorkflowId ?? string.Empty,
            NodeCount = dto.Nodes?.Count ?? 0,
            ConnectionCount = dto.Connections?.Count ?? 0,
            NodesByKind = nodesByKind,
        };
    }

    /// <summary>
    /// Validates that all node runtime types in a DTO are recognized.
    /// </summary>
    public static List<string> FindUnrecognizedRuntimeTypes(WorkflowDefinitionDto dto)
    {
        return (dto.Nodes ?? [])
            .Where(n => !EngineDtoMapper.SupportedRuntimeTypes.Contains(n.RuntimeType))
            .Select(n => n.RuntimeType)
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Summary of a mapped workflow for structured logging.
/// </summary>
public class MapperSummary
{
    public string WorkflowId { get; set; } = string.Empty;

    public int NodeCount { get; set; }

    public int ConnectionCount { get; set; }

    public Dictionary<string, int> NodesByKind { get; set; } = [];
}
