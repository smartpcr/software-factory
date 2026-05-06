// -----------------------------------------------------------------------
// <copyright file="MemoryMcpToolRegistrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Memory.InMemory;
using Ast.Memory.Interfaces;
using Ast.Memory.Mcp;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

[TestClass]
public class MemoryMcpToolRegistrationTests
{
    [TestMethod]
    public void Tools_are_enumerated_then_exactly_5_tools_are_registered()
    {
        var registration = new MemoryMcpToolRegistration();

        var tools = registration.Tools;
        var toolNames = registration.GetToolNames();

        tools.Should().HaveCount(5);
        toolNames.Should().BeEquivalentTo(new[]
        {
            "memory_query",
            "memory_status",
            "memory_record_expertise",
            "memory_sync",
            "memory_consolidate"
        });
    }

    [TestMethod]
    public void GetTool_returns_correct_tool_by_name()
    {
        var registration = new MemoryMcpToolRegistration();

        var queryTool = registration.GetTool("memory_query");
        queryTool.Should().NotBeNull();
        queryTool!.Name.Should().Be("memory_query");
        queryTool.Description.Should().Contain("retrieval");

        var statusTool = registration.GetTool("memory_status");
        statusTool.Should().NotBeNull();
        statusTool!.Name.Should().Be("memory_status");

        var expertiseTool = registration.GetTool("memory_record_expertise");
        expertiseTool.Should().NotBeNull();
        expertiseTool!.Name.Should().Be("memory_record_expertise");
        expertiseTool.InputSchema.Required.Should().Contain("targetNodeId");
        expertiseTool.InputSchema.Required.Should().Contain("kind");
        expertiseTool.InputSchema.Required.Should().Contain("title");
        expertiseTool.InputSchema.Required.Should().Contain("body");
        expertiseTool.InputSchema.Required.Should().Contain("sourceAgent");

        var syncTool = registration.GetTool("memory_sync");
        syncTool.Should().NotBeNull();

        var consolidateTool = registration.GetTool("memory_consolidate");
        consolidateTool.Should().NotBeNull();
    }

    [TestMethod]
    public void GetTool_returns_null_for_unknown_tool()
    {
        var registration = new MemoryMcpToolRegistration();
        registration.GetTool("unknown_tool").Should().BeNull();
    }

    [TestMethod]
    public void Memory_record_expertise_schema_has_required_and_optional_parameters()
    {
        var registration = new MemoryMcpToolRegistration();
        var tool = registration.GetTool("memory_record_expertise")!;

        tool.InputSchema.Properties.Should().ContainKey("targetNodeId");
        tool.InputSchema.Properties.Should().ContainKey("kind");
        tool.InputSchema.Properties.Should().ContainKey("title");
        tool.InputSchema.Properties.Should().ContainKey("body");
        tool.InputSchema.Properties.Should().ContainKey("sourceAgent");
        tool.InputSchema.Properties.Should().ContainKey("scope");
        tool.InputSchema.Properties.Should().ContainKey("evidence");

        tool.InputSchema.Required.Should().HaveCount(5);
        tool.InputSchema.Required.Should().NotContain("scope");
        tool.InputSchema.Required.Should().NotContain("evidence");

        // Evidence array items schema has required nodeId property
        var evidenceProp = tool.InputSchema.Properties["evidence"];
        evidenceProp.Type.Should().Be("array");
        evidenceProp.Items.Should().NotBeNull();
        evidenceProp.Items!.Type.Should().Be("object");
        evidenceProp.Items.Properties.Should().ContainKey("nodeId");
        evidenceProp.Items.Properties.Should().ContainKey("commitSha");
        evidenceProp.Items.Properties.Should().ContainKey("snippet");
        evidenceProp.Items.Required.Should().Contain("nodeId");

        // Kind enum values
        var kindProp = tool.InputSchema.Properties["kind"];
        kindProp.Enum.Should().BeEquivalentTo(new[] { "Pattern", "Pitfall", "Preference", "Insight" });

        // Scope enum values
        var scopeProp = tool.InputSchema.Properties["scope"];
        scopeProp.Enum.Should().BeEquivalentTo(new[] { "exact", "subtree" });

        // seedNodeIds for memory_query has items schema
        var queryTool = registration.GetTool("memory_query")!;
        var seedProp = queryTool.InputSchema.Properties["seedNodeIds"];
        seedProp.Type.Should().Be("array");
        seedProp.Items.Should().NotBeNull();
        seedProp.Items!.Type.Should().Be("string");
    }
}

/// <summary>
/// Tests for memory_query MCP tool roundtrip using a graph populated from
/// the <c>src/ast/Ast.Core/</c> fixture via <see cref="Ast.Csharp.CsharpAstParser"/>.
/// </summary>
[TestClass]
public class MemoryMcpHandlerQueryTests
{
    private const string TargetNodeId = "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode";

