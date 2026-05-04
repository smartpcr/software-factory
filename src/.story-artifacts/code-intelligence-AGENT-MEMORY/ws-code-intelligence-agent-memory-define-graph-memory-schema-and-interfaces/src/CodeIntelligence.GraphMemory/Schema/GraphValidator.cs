using System.Text.Json;

namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Severity level for a graph diagnostic.
/// </summary>
public enum DiagnosticSeverity
{
    Error,
    Warning,
}

/// <summary>
/// A single diagnostic produced by graph validation.
/// </summary>
public sealed record GraphDiagnostic(DiagnosticSeverity Severity, string Message)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>
/// Validates structural and semantic invariants of a <see cref="CodeKnowledgeGraph"/>.
/// </summary>
public static class GraphValidator
{
    /// <summary>
    /// Allowed target node kinds for each (EdgeKind, SourceNodeKind) pair.
    /// Missing entries mean the combination is invalid.
    /// </summary>
    public static IReadOnlyDictionary<EdgeKind, IReadOnlyDictionary<NodeKind, IReadOnlySet<NodeKind>>> EndpointRules { get; }
        = BuildEndpointRules();

    /// <summary>
    /// Edge kinds that must not form self-loops (source == target).
    /// </summary>
    private static readonly HashSet<EdgeKind> NoSelfLoopKinds =
    [
        EdgeKind.Contains,
        EdgeKind.Inherits,
        EdgeKind.Calls,
        EdgeKind.Imports,
    ];

    private static Dictionary<EdgeKind, IReadOnlyDictionary<NodeKind, IReadOnlySet<NodeKind>>> BuildEndpointRules()
    {
        static IReadOnlyDictionary<NodeKind, IReadOnlySet<NodeKind>> Rule(
            params (NodeKind source, NodeKind[] targets)[] pairs)
        {
            var dict = new Dictionary<NodeKind, IReadOnlySet<NodeKind>>();
            foreach (var (source, targets) in pairs)
                dict[source] = new HashSet<NodeKind>(targets);
            return dict;
        }

        return new Dictionary<EdgeKind, IReadOnlyDictionary<NodeKind, IReadOnlySet<NodeKind>>>
        {
            [EdgeKind.Contains] = Rule(
                (NodeKind.Repo, [NodeKind.Module, NodeKind.File]),
                (NodeKind.Module, [NodeKind.File, NodeKind.Module]),
                (NodeKind.File, [NodeKind.Class, NodeKind.Function, NodeKind.Variable]),
                (NodeKind.Class, [NodeKind.Function, NodeKind.Variable])),
            [EdgeKind.Calls] = Rule(
                (NodeKind.Function, [NodeKind.Function])),
            [EdgeKind.Imports] = Rule(
                (NodeKind.File, [NodeKind.File, NodeKind.Module]),
                (NodeKind.Module, [NodeKind.File, NodeKind.Module])),
            [EdgeKind.Inherits] = Rule(
                (NodeKind.Class, [NodeKind.Class])),
            // Expertise and SimilarTo: any source → any target (no pairwise constraint)
        };
    }

