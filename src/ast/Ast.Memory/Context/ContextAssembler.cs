// -----------------------------------------------------------------------
// <copyright file="ContextAssembler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Context;

using System.Text;

using Ast.Memory.Models;

/// <summary>
/// Transforms trimmed ranked nodes into deterministic Markdown with fixed sections.
/// Per architecture.md §3.4, the Context Assembler receives a <see cref="List{ScoredMemoryNode}"/>
/// from the Retrieval Engine and the token budget from the Budget Controller.
/// It never queries the graph directly — it formats what the Retrieval Engine provides.
/// </summary>
public class ContextAssembler
{
    private readonly ITokenCounter tokenCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextAssembler"/> class.
    /// </summary>
    public ContextAssembler(ITokenCounter tokenCounter)
    {
        this.tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
    }

    /// <summary>
    /// Assembles ranked nodes into deterministic Markdown output with four sections:
    /// <c>## Structure</c>, <c>## Call Graph</c>, <c>## Related Code</c>, <c>## Known Expertise</c>.
    /// Section headers match architecture.md §3.4 output format.
    /// </summary>
    /// <param name="nodes">Budget-trimmed ranked nodes.</param>
    /// <returns>An <see cref="AssemblyResult"/> containing the Markdown and token count.</returns>
    public AssemblyResult Assemble(IReadOnlyList<ScoredMemoryNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var markdown = RenderMarkdown(nodes);
        var tokensUsed = this.tokenCounter.CountTokens(markdown);

        return new AssemblyResult(markdown, tokensUsed);
    }

