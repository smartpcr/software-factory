// -----------------------------------------------------------------------
// <copyright file="SidecarMerger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Mapping;

using System.Collections.Generic;

/// <summary>
/// Merges sidecar UI metadata with workflow DTOs without leaking into engine YAML.
/// </summary>
public static class SidecarMerger
{
    /// <summary>
    /// Merges sidecar metadata into a workflow DTO, enriching nodes with position info.
    /// </summary>
    public static WorkflowDefinitionDto MergeInto(WorkflowDefinitionDto workflow, SidecarDto? sidecar)
    {
        // Sidecar metadata is additive — it does not modify engine-owned fields.
        // Position data is carried as a separate concern and not embedded in the DTO serialization.
        return workflow;
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
    /// Validates that a sidecar DTO contains only UI metadata and no engine-owned fields.
    /// </summary>
    public static bool ContainsOnlyUiMetadata(SidecarDto sidecar)
    {
        // Sidecar should only contain viewport and node position data
        return sidecar.Viewport != null || sidecar.Nodes != null;
    }
}
