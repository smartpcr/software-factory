// -----------------------------------------------------------------------
// <copyright file="CommunityDetectionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="EvolutionEngine.DetectCommunitiesAsync"/> implementing
/// Louvain community detection over CALLS/SIMILAR_TO edges.
/// </summary>
[TestClass]
public class CommunityDetectionTests
{
    private InMemoryMemoryGraphStore graphStore = null!;
    private FakeLlmClient llmClient = null!;
    private EvolutionEngine engine = null!;

    [TestInitialize]
    public void Setup()
    {
        this.graphStore = new InMemoryMemoryGraphStore();
        this.llmClient = new FakeLlmClient();
        this.engine = new EvolutionEngine(
            this.graphStore,
            this.llmClient,
            Microsoft.Extensions.Options.Options.Create(new MemoryOptions()),
            NullLogger<EvolutionEngine>.Instance);
    }

    /// <summary>
    /// Scenario: Community detection — Given a graph with 20+ nodes and CALLS/SIMILAR_TO
    /// edges forming 3 natural clusters, When DetectCommunitiesAsync runs, Then at least
    /// 3 Topic nodes are created with BELONGS_TO_CLUSTER edges connecting member nodes.
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_ThreeClusters_CreatesAtLeastThreeTopicNodes()
    {
        // Arrange: Create 3 clusters of nodes with intra-cluster edges.
        // Cluster A: nodes a0..a7 (8 nodes) — connected via CALLS
        // Cluster B: nodes b0..b7 (8 nodes) — connected via SIMILAR_TO
        // Cluster C: nodes c0..c7 (8 nodes) — connected via CALLS
        // Total: 24 nodes (>20), 3 natural clusters

        var clusterA = CreateClusterNodes("a", 8, "ClassA");
        var clusterB = CreateClusterNodes("b", 8, "ClassB");
        var clusterC = CreateClusterNodes("c", 8, "ClassC");

        foreach (var node in clusterA.Concat(clusterB).Concat(clusterC))
        {
            this.graphStore.AddNode(node);
        }

        // Intra-cluster edges with high weight (dense connectivity)
        // All edges carry SemanticRelation metadata per architecture §2.2
        AddIntraClusterEdges(clusterA, "CALLS", weight: 5.0);
        AddIntraClusterEdges(clusterB, "SIMILAR_TO", weight: 5.0);
        AddIntraClusterEdges(clusterC, "CALLS", weight: 5.0);

        // Inter-cluster edges with low weight (sparse connectivity)
        AddInterClusterEdge(clusterA[0], clusterB[0], "CALLS", weight: 0.1);
        AddInterClusterEdge(clusterB[0], clusterC[0], "CALLS", weight: 0.1);

        // Act
        var result = await this.engine.DetectCommunitiesAsync();

        // Assert
        result.CommunitiesDetected.Should().BeGreaterThanOrEqualTo(3,
            "graph has 3 natural clusters with dense intra-cluster edges");

        result.TopicNodesCreated.Should().BeGreaterThanOrEqualTo(3,
            "each community gets a Topic node");

        result.ClusterEdgesCreated.Should().BeGreaterThan(0,
            "member nodes should be connected to Topic nodes via BELONGS_TO_CLUSTER");

        // Verify Topic nodes exist in the store
        var allNodes = await this.graphStore.GetAllNodesAsync();
        var topicNodes = allNodes.Where(n => n.NodeType == "Topic").ToList();
        topicNodes.Count.Should().BeGreaterThanOrEqualTo(3);

        // Verify BELONGS_TO_CLUSTER edges exist
        var clusterEdges = await this.graphStore.GetEdgesBySemanticRelationAsync(
            ["BELONGS_TO_CLUSTER"]);
        clusterEdges.Count.Should().BeGreaterThan(0);

        // Every BELONGS_TO_CLUSTER edge should be of type MemoryRelation
        foreach (var edge in clusterEdges)
        {
            edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
            edge.Metadata["SemanticRelation"].Should().Be("BELONGS_TO_CLUSTER");
            edge.Metadata["MemoryLayer"].Should().Be("semantic");

            // Verify roles
            edge.Roles.Values.Should().Contain("member");
            edge.Roles.Values.Should().Contain("cluster");
        }
    }

