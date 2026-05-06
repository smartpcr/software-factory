// -----------------------------------------------------------------------
// <copyright file="DefaultSemanticIndexerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Sync;

using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using Ast.VectorDb;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

/// <summary>
/// Tests for <see cref="DefaultSemanticIndexer"/> — the production
/// SemanticIndexer that generates embeddings via IEmbeddingGenerator,
/// stores them in IVectorStore, and manages SIMILAR_TO edges in the graph.
/// </summary>
[TestClass]
public class DefaultSemanticIndexerTests
{
    private InMemoryMemoryGraphStore store = null!;
    private Mock<IEmbeddingGenerator> mockEmbedding = null!;
    private Mock<IVectorStore> mockVectorStore = null!;
    private DefaultSemanticIndexer indexer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryMemoryGraphStore();
        this.mockEmbedding = new Mock<IEmbeddingGenerator>();
        this.mockVectorStore = new Mock<IVectorStore>();

        // Default: embedding generator available
        this.mockEmbedding.Setup(e => e.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        this.mockEmbedding.Setup(e => e.Dimensions).Returns(3);
        this.mockEmbedding.Setup(e => e.ModelName).Returns("test-model");
        this.mockVectorStore.Setup(v => v.CollectionExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        this.mockVectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = NullLogger<DefaultSemanticIndexer>.Instance;
        this.indexer = new DefaultSemanticIndexer(
            this.store, this.mockEmbedding.Object, this.mockVectorStore.Object, options, logger);
    }

    [TestMethod]
    public async Task IndexNodesAsync_EmptyList_ReturnsZero()
    {
        var result = await this.indexer.IndexNodesAsync(Array.Empty<HypergraphNode>());
        result.Should().Be(0);
    }

    [TestMethod]
    public async Task IndexNodesAsync_EmbeddingUnavailable_MarksAsPending()
    {
        this.mockEmbedding.Setup(e => e.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var node = CreateNode("n1", "src/A.cs", embedding: null);
        var result = await this.indexer.IndexNodesAsync(new[] { node });

        result.Should().Be(0, "nodes without embeddings should not create SIMILAR_TO edges");
        node.Metadata[MemoryMetadataKeys.EmbeddingStatus].Should().Be("pending");
    }

    [TestMethod]
    public async Task IndexNodesAsync_GeneratesEmbeddings_UpsertsToVectorStore()
    {
        var generatedEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        this.mockEmbedding
            .Setup(e => e.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { generatedEmbedding });

        var node = CreateNode("n1", "src/A.cs", embedding: null);
        node.Metadata[MemoryMetadataKeys.SourceCode] = "public void Foo() {}";

        await this.indexer.IndexNodesAsync(new[] { node });

        // Verify embedding was stored in metadata
        node.Metadata[MemoryMetadataKeys.Embedding].Should().BeEquivalentTo(generatedEmbedding);
        node.Metadata[MemoryMetadataKeys.EmbeddingModel].Should().Be("test-model");
        node.Metadata[MemoryMetadataKeys.EmbeddingStatus].Should().Be("generated");

        // Verify IVectorStore.UpsertAsync was called
        this.mockVectorStore.Verify(
            v => v.UpsertAsync("memory_embeddings", It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [TestMethod]
    public async Task IndexNodesAsync_SimilarNodes_CreatesSimilarToEdges()
    {
        // Arrange: create a candidate node already in the store with an embedding
        var embedding1 = new float[] { 1.0f, 0.0f, 0.0f };
        var embedding2 = new float[] { 0.99f, 0.1f, 0.0f }; // very similar to embedding1

        // Mock batch embedding to return embedding1 for the changed node
        this.mockEmbedding
            .Setup(e => e.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { embedding1 });

        var existingNode = CreateNode("existing1", "src/A.cs", embedding: embedding2);
        existingNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
        await this.store.CreateNodeAsync(existingNode);

        var changedNode = CreateNode("changed1", "src/B.cs", embedding: embedding1);
        changedNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";

        // Act
        var result = await this.indexer.IndexNodesAsync(new[] { changedNode });

        // Assert
        result.Should().BeGreaterThan(0, "similar nodes should create SIMILAR_TO edges");

        var edges = this.store.AllEdges.Values
            .Where(e => e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr)
                && sr?.ToString() == "SIMILAR_TO")
            .ToList();

        edges.Should().NotBeEmpty("a SIMILAR_TO edge should exist between similar nodes");
        edges[0].NodeIds.Should().Contain("changed1");
        edges[0].NodeIds.Should().Contain("existing1");
    }

    [TestMethod]
    public async Task IndexNodesAsync_DissimilarNodes_NoEdgesCreated()
    {
        // Arrange: create nodes with very different embeddings
        var embedding1 = new float[] { 1.0f, 0.0f, 0.0f };
        var embedding2 = new float[] { 0.0f, 0.0f, 1.0f }; // orthogonal

        // Mock batch embedding to return embedding1 for the changed node
        this.mockEmbedding
            .Setup(e => e.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { embedding1 });

        var existingNode = CreateNode("existing1", "src/A.cs", embedding: embedding2);
        existingNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
        await this.store.CreateNodeAsync(existingNode);

        var changedNode = CreateNode("changed1", "src/B.cs", embedding: embedding1);
        changedNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";

        // Act
        var result = await this.indexer.IndexNodesAsync(new[] { changedNode });

        // Assert
        result.Should().Be(0, "dissimilar nodes should not create SIMILAR_TO edges");
    }

    [TestMethod]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1.0f, 2.0f, 3.0f };
        var sim = DefaultSemanticIndexer.CosineSimilarity(v, v);
        sim.Should().BeApproximately(1.0, 0.001);
    }

    [TestMethod]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f };
        var sim = DefaultSemanticIndexer.CosineSimilarity(a, b);
        sim.Should().BeApproximately(0.0, 0.001);
    }

