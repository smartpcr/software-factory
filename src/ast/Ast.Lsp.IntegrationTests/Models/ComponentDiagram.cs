// -----------------------------------------------------------------------
// <copyright file="ComponentDiagram.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.IntegrationTests.Models;

/// <summary>
/// Component diagram showing dependencies between namespaces and projects.
/// </summary>
public class ComponentDiagram
{
    /// <summary>
    /// Gets or sets the repository name.
    /// </summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the diagram was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the diagram nodes (projects, namespaces, classes).
    /// </summary>
    public List<ComponentNode> Nodes { get; set; } = [];

    /// <summary>
    /// Gets or sets the edges between components.
    /// </summary>
    public List<ComponentEdge> Edges { get; set; } = [];

    /// <summary>
    /// Gets or sets the Mermaid diagram representation.
    /// </summary>
    public string Mermaid { get; set; } = string.Empty;

    /// <summary>
    /// Generates a Mermaid diagram from nodes and edges.
    /// </summary>
    public void GenerateMermaid()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("graph TD");

        // Add subgraphs for projects
        var nodesByProject = this.Nodes
            .Where(n => !string.IsNullOrEmpty(n.ProjectId))
            .GroupBy(n => n.ProjectId)
            .ToList();

        foreach (var projectGroup in nodesByProject)
        {
            sb.AppendLine($"    subgraph {SanitizeId(projectGroup.Key!)}[\"{projectGroup.Key}\"]");
            foreach (var node in projectGroup)
            {
                var shape = node.NodeType switch
                {
                    ComponentNodeType.Namespace => $"[{node.Label}]",
                    ComponentNodeType.Class => $"([{node.Label}])",
                    ComponentNodeType.Interface => $"{{{{{node.Label}}}}}",
                    _ => $"[{node.Label}]"
                };
                sb.AppendLine($"        {SanitizeId(node.Id)}{shape}");
            }
            sb.AppendLine("    end");
        }

        // Add standalone nodes
        foreach (var node in this.Nodes.Where(n => string.IsNullOrEmpty(n.ProjectId)))
        {
            sb.AppendLine($"    {SanitizeId(node.Id)}[{node.Label}]");
        }

        // Add edges
        foreach (var edge in this.Edges)
        {
            var arrow = edge.EdgeType switch
            {
                ComponentEdgeType.Uses => "-->",
                ComponentEdgeType.Implements => "-.->",
                ComponentEdgeType.Inherits => "==>",
                ComponentEdgeType.References => "-->",
                _ => "-->"
            };
            var label = !string.IsNullOrEmpty(edge.Label) ? $"|{edge.Label}|" : "";
            sb.AppendLine($"    {SanitizeId(edge.FromId)} {arrow}{label} {SanitizeId(edge.ToId)}");
        }

        this.Mermaid = sb.ToString();
    }

    private static string SanitizeId(string id)
    {
        return id.Replace(".", "_").Replace("-", "_").Replace(" ", "_");
    }
}

/// <summary>
/// A node in the component diagram.
/// </summary>
public class ComponentNode
{
    /// <summary>
    /// Gets or sets the unique node ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display label.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the node type.
    /// </summary>
    public ComponentNodeType NodeType { get; set; }

    /// <summary>
    /// Gets or sets the parent project ID.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the file path if applicable.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets or sets additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = [];
}

/// <summary>
/// Type of component node.
/// </summary>
public enum ComponentNodeType
{
    /// <summary>Project/assembly.</summary>
    Project,

    /// <summary>Namespace.</summary>
    Namespace,

    /// <summary>Class.</summary>
    Class,

    /// <summary>Interface.</summary>
    Interface,

    /// <summary>Struct.</summary>
    Struct,

    /// <summary>Module (PowerShell).</summary>
    Module
}

/// <summary>
/// An edge between component nodes.
/// </summary>
public class ComponentEdge
{
    /// <summary>
    /// Gets or sets the source node ID.
    /// </summary>
    public string FromId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target node ID.
    /// </summary>
    public string ToId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the edge type.
    /// </summary>
    public ComponentEdgeType EdgeType { get; set; }

    /// <summary>
    /// Gets or sets an optional label.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the reference count.
    /// </summary>
    public int ReferenceCount { get; set; } = 1;
}

/// <summary>
/// Type of component edge.
/// </summary>
public enum ComponentEdgeType
{
    /// <summary>Uses/depends on.</summary>
    Uses,

    /// <summary>Implements interface.</summary>
    Implements,

    /// <summary>Inherits from class.</summary>
    Inherits,

    /// <summary>Project reference.</summary>
    References,

    /// <summary>Calls.</summary>
    Calls,

    /// <summary>Imports (PowerShell module).</summary>
    Imports
}