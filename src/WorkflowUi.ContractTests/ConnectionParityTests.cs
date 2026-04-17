// -----------------------------------------------------------------------
// <copyright file="ConnectionParityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System.Linq;
using System.Reflection;
using ExecutionEngine.Workflow;
using Xunit;

/// <summary>
/// Parity test for the 9 properties on NodeConnection.
/// </summary>
[Trait("Category", "DtoParity")]
public class ConnectionParityTests
{
    [Fact]
    public void NodeConnection_Has9PublicProperties_InExactShape()
    {
        var expected = new[]
        {
            "Condition", "IsEnabled", "Metadata", "Priority",
            "SourceNodeId", "SourcePort", "TargetNodeId", "TargetPort",
            "TriggerMessageType",
        };
        var actual = typeof(NodeConnection)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NodeConnection_IsEnabled_DefaultsTrue()
    {
        var connection = new NodeConnection();
        Assert.True(connection.IsEnabled);
    }
}
