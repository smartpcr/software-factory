// -----------------------------------------------------------------------
// <copyright file="RankerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Retrieval;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

[TestClass]
public class RankerTests
{
    private Mock<IMemoryGraphStore> mockStore = null!;
    private IOptions<MemoryOptions> defaultOptions = null!;
    private ILogger<Ranker> logger = null!;

    [TestInitialize]
    public void Setup()
    {
        this.mockStore = new Mock<IMemoryGraphStore>();
        this.defaultOptions = Options.Create(new MemoryOptions());
        this.logger = NullLogger<Ranker>.Instance;

        // Default: no edges
        this.mockStore
            .Setup(s => s.GetEdgesForNodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Hyperedge>());
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task ScoringWeightSum_DefaultWeights_CompositeEqualsWeightedSum()
    {
        // Arrange
        var weights = new ScoringWeights();
        weights.Structural.Should().Be(0.30);
        weights.Semantic.Should().Be(0.25);
        weights.Centrality.Should().Be(0.20);
        weights.Recency.Should().Be(0.15);
        weights.Expertise.Should().Be(0.10);
        weights.Sum.Should().BeApproximately(1.0, 1e-10);

        var queryEmbedding = CreateNormalizedEmbedding(1.0f, 0.0f, 0.0f);
        var nodeEmbedding = CreateNormalizedEmbedding(0.8f, 0.6f, 0.0f); // cosine ≈ 0.8

        var mockEmbedding = new Mock<IEmbeddingGenerator>();
        mockEmbedding.Setup(e => e.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var node = CreateMemoryNode("node-1", sinceIndex: 8, embedding: nodeEmbedding);

        // Setup one CALLS edge and one APPLIES_TO edge targeting node-1
        var callsEdge = CreateCallsEdge("caller-1", "node-1");
        var appliesToEdge = CreateAppliesToEdge("expertise-1", "node-1", confidence: 0.6);
        this.mockStore
            .Setup(s => s.GetEdgesForNodeAsync("node-1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { callsEdge, appliesToEdge });

        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger,
            mockEmbedding.Object);

        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int> { ["node-1"] = 2 },
            QueryEmbedding = queryEmbedding,
            HeadCommitIndex = 10,
        };

        // Act
        var results = await ranker.RankAsync(new[] { node }, context, weights);

        // Assert
        results.Should().HaveCount(1);
        var result = results[0];

        // Structural: 1/(1+2) = 0.333...
        result.StructuralScore.Should().BeApproximately(1.0 / 3.0, 1e-6);

        // Semantic: cosine(queryEmbed, nodeEmbed) — computed below
        var expectedSemantic = Ranker.CosineSimilarity(queryEmbedding, nodeEmbedding);
        result.SemanticScore.Should().BeApproximately(expectedSemantic, 1e-6);

        // Centrality: 1 caller, max=1, so 1/1 = 1.0
        result.CentralityScore.Should().BeApproximately(1.0, 1e-6);

        // Recency: 1 - (10-8)/max_dist. max_dist = 10-8 = 2, so 1 - 2/2 = 0.0
        result.RecencyScore.Should().BeApproximately(0.0, 1e-6);

        // Expertise: max Confidence of APPLIES_TO edges = 0.6
        result.ExpertiseScore.Should().BeApproximately(0.6, 1e-6);

        // Composite = sum of weighted signals
        var expectedComposite =
            (result.StructuralScore * weights.Structural) +
            (result.SemanticScore * weights.Semantic) +
            (result.CentralityScore * weights.Centrality) +
            (result.RecencyScore * weights.Recency) +
            (result.ExpertiseScore * weights.Expertise);

        result.CompositeScore.Should().BeApproximately(expectedComposite, 1e-6);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task SemanticFallback_EmbeddingUnavailable_SemanticZeroAndWeightsReNormalized()
    {
        // Arrange — no IEmbeddingGenerator (null)
        var node1 = CreateMemoryNode("node-a", sinceIndex: 5);
        var node2 = CreateMemoryNode("node-b", sinceIndex: 8);

        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger,
            embeddingGenerator: null);

        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int>
            {
                ["node-a"] = 0,
                ["node-b"] = 1,
            },
            QueryEmbedding = null,
            HeadCommitIndex = 10,
        };

        // Act
        var results = await ranker.RankAsync(new[] { node1, node2 }, context);

        // Assert
        foreach (var result in results)
        {
            result.SemanticScore.Should().Be(0.0, "semantic must be 0 when embedding is unavailable");
        }

        // Verify re-normalized weights sum to 1.0
        var weights = new ScoringWeights();
        var reNormalized = Ranker.ReNormalizeWithoutSemantic(weights);
        reNormalized.Semantic.Should().Be(0.0);
        reNormalized.Sum.Should().BeApproximately(1.0, 1e-10,
            "remaining 4 weights must sum to 1.0 after re-normalization");

        // Verify composite uses re-normalized weights (no semantic contribution)
        foreach (var result in results)
        {
            var expectedComposite =
                (result.StructuralScore * reNormalized.Structural) +
                (result.CentralityScore * reNormalized.Centrality) +
                (result.RecencyScore * reNormalized.Recency) +
                (result.ExpertiseScore * reNormalized.Expertise);

            result.CompositeScore.Should().BeApproximately(expectedComposite, 1e-6);
        }
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task ExpertiseSignal_AppliesTo_ReturnsMaxConfidence()
    {
        // Arrange — two APPLIES_TO edges with different Confidence values
        var node = CreateMemoryNode("target-node", sinceIndex: 5);

        var appliesToEdge08 = CreateAppliesToEdge("expertise-pitfall-1", "target-node", confidence: 0.8);
        var appliesToEdge05 = CreateAppliesToEdge("expertise-pattern-1", "target-node", confidence: 0.5);

        this.mockStore
            .Setup(s => s.GetEdgesForNodeAsync("target-node", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { appliesToEdge08, appliesToEdge05 });

        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger);

        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int> { ["target-node"] = 0 },
            HeadCommitIndex = 10,
        };

        // Act
        var results = await ranker.RankAsync(new[] { node }, context);

        // Assert — ExpertiseScore = max Confidence of APPLIES_TO edges = 0.8
        results.Should().HaveCount(1);
        results[0].ExpertiseScore.Should().Be(0.8,
            "expertise score should equal max Confidence of targeting APPLIES_TO edges");
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task ExpertiseSignal_GraphStoreFailure_Propagates()
    {
        // Arrange — graph store throws on edge retrieval
        var node = CreateMemoryNode("fail-node", sinceIndex: 5);

        this.mockStore
            .Setup(s => s.GetEdgesForNodeAsync("fail-node", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Graph store unavailable"));

        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger);

        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int> { ["fail-node"] = 0 },
            HeadCommitIndex = 10,
        };

        // Act & Assert — exception must propagate, not be swallowed
        var act = () => ranker.RankAsync(new[] { node }, context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Graph store unavailable");
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task RankAsync_EmptyCandidates_ReturnsEmptyList()
    {
        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger);

        var context = new RankingContext { HeadCommitIndex = 10 };

        var results = await ranker.RankAsync(Array.Empty<MemoryNode>(), context);

        results.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task RankAsync_MultipleCandidates_SortedDescendingByCompositeScore()
    {
        // Arrange — two nodes, one at BFS distance 0 (high structural) and one at distance 5
        var closeNode = CreateMemoryNode("close", sinceIndex: 9);
        var farNode = CreateMemoryNode("far", sinceIndex: 1);

        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger);

        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int>
            {
                ["close"] = 0,
                ["far"] = 5,
            },
            HeadCommitIndex = 10,
        };

        // Act
        var results = await ranker.RankAsync(new[] { farNode, closeNode }, context);

        // Assert — close node should rank higher
        results.Should().HaveCount(2);
        results[0].Node.Inner.Id.Should().Be("close");
        results[1].Node.Inner.Id.Should().Be("far");
        results[0].CompositeScore.Should().BeGreaterThan(results[1].CompositeScore);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void ReNormalizeWithoutSemantic_ProducesCorrectWeights()
    {
        var weights = new ScoringWeights();
        var result = Ranker.ReNormalizeWithoutSemantic(weights);

        result.Semantic.Should().Be(0.0);
        result.Sum.Should().BeApproximately(1.0, 1e-10);

        // Original non-semantic sum = 0.30 + 0.20 + 0.15 + 0.10 = 0.75
        // Factor = 1/0.75 = 4/3
        result.Structural.Should().BeApproximately(0.30 / 0.75, 1e-10);
        result.Centrality.Should().BeApproximately(0.20 / 0.75, 1e-10);
        result.Recency.Should().BeApproximately(0.15 / 0.75, 1e-10);
        result.Expertise.Should().BeApproximately(0.10 / 0.75, 1e-10);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void CosineSimilarity_IdenticalVectors_Returns1()
    {
        var v = new[] { 1.0f, 2.0f, 3.0f };
        Ranker.CosineSimilarity(v, v).Should().BeApproximately(1.0, 1e-6);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void CosineSimilarity_OrthogonalVectors_Returns0()
    {
        var a = new[] { 1.0f, 0.0f };
        var b = new[] { 0.0f, 1.0f };
        Ranker.CosineSimilarity(a, b).Should().BeApproximately(0.0, 1e-6);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void StructuralScore_NoSeedDistance_ReturnsZero()
    {
        var node = CreateMemoryNode("orphan", sinceIndex: 1);
        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int>(),
            HeadCommitIndex = 10,
        };

        Ranker.ComputeStructuralScore(node, context).Should().Be(0.0);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void StructuralScore_ZeroDistance_Returns1()
    {
        var node = CreateMemoryNode("seed", sinceIndex: 1);
        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int> { ["seed"] = 0 },
            HeadCommitIndex = 10,
        };

        Ranker.ComputeStructuralScore(node, context).Should().BeApproximately(1.0, 1e-10);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void RecencyScore_LatestCommit_Returns1()
    {
        var node = CreateMemoryNode("latest", sinceIndex: 10);
        Ranker.ComputeRecencyScore(node, headCommitIndex: 10, maxDistance: 10)
            .Should().BeApproximately(1.0, 1e-10);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void RecencyScore_ExternalNode_ReturnsNeutral()
    {
        // External node has null VersionRange
        var inner = new HypergraphNode
        {
            Id = "ext:System:List",
            Name = "List",
            NodeType = "External",
            Language = "C#",
            FilePath = "",
        };
        var node = new MemoryNode(inner);
        node.Version.Should().BeNull();

        Ranker.ComputeRecencyScore(node, headCommitIndex: 10, maxDistance: 10)
            .Should().BeApproximately(0.5, 1e-10);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public async Task SemanticFallback_EmbeddingGeneratorThrows_FallsBackGracefully()
    {
        // Arrange — embedding generator throws on availability check
        var mockEmbedding = new Mock<IEmbeddingGenerator>();
        mockEmbedding.Setup(e => e.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ollama down"));

        var node = CreateMemoryNode("node-1", sinceIndex: 5);

        var ranker = new Ranker(
            this.mockStore.Object,
            this.defaultOptions,
            this.logger,
            mockEmbedding.Object);

        var context = new RankingContext
        {
            BfsDistances = new Dictionary<string, int> { ["node-1"] = 0 },
            QueryEmbedding = new[] { 1.0f, 0.0f, 0.0f },
            HeadCommitIndex = 10,
        };

        // Act
        var results = await ranker.RankAsync(new[] { node }, context);

        // Assert
        results.Should().HaveCount(1);
        results[0].SemanticScore.Should().Be(0.0);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void ExtractConfidenceFromEdge_DoubleMeta_ReturnsValue()
    {
        var edge = CreateAppliesToEdge("e1", "t1", confidence: 0.75);
        Ranker.ExtractConfidenceFromEdge(edge).Should().BeApproximately(0.75, 1e-10);
    }

    [TestMethod]
    [TestCategory("ranker")]
    public void ExtractConfidenceFromEdge_MissingMeta_ReturnsZero()
    {
        var edge = new Hyperedge
        {
            Id = "edge-no-conf",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["a", "b"],
            Roles = new Dictionary<string, string> { ["a"] = "expertise", ["b"] = "target" },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
            },
        };
        Ranker.ExtractConfidenceFromEdge(edge).Should().Be(0.0);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static MemoryNode CreateMemoryNode(
        string id,
        int? sinceIndex = null,
        float[]? embedding = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
        };

        if (sinceIndex.HasValue)
        {
            metadata[MemoryMetadataKeys.SinceCommit] = $"commit-{sinceIndex}";
            metadata[MemoryMetadataKeys.SinceIndex] = sinceIndex.Value;
        }

        if (embedding is not null)
        {
            metadata[MemoryMetadataKeys.Embedding] = embedding;
        }

        var inner = new HypergraphNode
        {
            Id = id,
            Name = id,
            NodeType = "Method",
            Language = "C#",
            FilePath = $"/src/{id}.cs",
            Metadata = metadata,
        };

        return new MemoryNode(inner);
    }

    private static Hyperedge CreateCallsEdge(string callerId, string calleeId)
    {
        return new Hyperedge
        {
            Id = $"edge-{callerId}-{calleeId}",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = [callerId, calleeId],
            Roles = new Dictionary<string, string>
            {
                [callerId] = "caller",
                [calleeId] = "callee",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "call_chain",
            },
        };
    }

    private static Hyperedge CreateAppliesToEdge(string expertiseId, string targetId, double confidence)
    {
        return new Hyperedge
        {
            Id = $"edge:{expertiseId}→{targetId}",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = [expertiseId, targetId],
            Roles = new Dictionary<string, string>
            {
                [expertiseId] = "expertise",
                [targetId] = "target",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "APPLIES_TO",
                [MemoryMetadataKeys.MemoryLayer] = "expertise",
                [MemoryMetadataKeys.Confidence] = confidence,
            },
        };
    }

    private static float[] CreateNormalizedEmbedding(params float[] values)
    {
        var magnitude = (float)Math.Sqrt(values.Sum(v => v * v));
        return magnitude > 0 ? values.Select(v => v / magnitude).ToArray() : values;
    }
}