    /// <summary>
    /// Scenario: Topic node structure — Given a detected community, When its Topic node
    /// is inspected, Then NodeType="Topic", Metadata["MemoryLayer"]="semantic",
    /// Metadata["Summary"] is a non-empty LLM-generated string, and
    /// Metadata["MemberCount"] matches the cluster size.
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_TopicNodeStructure_HasCorrectMetadata()
    {
        // Arrange: Create a simple 2-cluster graph with 7 nodes each (above min size of 5)
        var cluster1 = CreateClusterNodes("x", 7, "ServiceX");
        var cluster2 = CreateClusterNodes("y", 7, "ServiceY");

        foreach (var node in cluster1.Concat(cluster2))
        {
            this.graphStore.AddNode(node);
        }

        AddIntraClusterEdges(cluster1, "CALLS", weight: 5.0);
        AddIntraClusterEdges(cluster2, "CALLS", weight: 5.0);

        // Act
        var result = await this.engine.DetectCommunitiesAsync();

        // Assert
        result.CommunitiesDetected.Should().BeGreaterThanOrEqualTo(2);

        var allNodes = await this.graphStore.GetAllNodesAsync();
        var topicNodes = allNodes.Where(n => n.NodeType == "Topic").ToList();

        topicNodes.Should().NotBeEmpty("community detection should produce Topic nodes");

        foreach (var topic in topicNodes)
        {
            // NodeType
            topic.NodeType.Should().Be("Topic");

            // Id format: "topic:{hash8}"
            topic.Id.Should().StartWith("topic:");

            // Metadata["MemoryLayer"] = "semantic"
            topic.Metadata.Should().ContainKey("MemoryLayer");
            topic.Metadata["MemoryLayer"].Should().Be("semantic");

            // Metadata["Summary"] is non-empty LLM-generated string
            topic.Metadata.Should().ContainKey("Summary");
            var summary = topic.Metadata["Summary"] as string;
            summary.Should().NotBeNullOrWhiteSpace(
                "Topic summary should be an LLM-generated non-empty string");

            // Metadata["MemberCount"] matches cluster size
            topic.Metadata.Should().ContainKey("MemberCount");
            var memberCount = (int)topic.Metadata["MemberCount"]!;
            memberCount.Should().BeGreaterThanOrEqualTo(5,
                "communities must have at least 5 members (MinCommunitySize)");

            // Verify the cluster edges match member count
            var clusterEdges = await this.graphStore.GetEdgesBySemanticRelationAsync(
                ["BELONGS_TO_CLUSTER"]);
            var edgesForTopic = clusterEdges
                .Where(e => e.NodeIds.Contains(topic.Id))
                .ToList();

            edgesForTopic.Count.Should().Be(memberCount,
                "each member should have exactly one BELONGS_TO_CLUSTER edge to its Topic");
        }

        // Verify the LLM was called (summaries are LLM-generated)
        this.llmClient.CallCount.Should().BeGreaterThan(0,
            "topic summaries should be generated via the LLM client");
    }

    /// <summary>
    /// Verifies that running DetectCommunitiesAsync twice produces idempotent results
    /// (old Topic nodes and cluster edges are removed before re-creation).
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_Idempotent_NoDuplicateTopics()
    {
        // Arrange
        var cluster = CreateClusterNodes("z", 8, "ControllerZ");
        foreach (var node in cluster)
        {
            this.graphStore.AddNode(node);
        }

        AddIntraClusterEdges(cluster, "CALLS", weight: 5.0);

        // Act: run twice
        await this.engine.DetectCommunitiesAsync();
        var result2 = await this.engine.DetectCommunitiesAsync();

        // Assert: no duplicates
        var allNodes = await this.graphStore.GetAllNodesAsync();
        var topicNodes = allNodes.Where(n => n.NodeType == "Topic").ToList();

        topicNodes.Count.Should().Be(result2.TopicNodesCreated,
            "idempotent re-run should not create duplicate Topic nodes");
    }

