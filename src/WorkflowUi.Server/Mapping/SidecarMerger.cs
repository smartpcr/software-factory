// -----------------------------------------------------------------------
// <copyright file="SidecarMerger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Merges sidecar UI metadata with workflow DTOs without leaking into engine YAML.
/// </summary>
public static class SidecarMerger
{
    /// <summary>
    /// Allowed top-level sidecar property names (UI-only metadata).
    /// </summary>
    private static readonly HashSet<string> AllowedSidecarProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "nodes",
        "viewport",
    };

    /// <summary>
    /// Merges sidecar metadata into a workflow DTO, producing a
    /// <see cref="MergedWorkflow"/> that carries both engine data and
    /// UI-only positional metadata without leaking into engine fields.
    /// </summary>
    public static MergedWorkflow MergeInto(WorkflowDefinitionDto workflow, SidecarDto? sidecar)
    {
        var result = new MergedWorkflow
        {
            Workflow = workflow,
            NodePositions = sidecar?.Nodes ?? [],
            Viewport = sidecar?.Viewport,
        };

        return result;
    }

    /// <summary>
    /// Extracts sidecar metadata from a <see cref="MergedWorkflow"/>.
    /// </summary>
    public static SidecarDto ExtractFrom(MergedWorkflow merged)
    {
        return new SidecarDto
        {
            Nodes = merged.NodePositions,
            Viewport = merged.Viewport ?? new ViewportMeta { X = 0, Y = 0, Zoom = 1.0 },
        };
    }

    /// <summary>
    /// Extracts sidecar metadata from a workflow DTO with positional info.
    /// </summary>
    public static SidecarDto ExtractFrom(WorkflowDefinitionDto workflow, Dictionary<string, NodeSidecarMeta>? positions)
    {
        return new SidecarDto
        {
            Nodes = positions ?? [],
            Viewport = new ViewportMeta { X = 0, Y = 0, Zoom = 1.0 },
        };
    }

    /// <summary>
    /// Validates that a sidecar JSON string contains only allowed UI metadata properties.
    /// Returns true if valid, false if engine-owned fields are detected.
    /// </summary>
    public static bool ContainsOnlyUiMetadata(string sidecarJson)
    {
        if (string.IsNullOrWhiteSpace(sidecarJson))
        {
            return true;
        }

        using var doc = JsonDocument.Parse(sidecarJson);
        return doc.RootElement.EnumerateObject()
            .All(prop => AllowedSidecarProperties.Contains(prop.Name));
    }

    /// <summary>
    /// Validates that a sidecar DTO contains only UI metadata.
    /// </summary>
    public static bool ContainsOnlyUiMetadata(SidecarDto sidecar)
    {
        // SidecarDto by design only has Nodes and Viewport — if it parsed
        // correctly, it only contains UI metadata.
        return sidecar != null;
    }
}
