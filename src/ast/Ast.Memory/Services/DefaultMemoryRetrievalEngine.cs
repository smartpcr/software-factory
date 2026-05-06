// -----------------------------------------------------------------------
// <copyright file="DefaultMemoryRetrievalEngine.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Services;

using System.Text;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Default retrieval engine that queries the memory graph store across four layers
/// (structural, call-chain, semantic, expertise) and returns ranked nodes with
/// mode-specific context markdown sections.
/// </summary>
public sealed class DefaultMemoryRetrievalEngine : IMemoryRetrievalEngine
{
    private readonly IMemoryGraphStore store;

    public DefaultMemoryRetrievalEngine(IMemoryGraphStore store)
    {
        this.store = store;
    }

    /// <inheritdoc/>
    public async Task<RetrievalResult> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken ct = default)
    {
        var stats = await this.store.GetMemoryStatisticsAsync(ct);
        var modesExecuted = new List<RetrievalMode>();
        var diagnostics = new List<string>
        {
            $"Graph has {stats.TotalNodes} nodes and {stats.TotalEdges} edges",
        };

        var weights = request.Weights ?? new ScoringWeights();
        var queryTerms = (request.QueryText ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Determine which modes to execute
        var modesToRun = request.Mode == RetrievalMode.Hybrid
            ? new[] { RetrievalMode.StructuralDrill, RetrievalMode.CallTrace, RetrievalMode.SemanticSearch, RetrievalMode.ExpertiseRecall }
            : new[] { request.Mode };

        var structuralNodes = new List<ScoredMemoryNode>();
        var callChainNodes = new List<ScoredMemoryNode>();
        var semanticNodes = new List<ScoredMemoryNode>();
        var expertiseNodes = new List<ScoredMemoryNode>();

        foreach (var mode in modesToRun)
        {
            ct.ThrowIfCancellationRequested();
            modesExecuted.Add(mode);

            switch (mode)
            {
                case RetrievalMode.StructuralDrill:
                {
                    var layerNodes = await this.store.GetNodesByLayerAsync("structural", request.AtCommitIndex, limit: 1000, ct: ct);
                    foreach (var node in layerNodes)
                    {
                        var score = ScoreNode(node, queryTerms, weights.Structural);
                        if (score > 0.0)
                        {
                            structuralNodes.Add(new ScoredMemoryNode(
                                new MemoryNode(node), score, StructuralScore: score,
                                SemanticScore: 0, CentralityScore: 0, RecencyScore: 0, ExpertiseScore: 0));
                        }
                    }

                    break;
                }

                case RetrievalMode.CallTrace:
                {
                    // Nodes are always in the "structural" layer; call_chain is an edge layer.
                    // Find structural nodes that participate in call_chain edges to surface call context.
                    var structNodes = await this.store.GetNodesByLayerAsync("structural", request.AtCommitIndex, limit: 1000, ct: ct);
                    foreach (var node in structNodes)
                    {
                        var score = ScoreNode(node, queryTerms, weights.Structural);
                        if (score > 0.0)
                        {
                            // Boost score based on call-chain connectivity
                            var callers = await this.store.GetCallersAsync(node.Id, depth: 1, ct);
                            var callees = await this.store.GetCalleesAsync(node.Id, depth: 1, ct);
                            var connectivity = callers.Count + callees.Count;

                            // Only include nodes that actually participate in call edges
                            if (connectivity > 0)
                            {
                                var centralityBoost = 1.0 + connectivity * 0.05;
                                score *= centralityBoost;

                                callChainNodes.Add(new ScoredMemoryNode(
                                    new MemoryNode(node), score, StructuralScore: 0,
                                    SemanticScore: 0, CentralityScore: centralityBoost - 1.0, RecencyScore: 0, ExpertiseScore: 0));
                            }
                        }
                    }

                    break;
                }

                case RetrievalMode.SemanticSearch:
                {
                    // All extracted nodes are in "structural" layer; text-match across them.
                    // Vector-based semantic search is deferred to a future stage.
                    var layerNodes = await this.store.GetNodesByLayerAsync("structural", request.AtCommitIndex, limit: 1000, ct: ct);
                    foreach (var node in layerNodes)
                    {
                        var score = ScoreNode(node, queryTerms, weights.Semantic);
                        if (score > 0.0)
                        {
                            semanticNodes.Add(new ScoredMemoryNode(
                                new MemoryNode(node), score, StructuralScore: 0,
                                SemanticScore: score, CentralityScore: 0, RecencyScore: 0, ExpertiseScore: 0));
                        }
                    }

                    break;
                }

                case RetrievalMode.ExpertiseRecall:
                {
                    // Query expertise entries across all structural nodes
                    var allNodes = await this.store.GetNodesByLayerAsync("structural", request.AtCommitIndex, limit: 5000, ct: ct);

                    foreach (var node in allNodes)
                    {
                        var entries = await this.store.GetExpertiseForNodeAsync(node.Id, ct: ct);
                        if (entries.Count > 0)
                        {
                            var score = ScoreNode(node, queryTerms, weights.Expertise);
                            var expertiseBoost = entries.Sum(e => e.Confidence) * 0.2;
                            score += expertiseBoost;
                            if (score > 0.0)
                            {
                                expertiseNodes.Add(new ScoredMemoryNode(
                                    new MemoryNode(node), score, StructuralScore: 0,
                                    SemanticScore: 0, CentralityScore: 0, RecencyScore: 0, ExpertiseScore: score));
                            }
                        }
                    }

                    break;
                }
            }
        }

        // Merge and deduplicate nodes
        var merged = new Dictionary<string, ScoredMemoryNode>();
        void MergeInto(List<ScoredMemoryNode> source)
        {
            foreach (var node in source)
            {
                var id = node.Node.Inner.Id;
                if (merged.TryGetValue(id, out var existing))
                {
                    merged[id] = new ScoredMemoryNode(
                        existing.Node,
                        existing.CompositeScore + node.CompositeScore,
                        existing.StructuralScore + node.StructuralScore,
                        existing.SemanticScore + node.SemanticScore,
                        existing.CentralityScore + node.CentralityScore,
                        existing.RecencyScore + node.RecencyScore,
                        existing.ExpertiseScore + node.ExpertiseScore);
                }
                else
                {
                    merged[id] = node;
                }
            }
        }

        MergeInto(structuralNodes);
        MergeInto(callChainNodes);
        MergeInto(semanticNodes);
        MergeInto(expertiseNodes);

        // Rank and budget-constrain
        var ranked = merged.Values
            .OrderByDescending(n => n.CompositeScore)
            .ToList();

        var budgetNodes = new List<ScoredMemoryNode>();
        var tokensUsed = 0;
        foreach (var node in ranked)
        {
            var nodeTokens = EstimateTokens(node);
            if (tokensUsed + nodeTokens > request.TokenBudget && budgetNodes.Count > 0)
            {
                break;
            }

            budgetNodes.Add(node);
            tokensUsed += nodeTokens;
        }

        diagnostics.Add($"Matched {ranked.Count} nodes, returned {budgetNodes.Count} within budget of {request.TokenBudget} tokens");

        // Build context markdown with mode-specific sections
        var contextMarkdown = BuildContextMarkdown(request, budgetNodes, structuralNodes, callChainNodes, semanticNodes, expertiseNodes, stats);

        return new RetrievalResult(
            Nodes: budgetNodes,
            ContextMarkdown: contextMarkdown,
            TokensUsed: tokensUsed,
            ModesExecuted: modesExecuted,
            Diagnostics: diagnostics);
    }

    private static string BuildContextMarkdown(
        RetrievalRequest request,
        List<ScoredMemoryNode> budgetNodes,
        List<ScoredMemoryNode> structural,
        List<ScoredMemoryNode> callChain,
        List<ScoredMemoryNode> semantic,
        List<ScoredMemoryNode> expertise,
        MemoryGraphStatistics stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Memory Context: {request.QueryText ?? "(no query)"}");
        sb.AppendLine();
        sb.AppendLine($"**Mode:** {request.Mode} | **Nodes:** {budgetNodes.Count} | **Graph:** {stats.TotalNodes} nodes, {stats.TotalEdges} edges");
        sb.AppendLine();

        // Structural drill section
        var structuralInBudget = budgetNodes.Where(n => n.StructuralScore > 0).ToList();
        if (structuralInBudget.Count > 0)
        {
            sb.AppendLine("## Structural Context");
            sb.AppendLine();
            foreach (var node in structuralInBudget.OrderByDescending(n => n.StructuralScore).Take(20))
            {
                sb.AppendLine($"- **{node.Node.Inner.NodeType}** `{node.Node.Inner.Name}` ({node.Node.Inner.FilePath}:{node.Node.Inner.LineStart}) — structural: {node.StructuralScore:F3}");
            }

            sb.AppendLine();
        }

        // Call-chain / call-trace section
        var callChainInBudget = budgetNodes.Where(n => n.CentralityScore > 0).ToList();
        if (callChainInBudget.Count > 0)
        {
            sb.AppendLine("## Call Chain Context");
            sb.AppendLine();
            foreach (var node in callChainInBudget.OrderByDescending(n => n.CentralityScore).Take(20))
            {
                sb.AppendLine($"- **{node.Node.Inner.NodeType}** `{node.Node.Inner.Name}` ({node.Node.Inner.FilePath}) — centrality: {node.CentralityScore:F3}");
            }

            sb.AppendLine();
        }

        // Semantic section
        var semanticInBudget = budgetNodes.Where(n => n.SemanticScore > 0).ToList();
        if (semanticInBudget.Count > 0)
        {
            sb.AppendLine("## Semantic Matches");
            sb.AppendLine();
            foreach (var node in semanticInBudget.OrderByDescending(n => n.SemanticScore).Take(20))
            {
                sb.AppendLine($"- **{node.Node.Inner.NodeType}** `{node.Node.Inner.Name}` ({node.Node.Inner.FilePath}) — semantic: {node.SemanticScore:F3}");
            }

            sb.AppendLine();
        }

        // Expertise section
        var expertiseInBudget = budgetNodes.Where(n => n.ExpertiseScore > 0).ToList();
        if (expertiseInBudget.Count > 0)
        {
            sb.AppendLine("## Expertise Annotations");
            sb.AppendLine();
            foreach (var node in expertiseInBudget.OrderByDescending(n => n.ExpertiseScore).Take(20))
            {
                sb.AppendLine($"- **{node.Node.Inner.NodeType}** `{node.Node.Inner.Name}` — expertise: {node.ExpertiseScore:F3}");
            }

            sb.AppendLine();
        }

        // If no sections matched, provide a summary
        if (structuralInBudget.Count == 0 && callChainInBudget.Count == 0
            && semanticInBudget.Count == 0 && expertiseInBudget.Count == 0)
        {
            sb.AppendLine("## Results");
            sb.AppendLine();
            foreach (var node in budgetNodes.Take(20))
            {
                sb.AppendLine($"- **{node.Node.Inner.NodeType}** `{node.Node.Inner.Name}` ({node.Node.Inner.FilePath}) — score: {node.CompositeScore:F3}");
            }
        }

        return sb.ToString();
    }

    private static double ScoreNode(
        Ast.Core.HypergraphNode node,
        string[] queryTerms,
        double modeWeight)
    {
        if (queryTerms.Length == 0)
        {
            return 0.1;
        }

        double score = 0.0;
        var nameUpper = node.Name.ToUpperInvariant();
        var pathUpper = node.FilePath.ToUpperInvariant();
        var typeUpper = node.NodeType.ToUpperInvariant();
        var qualifiedName = node.Metadata.TryGetValue(MemoryMetadataKeys.QualifiedName, out var qn)
            ? qn?.ToString()?.ToUpperInvariant() ?? string.Empty
            : string.Empty;

        foreach (var term in queryTerms)
        {
            var termUpper = term.ToUpperInvariant();

            if (nameUpper == termUpper)
            {
                score += 1.0;
            }
            else if (nameUpper.Contains(termUpper))
            {
                score += 0.6;
            }

            if (qualifiedName.Contains(termUpper))
            {
                score += 0.4;
            }

            if (pathUpper.Contains(termUpper))
            {
                score += 0.3;
            }

            if (typeUpper.Contains(termUpper))
            {
                score += 0.2;
            }
        }

        return (score / queryTerms.Length) * (1.0 + modeWeight);
    }

    private static int EstimateTokens(ScoredMemoryNode node)
    {
        return (node.Node.Inner.Name.Length + node.Node.Inner.FilePath.Length + 20) / 4;
    }
}