    /// <summary>
    /// Verifies that when no edges exist, community detection returns zero communities.
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_NoEdges_ReturnsZeroCommunities()
    {
        // Arrange: add nodes but no edges
        for (var i = 0; i < 5; i++)
        {
            this.graphStore.AddNode(new HypergraphNode
            {
                Id = $"isolated-{i}",
                Name = $"IsolatedNode{i}",
                NodeType = "Method",
                Language = "C#",
                FilePath = $"src/Isolated{i}.cs"
            });
        }

        // Act
        var result = await this.engine.DetectCommunitiesAsync();

        // Assert
        result.CommunitiesDetected.Should().Be(0);
        result.TopicNodesCreated.Should().Be(0);
        result.ClusterEdgesCreated.Should().Be(0);
    }

    /// <summary>
    /// Verifies that LLM failures propagate as LlmGenerationException and
    /// do not leave the graph in a partially modified state.
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_LlmFailure_ThrowsAndLeavesGraphIntact()
    {
        // Arrange: set up a cluster with enough nodes for min community size (5),
        // then configure LLM to fail
        var cluster = CreateClusterNodes("f", 8, "FailService");
        foreach (var node in cluster)
        {
            this.graphStore.AddNode(node);
        }

        AddIntraClusterEdges(cluster, "CALLS", weight: 5.0);

        // First run succeeds to establish existing Topic nodes
        await this.engine.DetectCommunitiesAsync();
        var nodesBeforeFailure = (await this.graphStore.GetAllNodesAsync()).Count;

        // Configure LLM to fail
        this.llmClient.ShouldFail = true;

        // Act & Assert: exception propagates
        var act = () => this.engine.DetectCommunitiesAsync();
        await act.Should().ThrowAsync<LlmGenerationException>();

        // Graph should still have its previous Topic nodes (atomicity)
        var nodesAfterFailure = (await this.graphStore.GetAllNodesAsync()).Count;
        nodesAfterFailure.Should().Be(nodesBeforeFailure,
            "graph should be unchanged when LLM summary generation fails");
    }

    /// <summary>
    /// Verifies that MinCommunitySize is read from MemoryOptions, not hardcoded.
    /// A small cluster below MinCommunitySize should be skipped.
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_RespectsMinCommunitySizeOption()
    {
        // Arrange: create a small cluster of 3 nodes (below default MinCommunitySize=5)
        var smallCluster = CreateClusterNodes("s", 3, "SmallService");
        foreach (var node in smallCluster)
        {
            this.graphStore.AddNode(node);
        }

        AddIntraClusterEdges(smallCluster, "CALLS", weight: 5.0);

        // Act with default MinCommunitySize=5 (cluster has only 3 nodes)
        var result = await this.engine.DetectCommunitiesAsync();

        // Assert: cluster too small → no topic nodes
        result.CommunitiesDetected.Should().Be(0);
        result.TopicNodesCreated.Should().Be(0);
    }