    /// <summary>
    /// Populates a graph from the Ast.Core fixture using the CsharpAstParser
    /// extraction pipeline and verifies memory_query returns contextMarkdown
    /// with a ## Structure section referencing HypergraphNode.
    /// </summary>
    [TestMethod]
    public async Task Memory_query_roundtrip_with_graph_populated_from_fixture()
    {
        // Arrange: populate graph via the CsharpAstParser extraction pipeline
        var graphStore = new InMemoryMemoryGraphStore();
        var fixtureNodes = await AstCoreFixtureBuilder.PopulateFromFixtureAsync(graphStore);

        // Verify the fixture was populated with real parsed nodes
        fixtureNodes.Should().NotBeEmpty("CsharpAstParser should extract nodes from HypergraphModels.cs");
        fixtureNodes.Should().Contain(n => n.Id == TargetNodeId,
            "fixture must contain the HypergraphNode class node");

        var retrievalEngine = new InMemoryRetrievalEngine(graphStore);
        var evolutionEngine = new InMemoryEvolutionEngine(graphStore);
        var handler = CreateHandler(graphStore, retrievalEngine, evolutionEngine);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "HypergraphNode embedding text", "tokenBudget": 4000}""");

        // Act
        var responseJson = await handler.HandleAsync("memory_query", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        // Assert: contextMarkdown contains ## Structure referencing HypergraphNode
        var contextMarkdown = response.GetProperty("contextMarkdown").GetString()!;
        contextMarkdown.Should().Contain("## Structure");
        contextMarkdown.Should().Contain("HypergraphNode");

        // tokensUsed is a positive integer ≤ 4000
        var tokensUsed = response.GetProperty("tokensUsed").GetInt32();
        tokensUsed.Should().BePositive().And.BeLessThanOrEqualTo(4000);

        // nodes array includes at least one entry with nodeId exactly matching the fixture
        var nodesArray = response.GetProperty("nodes");
        nodesArray.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var nodeIds = new List<string>();
        foreach (var node in nodesArray.EnumerateArray())
        {
            nodeIds.Add(node.GetProperty("nodeId").GetString()!);
        }

        nodeIds.Should().Contain(n => n == TargetNodeId,
            "nodeId must exactly match /src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode");
    }

    /// <summary>
    /// Verifies the handler maps JSON params to a typed <see cref="RetrievalRequest"/>
    /// and delegates to <see cref="IMemoryRetrievalEngine.RetrieveAsync"/>.
    /// </summary>
    [TestMethod]
    public async Task Memory_query_maps_json_params_to_typed_retrieval_request()
    {
        var mockRetrieval = new Mock<IMemoryRetrievalEngine>();
        mockRetrieval
            .Setup(x => x.RetrieveAsync(It.IsAny<RetrievalRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrievalResult(
                Nodes: Array.Empty<ScoredMemoryNode>(),
                ContextMarkdown: "## Structure\ntest",
                TokensUsed: 10,
                ModesExecuted: new[] { RetrievalMode.SemanticSearch },
                Diagnostics: Array.Empty<string>()));

        var handler = new MemoryMcpHandler(
            mockRetrieval.Object,
            new Mock<IMemoryGraphStore>().Object,
            new Mock<IEvolutionEngine>().Object,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "find methods", "tokenBudget": 2000, "mode": "SemanticSearch"}""");

        await handler.HandleAsync("memory_query", paramsJson);

        mockRetrieval.Verify(x => x.RetrieveAsync(
            It.Is<RetrievalRequest>(r =>
                r.QueryText == "find methods" &&
                r.TokenBudget == 2000 &&
                r.Mode == RetrievalMode.SemanticSearch),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Memory_query_with_invalid_mode_returns_error()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "test", "mode": "InvalidMode"}""");

        var responseJson = await handler.HandleAsync("memory_query", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("Invalid retrieval mode");
    }

    [TestMethod]
    public async Task Memory_query_with_invalid_token_budget_returns_error()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "test", "tokenBudget": -1}""");

        var responseJson = await handler.HandleAsync("memory_query", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("Invalid tokenBudget");
    }

    [TestMethod]
    public async Task Memory_query_with_excessive_token_budget_returns_error()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "test", "tokenBudget": 100000}""");

        var responseJson = await handler.HandleAsync("memory_query", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("Invalid tokenBudget");
    }

    [TestMethod]
    public async Task Memory_query_with_unknown_tool_returns_error()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.Deserialize<JsonElement>("{}");

        var responseJson = await handler.HandleAsync("unknown_tool", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("Unknown tool");
    }

    [TestMethod]
    public async Task Memory_query_validates_seedNodeIds_items_are_nonempty_strings()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "test", "seedNodeIds": ["valid", ""]}""");

        var responseJson = await handler.HandleAsync("memory_query", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("non-empty string");
    }

    private static MemoryMcpHandler CreateHandler(
        InMemoryMemoryGraphStore? graphStore = null,
        IMemoryRetrievalEngine? retrievalEngine = null,
        IEvolutionEngine? evolutionEngine = null)
    {
        var store = graphStore ?? new InMemoryMemoryGraphStore();
        return new MemoryMcpHandler(
            retrievalEngine ?? new InMemoryRetrievalEngine(store),
            store,
            evolutionEngine ?? new InMemoryEvolutionEngine(store),
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);
    }
}

