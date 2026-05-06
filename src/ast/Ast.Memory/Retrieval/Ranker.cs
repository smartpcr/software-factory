// -----------------------------------------------------------------------
// <copyright file="Ranker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Retrieval;

using System.Diagnostics;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Scores candidate nodes using a 5-signal weighted model (architecture.md §2.4).
/// </summary>
internal sealed class Ranker
{
    private readonly IMemoryGraphStore graphStore;
    private readonly ILogger logger;

    public Ranker(IMemoryGraphStore graphStore, ILogger logger)
    {
        this.graphStore = graphStore;
        this.logger = logger;
    }

    /// <summary>
    /// Scores and ranks candidate nodes, returning them sorted by descending composite score.
    /// </summary>
    /// <param name="candidates">Candidate nodes with BFS depth and optional ANN score.</param>
    /// <param name="queryEmbedding">Embedding of the query text (null if unavailable).</param>
    /// <param name="weights">Scoring weights to apply.</param>
    /// <param name="headCommitIndex">The HEAD commit index for recency calculation.</param>
    /// <param name="maxBfsDepth">Maximum BFS depth observed, for normalizing structural score.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<ScoredMemoryNode>> RankAsync(
        IReadOnlyList<(MemoryNode Node, int BfsDepth, float AnnScore)> candidates,
        float[]? queryEmbedding,
        ScoringWeights weights,
        int headCommitIndex,
        int maxBfsDepth,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var effectiveWeights = queryEmbedding == null
            ? weights.WithoutSemantic()
            : weights;

        var scored = new List<ScoredMemoryNode>(candidates.Count);

        foreach (var (node, bfsDepth, annScore) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var structuralScore = ComputeStructuralScore(bfsDepth, maxBfsDepth);
            var semanticScore = ComputeSemanticScore(node, queryEmbedding, annScore);
            var centralityScore = await this.ComputeCentralityScoreAsync(node, cancellationToken).ConfigureAwait(false);
            var recencyScore = ComputeRecencyScore(node, headCommitIndex);
            var expertiseScore = await this.ComputeExpertiseScoreAsync(node, cancellationToken).ConfigureAwait(false);

            var composite =
                (effectiveWeights.Structural * structuralScore) +
                (effectiveWeights.Semantic * semanticScore) +
                (effectiveWeights.Centrality * centralityScore) +
                (effectiveWeights.Recency * recencyScore) +
                (effectiveWeights.Expertise * expertiseScore);

            scored.Add(new ScoredMemoryNode
            {
                Node = node,
                CompositeScore = composite,
                StructuralScore = structuralScore,
                SemanticScore = semanticScore,
                CentralityScore = centralityScore,
                RecencyScore = recencyScore,
                ExpertiseScore = expertiseScore
            });
        }

        scored.Sort((a, b) => b.CompositeScore.CompareTo(a.CompositeScore));
        return scored;
    }

    private static double ComputeStructuralScore(int bfsDepth, int maxBfsDepth)
    {
        // Nodes not discovered by BFS (depth < 0) get zero structural score
        if (bfsDepth < 0)
        {
            return 0.0;
        }

        if (maxBfsDepth <= 0)
        {
            return bfsDepth == 0 ? 1.0 : 0.0;
        }

        return Math.Max(0.0, 1.0 - ((double)bfsDepth / maxBfsDepth));
    }

    /// <summary>
    /// Computes semantic score, always preferring the ANN score from vector search
    /// when available (annScore &gt; 0). This ensures that IVectorStore.SearchAsync
    /// similarity scores are never discarded in favour of node-metadata embeddings.
    /// Fallback to node-level cosine similarity only when no ANN score is present
    /// (e.g., for nodes discovered purely by structural or call-chain traversal).
    /// </summary>
    private static double ComputeSemanticScore(MemoryNode node, float[]? queryEmbedding, float annScore)
    {
        // Always prefer ANN score from IVectorStore.SearchAsync — this preserves
        // the vector store's similarity ranking and avoids discarding ANN relevance
        if (annScore > 0)
        {
            return annScore;
        }

        if (queryEmbedding == null)
        {
            return 0.0;
        }

        // Fallback: use node-level embedding (only for nodes not found by ANN)
        if (!node.Inner.Metadata.TryGetValue(MemoryMetadataKeys.Embedding, out var embeddingObj)
            || embeddingObj is not float[] nodeEmbedding
            || nodeEmbedding.Length != queryEmbedding.Length)
        {
            return 0.0;
        }

        return CosineSimilarity(queryEmbedding, nodeEmbedding);
    }

    private static double ComputeRecencyScore(MemoryNode node, int headCommitIndex)
    {
        if (node.Version == null)
        {
            return 0.5; // External boundary nodes get a neutral score
        }

        if (headCommitIndex <= 0)
        {
            return 1.0;
        }

        var distance = headCommitIndex - node.Version.SinceIndex;
        var score = 1.0 - ((double)distance / headCommitIndex);
        return Math.Clamp(score, 0.0, 1.0);
    }

    private async Task<double> ComputeCentralityScoreAsync(MemoryNode node, CancellationToken ct)
    {
        try
        {
            // Count incoming CALLS edges (fan-in) using SemanticRelation filter exclusively.
            // Fan-in = edges where this node is in the "callee" role (canonical CALLS role convention),
            // not all connected CALLS edges. Uses GetEdgesForNodeAsync from IGraphStore base.
            var allEdges = await this.graphStore.GetEdgesForNodeAsync(node.Inner.Id, cancellationToken: ct).ConfigureAwait(false);
            var fanIn = allEdges.Count(e =>
                e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var rel)
                && rel is string s && s == SemanticRelations.Calls
                && e.Roles.TryGetValue(node.Inner.Id, out var role)
                && role == "callee");
            // Normalize with diminishing returns: score = fanIn / (fanIn + 10)
            return (double)fanIn / (fanIn + 10.0);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to compute centrality for {NodeId}", node.Inner.Id);
            return 0.0;
        }
    }

    /// <summary>
    /// Computes expertise score by calling <see cref="IMemoryGraphStore.GetExpertiseForNodeAsync"/>
    /// (architecture.md §5.1). Returns the maximum effective confidence of non-archived
    /// expertise entries associated with the node.
    /// </summary>
    private async Task<double> ComputeExpertiseScoreAsync(MemoryNode node, CancellationToken ct)
    {
        try
        {
            var entries = await this.graphStore.GetExpertiseForNodeAsync(
                node.Inner.Id, 0.0, ct).ConfigureAwait(false);

            if (entries.Count == 0)
            {
                return 0.0;
            }

            return entries.Max(e => e.Confidence);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to compute expertise score for {NodeId}", node.Inner.Id);
            return 0.0;
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        Debug.Assert(a.Length == b.Length, "Embedding dimensions must match");

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0.0;
    }
}