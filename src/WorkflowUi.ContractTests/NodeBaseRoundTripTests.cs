// -----------------------------------------------------------------------
// <copyright file="NodeBaseRoundTripTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.IO;
using System.Linq;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using Xunit;

/// <summary>
/// Round-trip tests for NodeDefinition base properties (null policy preservation).
/// </summary>
[Trait("Category", "NodeBaseRoundTrip")]
public class NodeBaseRoundTripTests
{
    private readonly WorkflowSerializer serializer = new();

    [Fact]
    public void NullPolicy_StaysNull_AfterSerialize()
    {
        var def = new SetterNodeDefinition
        {
            NodeId = "n",
            RetryPolicy = null,
            CircuitBreakerPolicy = null,
        };
        var wf = this.Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        Assert.Null(back.Nodes[0].RetryPolicy);
        Assert.Null(back.Nodes[0].CircuitBreakerPolicy);
    }

    [Fact]
    public void NonNullRetryPolicy_PreservedAfterSerialize()
    {
        var def = new SetterNodeDefinition
        {
            NodeId = "n",
            RetryPolicy = new ExecutionEngine.Policies.RetryPolicy
            {
                MaxAttempts = 5,
            },
        };
        var wf = this.Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        Assert.NotNull(back.Nodes[0].RetryPolicy);
        Assert.Equal(5, back.Nodes[0].RetryPolicy!.MaxAttempts);
    }

    private WorkflowDefinition Wrap(NodeDefinition node)
    {
        return new WorkflowDefinition
        {
            WorkflowId = "base-round-trip",
            WorkflowName = "Base Round Trip Test",
            Nodes = [node],
            Connections = [],
        };
    }
}