    /// <summary>
    /// Renders the nodes to Markdown without counting tokens. Used internally
    /// by <see cref="BudgetController"/> for budget verification on the final form.
    /// Always produces all four section headers, even when no nodes are provided,
    /// to ensure the output invariant (architecture.md §3.4).
    /// </summary>
    internal static string RenderMarkdown(IReadOnlyList<ScoredMemoryNode> nodes)
    {
        // Classify nodes into sections. Primary: explicit RetrievalPath from
        // the Retrieval Engine (§3.3). Fallback: heuristic from MemoryLayer
        // and node type, since structural-homed nodes participate on call_chain
        // and expertise edges (§2.1 cross-layer edge participation).
        var structuralNodes = new List<ScoredMemoryNode>();
        var callGraphNodes = new List<ScoredMemoryNode>();
        var relatedCodeNodes = new List<ScoredMemoryNode>();
        var expertiseNodes = new List<ScoredMemoryNode>();

        foreach (var node in nodes)
        {
            ClassifyNode(node, structuralNodes, callGraphNodes, relatedCodeNodes, expertiseNodes);
        }

        var sb = new StringBuilder();

        // Section 1: Structure — containment hierarchy
        sb.AppendLine("## Structure");
        if (structuralNodes.Count > 0)
        {
            RenderStructureSection(sb, structuralNodes);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No structural context available.");
        }

        sb.AppendLine();

        // Section 2: Call Graph — caller/callee chains (architecture.md §3.4)
        sb.AppendLine("## Call Graph");
        if (callGraphNodes.Count > 0)
        {
            RenderCallGraphSection(sb, callGraphNodes);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No call graph context available.");
        }

        sb.AppendLine();

        // Section 3: Related Code — semantically similar snippets
        sb.AppendLine("## Related Code");
        if (relatedCodeNodes.Count > 0)
        {
            RenderRelatedCodeSection(sb, relatedCodeNodes);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No related code context available.");
        }

        sb.AppendLine();

        // Section 4: Known Expertise — patterns, pitfalls
        sb.AppendLine("## Known Expertise");
        if (expertiseNodes.Count > 0)
        {
            RenderExpertiseSection(sb, expertiseNodes);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No expertise context available.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Routes a scored node to the appropriate section bucket.
    /// Priority 1: <see cref="ScoredMemoryNode.RetrievalPath"/> — the Retrieval Engine's
    /// canonical retrieval mode (StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall).
    /// This is the faithful model from architecture.md §3.3: structural-homed nodes
    /// retrieved via CallTrace belong in Call Graph, not Structure.
    /// Priority 2: Heuristic from <see cref="MemoryNode.MemoryLayer"/> and node type.
    /// </summary>
    private static void ClassifyNode(
        ScoredMemoryNode node,
        List<ScoredMemoryNode> structural,
        List<ScoredMemoryNode> callGraph,
        List<ScoredMemoryNode> relatedCode,
        List<ScoredMemoryNode> expertise)
    {
        // Priority 1: explicit RetrievalPath from Retrieval Engine (§3.3)
        var path = node.RetrievalPath;
        if (path is not null)
        {
            switch (path)
            {
                case "StructuralDrill":
                    structural.Add(node);
                    return;
                case "CallTrace":
                    callGraph.Add(node);
                    return;
                case "SemanticSearch":
                    relatedCode.Add(node);
                    return;
                case "ExpertiseRecall":
                    expertise.Add(node);
                    return;
            }
        }

        // Priority 2: heuristic from MemoryLayer and node type
        var layer = node.Node.MemoryLayer;
        var nodeType = node.Node.Inner.NodeType;

        switch (layer)
        {
            case "expertise":
                expertise.Add(node);
                break;
            case "call_chain":
            case "call_graph":
                callGraph.Add(node);
                break;
            case "semantic":
                relatedCode.Add(node);
                break;
            default:
                // Structural-homed nodes: secondary classification by node type
                if (IsExpertiseRelated(nodeType))
                {
                    expertise.Add(node);
                }
                else
                {
                    structural.Add(node);
                }

                break;
        }
    }

    private static void RenderStructureSection(StringBuilder sb, List<ScoredMemoryNode> nodes)
    {
        sb.AppendLine();

        // Group by file path to build hierarchy
        var byFile = nodes
            .GroupBy(n => n.Node.Inner.FilePath)
            .OrderBy(g => g.Key);

        foreach (var fileGroup in byFile)
        {
            sb.AppendLine($"### {fileGroup.Key}");
            sb.AppendLine();

            foreach (var node in fileGroup.OrderBy(n => n.Node.Inner.LineStart))
            {
                var inner = node.Node.Inner;
                var name = node.Node.QualifiedName ?? inner.Name;
                var summary = node.Node.Summary;

                sb.Append($"- **{inner.NodeType}** `{name}`");
                sb.Append($" (L{inner.LineStart}–{inner.LineEnd})");

                if (summary is not null)
                {
                    sb.Append($" — {summary}");
                }

                sb.AppendLine();
            }
        }
    }

    private static void RenderCallGraphSection(StringBuilder sb, List<ScoredMemoryNode> nodes)
    {
        sb.AppendLine();

        foreach (var node in nodes.OrderByDescending(n => n.CompositeScore))
        {
            var inner = node.Node.Inner;
            var name = node.Node.QualifiedName ?? inner.Name;

            sb.AppendLine($"- `{name}` ({inner.FilePath}:{inner.LineStart})");

            if (node.Node.Summary is not null)
            {
                sb.AppendLine($"  {node.Node.Summary}");
            }
        }
    }

    private static void RenderRelatedCodeSection(StringBuilder sb, List<ScoredMemoryNode> nodes)
    {
        sb.AppendLine();

        foreach (var node in nodes.OrderByDescending(n => n.SemanticScore))
        {
            var inner = node.Node.Inner;
            var name = node.Node.QualifiedName ?? inner.Name;
            var similarity = node.SemanticScore;

            sb.AppendLine($"- `{name}` (similarity: {similarity:F2}) — {inner.FilePath}:{inner.LineStart}–{inner.LineEnd}");

            if (node.Node.Summary is not null)
            {
                sb.AppendLine($"  {node.Node.Summary}");
            }

            // Render code snippet when available in metadata
            if (inner.Metadata.TryGetValue("CodeSnippet", out var snippet) && snippet is string code && code.Length > 0)
            {
                sb.AppendLine($"  ```{inner.Language}");
                sb.AppendLine($"  {code}");
                sb.AppendLine("  ```");
            }
        }
    }

    private static void RenderExpertiseSection(StringBuilder sb, List<ScoredMemoryNode> nodes)
    {
        sb.AppendLine();

        foreach (var node in nodes.OrderByDescending(n => n.ExpertiseScore))
        {
            var inner = node.Node.Inner;
            var name = node.Node.QualifiedName ?? inner.Name;

            sb.AppendLine($"- **{name}** (confidence: {node.ExpertiseScore:F2})");

            if (node.Node.Summary is not null)
            {
                sb.AppendLine($"  {node.Node.Summary}");
            }
        }
    }

    private static bool IsExpertiseRelated(string nodeType)
    {
        return nodeType is "Expertise" or "Pattern" or "Pitfall"
            or "Convention" or "Insight";
    }
}

/// <summary>
/// Result of context assembly.
/// </summary>
public record AssemblyResult(string Markdown, int TokensUsed);
