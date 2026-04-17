// -----------------------------------------------------------------------
// <copyright file="SidecarMergerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System.Collections.Generic;
using WorkflowUi.Server.Mapping;
using Xunit;

/// <summary>
/// Tests for SidecarMerger behavior.
/// </summary>
public class SidecarMergerTests
{
    [Fact]
    public void MergeInto_carries_positions_without_modifying_workflow()
    {
        var workflow = new WorkflowDefinitionDto
        {
            WorkflowId = "test-wf",
            Nodes = [new NodeDto { NodeId = "n1", RuntimeType = "Noop" }],
        };

        var sidecar = new SidecarDto
        {
            Nodes = new Dictionary<string, NodeSidecarMeta>
            {
                ["n1"] = new NodeSidecarMeta { X = 100, Y = 200 },
            },
            Viewport = new ViewportMeta { X = 10, Y = 20, Zoom = 1.5 },
        };

        var merged = SidecarMerger.MergeInto(workflow, sidecar);

        // Workflow is carried through without modification
        Assert.Same(workflow, merged.Workflow);

        // Positions are accessible from the merged result
        Assert.Single(merged.NodePositions);
        Assert.Equal(100, merged.NodePositions["n1"].X);
        Assert.Equal(200, merged.NodePositions["n1"].Y);

        // Viewport is carried
        Assert.NotNull(merged.Viewport);
        Assert.Equal(10, merged.Viewport!.X);
        Assert.Equal(20, merged.Viewport.Y);
        Assert.Equal(1.5, merged.Viewport.Zoom);
    }

    [Fact]
    public void MergeInto_with_null_sidecar_produces_empty_positions()
    {
        var workflow = new WorkflowDefinitionDto { WorkflowId = "test" };
        var merged = SidecarMerger.MergeInto(workflow, null);

        Assert.Same(workflow, merged.Workflow);
        Assert.Empty(merged.NodePositions);
        Assert.Null(merged.Viewport);
    }

    [Fact]
    public void ExtractFrom_merged_produces_sidecar()
    {
        var workflow = new WorkflowDefinitionDto { WorkflowId = "test" };
        var positions = new Dictionary<string, NodeSidecarMeta>
        {
            ["n1"] = new NodeSidecarMeta { X = 50, Y = 75 },
        };

        var merged = new MergedWorkflow
        {
            Workflow = workflow,
            NodePositions = positions,
            Viewport = new ViewportMeta { X = 5, Y = 10, Zoom = 2.0 },
        };

        var sidecar = SidecarMerger.ExtractFrom(merged);
        Assert.NotNull(sidecar.Nodes);
        Assert.Single(sidecar.Nodes);
        Assert.Equal(50, sidecar.Nodes["n1"].X);
        Assert.Equal(75, sidecar.Nodes["n1"].Y);
        Assert.Equal(2.0, sidecar.Viewport!.Zoom);
    }

    [Fact]
    public void Sidecar_roundtrip_merge_then_extract_preserves_positions()
    {
        var workflow = new WorkflowDefinitionDto
        {
            WorkflowId = "rt-test",
            Nodes =
            [
                new NodeDto { NodeId = "a", RuntimeType = "Noop" },
                new NodeDto { NodeId = "b", RuntimeType = "CSharp" },
            ],
        };

        var sidecar = new SidecarDto
        {
            Nodes = new Dictionary<string, NodeSidecarMeta>
            {
                ["a"] = new NodeSidecarMeta { X = 100, Y = 200 },
                ["b"] = new NodeSidecarMeta { X = 300, Y = 400 },
            },
            Viewport = new ViewportMeta { X = 0, Y = 0, Zoom = 1.0 },
        };

        // Merge then extract
        var merged = SidecarMerger.MergeInto(workflow, sidecar);
        var extracted = SidecarMerger.ExtractFrom(merged);

        // Positions round-trip
        Assert.Equal(2, extracted.Nodes!.Count);
        Assert.Equal(100, extracted.Nodes["a"].X);
        Assert.Equal(200, extracted.Nodes["a"].Y);
        Assert.Equal(300, extracted.Nodes["b"].X);
        Assert.Equal(400, extracted.Nodes["b"].Y);

        // Workflow is unchanged
        Assert.Equal("rt-test", merged.Workflow.WorkflowId);
        Assert.Equal(2, merged.Workflow.Nodes!.Count);
    }

    [Fact]
    public void ExtractFrom_with_positions_creates_sidecar()
    {
        var workflow = new WorkflowDefinitionDto { WorkflowId = "test" };
        var positions = new Dictionary<string, NodeSidecarMeta>
        {
            ["n1"] = new NodeSidecarMeta { X = 50, Y = 75 },
        };

        var sidecar = SidecarMerger.ExtractFrom(workflow, positions);
        Assert.NotNull(sidecar.Nodes);
        Assert.Single(sidecar.Nodes);
        Assert.Equal(50, sidecar.Nodes["n1"].X);
    }

    [Fact]
    public void ContainsOnlyUiMetadata_dto_validates_correctly()
    {
        var valid = new SidecarDto
        {
            Viewport = new ViewportMeta { X = 0, Y = 0, Zoom = 1.0 },
        };
        Assert.True(SidecarMerger.ContainsOnlyUiMetadata(valid));
    }

    [Fact]
    public void ContainsOnlyUiMetadata_json_rejects_engine_fields()
    {
        var bad = """{"nodeId": "n1", "nodes": {"n1": {"x":0,"y":0}}}""";
        Assert.False(SidecarMerger.ContainsOnlyUiMetadata(bad));
    }

    [Fact]
    public void ContainsOnlyUiMetadata_json_accepts_valid_sidecar()
    {
        var good = """{"nodes": {"n1": {"x":100,"y":200}}, "viewport": {"x":0,"y":0,"zoom":1.0}}""";
        Assert.True(SidecarMerger.ContainsOnlyUiMetadata(good));
    }
}
