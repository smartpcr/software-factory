// -----------------------------------------------------------------------
// <copyright file="InMemoryRetrievalEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.InMemory;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// In-memory retrieval engine that searches the graph store for matching nodes,
/// retrieves associated expertise, and builds context markdown.
/// </summary>
public sealed class InMemoryRetrievalEngine : IMemoryRetrievalEngine
{
    private readonly IMemoryGraphStore graphStore;

    public InMemoryRetrievalEngine(IMemoryGraphStore graphStore)
    {
        this.graphStore = graphStore;
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct = default)
    {
        var tokenBudget = Math.Clamp(request.TokenBudget, 1, 32000);
        var matchedNodes = new List<ScoredMemoryNode>();
        var queryTokens = (request.QueryText ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        // Search nodes by seed IDs first
        if (request.SeedNodeIds is { Count: > 0 })
        {
            foreach (var seedId in request.SeedNodeIds)
            {
                var node = await this.graphStore.GetNodeAsync(seedId, ct).ConfigureAwait(false);
                if (node != null)
                {
                    var memNode = WrapAsMemoryNode(node);
                    matchedNodes.Add(ScoreNode(memNode, queryTokens, isDirectMatch: true));
                }
            }
        }

        // Search nodes by query text (name matching)
        if (queryTokens.Length > 0)
        {
            var allNodes = await this.graphStore.GetNodesAsync(limit: 1000, cancellationToken: ct).ConfigureAwait(false);
            foreach (var node in allNodes)
            {
                if (matchedNodes.Any(m => m.Node.Inner.Id == node.Id))
                {
                    continue;
                }

                var embeddingText = node.GetEmbeddingText().ToLowerInvariant();
                var nameLC = node.Name.ToLowerInvariant();
                var matchCount = queryTokens.Count(t => nameLC.Contains(t) || embeddingText.Contains(t));
                if (matchCount > 0)
                {
                    var memNode = WrapAsMemoryNode(node);
                    matchedNodes.Add(ScoreNode(memNode, queryTokens, isDirectMatch: false));
                }
            }
        }

        // Sort by composite score descending
        matchedNodes.Sort((a, b) => b.CompositeScore.CompareTo(a.CompositeScore));

        // Build context markdown
        var contextBuilder = new StringBuilder();
        var tokensUsed = 0;

        // ## Structure section
        contextBuilder.AppendLine("## Structure");
        foreach (var scored in matchedNodes)
        {
            var nodeText = $"- {scored.Node.Inner.Name} ({scored.Node.Inner.NodeType})";
            var lineTokens = EstimateTokens(nodeText);
            if (tokensUsed + lineTokens > tokenBudget)
            {
                break;
            }

            contextBuilder.AppendLine(nodeText);
            tokensUsed += lineTokens;
        }

        // ## Known Expertise section
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("## Known Expertise");
        var expertiseAdded = false;
        foreach (var scored in matchedNodes)
        {
            var entries = await this.graphStore.GetExpertiseForNodeAsync(scored.Node.Inner.Id, ct: ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var expertiseText = $"### {entry.Kind}: {entry.Title}\n{entry.Body}\n";
                var lineTokens = EstimateTokens(expertiseText);
                if (tokensUsed + lineTokens > tokenBudget)
                {
                    break;
                }

                contextBuilder.AppendLine(expertiseText);
                tokensUsed += lineTokens;
                expertiseAdded = true;

                // Update expertise score on the node
                matchedNodes = matchedNodes.Select(m =>
                    m.Node.Inner.Id == scored.Node.Inner.Id
                        ? m with { ExpertiseScore = 1.0 }
                        : m).ToList();
            }
        }

        if (!expertiseAdded)
        {
            contextBuilder.AppendLine("(none)");
            tokensUsed += EstimateTokens("(none)");
        }

        var modesExecuted = new List<RetrievalMode> { request.Mode };

        return new RetrievalResult(
            Nodes: matchedNodes,
            ContextMarkdown: contextBuilder.ToString(),
            TokensUsed: tokensUsed,
            ModesExecuted: modesExecuted,
            Diagnostics: Array.Empty<string>());
    }

    private static MemoryNode WrapAsMemoryNode(Ast.Core.HypergraphNode node)
    {
        var layer = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var l) ? (string?)l : "structural";
        if (!node.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer))
        {
            node.Metadata[MemoryMetadataKeys.MemoryLayer] = layer;
        }

        var sinceCommit = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sc) ? (string?)sc : "HEAD";
        var sinceIndex = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si) ? Convert.ToInt32(si) : 1;

        return new MemoryNode(node, new VersionRange(sinceCommit!, sinceIndex, null, null));
    }

    private static ScoredMemoryNode ScoreNode(MemoryNode memNode, string[] queryTokens, bool isDirectMatch)
    {
        var nameLC = memNode.Inner.Name.ToLowerInvariant();
        var embeddingText = memNode.Inner.GetEmbeddingText().ToLowerInvariant();

        double structuralScore = isDirectMatch ? 1.0 : 0.5;
        double semanticScore = 0.0;

        if (queryTokens.Length > 0)
        {
            var matchCount = queryTokens.Count(t => nameLC.Contains(t) || embeddingText.Contains(t));
            semanticScore = (double)matchCount / queryTokens.Length;
        }

        double centralityScore = 0.5;
        double recencyScore = 1.0;
        double expertiseScore = 0.0;

        var weights = new ScoringWeights();
        var composite = (structuralScore * weights.Structural)
            + (semanticScore * weights.Semantic)
            + (centralityScore * weights.Centrality)
            + (recencyScore * weights.Recency)
            + (expertiseScore * weights.Expertise);

        return new ScoredMemoryNode(
            memNode,
            CompositeScore: composite,
            StructuralScore: structuralScore,
            SemanticScore: semanticScore,
            CentralityScore: centralityScore,
            RecencyScore: recencyScore,
            ExpertiseScore: expertiseScore);
    }

    private static int EstimateTokens(string text)
    {
        // Approximate: ~4 characters per token
        return Math.Max(1, (text.Length + 3) / 4);
    }
}
