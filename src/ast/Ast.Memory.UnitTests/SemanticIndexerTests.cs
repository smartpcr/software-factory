// -----------------------------------------------------------------------
// <copyright file="SemanticIndexerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Extraction;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Storage;
using Ast.VectorDb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Unit tests for <see cref="SemanticIndexer"/> — Stage 2.3 of the extraction pipeline.
/// Covers the three required test scenarios from implementation-plan.md:
/// 1. Embedding generation (10 nodes → Metadata["Embedding"] of dim 768)
/// 2. SIMILAR_TO edge threshold (cosine ≥ 0.85 → edge; cosine &lt; 0.85 → no edge)
/// 3. Graceful degradation (unavailable generator → log warning, skip, no throw)
/// </summary>
[TestClass]
public class SemanticIndexerTests
{
    private const int DefaultDimension = 768;

    #region Scenario 1: Embedding generation

    [TestMethod]
    public async Task IndexAsync_GivenTenCodeNodes_EachNodeHasNonNullEmbeddingOfDimension768()
    {
        // Arrange: 10 code nodes and a mock generator returning 768-dim vectors
        const int nodeCount = 10;
        var nodes = CreateTestNodes(nodeCount);
        var embeddings = CreateRandomEmbeddings(nodeCount, DefaultDimension);

        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockGenerator.Setup(g => g.Dimensions).Returns(DefaultDimension);
        mockGenerator.Setup(g => g.ModelName).Returns("nomic-embed-text");
        mockGenerator.Setup(g => g.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embeddings);

        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, _) = await indexer.IndexAsync(nodes);

        // Assert
        result.EmbeddingsGenerated.Should().Be(nodeCount);
        result.Skipped.Should().BeFalse();