/// <summary>
/// Tests for memory_record_expertise MCP tool with real persistence and
/// subsequent memory_query showing the pitfall under ## Known Expertise.
/// </summary>
[TestClass]
public class MemoryMcpHandlerRecordExpertiseTests
{
    private const string TargetNodeId = "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode";

    /// <summary>
    /// Records expertise via MCP, verifies the response, then queries and
    /// verifies the pitfall appears under ## Known Expertise.
    /// </summary>
    [TestMethod]
    public async Task Memory_record_expertise_persists_and_subsequent_query_shows_pitfall()
    {
        // Arrange: populate graph via CsharpAstParser fixture extraction
        var graphStore = new InMemoryMemoryGraphStore();
        var fixtureNodes = await AstCoreFixtureBuilder.PopulateFromFixtureAsync(graphStore);
        fixtureNodes.Should().Contain(n => n.Id == TargetNodeId);

        var retrievalEngine = new InMemoryRetrievalEngine(graphStore);
        var evolutionEngine = new InMemoryEvolutionEngine(graphStore);

        var handler = new MemoryMcpHandler(
            retrievalEngine,
            graphStore,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        // Act 1: record expertise via MCP
        var recordParams = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
                "kind": "Pitfall",
                "title": "GetEmbeddingText misses enum members",
                "body": "GetEmbeddingText does not include enum member names in the embedding text, reducing semantic search quality for enum types",
                "sourceAgent": "test-agent-1"
            }
            """);

        var recordResponseJson = await handler.HandleAsync("memory_record_expertise", recordParams);
        var recordResponse = JsonSerializer.Deserialize<JsonElement>(recordResponseJson);

        // Assert 1: the entry has correct ID format and confidence
        var id = recordResponse.GetProperty("id").GetString()!;
        id.Should().MatchRegex(@"^expertise:pitfall:[0-9a-f]{8}$");
        recordResponse.GetProperty("confidence").GetDouble().Should().Be(1.0);
        recordResponse.GetProperty("kind").GetString().Should().Be("Pitfall");
        recordResponse.GetProperty("title").GetString().Should().Be("GetEmbeddingText misses enum members");
        recordResponse.GetProperty("targetNodeId").GetString().Should().Be(TargetNodeId);
        recordResponse.GetProperty("sourceAgent").GetString().Should().Be("test-agent-1");
        recordResponse.GetProperty("contentHash").GetString().Should().NotBeNullOrEmpty();

        // Act 2: subsequent memory_query for "HypergraphNode"
        var queryParams = JsonSerializer.Deserialize<JsonElement>(
            """{"queryText": "HypergraphNode", "tokenBudget": 4000}""");

        var queryResponseJson = await handler.HandleAsync("memory_query", queryParams);
        var queryResponse = JsonSerializer.Deserialize<JsonElement>(queryResponseJson);

        // Assert 2: the pitfall appears under ## Known Expertise
        var contextMarkdown = queryResponse.GetProperty("contextMarkdown").GetString()!;
        contextMarkdown.Should().Contain("## Known Expertise");
        contextMarkdown.Should().Contain("Pitfall");
        contextMarkdown.Should().Contain("GetEmbeddingText misses enum members");
    }

    /// <summary>
    /// Verifies handler maps JSON params to typed arguments and calls
    /// IEvolutionEngine.RecordExpertiseAsync with exact parameters.
    /// </summary>
    [TestMethod]
    public async Task Memory_record_expertise_maps_json_to_typed_evolution_engine_call()
    {
        var mockEvolution = new Mock<IEvolutionEngine>();
        mockEvolution
            .Setup(x => x.RecordExpertiseAsync(
                It.IsAny<string>(), It.IsAny<ExpertiseKind>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Citation>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExpertiseEntry
            {
                Id = "expertise:pitfall:12345678",
                Kind = ExpertiseKind.Pitfall,
                Title = "Test",
                Body = "Test body",
                TargetNodeId = "/node1",
                Confidence = 1.0,
                SourceAgent = "agent-x",
                ContentHash = "abcdef01"
            });

        var handler = new MemoryMcpHandler(
            new Mock<IMemoryRetrievalEngine>().Object,
            new Mock<IMemoryGraphStore>().Object,
            mockEvolution.Object,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/node1",
                "kind": "Pitfall",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-x",
                "scope": "subtree",
                "evidence": [{"nodeId": "/ref1", "commitSha": "abc", "snippet": "code()"}]
            }
            """);

