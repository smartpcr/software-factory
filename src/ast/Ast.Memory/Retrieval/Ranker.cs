// -----------------------------------------------------------------------
// <copyright file="Ranker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Retrieval;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Scores candidate memory nodes using a 5-signal weighted model:
/// structural (0.30), semantic (0.25), centrality (0.20), recency (0.15), expertise (0.10).
/// Produces a <see cref="List{ScoredMemoryNode}"/> sorted by descending composite score.
/// </summary>
public sealed class Ranker
{
    private readonly IMemoryGraphStore graphStore;
    private readonly IEmbeddingGenerator? embeddingGenerator;
    private readonly MemoryOptions options;
    private readonly ILogger<Ranker> logger;

    public Ranker(
        IMemoryGraphStore graphStore,
        IOptions<MemoryOptions> options,
        ILogger<Ranker> logger,
        IEmbeddingGenerator? embeddingGenerator = null)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.embeddingGenerator = embeddingGenerator;
    }

    /// <summary>
    /// Ranks candidate nodes using the 5-signal model.
    /// </summary>
    /// <param name="candidates">Candidate nodes from retrieval modes.</param>
    /// <param name="context">Pre-computed ranking context (BFS distances, query embedding, HEAD index).</param>
    /// <param name="weights">Optional scoring weight override. Defaults to <see cref="MemoryOptions.Weights"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nodes scored and sorted by descending composite score.</returns>
    public async Task<List<ScoredMemoryNode>> RankAsync(
        IReadOnlyList<MemoryNode> candidates,
        RankingContext context,
        ScoringWeights? weights = null,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        weights ??= this.options.Weights;

        var embeddingAvailable = await this.IsEmbeddingAvailableAsync(context, cancellationToken);
        var effectiveWeights = embeddingAvailable ? weights : ReNormalizeWithoutSemantic(weights);

        this.logger.LogDebug(
            "Ranking {Count} candidates. Embedding available: {Available}. Weights: S={S:F2} Se={Se:F2} C={C:F2} R={R:F2} E={E:F2}",
            candidates.Count,
            embeddingAvailable,
            effectiveWeights.Structural,
            effectiveWeights.Semantic,
            effectiveWeights.Centrality,
            effectiveWeights.Recency,
            effectiveWeights.Expertise);

        // Single pass over edges: compute fan-in (centrality) and max Confidence (expertise)
        var (fanInCounts, expertiseScores) = await this.ComputeEdgeSignalsAsync(candidates, cancellationToken);
        var maxFanIn = fanInCounts.Values.DefaultIfEmpty(0).Max();

        // Pre-compute max distance for recency normalization
        var maxDistance = ComputeMaxDistance(candidates, context.HeadCommitIndex);

        var scored = new List<ScoredMemoryNode>(candidates.Count);

        foreach (var node in candidates)
        {
            var nodeId = node.Inner.Id;

            var structural = ComputeStructuralScore(node, context);
            var semantic = embeddingAvailable ? ComputeSemanticScore(node, context) : 0.0;
            var centrality = ComputeCentralityScore(nodeId, fanInCounts, maxFanIn);
            var recency = ComputeRecencyScore(node, context.HeadCommitIndex, maxDistance);
            var expertise = expertiseScores.GetValueOrDefault(nodeId, 0.0);

            var composite =
                (structural * effectiveWeights.Structural) +
                (semantic * effectiveWeights.Semantic) +
                (centrality * effectiveWeights.Centrality) +
                (recency * effectiveWeights.Recency) +
                (expertise * effectiveWeights.Expertise);

            scored.Add(new ScoredMemoryNode
            {
                Node = node,
                CompositeScore = composite,
                StructuralScore = structural,
                SemanticScore = semantic,
                CentralityScore = centrality,
                RecencyScore = recency,
                ExpertiseScore = expertise,
            });
        }

        scored.Sort((a, b) => b.CompositeScore.CompareTo(a.CompositeScore));
        return scored;
    }

    /// <summary>
    /// Re-normalizes weights when semantic signal is unavailable, setting
    /// semantic weight to 0 and distributing its share among the remaining 4 signals.
    /// </summary>
    internal static ScoringWeights ReNormalizeWithoutSemantic(ScoringWeights weights)
    {
        var remainingSum = weights.Structural + weights.Centrality + weights.Recency + weights.Expertise;
        if (remainingSum <= 0.0)
        {
            return new ScoringWeights
            {
                Structural = 0.0,
                Semantic = 0.0,
                Centrality = 0.0,
                Recency = 0.0,
                Expertise = 0.0,
            };
        }

        var factor = 1.0 / remainingSum;
        return new ScoringWeights
        {
            Structural = weights.Structural * factor,
            Semantic = 0.0,
            Centrality = weights.Centrality * factor,
            Recency = weights.Recency * factor,
            Expertise = weights.Expertise * factor,
        };
    }

    /// <summary>
    /// Structural signal: inverse BFS distance from seed. 1/(1+d), bounded [0,1].
    /// </summary>
    internal static double ComputeStructuralScore(MemoryNode node, RankingContext context)
    {
        if (context.BfsDistances.TryGetValue(node.Inner.Id, out var distance))
        {
            return 1.0 / (1.0 + distance);
        }

        return 0.0;
    }

    /// <summary>
    /// Semantic signal: cosine similarity between query embedding and node embedding.
    /// </summary>
    internal static double ComputeSemanticScore(MemoryNode node, RankingContext context)
    {
        if (context.QueryEmbedding is not { Length: > 0 })
        {
            return 0.0;
        }

        if (!node.Inner.Metadata.TryGetValue(MemoryMetadataKeys.Embedding, out var embObj)
            || embObj is not float[] nodeEmbedding
            || nodeEmbedding.Length == 0)
        {
            return 0.0;
        }

        return CosineSimilarity(context.QueryEmbedding, nodeEmbedding);
    }

    /// <summary>
    /// Centrality signal: normalized fan-in count (callers via CALLS edges).
    /// </summary>
    internal static double ComputeCentralityScore(
        string nodeId,
        IReadOnlyDictionary<string, int> fanInCounts,
        int maxFanIn)
    {
        if (maxFanIn <= 0)
        {
            return 0.0;
        }

        var count = fanInCounts.GetValueOrDefault(nodeId, 0);
        return (double)count / maxFanIn;
    }

    /// <summary>
    /// Recency signal: 1.0 − (HEAD_idx − node_sinceIdx) / max_distance, clamped [0,1].
    /// External boundary nodes (null VersionRange) get a neutral 0.5.
    /// </summary>
    internal static double ComputeRecencyScore(MemoryNode node, int headCommitIndex, int maxDistance)
    {
        var sinceIdx = node.Version?.SinceIndex;
        if (!sinceIdx.HasValue)
        {
            return 0.5;
        }

        if (maxDistance <= 0)
        {
            return 1.0;
        }

        var distance = headCommitIndex - sinceIdx.Value;
        var score = 1.0 - ((double)distance / maxDistance);
        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// Computes cosine similarity between two embedding vectors.
    /// </summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0.0, magA = 0.0, magB = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            magA += a[i] * (double)a[i];
            magB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denominator < 1e-10 ? 0.0 : dot / denominator;
    }

    /// <summary>
    /// Extracts the Confidence value from an APPLIES_TO edge's metadata.
    /// </summary>
    internal static double ExtractConfidenceFromEdge(Hyperedge edge)
    {
        if (!edge.Metadata.TryGetValue(MemoryMetadataKeys.Confidence, out var confObj))
        {
            return 0.0;
        }

        return confObj switch
        {
            double d => Math.Clamp(d, 0.0, 1.0),
            float f => Math.Clamp(f, 0.0f, 1.0f),
            int i => Math.Clamp(i, 0, 1),
            _ => 0.0,
        };
    }

    private async Task<bool> IsEmbeddingAvailableAsync(
        RankingContext context,
        CancellationToken cancellationToken)
    {
        if (this.embeddingGenerator is null || context.QueryEmbedding is null)
        {
            return false;
        }

        try
        {
            return await this.embeddingGenerator.IsAvailableAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Embedding availability check failed; falling back to non-semantic scoring.");
            return false;
        }
    }

    /// <summary>
    /// Single pass over edges for each candidate: computes fan-in counts for
    /// centrality and max Confidence from APPLIES_TO edges for expertise.
    /// Failures from the graph store are propagated, not swallowed.
    /// </summary>
    private async Task<(Dictionary<string, int> FanInCounts, Dictionary<string, double> ExpertiseScores)> ComputeEdgeSignalsAsync(
        IReadOnlyList<MemoryNode> candidates,
        CancellationToken cancellationToken)
    {
        var fanInCounts = new Dictionary<string, int>(candidates.Count);
        var expertiseScores = new Dictionary<string, double>(candidates.Count);

        foreach (var node in candidates)
        {
            var nodeId = node.Inner.Id;
            var edges = await this.graphStore.GetEdgesForNodeAsync(nodeId, null, cancellationToken);

            var fanIn = 0;
            var maxConfidence = 0.0;

            foreach (var edge in edges)
            {
                if (!edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var relObj)
                    || relObj is not string rel)
                {
                    continue;
                }

                if (rel == "CALLS"
                    && edge.Roles.TryGetValue(nodeId, out var callRole)
                    && callRole == "callee")
                {
                    fanIn++;
                }

                if (rel == "APPLIES_TO"
                    && edge.Roles.TryGetValue(nodeId, out var appliesToRole)
                    && appliesToRole == "target")
                {
                    var confidence = ExtractConfidenceFromEdge(edge);
                    if (confidence > maxConfidence)
                    {
                        maxConfidence = confidence;
                    }
                }
            }

            fanInCounts[nodeId] = fanIn;
            expertiseScores[nodeId] = maxConfidence;
        }

        return (fanInCounts, expertiseScores);
    }

    private static int ComputeMaxDistance(IReadOnlyList<MemoryNode> candidates, int headCommitIndex)
    {
        var maxDistance = 0;
        foreach (var node in candidates)
        {
            var sinceIdx = node.Version?.SinceIndex;
            if (sinceIdx.HasValue)
            {
                var distance = headCommitIndex - sinceIdx.Value;
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                }
            }
        }

        return maxDistance > 0 ? maxDistance : 1;
    }
}