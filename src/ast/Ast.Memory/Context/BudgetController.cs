// -----------------------------------------------------------------------
// <copyright file="BudgetController.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Context;

using Ast.Memory.Models;

/// <summary>
/// Enforces token budget by trimming ranked nodes and collapsing children
/// into parent summaries when budget is tight (HiAgent pattern).
/// </summary>
internal sealed class BudgetController
{
    private const int AverageTokensPerNode = 150;
    private const int CollapsedSummaryTokens = 40;

    private readonly int tokenBudgetCeiling;

    public BudgetController(int tokenBudgetCeiling)
    {
        this.tokenBudgetCeiling = tokenBudgetCeiling;
    }

    /// <summary>
    /// Trims the ranked node list to fit within the token budget.
    /// </summary>
    /// <param name="rankedNodes">Nodes sorted by descending composite score.</param>
    /// <param name="tokenBudget">Available token budget.</param>
    /// <returns>Trimmed list and actual token count.</returns>
    public (List<ScoredMemoryNode> Nodes, int TokensUsed) ApplyBudget(
        List<ScoredMemoryNode> rankedNodes,
        int tokenBudget)
    {
        var effectiveBudget = Math.Clamp(tokenBudget, 1, this.tokenBudgetCeiling);
        var result = new List<ScoredMemoryNode>();
        var seen = new HashSet<string>();
        var tokensUsed = 0;

        // Group nodes by parent for potential collapse
        var parentGroups = new Dictionary<string, List<ScoredMemoryNode>>();
        var topLevel = new List<ScoredMemoryNode>();

        foreach (var node in rankedNodes)
        {
            var nodeId = node.Node.Inner.Id;
            if (seen.Contains(nodeId))
            {
                continue;
            }

            seen.Add(nodeId);

            var parentId = GetParentId(nodeId);
            if (parentId != null)
            {
                if (!parentGroups.TryGetValue(parentId, out var group))
                {
                    group = [];
                    parentGroups[parentId] = group;
                }

                group.Add(node);
            }
            else
            {
                topLevel.Add(node);
            }
        }

        // Add top-level nodes first
        foreach (var node in topLevel)
        {
            var cost = EstimateTokens(node);
            if (tokensUsed + cost > effectiveBudget)
            {
                break;
            }

            result.Add(node);
            tokensUsed += cost;
        }

        // Process parent groups: if budget tight, collapse children into parent summary
        foreach (var (parentId, children) in parentGroups.OrderByDescending(g => g.Value.Max(n => n.CompositeScore)))
        {
            if (tokensUsed >= effectiveBudget)
            {
                break;
            }

            var childrenCost = children.Sum(EstimateTokens);
            var remainingBudget = effectiveBudget - tokensUsed;

            if (childrenCost <= remainingBudget)
            {
                // Enough budget — add all children
                result.AddRange(children);
                tokensUsed += childrenCost;
            }
            else if (CollapsedSummaryTokens <= remainingBudget)
            {
                // Budget tight — collapse into parent summary
                // Take the highest-scored child as representative
                var best = children.OrderByDescending(c => c.CompositeScore).First();
                var summary = CreateCollapsedSummary(best, children.Count);
                result.Add(summary);
                tokensUsed += CollapsedSummaryTokens;
            }
        }

        return (result, tokensUsed);
    }

    private static int EstimateTokens(ScoredMemoryNode node)
    {
        var text = node.Node.Inner.GetEmbeddingText();
        // Rough approximation: ~4 chars per token
        return Math.Max(1, text.Length / 4);
    }

    private static string? GetParentId(string nodeId)
    {
        // Node IDs follow the pattern "/{path}::{qualifiedName}"
        // Parent is derived by stripping the last segment
        var colonIdx = nodeId.LastIndexOf("::", StringComparison.Ordinal);
        if (colonIdx > 0)
        {
            var dotIdx = nodeId.LastIndexOf('.', nodeId.Length - 1, nodeId.Length - colonIdx);
            if (dotIdx > colonIdx)
            {
                return nodeId[..dotIdx];
            }

            return nodeId[..colonIdx];
        }

        var slashIdx = nodeId.LastIndexOf('/');
        if (slashIdx > 0)
        {
            return nodeId[..slashIdx];
        }

        return null;
    }

    private static ScoredMemoryNode CreateCollapsedSummary(ScoredMemoryNode representative, int childCount)
    {
        var summaryMetadata = new Dictionary<string, object?>(representative.Node.Inner.Metadata)
        {
            [MemoryMetadataKeys.Summary] = $"Collapsed {childCount} children",
            [MemoryMetadataKeys.MemberCount] = childCount
        };

        var summaryNode = new Ast.Core.HypergraphNode
        {
            Id = representative.Node.Inner.Id + "::collapsed",
            Name = representative.Node.Inner.Name + $" (+{childCount - 1} more)",
            NodeType = representative.Node.Inner.NodeType,
            Language = representative.Node.Inner.Language,
            FilePath = representative.Node.Inner.FilePath,
            Scope = representative.Node.Inner.Scope,
            LineStart = representative.Node.Inner.LineStart,
            LineEnd = representative.Node.Inner.LineEnd,
            Metadata = summaryMetadata
        };

        return new ScoredMemoryNode
        {
            Node = new MemoryNode(summaryNode, representative.Node.Version),
            CompositeScore = representative.CompositeScore,
            StructuralScore = representative.StructuralScore,
            SemanticScore = representative.SemanticScore,
            CentralityScore = representative.CentralityScore,
            RecencyScore = representative.RecencyScore,
            ExpertiseScore = representative.ExpertiseScore
        };
    }
}