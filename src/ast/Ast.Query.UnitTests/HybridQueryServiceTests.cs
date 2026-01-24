// -----------------------------------------------------------------------
// <copyright file="HybridQueryServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query.UnitTests;

using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Moq;

[TestClass]
public class HybridQueryServiceTests
{
    private Mock<IGraphStore> mockGraphStore = null!;
    private Mock<IVectorStore> mockVectorStore = null!;
    private Mock<IEmbeddingGenerator> mockEmbeddingGenerator = null!;
    private Mock<ILogger<HybridQueryService>> mockLogger = null!;
    private HybridQueryService service = null!;

    [TestInitialize]
    public void Setup()
    {
        this.mockGraphStore = new Mock<IGraphStore>();
        this.mockVectorStore = new Mock<IVectorStore>();
        this.mockEmbeddingGenerator = new Mock<IEmbeddingGenerator>();
        this.mockLogger = new Mock<ILogger<HybridQueryService>>();
        this.service = new HybridQueryService(
            this.mockGraphStore.Object,
            this.mockLogger.Object,
            this.mockVectorStore.Object,
            this.mockEmbeddingGenerator.Object);
    }

    [TestMethod]
    public void Constructor_WithNullGraphStore_ThrowsArgumentNullException()
    {
        var act = () => new HybridQueryService(null!, this.mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("graphStore");
    }

    [TestMethod]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        var act = () => new HybridQueryService(this.mockGraphStore.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [TestMethod]
    public void SemanticSearchAvailable_WithBothDependencies_ReturnsTrue()
    {
        this.service.SemanticSearchAvailable.Should().BeTrue();
    }

    [TestMethod]
    public void SemanticSearchAvailable_WithoutVectorStore_ReturnsFalse()
    {
        var svc = new HybridQueryService(this.mockGraphStore.Object, this.mockLogger.Object);
        svc.SemanticSearchAvailable.Should().BeFalse();
    }

    [TestMethod]
    public void Query_ReturnsCodeQueryBuilder()
    {
        var query = this.service.Query();
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<ICodeQuery>();
    }

    [TestMethod]
    public async Task HybridSearchAsync_WithNullSearchText_ThrowsArgumentExceptionAsync()
    {
        var act = () => this.service.HybridSearchAsync(null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task HybridSearchAsync_WithEmptySearchText_ThrowsArgumentExceptionAsync()
    {
        var act = () => this.service.HybridSearchAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task HybridSearchAsync_PerformsGraphSearchAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "TestHelper", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var options = new QueryOptions { UseGraphSearch = true, UseSemanticSearch = false };
        var result = await this.service.HybridSearchAsync("TestHelper", options);

        result.Should().NotBeNull();
        result.Nodes.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task HybridSearchAsync_PerformsSemanticSearch_WhenEnabledAsync()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var searchResults = new List<VectorSearchResult>
        {
            new() { Id = "1", Score = 0.9f }
        };

        this.mockEmbeddingGenerator.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        this.mockVectorStore.Setup(x => x.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<float[]>(),
            It.IsAny<int>(),
            It.IsAny<VectorFilter?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(searchResults);

        this.mockGraphStore.Setup(x => x.GetNodeAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestNode("1", "TestClass", "Class"));

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode>());

        var options = new QueryOptions { UseGraphSearch = false, UseSemanticSearch = true, MinScore = 0.5f };
        var result = await this.service.HybridSearchAsync("test query", options);

        result.Should().NotBeNull();
        result.SemanticMatches.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task HybridSearchAsync_DeduplicatesResultsAsync()
    {
        var node = CreateTestNode("1", "TestClass", "Class");

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { node });

        this.mockEmbeddingGenerator.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0.1f]);

        this.mockVectorStore.Setup(x => x.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<float[]>(),
            It.IsAny<int>(),
            It.IsAny<VectorFilter?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult> { new() { Id = "1", Score = 0.9f } });

        this.mockGraphStore.Setup(x => x.GetNodeAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        var options = new QueryOptions { UseGraphSearch = true, UseSemanticSearch = true };
        var result = await this.service.HybridSearchAsync("TestClass", options);

        // Should have 1 node (deduplicated) + 1 semantic match
        result.Nodes.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task FindSimilarAsync_WithoutSemanticSearch_UsesGraphBasedSimilarityAsync()
    {
        var node = CreateTestNode("1", "TestClass", "Class");
        var similarNodes = new List<HypergraphNode>
        {
            CreateTestNode("2", "OtherClass", "Class"),
            CreateTestNode("3", "AnotherClass", "Class")
        };

        var serviceWithoutSemantic = new HybridQueryService(this.mockGraphStore.Object, this.mockLogger.Object);

        this.mockGraphStore.Setup(x => x.GetNodeAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes != null && f.NodeTypes.Contains("Class")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(similarNodes);

        var result = await serviceWithoutSemantic.FindSimilarAsync("1", 5);

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FindSimilarAsync_WithNodeNotFound_ReturnsEmptyAsync()
    {
        this.mockGraphStore.Setup(x => x.GetNodeAsync("unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync((HypergraphNode?)null);

        var result = await this.service.FindSimilarAsync("unknown");

        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExploreAsync_ReturnsNodesAtEachDepthAsync()
    {
        var startNode = CreateTestNode("start", "StartClass", "Class");
        var depth1Nodes = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("d1n1", "Depth1Node1", "Method"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation }
        };

        this.mockGraphStore.Setup(x => x.GetNodeAsync("start", It.IsAny<CancellationToken>()))
            .ReturnsAsync(startNode);

        this.mockGraphStore.Setup(x => x.GetCalleesAsync("start", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(depth1Nodes);

        this.mockGraphStore.Setup(x => x.GetCallersAsync("start", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>());

        this.mockGraphStore.Setup(x => x.GetCalleesAsync("d1n1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>());

        this.mockGraphStore.Setup(x => x.GetCallersAsync("d1n1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>());

        var result = await this.service.ExploreAsync("start", depth: 2);

        result.Should().ContainKey(0);
        result[0].Should().HaveCount(1);
        result[0][0].Id.Should().Be("start");
    }

    [TestMethod]
    public async Task ExploreAsync_WithInvalidNode_ReturnsEmptyAsync()
    {
        this.mockGraphStore.Setup(x => x.GetNodeAsync("unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync((HypergraphNode?)null);

        var result = await this.service.ExploreAsync("unknown");

        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnswerQuestionAsync_WithoutSemanticSearch_FallsBackToGraphSearchAsync()
    {
        var serviceWithoutSemantic = new HybridQueryService(this.mockGraphStore.Object, this.mockLogger.Object);

        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "AuthenticationService", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await serviceWithoutSemantic.AnswerQuestionAsync("How does authentication work?");

        result.Should().HaveCount(1);
        result[0].Score.Should().Be(1.0f);
    }

    private static HypergraphNode CreateTestNode(
        string id,
        string name,
        string nodeType,
        string language = "C#")
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = language,
            FilePath = "/test/file.cs",
            LineStart = 1,
            LineEnd = 10
        };
    }
}