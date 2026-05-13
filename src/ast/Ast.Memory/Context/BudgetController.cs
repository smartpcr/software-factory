// -----------------------------------------------------------------------
// <copyright file="BudgetController.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Context;

using Ast.Memory.Models;

/// <summary>
/// Enforces a token budget on ranked memory nodes. Walks the ranked list
/// top-down, accumulates token counts, and when budget is tight collapses
/// child nodes into parent summaries (HiAgent pattern, architecture.md §3.4).
/// Deduplicates nodes that represent the same code span reached via multiple paths.
///
/// The pipeline is: BudgetController trims nodes → ContextAssembler renders Markdown.
/// BudgetController uses <see cref="ContextAssembler.RenderMarkdown"/> internally
/// to measure token cost, ensuring the token count it reports is truthful — it
/// always equals the actual tokens of the Markdown that ContextAssembler would
/// produce from the returned nodes.
///
/// Budget constants are sourced from <see cref="MemoryOptions"/> (architecture.md §2.4).
/// </summary>
public class BudgetController
{
    private readonly ITokenCounter tokenCounter;
    private readonly MemoryOptions options;

    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetController"/> class
    /// with an explicit <see cref="MemoryOptions"/> configuration.
    /// </summary>
    public BudgetController(ITokenCounter tokenCounter, MemoryOptions options)
    {
        this.tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetController"/> class
    /// using default <see cref="MemoryOptions"/>.
    /// </summary>
    public BudgetController(ITokenCounter tokenCounter)
        : this(tokenCounter, new MemoryOptions())
    {
    }

    /// <summary>Default token budget per query (from <see cref="MemoryOptions.TokenBudgetDefault"/>).</summary>
    public int TokenBudgetDefault => this.options.TokenBudgetDefault;

    /// <summary>Maximum allowed token budget (from <see cref="MemoryOptions.TokenBudgetCeiling"/>).</summary>
    public int TokenBudgetCeiling => this.options.TokenBudgetCeiling;

    /// <summary>
    /// Trims a ranked list of scored nodes to fit within the token budget.
    /// Returns the trimmed node list. The caller should pass the result to
    /// <see cref="ContextAssembler.Assemble"/> to produce the final Markdown.
    ///
    /// <see cref="BudgetResult.TokensUsed"/> is always truthful — it equals
    /// the actual token cost of rendering the returned nodes via
    /// <see cref="ContextAssembler.RenderMarkdown"/>. For content nodes,
    /// TokensUsed ≤ <paramref name="tokenBudget"/> (after clamping).
    /// The four-section skeleton is always present in the output.
    /// </summary>
    /// <param name="rankedNodes">Nodes sorted by descending <see cref="ScoredMemoryNode.CompositeScore"/>.</param>
    /// <param name="tokenBudget">Target token budget. Clamped between 1 and <see cref="MemoryOptions.TokenBudgetCeiling"/>. Defaults to <see cref="MemoryOptions.TokenBudgetDefault"/>.</param>
    /// <returns>A result containing the trimmed node list and the actual token count.</returns>
    public BudgetResult EnforceBudget(
        IReadOnlyList<ScoredMemoryNode> rankedNodes,
        int? tokenBudget = null)
    {
        ArgumentNullException.ThrowIfNull(rankedNodes);
        var budget = Math.Clamp(
            tokenBudget ?? this.options.TokenBudgetDefault,
            1,
            this.options.TokenBudgetCeiling);

        if (rankedNodes.Count == 0)
        {
            var skeletonTokens = this.CountRenderedTokens([]);
            return new BudgetResult([], skeletonTokens);
        }

        // Step 1: Deduplicate nodes with the same code span (file + line range),
        // preserving distinct zero-span and expertise entries.
        var deduped = this.Deduplicate(rankedNodes);

        // Step 2: Try all nodes — render final Markdown and count
        var totalTokens = this.CountRenderedTokens(deduped);

        if (totalTokens <= budget)
        {
            return new BudgetResult(deduped, totalTokens);
        }

        // Step 3: Budget is tight — attempt HiAgent collapse using structural containment.
        // Only collapse groups where doing so actually reduces the token count.
        var collapsed = this.CollapseChildrenIntoParents(deduped);
        totalTokens = this.CountRenderedTokens(collapsed);

        if (totalTokens <= budget)
        {
            return new BudgetResult(collapsed, totalTokens);
        }

        // Step 4: Still over budget — trim lowest-scored nodes from the bottom
        // until the rendered Markdown fits within the budget.
        var trimmed = new List<ScoredMemoryNode>(collapsed);
        while (trimmed.Count > 0 && totalTokens > budget)
        {
            trimmed.RemoveAt(trimmed.Count - 1);
            totalTokens = this.CountRenderedTokens(trimmed);
        }

        return new BudgetResult(trimmed, totalTokens);
    }

    /// <summary>
    /// Counts tokens on the Markdown that <see cref="ContextAssembler"/> would produce.
    /// </summary>
    private int CountRenderedTokens(IReadOnlyList<ScoredMemoryNode> nodes)
    {
        var markdown = ContextAssembler.RenderMarkdown(nodes);
        return this.tokenCounter.CountTokens(markdown);
    }

    private List<ScoredMemoryNode> Deduplicate(IReadOnlyList<ScoredMemoryNode> nodes)
    {
        var seen = new HashSet<string>();
        var result = new List<ScoredMemoryNode>();

        foreach (var node in nodes)
        {
            var key = GetDeduplicationKey(node);
            if (seen.Add(key))
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// Produces a deduplication key. For nodes with a real code span
    /// (non-zero line range), the key is file+lines so the same span
    /// reached via multiple retrieval paths is counted once.
    /// File paths are normalized (forward slashes, lowercase) to handle
    /// casing and separator differences.
    /// For zero-span nodes (expertise, synthetic) the key includes the
    /// node ID to prevent distinct entries from being dropped.
    /// </summary>
    private static string GetDeduplicationKey(ScoredMemoryNode node)
    {
        var inner = node.Node.Inner;

        if (node.Node.IsZeroSpan)
        {
            return $"id:{inner.Id}";
        }

        var normalizedPath = NormalizePath(inner.FilePath);
        return $"{normalizedPath}:{inner.LineStart}-{inner.LineEnd}";
    }

    /// <summary>
    /// Normalizes a file path for comparison: converts backslashes to
    /// forward slashes and lowercases the entire path.
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Applies the HiAgent pattern: detects structural containment
    /// (same file, line range A ⊇ line range B) using only real parent-child
    /// relationships present in the node list — either via explicit ParentId
    /// or line-range containment where a parent node already exists.
    /// Does NOT synthesize parents from orphan nodes, which would lose
    /// individual node identity, RetrievalPath, and section classification.
    ///
    /// Budget-aware: each collapse group is only applied if the collapsed
    /// version uses fewer tokens than keeping the original nodes.
    /// </summary>
    private List<ScoredMemoryNode> CollapseChildrenIntoParents(
        List<ScoredMemoryNode> nodes)
    {
        // Detect parent-child relationships via structural containment.
        // Only matches nodes where a real parent exists in the list.
        var childToParent = new Dictionary<int, int>();

        for (var i = 0; i < nodes.Count; i++)
        {
            var candidate = nodes[i];
            if (candidate.Node.IsZeroSpan)
            {
                continue;
            }

            // Check explicit ParentId first
            var parentId = candidate.Node.ParentId;
            if (parentId is not null)
            {
                for (var j = 0; j < nodes.Count; j++)
                {
                    if (j != i && nodes[j].Node.Inner.Id == parentId)
                    {
                        childToParent[i] = j;
                        break;
                    }
                }
            }

            // If no explicit parent found, detect via line-range containment
            // (only when a real parent node exists in the list)
            if (!childToParent.ContainsKey(i))
            {
                int bestParent = -1;
                int bestSpan = int.MaxValue;

                for (var j = 0; j < nodes.Count; j++)
                {
                    if (j == i || nodes[j].Node.IsZeroSpan)
                    {
                        continue;
                    }

                    if (nodes[j].Node.StructurallyContains(candidate.Node))
                    {
                        var span = nodes[j].Node.Inner.LineEnd - nodes[j].Node.Inner.LineStart;
                        if (span < bestSpan)
                        {
                            bestSpan = span;
                            bestParent = j;
                        }
                    }
                }

                if (bestParent >= 0)
                {
                    childToParent[i] = bestParent;
                }
            }
        }

        // Group children by parent index
        var parentToChildren = new Dictionary<int, List<int>>();
        foreach (var (childIdx, parentIdx) in childToParent)
        {
            if (!parentToChildren.ContainsKey(parentIdx))
            {
                parentToChildren[parentIdx] = [];
            }

            parentToChildren[parentIdx].Add(childIdx);
        }

        if (parentToChildren.Count == 0)
        {
            return nodes;
        }

        // Build result with budget-aware collapse
        var collapsedIndices = new HashSet<int>();
        var collapsedNodes = new List<ScoredMemoryNode>();

        // Collapse real parent-child groups (only if it saves tokens)
        foreach (var (parentIdx, childIndices) in parentToChildren)
        {
            var parent = nodes[parentIdx];
            var children = childIndices.Select(ci => nodes[ci]).ToList();
            var collapsed = CreateCollapsedNode(parent, children);

            // Measure token cost of originals vs collapsed
            var originalNodes = new List<ScoredMemoryNode> { parent };
            originalNodes.AddRange(children);
            var originalTokens = this.MeasureNodeTokens(originalNodes);
            var collapsedTokens = this.MeasureNodeTokens([collapsed]);

            if (collapsedTokens < originalTokens)
            {
                collapsedNodes.Add(collapsed);
                collapsedIndices.Add(parentIdx);
                foreach (var ci in childIndices)
                {
                    collapsedIndices.Add(ci);
                }
            }
        }

        if (collapsedIndices.Count == 0)
        {
            return nodes;
        }

        var result = new List<ScoredMemoryNode>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!collapsedIndices.Contains(i))
            {
                result.Add(nodes[i]);
            }
        }

        result.AddRange(collapsedNodes);
        result.Sort((a, b) => b.CompositeScore.CompareTo(a.CompositeScore));
        return result;
    }

    /// <summary>
    /// Measures the token cost of rendering a set of nodes in isolation.
    /// Used to compare original vs collapsed token usage.
    /// </summary>
    private int MeasureNodeTokens(IReadOnlyList<ScoredMemoryNode> nodes)
    {
        var markdown = ContextAssembler.RenderMarkdown(nodes);
        return this.tokenCounter.CountTokens(markdown);
    }

    /// <summary>
    /// Creates a collapsed summary node from a parent and its children.
    /// Preserves child IDs and the best child's RetrievalPath in metadata
    /// for provenance tracking and correct section classification.
    /// </summary>
    private static ScoredMemoryNode CreateCollapsedNode(
        ScoredMemoryNode parent,
        List<ScoredMemoryNode> children)
    {
        var memberNames = children
            .OrderByDescending(c => c.CompositeScore)
            .Take(5)
            .Select(c => c.Node.Inner.Name)
            .ToList();

        var summaryText = $"Contains {children.Count} members: {string.Join(", ", memberNames)}";
        if (children.Count > 5)
        {
            summaryText += $" and {children.Count - 5} more";
        }

        var childIds = children.Select(c => c.Node.Inner.Id).ToList();

        var collapsedInner = new Ast.Core.HypergraphNode
        {
            Id = parent.Node.Inner.Id,
            Name = parent.Node.Inner.Name,
            NodeType = parent.Node.Inner.NodeType,
            Language = parent.Node.Inner.Language,
            FilePath = parent.Node.Inner.FilePath,
            Scope = parent.Node.Inner.Scope,
            LineStart = parent.Node.Inner.LineStart,
            LineEnd = parent.Node.Inner.LineEnd,
            Metadata = new Dictionary<string, object?>
            {
                ["Summary"] = summaryText,
                ["MemberCount"] = children.Count,
                ["MemoryLayer"] = parent.Node.MemoryLayer,
                ["Collapsed"] = true,
                ["CollapsedChildIds"] = childIds
            }
        };

        var bestScore = Math.Max(parent.CompositeScore,
            children.Max(c => c.CompositeScore));

        // Preserve RetrievalPath: prefer parent's, else use best child's
        var retrievalPath = parent.RetrievalPath
            ?? children.OrderByDescending(c => c.CompositeScore).First().RetrievalPath;

        return new ScoredMemoryNode(
            new MemoryNode(collapsedInner),
            bestScore,
            parent.StructuralScore,
            parent.SemanticScore,
            parent.CentralityScore,
            parent.RecencyScore,
            parent.ExpertiseScore,
            retrievalPath);
    }
}

/// <summary>
/// Result of budget enforcement. Nodes are trimmed to fit within the token budget.
/// Pass the nodes to <see cref="ContextAssembler.Assemble"/> to produce the final Markdown.
/// <see cref="TokensUsed"/> is always truthful — it equals the actual token cost
/// of rendering the nodes via <see cref="ContextAssembler.RenderMarkdown"/>.
/// The four-section Markdown skeleton is always included in the output.
/// </summary>
/// <param name="Nodes">Trimmed node list. Pass to <see cref="ContextAssembler.Assemble"/> for final Markdown.</param>
/// <param name="TokensUsed">Truthful token count of the rendered Markdown for these nodes.</param>
public record BudgetResult(
    IReadOnlyList<ScoredMemoryNode> Nodes,
    int TokensUsed);
