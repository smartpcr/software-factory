// -----------------------------------------------------------------------
// <copyright file="CodeVectorServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb.UnitTests;

using Ast.Core;
using Ast.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

[TestClass]
public class CodeVectorServiceTests
{
    private Mock<IVectorStore> mockVectorStore = null!;
    private Mock<IEmbeddingGenerator> mockEmbeddingGenerator = null!;
    private VectorStoreOptions options = null!;
    private CodeVectorService service = null!;

    [TestInitialize]
    public void Setup()
    {
        mockVectorStore = new Mock<IVectorStore>();
        mockEmbeddingGenerator = new Mock<IEmbeddingGenerator>();
        options = new VectorStoreOptions
        {
            NodesCollection = "test_nodes",
            EdgesCollection = "test_edges"
        };

        mockEmbeddingGenerator.Setup(e => e.Dimensions).Returns(768);
        mockEmbeddingGenerator.Setup(e => e.ModelName).Returns("test-model");

        service = new CodeVectorService(
            mockVectorStore.Object,
            mockEmbeddingGenerator.Object,
            Options.Create(options),
            NullLogger<CodeVectorService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        service?.Dispose();
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesCollections_WhenNotExist()
    {
        // Arrange
        mockVectorStore.Setup(v => v.CollectionExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        await service.InitializeAsync();

        // Assert
        mockVectorStore.Verify(
            v => v.CreateCollectionAsync("test_nodes", 768, It.IsAny<CancellationToken>()),
            Times.Once);
        mockVectorStore.Verify(
            v => v.CreateCollectionAsync("test_edges", 768, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task InitializeAsync_SkipsCreation_WhenCollectionsExist()
    {
        // Arrange
        mockVectorStore.Setup(v => v.CollectionExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await service.InitializeAsync();

        // Assert
        mockVectorStore.Verify(
            v => v.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task IndexNodesAsync_IndexesAllNodes()
    {
        // Arrange
        var data = CreateTestHypergraphData();

        mockEmbeddingGenerator
            .Setup(e => e.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[768]).ToList());

        // Act
        var count = await service.IndexNodesAsync(data);

        // Assert
        count.Should().Be(2);
        mockVectorStore.Verify(
            v => v.UpsertAsync("test_nodes", It.Is<IEnumerable<VectorPoint>>(p => p.Count() == 2), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task IndexNodesAsync_ReturnsZero_WhenNoNodes()
    {
        // Arrange
        var data = new HypergraphData();

        // Act
        var count = await service.IndexNodesAsync(data);

        // Assert
        count.Should().Be(0);
        mockVectorStore.Verify(
            v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task IndexEdgesAsync_IndexesAllEdges()
    {
        // Arrange
        var data = CreateTestHypergraphData();

        mockEmbeddingGenerator
            .Setup(e => e.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[768]).ToList());

        // Act
        var count = await service.IndexEdgesAsync(data);

        // Assert
        count.Should().Be(1);
        mockVectorStore.Verify(
            v => v.UpsertAsync("test_edges", It.Is<IEnumerable<VectorPoint>>(p => p.Count() == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SearchNodesAsync_ReturnsResults()
    {
        // Arrange
        var queryVector = new float[768];
        mockEmbeddingGenerator
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryVector);

        mockVectorStore
            .Setup(v => v.SearchAsync(
                "test_nodes",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new VectorSearchResult
                {
                    Id = "node1",
                    Score = 0.95f,
                    Payload = new Dictionary<string, object>
                    {
                        ["name"] = "TestClass",
                        ["node_type"] = "Class",
                        ["language"] = "C#",
                        ["file_path"] = "test.cs",
                        ["line_start"] = 1L,
                        ["line_end"] = 10L
                    }
                }
            });

        // Act
        var results = await service.SearchNodesAsync("find test class", limit: 10);

        // Assert
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("node1");
        results[0].Score.Should().BeApproximately(0.95f, 0.001f);
        results[0].Name.Should().Be("TestClass");
        results[0].NodeType.Should().Be("Class");
        results[0].Language.Should().Be("C#");
    }

    [TestMethod]
    public async Task SearchNodesAsync_AppliesFilter()
    {
        // Arrange
        var queryVector = new float[768];
        mockEmbeddingGenerator
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryVector);

        mockVectorStore
            .Setup(v => v.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.Is<VectorFilter>(f => f != null && f.Must.Any(c => c.Field == "language")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>());

        var filter = new CodeSearchFilter { Language = "C#" };

        // Act
        await service.SearchNodesAsync("test", filter: filter);

        // Assert
        mockVectorStore.Verify(
            v => v.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.Is<VectorFilter>(f => f != null && f.Must.Any(c => c.Field == "language" && c.Value.ToString() == "C#")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SearchEdgesAsync_ReturnsResults()
    {
        // Arrange
        var queryVector = new float[768];
        mockEmbeddingGenerator
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryVector);

        mockVectorStore
            .Setup(v => v.SearchAsync(
                "test_edges",
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<VectorFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new VectorSearchResult
                {
                    Id = "edge1",
                    Score = 0.88f,
                    Payload = new Dictionary<string, object>
                    {
                        ["edge_type"] = "MethodInvocation",
                        ["cardinality"] = 2L,
                        ["is_cross_language"] = false
                    }
                }
            });

        // Act
        var results = await service.SearchEdgesAsync("method call");

        // Assert
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("edge1");
        results[0].EdgeType.Should().Be(HyperedgeType.MethodInvocation);
        results[0].Cardinality.Should().Be(2);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_ReturnsCorrectCounts()
    {
        // Arrange
        mockVectorStore
            .Setup(v => v.CountAsync("test_nodes", It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);
        mockVectorStore
            .Setup(v => v.CountAsync("test_edges", It.IsAny<CancellationToken>()))
            .ReturnsAsync(50);

        // Act
        var stats = await service.GetStatisticsAsync();

        // Assert
        stats.NodesCollection.Should().Be("test_nodes");
        stats.EdgesCollection.Should().Be("test_edges");
        stats.NodeCount.Should().Be(100);
        stats.EdgeCount.Should().Be(50);
        stats.VectorDimensions.Should().Be(768);
        stats.EmbeddingModel.Should().Be("test-model");
    }

    [TestMethod]
    public async Task ClearAsync_DeletesAndRecreatesCollections()
    {
        // Arrange
        var callCount = 0;
        mockVectorStore.Setup(v => v.CollectionExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // First 2 calls (for delete check) return true, next 2 (for create check) return false
                callCount++;
                return callCount <= 2;
            });

        // Act
        await service.ClearAsync();

        // Assert
        mockVectorStore.Verify(
            v => v.DeleteCollectionAsync("test_nodes", It.IsAny<CancellationToken>()),
            Times.Once);
        mockVectorStore.Verify(
            v => v.DeleteCollectionAsync("test_edges", It.IsAny<CancellationToken>()),
            Times.Once);
        mockVectorStore.Verify(
            v => v.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [TestMethod]
    public void Dispose_DisposesUnderlyingServices()
    {
        // Act
        service.Dispose();

        // Assert
        mockVectorStore.Verify(v => v.Dispose(), Times.Once);
        mockEmbeddingGenerator.Verify(e => e.Dispose(), Times.Once);
    }

    [TestMethod]
    public async Task DisposedService_ThrowsObjectDisposedException()
    {
        // Arrange
        service.Dispose();

        // Act & Assert
        var act = () => service.InitializeAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static HypergraphData CreateTestHypergraphData()
    {
        return new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                ["node1"] = new HypergraphNode
                {
                    Id = "node1",
                    Name = "TestClass",
                    NodeType = "Class",
                    Language = "C#",
                    FilePath = "test.cs",
                    LineStart = 1,
                    LineEnd = 10
                },
                ["node2"] = new HypergraphNode
                {
                    Id = "node2",
                    Name = "TestMethod",
                    NodeType = "Method",
                    Language = "C#",
                    FilePath = "test.cs",
                    LineStart = 5,
                    LineEnd = 8
                }
            },
            Hyperedges = new List<Hyperedge>
            {
                new Hyperedge
                {
                    Id = "edge1",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = new List<string> { "node1", "node2" },
                    Roles = new Dictionary<string, string>
                    {
                        ["node1"] = "caller",
                        ["node2"] = "callee"
                    }
                }
            }
        };
    }
}