    /// <summary>
    /// Validate <paramref name="graph"/> and return all diagnostics found.
    /// </summary>
    public static IReadOnlyList<GraphDiagnostic> Validate(CodeKnowledgeGraph graph)
    {
        var diagnostics = new List<GraphDiagnostic>();

        ValidateSchemaVersion(graph.SchemaVersion, diagnostics);
        var (nodeIds, nodeKinds) = ValidateNodes(graph.Nodes, diagnostics);
        ValidateEdges(graph.Edges, nodeIds, nodeKinds, diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// Returns only error-severity diagnostics as strings.
    /// Backward-compatible convenience for callers that only need error messages.
    /// </summary>
    public static IReadOnlyList<string> GetErrors(CodeKnowledgeGraph graph)
        => Validate(graph)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Message)
            .ToList();

    private static void ValidateSchemaVersion(SchemaVersion version, List<GraphDiagnostic> diagnostics)
    {
        if (version.Major < 0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"SchemaVersion.Major must be non-negative, got {version.Major}."));
        if (version.Minor < 0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"SchemaVersion.Minor must be non-negative, got {version.Minor}."));
    }

    private static (HashSet<string> nodeIds, Dictionary<string, NodeKind> nodeKinds) ValidateNodes(
        IReadOnlyList<GraphNode> nodes, List<GraphDiagnostic> diagnostics)
    {
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeKinds = new Dictionary<string, NodeKind>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                diagnostics.Add(new(DiagnosticSeverity.Error, "Node has null or empty Id."));
            else if (!nodeIds.Add(node.Id))
                diagnostics.Add(new(DiagnosticSeverity.Error, $"Duplicate node Id: '{node.Id}'."));
            else
                nodeKinds[node.Id] = node.Kind;

            if (node.Kind == NodeKind.Unknown)
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Node '{node.Id}' has Kind=Unknown; producers must not emit Unknown."));

            if (node.UpdatedAt < node.CreatedAt)
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Node '{node.Id}' has UpdatedAt < CreatedAt."));

            ValidateSourceLocation(node.Id, node.Location, diagnostics);
        }

        return (nodeIds, nodeKinds);
    }

    private static void ValidateSourceLocation(string nodeId, SourceLocation? location, List<GraphDiagnostic> diagnostics)
    {
        if (location is null) return;

        if (location.StartLine is < 0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Node '{nodeId}': SourceLocation.StartLine must be non-negative."));
        if (location.StartColumn is < 0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Node '{nodeId}': SourceLocation.StartColumn must be non-negative."));
        if (location.EndLine is < 0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Node '{nodeId}': SourceLocation.EndLine must be non-negative."));
        if (location.EndColumn is < 0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Node '{nodeId}': SourceLocation.EndColumn must be non-negative."));

        if (location.StartLine.HasValue && location.EndLine.HasValue && location.EndLine < location.StartLine)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Node '{nodeId}': SourceLocation.EndLine ({location.EndLine}) < StartLine ({location.StartLine})."));

        if (location.StartLine.HasValue && location.EndLine.HasValue &&
            location.StartLine == location.EndLine &&
            location.StartColumn.HasValue && location.EndColumn.HasValue &&
            location.EndColumn < location.StartColumn)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Node '{nodeId}': SourceLocation.EndColumn ({location.EndColumn}) < StartColumn ({location.StartColumn}) on same line."));
    }

    private static void ValidateEdges(
        IReadOnlyList<GraphEdge> edges,
        HashSet<string> nodeIds,
        Dictionary<string, NodeKind> nodeKinds,
        List<GraphDiagnostic> diagnostics)
    {
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id))
                diagnostics.Add(new(DiagnosticSeverity.Error, "Edge has null or empty Id."));
            else if (!edgeIds.Add(edge.Id))
                diagnostics.Add(new(DiagnosticSeverity.Error, $"Duplicate edge Id: '{edge.Id}'."));

            if (!nodeIds.Contains(edge.SourceNodeId))
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Edge '{edge.Id}' references non-existent SourceNodeId: '{edge.SourceNodeId}'."));

            if (!nodeIds.Contains(edge.TargetNodeId))
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Edge '{edge.Id}' references non-existent TargetNodeId: '{edge.TargetNodeId}'."));

            if (edge.Kind == EdgeKind.Unknown)
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Edge '{edge.Id}' has Kind=Unknown; producers must not emit Unknown."));

            if (edge.Weight is < 0.0 or > 1.0)
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Edge '{edge.Id}' has Weight={edge.Weight} outside [0.0, 1.0]."));

            // Self-loop detection for structural edge kinds
            if (NoSelfLoopKinds.Contains(edge.Kind) &&
                string.Equals(edge.SourceNodeId, edge.TargetNodeId, StringComparison.Ordinal))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error,
                    $"Edge '{edge.Id}' ({edge.Kind}): self-loop is not allowed for this edge kind."));
            }

            // Endpoint type constraints
            if (EndpointRules.TryGetValue(edge.Kind, out var kindRules)
                && nodeKinds.TryGetValue(edge.SourceNodeId, out var srcKind)
                && nodeKinds.TryGetValue(edge.TargetNodeId, out var tgtKind))
            {
                if (!kindRules.TryGetValue(srcKind, out var allowedTargets))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error,
                        $"Edge '{edge.Id}' ({edge.Kind}): source kind '{srcKind}' is not allowed."));
                }
                else if (!allowedTargets.Contains(tgtKind))
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error,
                        $"Edge '{edge.Id}' ({edge.Kind}): target kind '{tgtKind}' is not allowed for source kind '{srcKind}'."));
                }
            }

            // SimilarTo must carry cosine_similarity in [0,1]
            if (edge.Kind == EdgeKind.SimilarTo)
            {
                ValidateSimilarToEdge(edge, diagnostics);
            }

            // Expertise edges should carry Weight
            if (edge.Kind == EdgeKind.Expertise && edge.Weight is null)
            {
                diagnostics.Add(new(DiagnosticSeverity.Warning,
                    $"Edge '{edge.Id}' (Expertise) should carry a Weight."));
            }
        }
    }

    private static void ValidateSimilarToEdge(GraphEdge edge, List<GraphDiagnostic> diagnostics)
    {
        if (!edge.Properties.TryGetValue("cosine_similarity", out var csElement))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Edge '{edge.Id}' (SimilarTo) must carry 'cosine_similarity' property."));
            return;
        }

        if (csElement.ValueKind != JsonValueKind.Number)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Edge '{edge.Id}' (SimilarTo): 'cosine_similarity' must be a number."));
            return;
        }

        var cs = csElement.GetDouble();
        if (cs < 0.0 || cs > 1.0)
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"Edge '{edge.Id}' (SimilarTo): 'cosine_similarity' must be in [0.0, 1.0], got {cs}."));

        // If Weight is also present, it should be consistent with cosine_similarity
        if (edge.Weight.HasValue && Math.Abs(edge.Weight.Value - cs) > 1e-9)
            diagnostics.Add(new(DiagnosticSeverity.Warning,
                $"Edge '{edge.Id}' (SimilarTo): Weight ({edge.Weight.Value}) differs from cosine_similarity ({cs}). " +
                "Consider keeping them consistent."));
    }
}
