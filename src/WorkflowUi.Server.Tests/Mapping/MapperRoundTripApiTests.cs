// -----------------------------------------------------------------------
// <copyright file="MapperRoundTripApiTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Mapping;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkflowUi.Server.Mapping;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// WebApplicationFactory tests for the mapper round-trip endpoint.
/// </summary>
public class MapperRoundTripApiTests : IClassFixture<TestWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient client;

    public MapperRoundTripApiTests(TestWebApplicationFactory factory)
    {
        this.client = factory.CreateClient();
    }

    [Fact]
    public async Task Roundtrip_noop_returns_same_structure()
    {
        var request = new WorkflowDefinitionDto
        {
            WorkflowId = "rt-noop",
            WorkflowName = "RT Setter",
            Version = "1.0",
            Nodes =
            [
                new NodeDto
                {
                    RuntimeType = "Setter",
                    NodeId = "n1",
                    NodeName = "Test",
                    JoinType = "Any",
                    Priority = "Normal",
                },
            ],
            Connections = [],
        };

        var response = await this.client.PostAsJsonAsync("/api/mapper/roundtrip", request, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<MapperRoundTripResponse>(JsonOpts);
        Assert.NotNull(result);
        Assert.Equal("rt-noop", result!.Workflow.WorkflowId);
        Assert.Single(result.Workflow.Nodes!);
        Assert.Equal("Setter", result.Workflow.Nodes![0].RuntimeType);
        Assert.Null(result.UnresolvedTypeNames);
    }

    [Fact]
    public async Task Roundtrip_rejects_unknown_runtime_type()
    {
        var request = new WorkflowDefinitionDto
        {
            WorkflowId = "bad-wf",
            Nodes = [new NodeDto { RuntimeType = "UnknownKind", NodeId = "x" }],
        };

        var response = await this.client.PostAsJsonAsync("/api/mapper/roundtrip", request, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Roundtrip_preserves_policy_null_vs_populated()
    {
        var withPolicy = new WorkflowDefinitionDto
        {
            WorkflowId = "with-policy",
            Nodes =
            [
                new NodeDto
                {
                    RuntimeType = "Setter",
                    NodeId = "p1",
                    NodeName = "With Policy",
                    JoinType = "Any",
                    Priority = "Normal",
                    RetryPolicy = new RetryPolicyDto
                    {
                        Strategy = "Exponential",
                        MaxAttempts = 5,
                        InitialDelay = "PT2S",
                        MaxDelay = "PT2M",
                        Multiplier = 3.0,
                    },
                },
            ],
        };

        var response1 = await this.client.PostAsJsonAsync("/api/mapper/roundtrip", withPolicy, JsonOpts);
        var result1 = await response1.Content.ReadFromJsonAsync<MapperRoundTripResponse>(JsonOpts);
        Assert.NotNull(result1!.Workflow.Nodes![0].RetryPolicy);

        var withoutPolicy = new WorkflowDefinitionDto
        {
            WorkflowId = "no-policy",
            Nodes =
            [
                new NodeDto
                {
                    RuntimeType = "Setter",
                    NodeId = "p2",
                    NodeName = "No Policy",
                    JoinType = "Any",
                    Priority = "Normal",
                },
            ],
        };

        var response2 = await this.client.PostAsJsonAsync("/api/mapper/roundtrip", withoutPolicy, JsonOpts);
        var result2 = await response2.Content.ReadFromJsonAsync<MapperRoundTripResponse>(JsonOpts);
        Assert.Null(result2!.Workflow.Nodes![0].RetryPolicy);
    }

    [Fact]
    public async Task Roundtrip_all_13_runtime_types()
    {
        var nodes = new List<NodeDto>
        {
            new() { RuntimeType = "Setter", NodeId = "n1", NodeName = "N1", JoinType = "Any", Priority = "Normal" },
            new() { RuntimeType = "CSharp", NodeId = "n2", NodeName = "N2", JoinType = "Any", Priority = "Normal", AssemblyPath = "a.dll", TypeName = "T" },
            new() { RuntimeType = "CSharpScript", NodeId = "n3", NodeName = "N3", JoinType = "Any", Priority = "Normal", ScriptContent = "x", ScriptPath = "y" },
            new() { RuntimeType = "CSharpTask", NodeId = "n4", NodeName = "N4", JoinType = "Any", Priority = "Normal", ScriptContent = "z" },
            new() { RuntimeType = "PowerShell", NodeId = "n5", NodeName = "N5", JoinType = "Any", Priority = "Normal", ScriptPath = "p.ps1" },
            new() { RuntimeType = "PowerShellTask", NodeId = "n6", NodeName = "N6", JoinType = "Any", Priority = "Normal", ScriptContent = "w" },
            new() { RuntimeType = "IfElse", NodeId = "n7", NodeName = "N7", JoinType = "Any", Priority = "Normal", Condition = "true" },
            new() { RuntimeType = "ForEach", NodeId = "n8", NodeName = "N8", JoinType = "Any", Priority = "Normal", CollectionExpression = "c", ItemVariableName = "i" },
            new() { RuntimeType = "While", NodeId = "n9", NodeName = "N9", JoinType = "Any", Priority = "Normal", ConditionExpression = "e", MaxIterations = 100 },
            new() { RuntimeType = "Switch", NodeId = "n10", NodeName = "N10", JoinType = "Any", Priority = "Normal", Expression = "s", Cases = new() { ["a"] = "b" } },
            new() { RuntimeType = "Subflow", NodeId = "n11", NodeName = "N11", JoinType = "Any", Priority = "Normal", WorkflowFilePath = "f.yaml", Timeout = "PT5M" },
            new() { RuntimeType = "Timer", NodeId = "n12", NodeName = "N12", JoinType = "Any", Priority = "Normal", Schedule = "* * * * *" },
            new() { RuntimeType = "Group", NodeId = "n13", NodeName = "N13", JoinType = "Any", Priority = "Normal", ExecutionMode = "Parallel" },
        };

        var request = new WorkflowDefinitionDto
        {
            WorkflowId = "all-kinds",
            Nodes = nodes,
        };

        var response = await this.client.PostAsJsonAsync("/api/mapper/roundtrip", request, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<MapperRoundTripResponse>(JsonOpts);
        Assert.Equal(13, result!.Workflow.Nodes!.Count);

        var rtTypes = result.Workflow.Nodes!.Select(n => n.RuntimeType).OrderBy(r => r, StringComparer.Ordinal).ToList();
        var expected = nodes.Select(n => n.RuntimeType!).OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, rtTypes);
    }

    [Fact]
    public async Task Roundtrip_surfaces_unresolved_type_names()
    {
        var request = new WorkflowDefinitionDto
        {
            WorkflowId = "unresolved-wf",
            Nodes =
            [
                new NodeDto
                {
                    RuntimeType = "Setter",
                    NodeId = "n1",
                    NodeName = "N1",
                    JoinType = "Any",
                    Priority = "Normal",
                    RetryPolicy = new RetryPolicyDto
                    {
                        Strategy = "Fixed",
                        InitialDelay = "PT1S",
                        MaxDelay = "PT1M",
                        RetryOn = ["System.TimeoutException, System.Runtime", "Bogus.Missing, Bad.Asm"],
                        DoNotRetryOn = ["Another.Missing, Bad.Asm"],
                    },
                },
            ],
        };

        var response = await this.client.PostAsJsonAsync("/api/mapper/roundtrip", request, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<MapperRoundTripResponse>(JsonOpts);
        Assert.NotNull(result!.UnresolvedTypeNames);
        Assert.Equal(2, result.UnresolvedTypeNames!.Count);
        Assert.Contains(result.UnresolvedTypeNames, u => u.Contains("Bogus.Missing"));
        Assert.Contains(result.UnresolvedTypeNames, u => u.Contains("Another.Missing"));
    }
}
