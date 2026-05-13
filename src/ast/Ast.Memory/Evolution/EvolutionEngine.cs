// -----------------------------------------------------------------------
// <copyright file="EvolutionEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Evolution engine implementing write-back, consolidation, decay, and community
/// detection. This stage implements <see cref="IEvolutionEngine.DetectCommunitiesAsync"/>
/// using the Louvain algorithm over CALLS/SIMILAR_TO edges.
/// </summary>
public sealed class EvolutionEngine : IEvolutionEngine
{
    private readonly IMemoryGraphStore graphStore;
    private readonly ILlmClient llmClient;
    private readonly MemoryOptions memoryOptions;
    private readonly ILogger<EvolutionEngine> logger;

    /// <summary>
    /// Semantic relation constant for CALLS edges.
    /// </summary>
    internal const string CallsRelation = "CALLS";

    /// <summary>
    /// Semantic relation constant for SIMILAR_TO edges.
    /// </summary>
    internal const string SimilarToRelation = "SIMILAR_TO";

    /// <summary>
    /// Semantic relation constant for BELONGS_TO_CLUSTER edges.
    /// </summary>
    internal const string BelongsToClusterRelation = "BELONGS_TO_CLUSTER";

    /// <summary>
    /// Node type for Topic summary nodes.
    /// </summary>
    internal const string TopicNodeType = "Topic";

    public EvolutionEngine(
        IMemoryGraphStore graphStore,
        ILlmClient llmClient,
        IOptions<MemoryOptions> memoryOptions,
        ILogger<EvolutionEngine> logger)
    {
        this.graphStore = graphStore;
        this.llmClient = llmClient;
        this.memoryOptions = memoryOptions.Value;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ExpertiseEntry> RecordExpertiseAsync(
        string targetNodeId,
        ExpertiseKind kind,
        string title,
        string body,
        string sourceAgent,
        string scope = "exact",
        IReadOnlyList<Citation>? evidence = null,
        CancellationToken ct = default)
    {
        // Minimal implementation — full logic in Expertise Write-Back workstream (Stage 5.1)
        var contentHash = ComputeHash8(title + body + targetNodeId);
        var entry = new ExpertiseEntry
        {
            Id = $"expertise:{kind.ToString().ToLowerInvariant()}:{contentHash}",
            Kind = kind,
            Title = title,
            Body = body,
            TargetNodeId = targetNodeId,
            Scope = scope,
            SourceAgent = sourceAgent,
            Evidence = evidence ?? [],
            ContentHash = contentHash
        };

        await this.graphStore.UpsertExpertiseAsync(entry, ct);
        return entry;
    }

    /// <inheritdoc/>
    public Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationOptions options,
        CancellationToken ct = default)
    {
        // Stub — full implementation in ConsolidationService workstream (Stage 5.2)
        this.logger.LogDebug("ConsolidateAsync stub invoked; returning empty result");
        return Task.FromResult(new ConsolidationResult(0, 0, []));
    }

