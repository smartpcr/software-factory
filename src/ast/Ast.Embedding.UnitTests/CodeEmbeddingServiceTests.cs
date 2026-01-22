// -----------------------------------------------------------------------
// <copyright file="CodeEmbeddingServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding.UnitTests;

using Ast.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

[TestClass]
public class CodeEmbeddingServiceTests
{
    [TestMethod]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        // Arrange
        float[] a = [1.0f, 2.0f, 3.0f];
        float[] b = [1.0f, 2.0f, 3.0f];

        // Act
        var similarity = CodeEmbeddingService.CosineSimilarity(a, b);

        // Assert
        similarity.Should().BeApproximately(1.0f, 0.0001f);
    }

    [TestMethod]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        // Arrange
        float[] a = [1.0f, 0.0f, 0.0f];
        float[] b = [0.0f, 1.0f, 0.0f];

        // Act
        var similarity = CodeEmbeddingService.CosineSimilarity(a, b);

        // Assert
        similarity.Should().BeApproximately(0.0f, 0.0001f);
    }

    [TestMethod]
    public void CosineSimilarity_OppositeVectors_ReturnsMinusOne()
    {
        // Arrange
        float[] a = [1.0f, 2.0f, 3.0f];
        float[] b = [-1.0f, -2.0f, -3.0f];

        // Act
        var similarity = CodeEmbeddingService.CosineSimilarity(a, b);

        // Assert
        similarity.Should().BeApproximately(-1.0f, 0.0001f);
    }

    [TestMethod]
    public void CosineSimilarity_DifferentLengths_ThrowsException()
    {
        // Arrange
        float[] a = [1.0f, 2.0f, 3.0f];
        float[] b = [1.0f, 2.0f];

        // Act
        var act = () => CodeEmbeddingService.CosineSimilarity(a, b);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*same dimension*");
    }

    [TestMethod]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        // Arrange
        float[] a = [0.0f, 0.0f, 0.0f];
        float[] b = [1.0f, 2.0f, 3.0f];

        // Act
        var similarity = CodeEmbeddingService.CosineSimilarity(a, b);

        // Assert
        similarity.Should().Be(0.0f);
    }

    [TestMethod]
    public void FindSimilar_ReturnsTopK()
    {
        // Arrange
        float[] query = [1.0f, 0.0f, 0.0f];
        var candidates = new List<CodeEmbedding>
        {
            CreateCodeEmbedding("a", [1.0f, 0.0f, 0.0f]),   // similarity = 1.0
            CreateCodeEmbedding("b", [0.0f, 1.0f, 0.0f]),   // similarity = 0.0
            CreateCodeEmbedding("c", [0.5f, 0.5f, 0.0f]),   // similarity = ~0.707
            CreateCodeEmbedding("d", [0.8f, 0.2f, 0.0f]),   // similarity = ~0.97
            CreateCodeEmbedding("e", [-1.0f, 0.0f, 0.0f]),  // similarity = -1.0
        };

        // Act
        var results = CodeEmbeddingService.FindSimilar(query, candidates, topK: 3);

        // Assert
        results.Should().HaveCount(3);
        results[0].Embedding.Name.Should().Be("a");
        results[0].Similarity.Should().BeApproximately(1.0f, 0.01f);
        results[1].Embedding.Name.Should().Be("d");
        results[2].Embedding.Name.Should().Be("c");
    }

    [TestMethod]
    public void FindSimilar_EmptyCandidates_ReturnsEmpty()
    {
        // Arrange
        float[] query = [1.0f, 0.0f, 0.0f];
        var candidates = new List<CodeEmbedding>();

        // Act
        var results = CodeEmbeddingService.FindSimilar(query, candidates, topK: 5);

        // Assert
        results.Should().BeEmpty();
    }

    [TestMethod]
    public void FindSimilar_FewerThanTopK_ReturnsAll()
    {
        // Arrange
        float[] query = [1.0f, 0.0f, 0.0f];
        var candidates = new List<CodeEmbedding>
        {
            CreateCodeEmbedding("a", [1.0f, 0.0f, 0.0f]),
            CreateCodeEmbedding("b", [0.5f, 0.5f, 0.0f]),
        };

        // Act
        var results = CodeEmbeddingService.FindSimilar(query, candidates, topK: 10);

        // Assert
        results.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GenerateQueryEmbeddingAsync_CallsGenerator()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var expectedEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        mockGenerator
            .Setup(g => g.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEmbedding);

        var service = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        // Act
        var result = await service.GenerateQueryEmbeddingAsync("test query");

        // Assert
        result.Should().BeEquivalentTo(expectedEmbedding);
        mockGenerator.Verify(
            g => g.GenerateEmbeddingAsync("test query", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task GenerateNodeEmbeddingsAsync_ProcessesAllNodes()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList());
        mockGenerator.Setup(g => g.ModelName).Returns("test-model");

        var service = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var data = new HypergraphData
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
            }
        };

        // Act
        var results = await service.GenerateNodeEmbeddingsAsync(data);

        // Assert
        results.Should().HaveCount(2);
        results.Should().Contain(e => e.NodeId == "node1" && e.Name == "TestClass");
        results.Should().Contain(e => e.NodeId == "node2" && e.Name == "TestMethod");
    }

    [TestMethod]
    public async Task GenerateEdgeEmbeddingsAsync_ProcessesAllEdges()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList());
        mockGenerator.Setup(g => g.ModelName).Returns("test-model");

        var service = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        var data = new HypergraphData
        {
            Hyperedges =
            [
                new Hyperedge
                {
                    Id = "edge1",
                    EdgeType = HyperedgeType.MethodInvocation,
                    NodeIds = ["node1", "node2"],
                    Roles = new Dictionary<string, string>
                    {
                        ["node1"] = "caller",
                        ["node2"] = "callee"
                    }
                }
            ]
        };

        // Act
        var results = await service.GenerateEdgeEmbeddingsAsync(data);

        // Assert
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("edge1");
        results[0].Metadata.Should().ContainKey("edge_type");
        results[0].Metadata!["edge_type"].Should().Be("MethodInvocation");
    }

    [TestMethod]
    public void Dispose_DisposesGenerator()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var service = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        // Act
        service.Dispose();

        // Assert
        mockGenerator.Verify(g => g.Dispose(), Times.Once);
    }

    [TestMethod]
    public async Task DisposedService_ThrowsObjectDisposedException()
    {
        // Arrange
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        var service = new CodeEmbeddingService(
            mockGenerator.Object,
            NullLogger<CodeEmbeddingService>.Instance);

        service.Dispose();

        // Act & Assert
        var act = () => service.GenerateQueryEmbeddingAsync("test");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static CodeEmbedding CreateCodeEmbedding(string name, float[] vector)
    {
        return new CodeEmbedding
        {
            NodeId = $"node-{name}",
            FilePath = "test.cs",
            ElementType = "Class",
            Name = name,
            SourceText = $"class {name} {{ }}",
            Vector = vector,
            Language = "C#"
        };
    }
}
