namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Canonical mapping from the graph memory schema types to Neo4j labels
/// and relationship types. Downstream persistence layers should use these
/// constants when reading/writing the Neo4j database.
/// </summary>
public static class Neo4jMapping
{
    /// <summary>
    /// Maps a <see cref="NodeKind"/> to its canonical Neo4j node label.
    /// Labels use PascalCase per Neo4j convention.
    /// </summary>
    public static string ToNeo4jLabel(this NodeKind kind) => kind switch
    {
        NodeKind.Repo => "Repo",
        NodeKind.Module => "Module",
        NodeKind.File => "File",
        NodeKind.Class => "Class",
        NodeKind.Function => "Function",
        NodeKind.Variable => "Variable",
        NodeKind.Unknown => "Unknown",
        _ => "Unknown",
    };

    /// <summary>
    /// Maps an <see cref="EdgeKind"/> to its canonical Neo4j relationship type.
    /// Relationship types use UPPER_SNAKE_CASE per Neo4j convention.
    /// </summary>
    public static string ToNeo4jRelationshipType(this EdgeKind kind) => kind switch
    {
        EdgeKind.Contains => "CONTAINS",
        EdgeKind.Calls => "CALLS",
        EdgeKind.Imports => "IMPORTS",
        EdgeKind.Inherits => "INHERITS",
        EdgeKind.Expertise => "EXPERTISE",
        EdgeKind.SimilarTo => "SIMILAR_TO",
        EdgeKind.Unknown => "UNKNOWN",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// All canonical Neo4j node labels, indexed by <see cref="NodeKind"/>.
    /// </summary>
    public static IReadOnlyDictionary<NodeKind, string> NodeLabels { get; } =
        Enum.GetValues<NodeKind>().ToDictionary(k => k, k => k.ToNeo4jLabel());

    /// <summary>
    /// All canonical Neo4j relationship types, indexed by <see cref="EdgeKind"/>.
    /// </summary>
    public static IReadOnlyDictionary<EdgeKind, string> RelationshipTypes { get; } =
        Enum.GetValues<EdgeKind>().ToDictionary(k => k, k => k.ToNeo4jRelationshipType());
}