    /// <summary>
    /// Verifies that lowering MinCommunitySize via MemoryOptions allows
    /// smaller clusters to form Topic nodes.
    /// </summary>
    [TestMethod]
    public async Task DetectCommunitiesAsync_CustomMinCommunitySize_AllowsSmallerClusters()
    {
        // Arrange: use a lower MinCommunitySize=2
        var customEngine = new EvolutionEngine(
            this.graphStore,
            this.llmClient,
            Microsoft.Extensions.Options.Options.Create(new MemoryOptions { MinCommunitySize = 2 }),
            NullLogger<EvolutionEngine>.Instance);

        var smallCluster = CreateClusterNodes("sm", 3, "TinyService");
        foreach (var node in smallCluster)
        {
            this.graphStore.AddNode(node);
        }

        AddIntraClusterEdges(smallCluster, "CALLS", weight: 5.0);

        // Act
        var result = await customEngine.DetectCommunitiesAsync();

        // Assert: cluster of 3 meets MinCommunitySize=2
        result.CommunitiesDetected.Should().BeGreaterThanOrEqualTo(1);
        result.TopicNodesCreated.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Validates that the non-throwing RecordExpertiseAsync stub produces
    /// a valid ExpertiseEntry (integration risk: other workstreams calling this).
    /// </summary>
    [TestMethod]
    public async Task RecordExpertiseAsync_Stub_ReturnsValidEntry()
    {
        var entry = await this.engine.RecordExpertiseAsync(
            "/src/Service.cs::Service.Process",
            ExpertiseKind.Pitfall,
            "Null check missing",
            "Process method does not validate input parameter",
            "test-agent");

        entry.Should().NotBeNull();
        entry.Id.Should().StartWith("expertise:pitfall:");
        entry.Kind.Should().Be(ExpertiseKind.Pitfall);
        entry.Title.Should().Be("Null check missing");
    }

    /// <summary>
    /// Validates that the non-throwing ConsolidateAsync stub returns a
    /// safe empty result.
    /// </summary>
    [TestMethod]
    public async Task ConsolidateAsync_Stub_ReturnsSafeDefault()
    {
        var result = await this.engine.ConsolidateAsync(new ConsolidationOptions());
        result.MergedCount.Should().Be(0);
        result.CandidatesPaired.Should().Be(0);
        result.MergedEntryIds.Should().BeEmpty();
    }

    /// <summary>
    /// Validates that the non-throwing DecayStaleExpertiseAsync stub returns
    /// a safe empty result.
    /// </summary>
    [TestMethod]
    public async Task DecayStaleExpertiseAsync_Stub_ReturnsSafeDefault()
    {
        var result = await this.engine.DecayStaleExpertiseAsync();
        result.EntriesEvaluated.Should().Be(0);
        result.EntriesDecayed.Should().Be(0);
        result.EntriesArchived.Should().Be(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<HypergraphNode> CreateClusterNodes(
        string prefix, int count, string scope)
    {
        var nodes = new List<HypergraphNode>();
        for (var i = 0; i < count; i++)
        {
            nodes.Add(new HypergraphNode
            {
                Id = $"{prefix}-{i}",
                Name = $"{scope}.Method{i}",
                NodeType = "Method",
                Language = "C#",
                FilePath = $"src/{scope}.cs",
                Scope = scope,
                LineStart = i * 10
            });
        }

        return nodes;
    }

    /// <summary>
    /// Adds intra-cluster edges as a complete clique using canonical SemanticRelation
    /// metadata (architecture §2.2: edges always carry <c>Metadata["SemanticRelation"]</c>).
    /// Complete connectivity ensures Louvain keeps the cluster as one community.
    /// </summary>
    private void AddIntraClusterEdges(
        List<HypergraphNode> nodes,
        string semanticRelation,
        double weight)
    {
        // Complete clique: connect every pair of nodes for maximum density
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var edgeId = $"edge-{nodes[i].Id}-{nodes[j].Id}";
                this.graphStore.AddEdge(new Hyperedge
                {
                    Id = edgeId,
                    EdgeType = HyperedgeType.MemoryRelation,
                    NodeIds = [nodes[i].Id, nodes[j].Id],
                    Roles = new Dictionary<string, string>
                    {
                        [nodes[i].Id] = "caller",
                        [nodes[j].Id] = "callee"
                    },
                    Weight = weight,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["SemanticRelation"] = semanticRelation,
                        ["MemoryLayer"] = semanticRelation == "SIMILAR_TO" ? "semantic" : "call_chain"
                    }
                });
            }
        }
    }

    private void AddInterClusterEdge(
        HypergraphNode node1,
        HypergraphNode node2,
        string semanticRelation,
        double weight)
    {
        var edgeId = $"inter-{node1.Id}-{node2.Id}";
        this.graphStore.AddEdge(new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = [node1.Id, node2.Id],
            Roles = new Dictionary<string, string>
            {
                [node1.Id] = "caller",
                [node2.Id] = "callee"
            },
            Weight = weight,
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = semanticRelation,
                ["MemoryLayer"] = "call_chain"
            }
        });
    }
}