// -----------------------------------------------------------------------
// <copyright file="ContextAssembler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Context;

using System.Text;
using Ast.Memory.Models;

/// <summary>
/// Transforms ranked nodes into deterministic Markdown with fixed sections:
/// Structure, Call Graph, Related Code, Known Expertise (architecture.md §3.4).
/// </summary>
internal sealed class ContextAssembler
{
    /// <summary>
    /// Assembles context markdown from scored nodes and optional expertise entries.
    /// </summary>
    public string Assemble(
        IReadOnlyList<ScoredMemoryNode> nodes,
        IReadOnlyList<ExpertiseEntry> expertise)
    {
        var sb = new StringBuilder();

        this.AppendStructureSection(sb, nodes);
        this.AppendCallGraphSection(sb, nodes);
        this.AppendRelatedCodeSection(sb, nodes);
        this.AppendExpertiseSection(sb, nodes, expertise);

        return sb.ToString();
    }

    private void AppendStructureSection(StringBuilder sb, IReadOnlyList<ScoredMemoryNode> nodes)
    {
        sb.AppendLine("## Structure");
        sb.AppendLine();

        var structuralNodes = nodes
            .Where(n => n.Node.MemoryLayer == MemoryLayers.Structural
                || IsContainmentNodeType(n.Node.NodeKind))
            .ToList();

        if (structuralNodes.Count == 0)
        {
            sb.AppendLine("_No structural nodes in result._");
            sb.AppendLine();
            return;
        }

        foreach (var node in structuralNodes)
        {
            var indent = GetIndentForNodeType(node.Node.NodeKind);
            sb.AppendLine($"{indent}- **{node.Node.NodeKind}** `{node.Node.Inner.Name}` ({node.Node.Inner.FilePath}:{node.Node.Inner.LineStart})");
        }

        sb.AppendLine();
    }

    private void AppendCallGraphSection(StringBuilder sb, IReadOnlyList<ScoredMemoryNode> nodes)
    {
        sb.AppendLine("## Call Graph");
        sb.AppendLine();

        var callNodes = nodes
            .Where(n => n.Node.MemoryLayer == MemoryLayers.CallChain
                || IsCallNodeType(n.Node.NodeKind))
            .ToList();

        if (callNodes.Count == 0)
        {
            sb.AppendLine("_No call graph nodes in result._");
            sb.AppendLine();
            return;
        }

        foreach (var node in callNodes)
        {
            sb.AppendLine($"- `{node.Node.Inner.Name}` ({node.Node.Inner.FilePath}:{node.Node.Inner.LineStart}) [score: {node.CompositeScore:F3}]");
        }

        sb.AppendLine();
    }

    private void AppendRelatedCodeSection(StringBuilder sb, IReadOnlyList<ScoredMemoryNode> nodes)
    {
        sb.AppendLine("## Related Code");
        sb.AppendLine();

        var semanticNodes = nodes
            .Where(n => n.SemanticScore > 0)
            .OrderByDescending(n => n.SemanticScore)
            .ToList();

        if (semanticNodes.Count == 0)
        {
            sb.AppendLine("_No semantically similar code in result._");
            sb.AppendLine();
            return;
        }

        foreach (var node in semanticNodes)
        {
            sb.AppendLine($"- `{node.Node.Inner.Name}` (similarity: {node.SemanticScore:F3}) — {node.Node.Inner.FilePath}:{node.Node.Inner.LineStart}");
        }

        sb.AppendLine();
    }

    private void AppendExpertiseSection(
        StringBuilder sb,
        IReadOnlyList<ScoredMemoryNode> nodes,
        IReadOnlyList<ExpertiseEntry> expertise)
    {
        sb.AppendLine("## Known Expertise");
        sb.AppendLine();

        if (expertise.Count == 0)
        {
            var nodesWithExpertise = nodes.Where(n => n.ExpertiseScore > 0).ToList();
            if (nodesWithExpertise.Count == 0)
            {
                sb.AppendLine("_No expertise entries apply to selected code._");
                sb.AppendLine();
                return;
            }

            foreach (var node in nodesWithExpertise)
            {
                sb.AppendLine($"- [{node.ExpertiseScore:F2}] `{node.Node.Inner.Name}` has associated expertise");
            }
        }
        else
        {
            foreach (var entry in expertise)
            {
                sb.AppendLine($"- **{entry.Kind}** [{entry.Confidence:F2}]: {entry.Title}");
                if (!string.IsNullOrWhiteSpace(entry.Body))
                {
                    sb.AppendLine($"  {entry.Body}");
                }
            }
        }

        sb.AppendLine();
    }

    private static bool IsContainmentNodeType(string nodeType)
    {
        return nodeType is "Repo" or "Workspace" or "Package" or "Module"
            or "Class" or "Interface" or "Function" or "Method" or "Property"
            or "Struct" or "Record";
    }

    private static bool IsCallNodeType(string nodeType)
    {
        return nodeType is "Method" or "Function" or "Constructor" or "Property";
    }

    private static string GetIndentForNodeType(string nodeType)
    {
        return nodeType switch
        {
            "Repo" => "",
            "Workspace" => "  ",
            "Package" => "  ",
            "Module" => "    ",
            "Class" or "Interface" or "Struct" or "Record" => "      ",
            "Function" or "Method" or "Property" or "Constructor" => "        ",
            _ => "  "
        };
    }
}