        await handler.HandleAsync("memory_record_expertise", paramsJson);

        mockEvolution.Verify(x => x.RecordExpertiseAsync(
            "/node1",
            ExpertiseKind.Pitfall,
            "Test",
            "Test body",
            "agent-x",
            "subtree",
            It.Is<IReadOnlyList<Citation>>(c =>
                c.Count == 1 &&
                c[0].NodeId == "/ref1" &&
                c[0].CommitSha == "abc" &&
                c[0].Snippet == "code()"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_optional_scope_and_evidence()
    {
        var graphStore = new InMemoryMemoryGraphStore();
        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/test",
            Name = "TestNode",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/test.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        var evolutionEngine = new InMemoryEvolutionEngine(graphStore);
        var handler = new MemoryMcpHandler(
            new InMemoryRetrievalEngine(graphStore),
            graphStore,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pattern",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1",
                "scope": "subtree",
                "evidence": [{"nodeId": "/ref", "commitSha": "abc", "snippet": "code"}]
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("scope").GetString().Should().Be("subtree");
        response.GetProperty("kind").GetString().Should().Be("Pattern");

        var entries = await graphStore.GetExpertiseForNodeAsync("/test");
        entries.Should().HaveCount(1);
        entries[0].Scope.Should().Be("subtree");
        entries[0].Evidence.Should().HaveCount(1);
        entries[0].Evidence[0].NodeId.Should().Be("/ref");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_invalid_kind_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "InvalidKind",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("Invalid expertise kind");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_invalid_scope_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pitfall",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1",
                "scope": "invalid_scope"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("Invalid scope");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_invalid_evidence_shape_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pitfall",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1",
                "evidence": "not-an-array"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("evidence must be an array");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_evidence_missing_nodeId_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pitfall",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1",
                "evidence": [{"snippet": "code"}]
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("nodeId");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_empty_targetNodeId_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "",
                "kind": "Pitfall",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("must not be empty");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_empty_title_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pitfall",
                "title": "   ",
                "body": "Test body",
                "sourceAgent": "agent-1"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("must not be empty");
    }

    [TestMethod]
    public async Task Memory_record_expertise_with_nonexistent_target_returns_error()
    {
        var handler = CreateDefaultHandler();

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/nonexistent/node",
                "kind": "Pitfall",
                "title": "Test",
                "body": "Test body",
                "sourceAgent": "agent-1"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("does not exist");
    }

    [TestMethod]
    public async Task Memory_record_expertise_deduplicates_identical_entries()
    {
        var graphStore = new InMemoryMemoryGraphStore();
        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/test",
            Name = "TestNode",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/test.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        var evolutionEngine = new InMemoryEvolutionEngine(graphStore);
        var handler = new MemoryMcpHandler(
            new InMemoryRetrievalEngine(graphStore),
            graphStore,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pitfall",
                "title": "Same title",
                "body": "Same body",
                "sourceAgent": "agent-1"
            }
            """);

        var resp1Json = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var resp2Json = await handler.HandleAsync("memory_record_expertise", paramsJson);

        // Both return the same ID (deduplicated)
        var id1 = JsonSerializer.Deserialize<JsonElement>(resp1Json).GetProperty("id").GetString();
        var id2 = JsonSerializer.Deserialize<JsonElement>(resp2Json).GetProperty("id").GetString();
        id1.Should().Be(id2);

        var entries = await graphStore.GetExpertiseForNodeAsync("/test");
        entries.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Memory_record_expertise_creates_applies_to_edge()
    {
        var graphStore = new InMemoryMemoryGraphStore();
        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/test",
            Name = "TestNode",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/test.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        var evolutionEngine = new InMemoryEvolutionEngine(graphStore);
        var handler = new MemoryMcpHandler(
            new InMemoryRetrievalEngine(graphStore),
            graphStore,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Pattern",
                "title": "Builder pattern",
                "body": "Uses builder pattern for config",
                "sourceAgent": "agent-1"
            }
            """);

        var responseJson = await handler.HandleAsync("memory_record_expertise", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);
        var entryId = response.GetProperty("id").GetString()!;

        // Verify APPLIES_TO edge was created
        var edgeId = $"edge:{entryId}→/test";
        var edge = await graphStore.GetEdgeAsync(edgeId);
        edge.Should().NotBeNull();
        edge!.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
        edge.NodeIds.Should().Contain(entryId);
        edge.NodeIds.Should().Contain("/test");
        ((string)edge.Metadata[MemoryMetadataKeys.SemanticRelation]!).Should().Be("APPLIES_TO");
        ((string)edge.Metadata[MemoryMetadataKeys.MemoryLayer]!).Should().Be("expertise");
    }

    [TestMethod]
    public async Task Memory_record_expertise_logs_audit_trail()
    {
        var graphStore = new InMemoryMemoryGraphStore();
        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/test",
            Name = "TestNode",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/test.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        var mockGraphStore = new Mock<IMemoryGraphStore>();
        mockGraphStore
            .Setup(x => x.GetNodeAsync("/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HypergraphNode
            {
                Id = "/test", Name = "T", NodeType = "Class", Language = "C#", FilePath = "/t.cs"
            });
        mockGraphStore
            .Setup(x => x.GetExpertiseForNodeAsync("/test", It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExpertiseEntry>());
        mockGraphStore
            .Setup(x => x.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockGraphStore
            .Setup(x => x.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockGraphStore
            .Setup(x => x.LogAuditAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var evolutionEngine = new InMemoryEvolutionEngine(mockGraphStore.Object);
        var handler = new MemoryMcpHandler(
            new Mock<IMemoryRetrievalEngine>().Object,
            mockGraphStore.Object,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """
            {
                "targetNodeId": "/test",
                "kind": "Insight",
                "title": "Audit test",
                "body": "Body",
                "sourceAgent": "audit-agent"
            }
            """);

        await handler.HandleAsync("memory_record_expertise", paramsJson);

        // Verify audit log was written
        mockGraphStore.Verify(x => x.LogAuditAsync(
            "audit-agent",
            "RecordExpertise",
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify APPLIES_TO edge was created
        mockGraphStore.Verify(x => x.CreateEdgeAsync(
            It.Is<Hyperedge>(e =>
                e.EdgeType == HyperedgeType.MemoryRelation &&
                (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]! == "APPLIES_TO"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify expertise was upserted
        mockGraphStore.Verify(x => x.UpsertExpertiseAsync(
            It.Is<ExpertiseEntry>(e =>
                e.TargetNodeId == "/test" &&
                e.Kind == ExpertiseKind.Insight &&
                e.Confidence == 1.0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MemoryMcpHandler CreateDefaultHandler()
    {
        var store = new InMemoryMemoryGraphStore();
        return new MemoryMcpHandler(
            new InMemoryRetrievalEngine(store),
            store,
            new InMemoryEvolutionEngine(store),
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);
    }
}

/// <summary>
/// Tests for memory_status including EdgesByLayer population.
/// </summary>
[TestClass]
public class MemoryMcpHandlerStatusTests
{
    [TestMethod]
    public async Task Memory_status_returns_graph_statistics_with_edges_by_layer()
    {
        var graphStore = new InMemoryMemoryGraphStore();
        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/node1",
            Name = "Node1",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/f1.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/node2",
            Name = "Node2",
            NodeType = "Method",
            Language = "C#",
            FilePath = "/f2.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "call_chain"
            }
        });

        // Create edges with MemoryLayer metadata
        await graphStore.CreateEdgeAsync(new Hyperedge
        {
            Id = "edge1",
            EdgeType = HyperedgeType.ClassDefinition,
            NodeIds = new List<string> { "/node1", "/node2" },
            Roles = new Dictionary<string, string> { ["/node1"] = "parent", ["/node2"] = "member" },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.SemanticRelation] = "CONTAINS"
            }
        });

        await graphStore.CreateEdgeAsync(new Hyperedge
        {
            Id = "edge2",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "/node2", "/node1" },
            Roles = new Dictionary<string, string> { ["/node2"] = "caller", ["/node1"] = "callee" },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "call_chain",
                [MemoryMetadataKeys.SemanticRelation] = "CALLS"
            }
        });

        // Add expertise to test TotalExpertise
        await graphStore.UpsertExpertiseAsync(new ExpertiseEntry
        {
            Id = "expertise:pattern:test1234",
            Kind = ExpertiseKind.Pattern,
            Title = "Test",
            Body = "Test body",
            TargetNodeId = "/node1",
            SourceAgent = "test",
            ContentHash = "hash1"
        });

        var handler = new MemoryMcpHandler(
            new InMemoryRetrievalEngine(graphStore),
            graphStore,
            new InMemoryEvolutionEngine(graphStore),
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>("{}");
        var responseJson = await handler.HandleAsync("memory_status", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("totalNodes").GetInt32().Should().Be(2);
        response.GetProperty("totalEdges").GetInt32().Should().Be(2);
        response.GetProperty("totalExpertise").GetInt32().Should().Be(1);

        // EdgesByLayer should be populated
        var edgesByLayer = response.GetProperty("edgesByLayer");
        edgesByLayer.GetProperty("structural").GetInt32().Should().Be(1);
        edgesByLayer.GetProperty("call_chain").GetInt32().Should().Be(1);

        // NodesByLayer should be populated
        var nodesByLayer = response.GetProperty("nodesByLayer");
        nodesByLayer.GetProperty("structural").GetInt32().Should().Be(1);
        nodesByLayer.GetProperty("call_chain").GetInt32().Should().Be(1);

        // NodesByType should be populated
        var nodesByType = response.GetProperty("nodesByType");
        nodesByType.GetProperty("Class").GetInt32().Should().Be(1);
        nodesByType.GetProperty("Method").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// Verifies handler delegates to IMemoryGraphStore.GetMemoryStatisticsAsync.
    /// </summary>
    [TestMethod]
    public async Task Memory_status_delegates_to_graph_store()
    {
        var mockStore = new Mock<IMemoryGraphStore>();
        mockStore
            .Setup(x => x.GetMemoryStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryGraphStatistics
            {
                TotalNodes = 42,
                TotalEdges = 100,
                TotalExpertise = 5,
                ArchivedExpertise = 2,
                NodesByLayer = new Dictionary<string, int> { ["structural"] = 30, ["call_chain"] = 12 },
                EdgesByLayer = new Dictionary<string, int> { ["structural"] = 60, ["expertise"] = 40 },
                NodesByType = new Dictionary<string, int> { ["Class"] = 20, ["Method"] = 22 }
            });

        var handler = new MemoryMcpHandler(
            new Mock<IMemoryRetrievalEngine>().Object,
            mockStore.Object,
            new Mock<IEvolutionEngine>().Object,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>("{}");
        var responseJson = await handler.HandleAsync("memory_status", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("totalNodes").GetInt32().Should().Be(42);
        response.GetProperty("totalEdges").GetInt32().Should().Be(100);
        response.GetProperty("totalExpertise").GetInt32().Should().Be(5);
        response.GetProperty("archivedExpertise").GetInt32().Should().Be(2);
        response.GetProperty("edgesByLayer").GetProperty("structural").GetInt32().Should().Be(60);
        response.GetProperty("edgesByLayer").GetProperty("expertise").GetInt32().Should().Be(40);

        mockStore.Verify(x => x.GetMemoryStatisticsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

/// <summary>
/// Tests for memory_sync verifying typed request mapping and delegation.
/// </summary>
[TestClass]
public class MemoryMcpHandlerSyncTests
{
    [TestMethod]
    public async Task Memory_sync_delegates_to_incremental_sync_with_correct_params()
    {
        var syncResult = new SyncResult(3, 5, 2, 10, 1, 1, TimeSpan.FromMilliseconds(150));

        var mockSync = new Mock<IIncrementalSync>();
        mockSync
            .Setup(x => x.ApplyDiffAsync("C:\\repo", "abc123", "def456", It.IsAny<CancellationToken>()))
            .ReturnsAsync(syncResult);

        var store = new InMemoryMemoryGraphStore();
        var handler = new MemoryMcpHandler(
            new InMemoryRetrievalEngine(store),
            store,
            new InMemoryEvolutionEngine(store),
            mockSync.Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"repoRoot": "C:\\repo", "fromCommit": "abc123", "toCommit": "def456"}""");

        var responseJson = await handler.HandleAsync("memory_sync", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("filesChanged").GetInt32().Should().Be(3);
        response.GetProperty("nodesAdded").GetInt32().Should().Be(5);
        response.GetProperty("nodesRetired").GetInt32().Should().Be(2);
        response.GetProperty("edgesAdded").GetInt32().Should().Be(10);
        response.GetProperty("edgesRetired").GetInt32().Should().Be(1);
        response.GetProperty("expertiseDecayed").GetInt32().Should().Be(1);
        response.GetProperty("durationMs").GetDouble().Should().BeGreaterThan(0);

        // Verify the mock was called with exact parameters
        mockSync.Verify(x => x.ApplyDiffAsync(
            "C:\\repo", "abc123", "def456", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Memory_sync_with_missing_required_param_returns_error()
    {
        var handler = new MemoryMcpHandler(
            new Mock<IMemoryRetrievalEngine>().Object,
            new Mock<IMemoryGraphStore>().Object,
            new Mock<IEvolutionEngine>().Object,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"repoRoot": "C:\\repo"}""");

        var responseJson = await handler.HandleAsync("memory_sync", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetString().Should().Contain("fromCommit");
    }
}

/// <summary>
/// Tests for memory_consolidate with real duplicate data to exercise merge logic.
/// </summary>
[TestClass]
public class MemoryMcpHandlerConsolidateTests
{
    [TestMethod]
    public async Task Memory_consolidate_delegates_to_evolution_engine_with_correct_options()
    {
        var mockEvolution = new Mock<IEvolutionEngine>();
        mockEvolution
            .Setup(x => x.ConsolidateAsync(It.IsAny<ConsolidationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationResult(3, 1, new[] { "merged-1" }));

        var handler = new MemoryMcpHandler(
            new Mock<IMemoryRetrievalEngine>().Object,
            new Mock<IMemoryGraphStore>().Object,
            mockEvolution.Object,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"similarityThreshold": 0.90, "maxMergesPerRun": 25}""");

        var responseJson = await handler.HandleAsync("memory_consolidate", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("candidatesPaired").GetInt32().Should().Be(3);
        response.GetProperty("mergedCount").GetInt32().Should().Be(1);
        response.GetProperty("mergedEntryIds").GetArrayLength().Should().Be(1);

        // Verify typed options were correctly mapped
        mockEvolution.Verify(x => x.ConsolidateAsync(
            It.Is<ConsolidationOptions>(o =>
                Math.Abs(o.SimilarityThreshold - 0.90) < 0.01 &&
                o.MaxMergesPerRun == 25),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Memory_consolidate_with_default_options_uses_defaults()
    {
        var mockEvolution = new Mock<IEvolutionEngine>();
        mockEvolution
            .Setup(x => x.ConsolidateAsync(It.IsAny<ConsolidationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationResult(0, 0, Array.Empty<string>()));

        var handler = new MemoryMcpHandler(
            new Mock<IMemoryRetrievalEngine>().Object,
            new Mock<IMemoryGraphStore>().Object,
            mockEvolution.Object,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>("{}");
        await handler.HandleAsync("memory_consolidate", paramsJson);

        mockEvolution.Verify(x => x.ConsolidateAsync(
            It.Is<ConsolidationOptions>(o =>
                Math.Abs(o.SimilarityThreshold - 0.85) < 0.01 &&
                o.MaxMergesPerRun == 50),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Exercises consolidation with real duplicate expertise entries
    /// to verify the in-memory engine actually archives duplicates.
    /// </summary>
    [TestMethod]
    public async Task Memory_consolidate_archives_exact_duplicates()
    {
        var store = new InMemoryMemoryGraphStore();
        await store.CreateNodeAsync(new HypergraphNode
        {
            Id = "/test",
            Name = "TestNode",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/test.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        // Insert two expertise entries with matching contentHash (exact duplicates)
        var hash = ExpertiseEntry.ComputeContentHash("Title", "Body", "/test");
        await store.UpsertExpertiseAsync(new ExpertiseEntry
        {
            Id = "expertise:pitfall:dup1",
            Kind = ExpertiseKind.Pitfall,
            Title = "Title",
            Body = "Body",
            TargetNodeId = "/test",
            SourceAgent = "agent-1",
            ContentHash = hash
        });

        await store.UpsertExpertiseAsync(new ExpertiseEntry
        {
            Id = "expertise:pitfall:dup2",
            Kind = ExpertiseKind.Pitfall,
            Title = "Title 2",
            Body = "Body 2",
            TargetNodeId = "/test",
            SourceAgent = "agent-2",
            ContentHash = hash
        });

        var evolutionEngine = new InMemoryEvolutionEngine(store);
        var handler = new MemoryMcpHandler(
            new InMemoryRetrievalEngine(store),
            store,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        var paramsJson = JsonSerializer.Deserialize<JsonElement>(
            """{"similarityThreshold": 0.85, "maxMergesPerRun": 10}""");

        var responseJson = await handler.HandleAsync("memory_consolidate", paramsJson);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        // Should have found candidates and merged the duplicate
        response.GetProperty("candidatesPaired").GetInt32().Should().BeGreaterThan(0);
        response.GetProperty("mergedCount").GetInt32().Should().BeGreaterThan(0);

        // Verify one entry was archived
        var archived = await store.GetArchivedExpertiseAsync("/test");
        archived.Should().HaveCount(1);
    }
}

[TestClass]
public class ExpertiseEntryTests
{
    [TestMethod]
    public void ExpertiseEntry_id_generation_follows_canonical_format()
    {
        var title = "GetEmbeddingText misses enum members";
        var body = "GetEmbeddingText does not include enum member names in the embedding text, reducing semantic search quality for enum types";
        var targetNodeId = "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode";

        var contentHash = ExpertiseEntry.ComputeContentHash(title, body, targetNodeId);
        var id = ExpertiseEntry.GenerateId(ExpertiseKind.Pitfall, contentHash);

        id.Should().MatchRegex(@"^expertise:pitfall:[0-9a-f]{8}$");
        contentHash.Should().HaveLength(64);
    }

    [TestMethod]
    public void ExpertiseEntry_content_hash_is_deterministic()
    {
        var hash1 = ExpertiseEntry.ComputeContentHash("title", "body", "/node");
        var hash2 = ExpertiseEntry.ComputeContentHash("title", "body", "/node");

        hash1.Should().Be(hash2);
    }

    [TestMethod]
    public void ExpertiseEntry_different_inputs_produce_different_hashes()
    {
        var hash1 = ExpertiseEntry.ComputeContentHash("title1", "body", "/node");
        var hash2 = ExpertiseEntry.ComputeContentHash("title2", "body", "/node");

        hash1.Should().NotBe(hash2);
    }
}

/// <summary>
/// Tests for the MCP server JSON-RPC transport layer.
/// </summary>
[TestClass]
public class McpServerTests
{
    [TestMethod]
    public async Task McpServer_tools_list_returns_all_5_tools()
    {
        var server = CreateServer();
        var request = """{"jsonrpc":"2.0","method":"tools/list","id":1}""";

        var responseJson = await server.ProcessRequestAsync(request);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        var result = response.GetProperty("result");
        var tools = result.GetProperty("tools");
        tools.GetArrayLength().Should().Be(5);

        var toolNames = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            toolNames.Add(tool.GetProperty("name").GetString()!);
        }

        toolNames.Should().BeEquivalentTo(new[]
        {
            "memory_query",
            "memory_status",
            "memory_record_expertise",
            "memory_sync",
            "memory_consolidate"
        });
    }

    [TestMethod]
    public async Task McpServer_tools_call_dispatches_to_handler()
    {
        var graphStore = new InMemoryMemoryGraphStore();
        await graphStore.CreateNodeAsync(new HypergraphNode
        {
            Id = "/src/ast/Ast.Core/HypergraphModels.cs::HypergraphNode",
            Name = "HypergraphNode",
            NodeType = "Class",
            Language = "C#",
            FilePath = "/src/ast/Ast.Core/HypergraphModels.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural"
            }
        });

        var server = CreateServer(graphStore);
        var request = """
            {
                "jsonrpc": "2.0",
                "method": "tools/call",
                "id": 2,
                "params": {
                    "name": "memory_query",
                    "arguments": {
                        "queryText": "HypergraphNode",
                        "tokenBudget": 4000
                    }
                }
            }
            """;

        var responseJson = await server.ProcessRequestAsync(request);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        var result = response.GetProperty("result");
        var content = result.GetProperty("content");
        content.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var toolResult = content[0].GetProperty("text").GetString()!;
        var toolResponse = JsonSerializer.Deserialize<JsonElement>(toolResult);
        toolResponse.GetProperty("contextMarkdown").GetString().Should().Contain("HypergraphNode");
    }

    [TestMethod]
    public async Task McpServer_initialize_returns_capabilities()
    {
        var server = CreateServer();
        var request = """{"jsonrpc":"2.0","method":"initialize","id":0}""";

        var responseJson = await server.ProcessRequestAsync(request);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        var result = response.GetProperty("result");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("ast-memory-mcp");
        result.GetProperty("protocolVersion").GetString().Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task McpServer_unknown_method_returns_error()
    {
        var server = CreateServer();
        var request = """{"jsonrpc":"2.0","method":"unknown/method","id":3}""";

        var responseJson = await server.ProcessRequestAsync(request);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    [TestMethod]
    public async Task McpServer_tools_call_unknown_tool_returns_error()
    {
        var server = CreateServer();
        var request = """
            {
                "jsonrpc": "2.0",
                "method": "tools/call",
                "id": 4,
                "params": {
                    "name": "nonexistent_tool",
                    "arguments": {}
                }
            }
            """;

        var responseJson = await server.ProcessRequestAsync(request);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        response.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
        response.GetProperty("error").GetProperty("message").GetString().Should().Contain("Unknown tool");
    }

    [TestMethod]
    public async Task McpServer_DI_registration_works()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryMcpToolsWithInMemoryBackend();

        var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();

        var request = """{"jsonrpc":"2.0","method":"tools/list","id":1}""";
        var responseJson = await server.ProcessRequestAsync(request);
        var response = JsonSerializer.Deserialize<JsonElement>(responseJson);

        var tools = response.GetProperty("result").GetProperty("tools");
        tools.GetArrayLength().Should().Be(5);
    }

    private static McpServer CreateServer(InMemoryMemoryGraphStore? graphStore = null)
    {
        var store = graphStore ?? new InMemoryMemoryGraphStore();
        var retrievalEngine = new InMemoryRetrievalEngine(store);
        var evolutionEngine = new InMemoryEvolutionEngine(store);
        var registration = new MemoryMcpToolRegistration();
        var handler = new MemoryMcpHandler(
            retrievalEngine,
            store,
            evolutionEngine,
            new Mock<IIncrementalSync>().Object,
            new Mock<ILogger<MemoryMcpHandler>>().Object);

        return new McpServer(
            registration,
            handler,
            new Mock<ILogger<McpServer>>().Object);
    }
}