        foreach (var node in nodes)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.Embedding,
                because: "each processed node must have an embedding stored in metadata");
            var embedding = node.Metadata[MemoryMetadataKeys.Embedding] as float[];
            embedding.Should().NotBeNull(
                because: "the embedding value must be a non-null float array");
            embedding!.Length.Should().Be(DefaultDimension,
                because: "embedding dimension must match the generator's dimension (768)");
        }
    }

    [TestMethod]
    public async Task IndexAsync_GivenTenCodeNodes_EmbeddingModelStoredInMetadata()
    {
        // Arrange
        const int nodeCount = 10;
        var nodes = CreateTestNodes(nodeCount);
        var embeddings = CreateRandomEmbeddings(nodeCount, DefaultDimension);

        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockGenerator.Setup(g => g.Dimensions).Returns(DefaultDimension);
        mockGenerator.Setup(g => g.ModelName).Returns("nomic-embed-text");
        mockGenerator.Setup(g => g.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embeddings);

        var options = Options.Create(new MemoryOptions());
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        await indexer.IndexAsync(nodes);

        // Assert
        foreach (var node in nodes)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.EmbeddingModel);
            node.Metadata[MemoryMetadataKeys.EmbeddingModel].Should().Be("nomic-embed-text");
        }
    }

    [TestMethod]
    public async Task IndexAsync_CallsGenerateBatchEmbeddingsAsync_NotSingleEmbeddingCalls()
    {
        // Arrange: verify batch API is used
        const int nodeCount = 5;
        var nodes = CreateTestNodes(nodeCount);
        var embeddings = CreateRandomEmbeddings(nodeCount, DefaultDimension);

        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockGenerator.Setup(g => g.Dimensions).Returns(DefaultDimension);
        mockGenerator.Setup(g => g.ModelName).Returns("nomic-embed-text");
        mockGenerator.Setup(g => g.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embeddings);

        var options = Options.Create(new MemoryOptions { EmbeddingBatchSize = 32 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        await indexer.IndexAsync(nodes);

        // Assert: batch API was called at least once
        mockGenerator.Verify(
            g => g.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    #endregion

    #region Scenario 2: SIMILAR_TO edge threshold

    [TestMethod]
    public async Task IndexAsync_TwoNodesWithCosineSimilarity090_CreatesSimilarToEdge()
    {
        // Arrange: two unit vectors with cosine similarity = 0.90
        var nodes = CreateTestNodes(2);
        var v1 = CreateUnitVectorAlongAxis0(DefaultDimension);
        var v2 = CreateVectorWithKnownCosine(DefaultDimension, 0.90f);
        var embeddings = new List<float[]> { v1, v2 };

        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);
        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: edge should exist because 0.90 ≥ 0.85
        result.SimilarToEdgesCreated.Should().Be(1);
        edges.Should().HaveCount(1);

        var edge = edges[0];
        edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
        edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SemanticRelation);
        edge.Metadata[MemoryMetadataKeys.SemanticRelation].Should().Be("SIMILAR_TO");
        edge.Metadata.Should().ContainKey(MemoryMetadataKeys.SimilarityScore);

        // Roles must contain node_a and node_b
        edge.Roles.Values.Should().Contain("node_a");
        edge.Roles.Values.Should().Contain("node_b");

        // Both nodes must be referenced
        edge.NodeIds.Should().HaveCount(2);
        edge.NodeIds.Should().Contain(nodes[0].Id);
        edge.NodeIds.Should().Contain(nodes[1].Id);

        // Verify similarity score is approximately 0.90
        var score = Convert.ToSingle(edge.Metadata[MemoryMetadataKeys.SimilarityScore]);
        score.Should().BeApproximately(0.90f, 0.01f);
    }

    [TestMethod]
    public async Task IndexAsync_TwoNodesWithCosineSimilarity080_NoSimilarToEdge()
    {
        // Arrange: two unit vectors with cosine similarity = 0.80
        var nodes = CreateTestNodes(2);
        var v1 = CreateUnitVectorAlongAxis0(DefaultDimension);
        var v2 = CreateVectorWithKnownCosine(DefaultDimension, 0.80f);
        var embeddings = new List<float[]> { v1, v2 };

        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);
        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: no edge because 0.80 < 0.85
        result.SimilarToEdgesCreated.Should().Be(0);
        edges.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IndexAsync_ThreeNodesOnePairAboveThreshold_OnlyOneEdge()
    {
        // Arrange: 3 nodes; pair (0,1) has similarity 0.90 (above), pair (0,2) and (1,2) below
        var nodes = CreateTestNodes(3);
        var v1 = CreateUnitVectorAlongAxis0(DefaultDimension);
        var v2 = CreateVectorWithKnownCosine(DefaultDimension, 0.90f);
        // v3 is orthogonal — cosine with v1 ≈ 0, cosine with v2 ≈ small
        var v3 = new float[DefaultDimension];
        v3[2] = 1.0f;
        var embeddings = new List<float[]> { v1, v2, v3 };

        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);
        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: only the (0,1) pair exceeds threshold
        result.SimilarToEdgesCreated.Should().Be(1);
        edges.Should().HaveCount(1);
        edges[0].NodeIds.Should().Contain(nodes[0].Id);
        edges[0].NodeIds.Should().Contain(nodes[1].Id);
    }

    [TestMethod]
    public async Task IndexAsync_SimilarToEdge_HasCorrectMemoryLayerMetadata()
    {
        // Arrange: two identical vectors → cosine = 1.0 (above any threshold)
        var nodes = CreateTestNodes(2);
        var vector = CreateRandomEmbedding(DefaultDimension);
        var embeddings = new List<float[]> { vector, (float[])vector.Clone() };

        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);
        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (_, edges) = await indexer.IndexAsync(nodes);

        // Assert: edge should have semantic layer metadata
        edges.Should().HaveCount(1);
        edges[0].Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer);
        edges[0].Metadata[MemoryMetadataKeys.MemoryLayer].Should().Be("semantic");
    }

    #endregion

    #region Scenario 3: Graceful degradation

    [TestMethod]
    public async Task IndexAsync_GeneratorIsNull_LogsWarningAndSkipsWithoutThrowing()
    {
        // Arrange: null embedding generator
        var nodes = CreateTestNodes(5);
        var options = Options.Create(new MemoryOptions());
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            null, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: skipped gracefully
        result.Skipped.Should().BeTrue();
        result.EmbeddingsGenerated.Should().Be(0);
        edges.Should().BeEmpty();

        // Verify a warning was logged about unavailability
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "A warning should be logged when IEmbeddingGenerator is null");
    }

    [TestMethod]
    public async Task IndexAsync_GeneratorReportsUnavailable_LogsWarningAndSkips()
    {
        // Arrange: generator exists but reports unavailable
        var nodes = CreateTestNodes(5);
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockGenerator.Setup(g => g.Dimensions).Returns(DefaultDimension);
        mockGenerator.Setup(g => g.ModelName).Returns("nomic-embed-text");

        var options = Options.Create(new MemoryOptions());
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: skipped gracefully
        result.Skipped.Should().BeTrue();
        result.EmbeddingsGenerated.Should().Be(0);
        edges.Should().BeEmpty();

        // Verify a warning was logged
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "A warning should be logged when IEmbeddingGenerator is unavailable");
    }

    [TestMethod]
    public async Task IndexAsync_GeneratorAvailabilityCheckThrows_LogsWarningAndSkips()
    {
        // Arrange: generator throws on availability check
        var nodes = CreateTestNodes(3);
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        mockGenerator.Setup(g => g.Dimensions).Returns(DefaultDimension);
        mockGenerator.Setup(g => g.ModelName).Returns("nomic-embed-text");

        var options = Options.Create(new MemoryOptions());
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: skipped gracefully, no exception propagated
        result.Skipped.Should().BeTrue();
        result.EmbeddingsGenerated.Should().Be(0);
        edges.Should().BeEmpty();

        // Verify a warning was logged
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "A warning should be logged when availability check throws");
    }

    [TestMethod]
    public async Task IndexAsync_GeneratorBatchCallThrows_LogsWarningAndSkips()
    {
        // Arrange: generator throws during batch embedding generation
        var nodes = CreateTestNodes(3);
        var mockGenerator = new Mock<IEmbeddingGenerator>();
        mockGenerator.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockGenerator.Setup(g => g.Dimensions).Returns(DefaultDimension);
        mockGenerator.Setup(g => g.ModelName).Returns("nomic-embed-text");
        mockGenerator.Setup(g => g.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Model not loaded"));

        var options = Options.Create(new MemoryOptions());
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, edges) = await indexer.IndexAsync(nodes);

        // Assert: skipped gracefully
        result.Skipped.Should().BeTrue();
        result.EmbeddingsGenerated.Should().Be(0);
        edges.Should().BeEmpty();

        // Verify a warning was logged
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "A warning should be logged when batch embedding generation fails");
    }

    #endregion

    #region Scenario 4: Backend selection and vector persistence

    [TestMethod]
    public async Task IndexAsync_BackendSqlite_WithSqlitePath_UsesSqliteVssVectorStore()
    {
        // Arrange: Backend=Sqlite, SqlitePath set to :memory: for testing
        var nodes = CreateTestNodes(3);
        var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var options = Options.Create(new MemoryOptions
        {
            Backend = MemoryBackend.Sqlite,
            SqlitePath = ":memory:",
            SimilarityThreshold = 0.85
        });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, _) = await indexer.IndexAsync(nodes);

        // Assert: SqliteVssVectorStore was used (architecture.md §3.2 requires sqlite-vss selection)
        result.VectorBackend.Should().Be("SqliteVss",
            because: "Backend=Sqlite with SqlitePath should resolve to SqliteVssVectorStore per architecture.md §3.2");
        result.VectorsStored.Should().Be(3,
            because: "all 3 nodes should have their vectors stored");
    }

    [TestMethod]
    public async Task IndexAsync_BackendSqlite_WithoutSqlitePath_VectorStorageDisabled()
    {
        // Arrange: Backend=Sqlite but SqlitePath is null — vector storage should be disabled
        var nodes = CreateTestNodes(3);
        var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var options = Options.Create(new MemoryOptions
        {
            Backend = MemoryBackend.Sqlite,
            SqlitePath = null,
            SimilarityThreshold = 0.85
        });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, _) = await indexer.IndexAsync(nodes);

        // Assert: no vector store resolved — SqlitePath is required for local persistence
        result.VectorsStored.Should().Be(0,
            because: "Backend=Sqlite without SqlitePath should disable vector storage");
        result.VectorBackend.Should().BeNull();

        // Embeddings still generated and stored in metadata
        result.EmbeddingsGenerated.Should().Be(3);
        foreach (var node in nodes)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.Embedding);
        }
    }

    [TestMethod]
    public async Task IndexAsync_BackendNeo4j_WithoutEndpoint_VectorsSkipped()
    {
        // Arrange: Backend=Neo4j, no QdrantEndpoint, no injected store
        var nodes = CreateTestNodes(3);
        var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var options = Options.Create(new MemoryOptions
        {
            Backend = MemoryBackend.Neo4j,
            QdrantEndpoint = null,
            SimilarityThreshold = 0.85
        });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, null, options, logger.Object);

        // Act
        var (result, _) = await indexer.IndexAsync(nodes);

        // Assert: no vector store resolved → vectors not stored
        result.VectorsStored.Should().Be(0,
            because: "Backend=Neo4j without QdrantEndpoint should have no vector store");
        result.VectorBackend.Should().BeNull(
            because: "no vector store was available");

        // Embeddings still generated and stored in metadata
        result.EmbeddingsGenerated.Should().Be(3);
        foreach (var node in nodes)
        {
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.Embedding);
        }
    }

    [TestMethod]
    public async Task IndexAsync_InjectedVectorStore_TakesPriorityOverBackendSetting()
    {
        // Arrange: inject a mock IVectorStore even though Backend=Sqlite
        var nodes = CreateTestNodes(3);
        var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.Name).Returns("MockStore");
        mockVectorStore.Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.CollectionExistsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockVectorStore.Setup(v => v.CreateCollectionAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockVectorStore.Setup(v => v.UpsertAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = Options.Create(new MemoryOptions
        {
            Backend = MemoryBackend.Sqlite,
            SimilarityThreshold = 0.85
        });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, mockVectorStore.Object, options, logger.Object);

        // Act
        var (result, _) = await indexer.IndexAsync(nodes);

        // Assert: injected store was used, not SqliteVssVectorStore
        result.VectorBackend.Should().Be("MockStore",
            because: "injected IVectorStore should take priority over Backend setting");
        result.VectorsStored.Should().Be(3);

        // Verify the mock store received the calls
        mockVectorStore.Verify(
            v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "UpsertAsync should be called on the injected store");
    }

    [TestMethod]
    public async Task IndexAsync_VectorPersistence_CreatesCollectionAndUpserts()
    {
        // Arrange: mock vector store reports collection doesn't exist
        var nodes = CreateTestNodes(5);
        var embeddings = CreateRandomEmbeddings(5, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.Name).Returns("TestStore");
        mockVectorStore.Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.CollectionExistsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockVectorStore.Setup(v => v.CreateCollectionAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockVectorStore.Setup(v => v.UpsertAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var collectionName = "test_vectors";
        var options = Options.Create(new MemoryOptions
        {
            VectorCollectionName = collectionName,
            SimilarityThreshold = 0.85
        });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, mockVectorStore.Object, options, logger.Object);

        // Act
        var (result, _) = await indexer.IndexAsync(nodes);

        // Assert: collection existence was checked
        mockVectorStore.Verify(
            v => v.CollectionExistsAsync(collectionName, It.IsAny<CancellationToken>()),
            Times.Once,
            "CollectionExistsAsync should be called with the configured collection name");

        // Assert: collection was created since it didn't exist
        mockVectorStore.Verify(
            v => v.CreateCollectionAsync(collectionName, DefaultDimension,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "CreateCollectionAsync should be called when collection doesn't exist");

        // Assert: vectors were upserted
        mockVectorStore.Verify(
            v => v.UpsertAsync(collectionName, It.IsAny<IEnumerable<VectorPoint>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "UpsertAsync should be called with the collection name");

        result.VectorsStored.Should().Be(5);
    }

    [TestMethod]
    public async Task IndexAsync_VectorPersistence_CollectionAlreadyExists_SkipsCreation()
    {
        // Arrange: mock vector store reports collection already exists
        var nodes = CreateTestNodes(3);
        var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.Name).Returns("TestStore");
        mockVectorStore.Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.CollectionExistsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.UpsertAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, mockVectorStore.Object, options, logger.Object);

        // Act
        await indexer.IndexAsync(nodes);

        // Assert: CreateCollectionAsync should NOT be called
        mockVectorStore.Verify(
            v => v.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "CreateCollectionAsync should not be called when collection already exists");
    }

    [TestMethod]
    public async Task IndexAsync_VectorPersistence_PayloadContainsExpectedFields()
    {
        // Arrange: capture the VectorPoints passed to UpsertAsync
        var nodes = CreateTestNodes(2);
        var embeddings = CreateRandomEmbeddings(2, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        List<VectorPoint>? capturedPoints = null;
        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.Name).Returns("TestStore");
        mockVectorStore.Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.CollectionExistsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.UpsertAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<VectorPoint>, CancellationToken>(
                (_, points, _) => capturedPoints = points.ToList())
            .Returns(Task.CompletedTask);

        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, null, mockVectorStore.Object, options, logger.Object);

        // Act
        await indexer.IndexAsync(nodes);

        // Assert: payload shape matches architecture.md §3.2.2
        capturedPoints.Should().NotBeNull("UpsertAsync should have been called");
        capturedPoints!.Should().HaveCount(2);

        for (var i = 0; i < capturedPoints.Count; i++)
        {
            var point = capturedPoints[i];
            var node = nodes[i];

            point.Id.Should().Be(node.Id, "VectorPoint.Id should match the node ID");
            point.Vector.Should().NotBeNull("VectorPoint.Vector must be set");
            point.Vector.Length.Should().Be(DefaultDimension);

            // Verify required payload fields and values
            point.Payload.Should().ContainKey("node_id");
            point.Payload["node_id"].Should().Be(node.Id);

            point.Payload.Should().ContainKey(MemoryMetadataKeys.MemoryLayer);
            point.Payload[MemoryMetadataKeys.MemoryLayer].Should().Be("semantic");

            point.Payload.Should().ContainKey("NodeType");
            point.Payload["NodeType"].Should().Be(node.NodeType);

            point.Payload.Should().ContainKey("name");
            point.Payload["name"].Should().Be(node.Name);

            point.Payload.Should().ContainKey("file_path");
            point.Payload["file_path"].Should().Be(node.FilePath);

            point.Payload.Should().ContainKey("language");
            point.Payload["language"].Should().Be(node.Language);
        }
    }

    [TestMethod]
    public async Task IndexAsync_GraphStorePresentWithoutVersion_StillStoresVectors()
    {
        // Arrange: graph store is present but version is null — vectors should still store
        var nodes = CreateTestNodes(3);
        var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
        var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

        var mockGraphStore = new Mock<IMemoryGraphStore>();
        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.Name).Returns("TestStore");
        mockVectorStore.Setup(v => v.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.CollectionExistsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockVectorStore.Setup(v => v.UpsertAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = Options.Create(new MemoryOptions { SimilarityThreshold = 0.85 });
        var logger = new Mock<ILogger<SemanticIndexer>>();

        using var indexer = new SemanticIndexer(
            mockGenerator.Object, mockGraphStore.Object, mockVectorStore.Object, options, logger.Object);

        // Act: pass version: null
        var (result, _) = await indexer.IndexAsync(nodes, version: null);

        // Assert: graph persistence skipped (no version), but vectors still stored
        result.NodesPersisted.Should().Be(0,
            because: "graph node persistence requires a VersionRange");
        result.EdgesPersisted.Should().Be(0,
            because: "graph edge persistence requires a VersionRange");
        result.VectorsStored.Should().Be(3,
            because: "vector storage should proceed even when graphStore is present but version is null");
        result.VectorBackend.Should().Be("TestStore");

        mockVectorStore.Verify(
            v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IEnumerable<VectorPoint>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "UpsertAsync must be called even when graph store version is null");
    }

    #endregion

    #region Scenario 5: SqliteVssVectorStore local contract

    [TestMethod]
    public async Task SqliteVssVectorStore_InMemory_FallbackMode_FullLifecycle()
    {
        // Test SqliteVssVectorStore in fallback mode (sqlite-vss extension not available)
        // — verifies brute-force cosine path with proper vector round-trip
        var storeLogger = new Mock<ILogger>();
        using var store = new SqliteVssVectorStore(":memory:", storeLogger.Object);

        // Store should be available
        var available = await store.IsAvailableAsync();
        available.Should().BeTrue("in-memory SqliteVssVectorStore should be available");
        store.Name.Should().Be("SqliteVss");

        // Create a collection
        const string collection = "test_embeddings";
        const int vectorDim = 768;
        await store.CreateCollectionAsync(collection, vectorDim);

        var exists = await store.CollectionExistsAsync(collection);
        exists.Should().BeTrue("collection should exist after creation");

        // Upsert vectors
        var v1 = new float[vectorDim];
        v1[0] = 1.0f;
        var v2 = new float[vectorDim];
        v2[1] = 1.0f;
        var v3 = new float[vectorDim];
        v3[0] = 0.9f;
        v3[1] = MathF.Sqrt(1 - 0.81f);

        var points = new List<VectorPoint>
        {
            new() { Id = "node-a", Vector = v1, Payload = new Dictionary<string, object> { ["node_id"] = "node-a", ["NodeType"] = "Class" } },
            new() { Id = "node-b", Vector = v2, Payload = new Dictionary<string, object> { ["node_id"] = "node-b", ["NodeType"] = "Method" } },
            new() { Id = "node-c", Vector = v3, Payload = new Dictionary<string, object> { ["node_id"] = "node-c", ["NodeType"] = "Class" } }
        };

        await store.UpsertAsync(collection, points);

        // Count
        var count = await store.CountAsync(collection);
        count.Should().Be(3, "3 points were upserted");

        // Get by ID should return the correct point with vector round-trip
        var retrieved = await store.GetAsync(collection, "node-a");
        retrieved.Should().NotBeNull("point 'node-a' was upserted");
        retrieved!.Id.Should().Be("node-a");
        retrieved.Vector.Should().HaveCount(vectorDim);
        retrieved.Vector[0].Should().BeApproximately(1.0f, 0.001f);
        retrieved.Payload.Should().ContainKey("node_id");
        retrieved.Payload["node_id"].ToString().Should().Be("node-a");
        retrieved.Payload.Should().ContainKey("NodeType");
        retrieved.Payload["NodeType"].ToString().Should().Be("Class");

        // Search: query with v1 — node-a should be the top hit
        var results = await store.SearchAsync(collection, v1, limit: 3);
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results[0].Id.Should().Be("node-a", "exact match should be top hit");
        results[0].Score.Should().BeApproximately(1.0f, 0.01f);

        // Delete
        await store.DeleteAsync(collection, new[] { "node-b" });
        count = await store.CountAsync(collection);
        count.Should().Be(2, "one point was deleted");

        // Delete collection
        await store.DeleteCollectionAsync(collection);
        exists = await store.CollectionExistsAsync(collection);
        exists.Should().BeFalse("collection should not exist after deletion");
    }

    [TestMethod]
    public async Task SqliteVssVectorStore_FileBased_VectorsPersistAcrossInstances()
    {
        // Test durable vector storage: write, dispose, reopen, verify round-trip
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_vss_{Guid.NewGuid():N}.db");

        try
        {
            var storeLogger = new Mock<ILogger>();
            const string collection = "memory_embeddings";
            const int dim = 4;

            using (var store = new SqliteVssVectorStore(tempFile, storeLogger.Object))
            {
                await store.CreateCollectionAsync(collection, dim);
                await store.UpsertAsync(collection, new[]
                {
                    new VectorPoint
                    {
                        Id = "persistent-1",
                        Vector = new float[] { 1, 0, 0, 0 },
                        Payload = new Dictionary<string, object> { ["node_id"] = "persistent-1" }
                    },
                    new VectorPoint
                    {
                        Id = "persistent-2",
                        Vector = new float[] { 0, 1, 0, 0 },
                        Payload = new Dictionary<string, object> { ["node_id"] = "persistent-2" }
                    }
                });

                var count = await store.CountAsync(collection);
                count.Should().Be(2);
            }

            // Reopen — data should persist
            using (var store2 = new SqliteVssVectorStore(tempFile, storeLogger.Object))
            {
                var exists = await store2.CollectionExistsAsync(collection);
                exists.Should().BeTrue("collection should persist on disk");

                var count = await store2.CountAsync(collection);
                count.Should().Be(2, "vectors should survive store dispose/reopen");

                var point = await store2.GetAsync(collection, "persistent-1");
                point.Should().NotBeNull("vector should be retrievable after reopen");
                point!.Vector.Should().HaveCount(dim);
                point.Vector[0].Should().BeApproximately(1.0f, 0.001f);
                point.Payload["node_id"].ToString().Should().Be("persistent-1");

                // Search should work
                var results = await store2.SearchAsync(collection, new float[] { 1, 0, 0, 0 }, limit: 2);
                results.Should().HaveCountGreaterThanOrEqualTo(1);
                results[0].Id.Should().Be("persistent-1");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [TestMethod]
    public async Task SqliteVectorStore_InMemory_CreateUpsertCountGetSearchLifecycle()
    {
        // Test the SqliteVectorStore directly — full lifecycle with in-memory database
        var storeLogger = new Mock<ILogger>();
        using var store = new SqliteVectorStore(":memory:", storeLogger.Object);

        // Store should be available
        var available = await store.IsAvailableAsync();
        available.Should().BeTrue("in-memory SqliteVectorStore should be available");
        store.Name.Should().Be("Sqlite");

        // Create a collection
        const string collection = "test_embeddings";
        const int vectorDim = 768;
        await store.CreateCollectionAsync(collection, vectorDim);

        var exists = await store.CollectionExistsAsync(collection);
        exists.Should().BeTrue("collection should exist after creation");

        // Upsert vectors
        var v1 = new float[vectorDim];
        v1[0] = 1.0f;
        var v2 = new float[vectorDim];
        v2[1] = 1.0f;
        var v3 = new float[vectorDim];
        v3[0] = 0.9f;
        v3[1] = MathF.Sqrt(1 - 0.81f);

        var points = new List<VectorPoint>
        {
            new() { Id = "node-a", Vector = v1, Payload = new Dictionary<string, object> { ["node_id"] = "node-a", ["NodeType"] = "Class" } },
            new() { Id = "node-b", Vector = v2, Payload = new Dictionary<string, object> { ["node_id"] = "node-b", ["NodeType"] = "Method" } },
            new() { Id = "node-c", Vector = v3, Payload = new Dictionary<string, object> { ["node_id"] = "node-c", ["NodeType"] = "Class" } }
        };

        await store.UpsertAsync(collection, points);

        // Count should be 3
        var count = await store.CountAsync(collection);
        count.Should().Be(3, "3 points were upserted");

        // Get by ID should return the correct point with vector and payload round-trip
        var retrieved = await store.GetAsync(collection, "node-a");
        retrieved.Should().NotBeNull("point 'node-a' was upserted");
        retrieved!.Id.Should().Be("node-a");
        retrieved.Vector.Should().HaveCount(vectorDim);
        retrieved.Vector[0].Should().BeApproximately(1.0f, 0.001f);
        retrieved.Payload.Should().ContainKey("node_id");
        retrieved.Payload["node_id"].ToString().Should().Be("node-a");
        retrieved.Payload.Should().ContainKey("NodeType");
        retrieved.Payload["NodeType"].ToString().Should().Be("Class");

        // Get non-existent should return null
        var missing = await store.GetAsync(collection, "nonexistent");
        missing.Should().BeNull();

        // Search: query with v1 — node-a should be the top hit (cosine = 1.0),
        // node-c should be second (cosine ≈ 0.9)
        var results = await store.SearchAsync(collection, v1, limit: 3);
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results[0].Id.Should().Be("node-a", "exact match should be top hit");
        results[0].Score.Should().BeApproximately(1.0f, 0.01f);
        results[1].Id.Should().Be("node-c", "node-c has cosine ~0.9 with v1");
        results[1].Score.Should().BeGreaterThan(0.8f);

        // Upsert (update existing point)
        var updatedV1 = new float[vectorDim];
        updatedV1[2] = 1.0f;
        await store.UpsertAsync(collection, new[] { new VectorPoint
        {
            Id = "node-a",
            Vector = updatedV1,
            Payload = new Dictionary<string, object> { ["node_id"] = "node-a", ["NodeType"] = "Interface" }
        }});

        var updatedPoint = await store.GetAsync(collection, "node-a");
        updatedPoint.Should().NotBeNull();
        updatedPoint!.Vector[2].Should().BeApproximately(1.0f, 0.001f, "vector should be updated");
        updatedPoint.Payload["NodeType"].ToString().Should().Be("Interface", "payload should be updated");

        // Delete
        await store.DeleteAsync(collection, new[] { "node-b" });
        count = await store.CountAsync(collection);
        count.Should().Be(2, "one point was deleted");

        // Delete collection
        await store.DeleteCollectionAsync(collection);
        exists = await store.CollectionExistsAsync(collection);
        exists.Should().BeFalse("collection should not exist after deletion");
    }

    [TestMethod]
    public async Task SqliteVectorStore_FileBased_VectorsPersistAcrossInstances()
    {
        // Test durable vector storage: write vectors, dispose store, reopen, verify data persists
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_vectors_{Guid.NewGuid():N}.db");

        try
        {
            var storeLogger = new Mock<ILogger>();
            const string collection = "memory_embeddings";
            const int dim = 4;

            // Write vectors to file-based store
            using (var store = new SqliteVectorStore(tempFile, storeLogger.Object))
            {
                await store.CreateCollectionAsync(collection, dim);
                await store.UpsertAsync(collection, new[]
                {
                    new VectorPoint
                    {
                        Id = "persistent-1",
                        Vector = new float[] { 1, 0, 0, 0 },
                        Payload = new Dictionary<string, object> { ["node_id"] = "persistent-1" }
                    },
                    new VectorPoint
                    {
                        Id = "persistent-2",
                        Vector = new float[] { 0, 1, 0, 0 },
                        Payload = new Dictionary<string, object> { ["node_id"] = "persistent-2" }
                    }
                });

                var count = await store.CountAsync(collection);
                count.Should().Be(2);
            }

            // Reopen the same file — data should persist
            using (var store2 = new SqliteVectorStore(tempFile, storeLogger.Object))
            {
                var exists = await store2.CollectionExistsAsync(collection);
                exists.Should().BeTrue("collection should persist on disk");

                var count = await store2.CountAsync(collection);
                count.Should().Be(2, "vectors should survive store dispose/reopen");

                var point = await store2.GetAsync(collection, "persistent-1");
                point.Should().NotBeNull("vector should be retrievable after reopen");
                point!.Vector.Should().HaveCount(dim);
                point.Vector[0].Should().BeApproximately(1.0f, 0.001f);
                point.Payload["node_id"].ToString().Should().Be("persistent-1");

                // Search should also work
                var results = await store2.SearchAsync(collection, new float[] { 1, 0, 0, 0 }, limit: 2);
                results.Should().HaveCountGreaterThanOrEqualTo(1);
                results[0].Id.Should().Be("persistent-1");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [TestMethod]
    public async Task SqliteVectorStore_SearchWithFilter_RespectsConditions()
    {
        var storeLogger = new Mock<ILogger>();
        using var store = new SqliteVectorStore(":memory:", storeLogger.Object);

        const string collection = "filter_test";
        const int dim = 4;
        await store.CreateCollectionAsync(collection, dim);

        var points = new List<VectorPoint>
        {
            new() { Id = "a", Vector = new float[] { 1, 0, 0, 0 }, Payload = new Dictionary<string, object> { ["type"] = "Class" } },
            new() { Id = "b", Vector = new float[] { 0.9f, 0.44f, 0, 0 }, Payload = new Dictionary<string, object> { ["type"] = "Method" } },
            new() { Id = "c", Vector = new float[] { 0.8f, 0.6f, 0, 0 }, Payload = new Dictionary<string, object> { ["type"] = "Class" } }
        };

        await store.UpsertAsync(collection, points);

        // Search with filter: only type=Class
        var filter = new VectorFilter
        {
            Must = new List<FilterCondition>
            {
                new() { Field = "type", Operator = FilterOperator.Equals, Value = "Class" }
            }
        };

        var results = await store.SearchAsync(collection, new float[] { 1, 0, 0, 0 }, limit: 10, filter: filter);
        results.Should().HaveCount(2, "only Class-type points should be returned");
        results.All(r => r.Payload["type"].ToString() == "Class").Should().BeTrue();
    }

    [TestMethod]
    public async Task IndexAsync_BackendSqlite_VectorStoreSharesSqlitePath()
    {
        // Verify that SemanticIndexer creates the vector store on the configured SqlitePath,
        // ensuring graph and vector stores share the same database (architecture.md §3.2)
        var tempFile = Path.Combine(Path.GetTempPath(), $"shared_db_{Guid.NewGuid():N}.db");

        try
        {
            var nodes = CreateTestNodes(3);
            var embeddings = CreateRandomEmbeddings(3, DefaultDimension);
            var mockGenerator = CreateMockGenerator(DefaultDimension, embeddings);

            var options = Options.Create(new MemoryOptions
            {
                Backend = MemoryBackend.Sqlite,
                SqlitePath = tempFile,
                SimilarityThreshold = 0.85,
                VectorCollectionName = "memory_embeddings"
            });
            var logger = new Mock<ILogger<SemanticIndexer>>();

            using (var indexer = new SemanticIndexer(
                mockGenerator.Object, null, null, options, logger.Object))
            {
                var (result, _) = await indexer.IndexAsync(nodes);

                result.VectorBackend.Should().Be("SqliteVss");
                result.VectorsStored.Should().Be(3);
            }

            // Verify vectors persist on disk by reopening with SqliteVssVectorStore
            var storeLogger = new Mock<ILogger>();
            using var verifyStore = new SqliteVssVectorStore(tempFile, storeLogger.Object);
            var exists = await verifyStore.CollectionExistsAsync("memory_embeddings");
            exists.Should().BeTrue("SemanticIndexer should have written to the shared SQLite database");

            var count = await verifyStore.CountAsync("memory_embeddings");
            count.Should().Be(3, "all 3 vectors should persist in the shared database");

            // Verify vector round-trip
            var point = await verifyStore.GetAsync("memory_embeddings", nodes[0].Id);
            point.Should().NotBeNull("vector should be retrievable from the shared database");
            point!.Vector.Length.Should().Be(DefaultDimension);
            point.Payload.Should().ContainKey("node_id");
            point.Payload["node_id"].ToString().Should().Be(nodes[0].Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    #endregion

    #region Helper methods

    private static IReadOnlyList<HypergraphNode> CreateTestNodes(int count)
    {
        var nodes = new List<HypergraphNode>(count);
        for (var i = 0; i < count; i++)
        {
            nodes.Add(new HypergraphNode
            {
                Id = $"/src/TestFile{i}.cs::TestClass{i}",
                Name = $"TestClass{i}",
                NodeType = "Class",
                Language = "C#",
                FilePath = $"src/TestFile{i}.cs",
                LineStart = i * 10 + 1,
                LineEnd = i * 10 + 20
            });
        }

        return nodes;
    }

    private static IReadOnlyList<float[]> CreateRandomEmbeddings(int count, int dimension)
    {
        var rng = new Random(42);
        var embeddings = new List<float[]>(count);
        for (var i = 0; i < count; i++)
        {
            embeddings.Add(CreateRandomEmbedding(dimension, rng));
        }

        return embeddings;
    }

    private static float[] CreateRandomEmbedding(int dimension, Random? rng = null)
    {
        rng ??= new Random(42);
        var vector = new float[dimension];
        for (var j = 0; j < dimension; j++)
        {
            vector[j] = (float)(rng.NextDouble() * 2 - 1);
        }

        // Normalize to unit vector
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude > 0)
        {
            for (var j = 0; j < dimension; j++)
            {
                vector[j] /= magnitude;
            }
        }

        return vector;
    }

    /// <summary>
    /// Creates a unit vector [1, 0, 0, ..., 0] in the given dimension.
    /// </summary>
    private static float[] CreateUnitVectorAlongAxis0(int dimension)
    {
        var vector = new float[dimension];
        vector[0] = 1.0f;
        return vector;
    }

    /// <summary>
    /// Creates a unit vector that has the specified cosine similarity with [1, 0, 0, ...].
    /// The vector is [c, sqrt(1-c²), 0, 0, ...] which is unit length and has
    /// cosine similarity exactly c with the axis-0 unit vector.
    /// </summary>
    private static float[] CreateVectorWithKnownCosine(int dimension, float targetCosine)
    {
        var vector = new float[dimension];
        vector[0] = targetCosine;
        vector[1] = MathF.Sqrt(1.0f - targetCosine * targetCosine);
        return vector;
    }

    private static Mock<IEmbeddingGenerator> CreateMockGenerator(
        int dimension,
        IReadOnlyList<float[]> embeddings)
    {
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(g => g.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mock.Setup(g => g.Dimensions).Returns(dimension);
        mock.Setup(g => g.ModelName).Returns("nomic-embed-text");
        mock.Setup(g => g.GenerateBatchEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embeddings);
        return mock;
    }

    #endregion
}