    /// <inheritdoc/>
    public Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken ct = default)
    {
        // Stub — full implementation in DecayService workstream (Stage 5.3)
        this.logger.LogDebug("DecayStaleExpertiseAsync stub invoked; returning empty result");
        return Task.FromResult(new DecayResult(0, 0, 0));
    }

    /// <inheritdoc/>
    public Task RestoreArchivedExpertiseAsync(
        string expertiseId,
        double restoredConfidence = 0.5,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Expertise Write-Back workstream (Stage 5.1)
        this.logger.LogDebug("RestoreArchivedExpertiseAsync stub invoked for {Id}", expertiseId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<CommunityResult> DetectCommunitiesAsync(CancellationToken ct = default)
    {
        this.logger.LogInformation("Starting community detection via Louvain algorithm");

        // 1. Fetch all code nodes (exclude Topic nodes from previous runs)
        var allNodes = await this.graphStore.GetAllNodesAsync(ct: ct);
        var codeNodes = allNodes
            .Where(n => n.NodeType != TopicNodeType)
            .ToList();

        if (codeNodes.Count == 0)
        {
            this.logger.LogWarning("No code nodes found; skipping community detection");
            return new CommunityResult(0, 0, 0);
        }

        // 2. Fetch edges — bridge canonical AST edges AND memory-layer edges.
        //    GetEdgesBySemanticRelationAsync finds edges with Metadata["SemanticRelation"]
        //    matching "CALLS"/"SIMILAR_TO".
        var semanticEdges = await this.graphStore.GetEdgesBySemanticRelationAsync(
            [CallsRelation, SimilarToRelation],
            ct);

        // 2b. Also fetch edges by canonical HyperedgeType to bridge existing graphs
        //     where SemanticRelation metadata is not set (e.g., edges created by
        //     Ast.GraphDb.Neo4jGraphStore using MethodInvocation/CmdletCall types).
        var canonicalCallTypes = SemanticRelationMap.ToHyperedgeTypes([CallsRelation, SimilarToRelation]);
        IReadOnlyList<Hyperedge> typeBasedEdges = canonicalCallTypes.Count > 0
            ? await this.graphStore.GetEdgesByTypeAsync(canonicalCallTypes, ct)
            : [];

        // Merge and deduplicate (semantic edges first, then type-based that aren't already included)
        var seenEdgeIds = new HashSet<string>(semanticEdges.Select(e => e.Id));
        var allEdges = new List<Hyperedge>(semanticEdges);
        foreach (var edge in typeBasedEdges)
        {
            if (seenEdgeIds.Add(edge.Id))
            {
                allEdges.Add(edge);
            }
        }

        // 3. Build weighted edge list for Louvain
        var nodeIdSet = new HashSet<string>(codeNodes.Select(n => n.Id));
        var edgeList = new List<(string Source, string Target, double Weight)>();

        foreach (var edge in allEdges)
        {
            AddEdgePairs(edge, nodeIdSet, edgeList);
        }

        if (edgeList.Count == 0)
        {
            this.logger.LogWarning("No CALLS/SIMILAR_TO edges found; skipping community detection");
            return new CommunityResult(0, 0, 0);
        }

        // 4. Run multilevel Louvain community detection
        var detector = new LouvainCommunityDetector();
        var nodeIds = codeNodes.Select(n => n.Id).ToList();
        var communities = detector.Detect(nodeIds, edgeList);

        // 5. Group nodes by community
        var communityGroups = communities
            .GroupBy(kv => kv.Value)
            .Where(g => g.Count() >= this.memoryOptions.MinCommunitySize)
            .ToList();

        this.logger.LogInformation(
            "Louvain detected {Count} communities from {NodeCount} nodes and {EdgeCount} edges",
            communityGroups.Count, nodeIds.Count, edgeList.Count);

        if (communityGroups.Count == 0)
        {
            return new CommunityResult(0, 0, 0);
        }

        // 6. Pre-generate ALL summaries before mutating the graph (atomicity:
        //    if any LLM call fails, the exception propagates and the graph is
        //    left unchanged with its previous community state intact).
        var topicPlans = new List<(
            string TopicId,
            string TopicHash,
            List<string> MemberNodeIds,
            List<HypergraphNode> MemberNodes,
            string Summary)>();

        foreach (var group in communityGroups)
        {
            var memberNodeIds = group.Select(kv => kv.Key).OrderBy(id => id).ToList();
            var memberNodes = codeNodes.Where(n => memberNodeIds.Contains(n.Id)).ToList();

            var topicHash = ComputeHash8(string.Join(",", memberNodeIds));
            var topicId = $"topic:{topicHash}";

            // LLM call — throws LlmGenerationException on failure
            var summary = await this.GenerateTopicSummaryAsync(memberNodes, ct);

            topicPlans.Add((topicId, topicHash, memberNodeIds, memberNodes, summary));
        }

        // 7. All summaries generated successfully — now safe to mutate the graph.
        //    Remove existing Topic nodes and BELONGS_TO_CLUSTER edges for idempotency.
        await this.graphStore.RemoveEdgesBySemanticRelationAsync(BelongsToClusterRelation, ct);
        await this.graphStore.RemoveNodesByTypeAsync(TopicNodeType, ct);

        // 8. Create Topic nodes and BELONGS_TO_CLUSTER edges
        var topicNodesCreated = 0;
        var clusterEdgesCreated = 0;

        foreach (var (topicId, topicHash, memberNodeIds, memberNodes, summary) in topicPlans)
        {
            var topicNode = new HypergraphNode
            {
                Id = topicId,
                Name = $"Topic {topicHash}",
                NodeType = TopicNodeType,
                Language = "mixed",
                FilePath = string.Empty,
                Metadata = new Dictionary<string, object?>
                {
                    ["MemoryLayer"] = "semantic",
                    ["Summary"] = summary,
                    ["MemberCount"] = memberNodes.Count
                }
            };

            await this.graphStore.UpsertNodeAsync(topicNode, ct);
            topicNodesCreated++;

            foreach (var memberId in memberNodeIds)
            {
                var edgeId = $"btc:{ComputeHash8(memberId + "→" + topicId)}";
                var clusterEdge = new Hyperedge
                {
                    Id = edgeId,
                    EdgeType = HyperedgeType.MemoryRelation,
                    NodeIds = [memberId, topicId],
                    Roles = new Dictionary<string, string>
                    {
                        [memberId] = "member",
                        [topicId] = "cluster"
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        ["SemanticRelation"] = BelongsToClusterRelation,
                        ["MemoryLayer"] = "semantic"
                    }
                };

                await this.graphStore.UpsertEdgeAsync(clusterEdge, ct);
                clusterEdgesCreated++;
            }
        }

        this.logger.LogInformation(
            "Community detection complete: {Communities} communities, {Topics} topic nodes, {Edges} cluster edges",
            communityGroups.Count, topicNodesCreated, clusterEdgesCreated);

        return new CommunityResult(communityGroups.Count, topicNodesCreated, clusterEdgesCreated);
    }

    /// <exception cref="LlmGenerationException">
    /// Thrown when the LLM client fails or returns empty. No fallback.
    /// </exception>
    private async Task<string> GenerateTopicSummaryAsync(
        List<HypergraphNode> memberNodes,
        CancellationToken ct)
    {
        var nodeDescriptions = memberNodes
            .Take(20) // Limit to avoid overly long prompts
            .Select(n => $"- {n.NodeType} '{n.Name}' in {n.FilePath}")
            .ToList();

        var prompt = $"""
            Summarize the following cluster of code symbols into a concise topic description
            (1-2 sentences). Focus on the common responsibility or architectural role.

            Cluster members:
            {string.Join("\n", nodeDescriptions)}

            Topic summary:
            """;

        // Propagates LlmGenerationException — no fallback summary
        return await this.llmClient.GenerateAsync(prompt, ct);
    }

    private static void AddEdgePairs(
        Hyperedge edge,
        HashSet<string> validNodeIds,
        List<(string Source, string Target, double Weight)> edgeList)
    {
        // For hyperedges with 2 nodes, add a single edge
        // For hyperedges with >2 nodes, add pairwise edges
        var relevantNodes = edge.NodeIds.Where(validNodeIds.Contains).ToList();
        if (relevantNodes.Count < 2)
        {
            return;
        }

        for (var i = 0; i < relevantNodes.Count - 1; i++)
        {
            for (var j = i + 1; j < relevantNodes.Count; j++)
            {
                edgeList.Add((relevantNodes[i], relevantNodes[j], edge.Weight));
            }
        }
    }

    internal static string ComputeHash8(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}