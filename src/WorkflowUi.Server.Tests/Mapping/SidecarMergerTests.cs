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
    public void MergeInto_returns_workflow_unchanged()
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
        };

        var result = SidecarMerger.MergeInto(workflow, sidecar);
        Assert.Same(workflow, result);
    }

    [Fact]
    public void ExtractFrom_creates_sidecar_from_positions()
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
    public void ContainsOnlyUiMetadata_validates_correctly()
    {
        var valid = new SidecarDto
        {
            Viewport = new ViewportMeta { X = 0, Y = 0, Zoom = 1.0 },
        };
        Assert.True(SidecarMerger.ContainsOnlyUiMetadata(valid));
    }
}