    [TestMethod]
    public async Task IndexNodesAsync_FallsBackToVectorStore_ForCandidateEmbeddings()
    {
        // Arrange: a candidate node in the store WITHOUT an in-memory embedding.
        // The vector store has a persisted embedding for that node from a prior session.
        var changedEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var storedEmbedding = new float[] { 0.99f, 0.1f, 0.0f }; // similar

        this.mockEmbedding
            .Setup(e => e.GenerateBatchEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { changedEmbedding });

        // Candidate node has NO embedding in metadata (simulates cross-session scenario)
        var candidateNode = CreateNode("candidate1", "src/A.cs", embedding: null);
        candidateNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";
        await this.store.CreateNodeAsync(candidateNode);

        // IVectorStore returns the embedding for the candidate
        this.mockVectorStore
            .Setup(v => v.GetAsync("memory_embeddings", "candidate1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorPoint { Id = "candidate1", Vector = storedEmbedding });

        var changedNode = CreateNode("changed1", "src/B.cs", embedding: changedEmbedding);
        changedNode.Metadata[MemoryMetadataKeys.MemoryLayer] = "structural";

        // Act
        var result = await this.indexer.IndexNodesAsync(new[] { changedNode });

        // Assert: SIMILAR_TO edge should be created using the vector-store-retrieved embedding
        result.Should().BeGreaterThan(0, "vector store fallback should enable similarity comparison");
        this.mockVectorStore.Verify(
            v => v.GetAsync("memory_embeddings", "candidate1", It.IsAny<CancellationToken>()),
            Times.Once(),
            "should retrieve embedding from vector store when not in node metadata");
    }

    [TestMethod]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var sim = DefaultSemanticIndexer.CosineSimilarity(Array.Empty<float>(), Array.Empty<float>());
        sim.Should().Be(0.0);
    }

    private static HypergraphNode CreateNode(string id, string filePath, float[]? embedding)
    {
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = $"hash_{id}",
            [MemoryMetadataKeys.SinceCommit] = "commit0",
            [MemoryMetadataKeys.SinceIndex] = 1,
        };

        if (embedding is not null)
        {
            metadata[MemoryMetadataKeys.Embedding] = embedding;
        }

        return new HypergraphNode
        {
            Id = id,
            Name = id,
            NodeType = "Method",
            Language = "C#",
            FilePath = filePath,
            Metadata = metadata,
        };
    }
}
