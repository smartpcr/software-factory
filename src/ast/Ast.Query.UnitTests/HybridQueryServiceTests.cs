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
        mockGraphStore = new Mock<IGraphStore>();
        mockVectorStore = new Mock<IVectorStore>();
        mockEmbeddingGenerator = new Mock<IEmbeddingGenerator>();
        mockLogger = new Mock<ILogger<HybridQueryService>>();
        service = new HybridQueryService(
            mockGraphStore.Object,
            mockLogger.Object,
            mockVectorStore.Object,
            mockEmbeddingGenerator.Object);
    }

    [TestMethod]
    public void Constructor_WithNullGraphStore_ThrowsArgumentNullException()
    {
        var act = () => new HybridQueryService(null!, mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("graphStore");
    }

    [TestMethod]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        var act = () => new HybridQueryService(mockGraphStore.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [TestMethod]
    public void SemanticSearchAvailable_WithBothDependencies_ReturnsTrue()
    {
        service.SemanticSearchAvailable.Should().BeTrue();
    }

    [TestMethod]
    public void SemanticSearchAvailable_WithoutVectorStore_ReturnsFalse()
    {
        var svc = new HybridQueryService(mockGraphStore.Object, mockLogger.Object);
        svc.SemanticSearchAvailable.Should().BeFalse();
    }

    [TestMethod]
    public void Query_ReturnsCodeQueryBuilder()
    {
        var query = service.Query();
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<ICodeQuery>();
    }

    [TestMethod]
    public async Task HybridSearchAsync_WithNullSearchText_ThrowsArgumentException()
    {
        var act = () => service.HybridSearchAsync(null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task HybridSearchAsync_WithEmptySearchText_ThrowsArgumentException()
    {
        var act = () => service.HybridSearchAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task HybridSearchAsync_PerformsGraphSearch()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "TestHelper", "Class")
        };

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var options = new QueryOptions { UseGraphSearch = true, UseSemanticSearch = false };
        var result = await service.HybridSearchAsync("TestHelper", options);

        result.Should().NotBeNull();
        result.Nodes.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task HybridSearchAsync_PerformsSemanticSearch_WhenEnabled()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var searchResults = new List<VectorSearchResult>
        {
            new() { Id = "1", Score = 0.9f }
        };

        mockEmbeddingGenerator.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        mockVectorStore.Setup(x => x.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<float[]>(),
            It.IsAny<int>(),
            It.IsAny<VectorFilter?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(searchResults);

        mockGraphStore.Setup(x => x.GetNodeAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestNode("1", "TestClass", "Class"));

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode>());

        var options = new QueryOptions { UseGraphSearch = false, UseSemanticSearch = true, MinScore = 0.5f };
        var result = await service.HybridSearchAsync("test query", options);

        result.Should().NotBeNull();
        result.SemanticMatches.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task HybridSearchAsync_DeduplicatesResults()
    {
        var node = CreateTestNode("1", "TestClass", "Class");

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { node });

        mockEmbeddingGenerator.Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f });

        mockVectorStore.Setup(x => x.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<float[]>(),
            It.IsAny<int>(),
            It.IsAny<VectorFilter?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult> { new() { Id = "1", Score = 0.9f } });

        mockGraphStore.Setup(x => x.GetNodeAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        var options = new QueryOptions { UseGraphSearch = true, UseSemanticSearch = true };
        var result = await service.HybridSearchAsync("TestClass", options);

        // Should have 1 node (deduplicated) + 1 semantic match
        result.Nodes.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task FindSimilarAsync_WithoutSemanticSearch_UsesGraphBasedSimilarity()
    {
        var node = CreateTestNode("1", "TestClass", "Class");
        var similarNodes = new List<HypergraphNode>
        {
            CreateTestNode("2", "OtherClass", "Class"),
            CreateTestNode("3", "AnotherClass", "Class")
        };

        var serviceWithoutSemantic = new HybridQueryService(mockGraphStore.Object, mockLogger.Object);

        mockGraphStore.Setup(x => x.GetNodeAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes != null && f.NodeTypes.Contains("Class")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(similarNodes);

        var result = await serviceWithoutSemantic.FindSimilarAsync("1", 5);

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FindSimilarAsync_WithNodeNotFound_ReturnsEmpty()
    {
        mockGraphStore.Setup(x => x.GetNodeAsync("unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync((HypergraphNode?)null);

        var result = await service.FindSimilarAsync("unknown");

        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExploreAsync_ReturnsNodesAtEachDepth()
    {
        var startNode = CreateTestNode("start", "StartClass", "Class");
        var depth1Nodes = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("d1n1", "Depth1Node1", "Method"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation }
        };

        mockGraphStore.Setup(x => x.GetNodeAsync("start", It.IsAny<CancellationToken>()))
            .ReturnsAsync(startNode);

        mockGraphStore.Setup(x => x.GetCalleesAsync("start", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(depth1Nodes);

        mockGraphStore.Setup(x => x.GetCallersAsync("start", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>());

        mockGraphStore.Setup(x => x.GetCalleesAsync("d1n1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>());

        mockGraphStore.Setup(x => x.GetCallersAsync("d1n1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>());

        var result = await service.ExploreAsync("start", depth: 2);

        result.Should().ContainKey(0);
        result[0].Should().HaveCount(1);
        result[0][0].Id.Should().Be("start");
    }

    [TestMethod]
    public async Task ExploreAsync_WithInvalidNode_ReturnsEmpty()
    {
        mockGraphStore.Setup(x => x.GetNodeAsync("unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync((HypergraphNode?)null);

        var result = await service.ExploreAsync("unknown");

        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnswerQuestionAsync_WithoutSemanticSearch_FallsBackToGraphSearch()
    {
        var serviceWithoutSemantic = new HybridQueryService(mockGraphStore.Object, mockLogger.Object);

        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "AuthenticationService", "Class")
        };

        mockGraphStore.Setup(x => x.GetNodesAsync(
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
