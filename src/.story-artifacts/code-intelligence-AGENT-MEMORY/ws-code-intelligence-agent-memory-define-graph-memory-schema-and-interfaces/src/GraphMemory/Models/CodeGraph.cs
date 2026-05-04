using System.Text.Json.Serialization;

namespace GraphMemory.Models;

/// <summary>
/// Root container for a serialised code knowledge graph.
/// </summary>
public sealed class CodeGraph
{
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; set; }

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("nodes")]
    public required List<GraphNode> Nodes { get; set; }

    [JsonPropertyName("edges")]
    public required List<GraphEdge> Edges { get; set; }

    [JsonPropertyName("metadata")]
    public required GraphMetadata Metadata { get; set; }

    public const string CurrentSchemaVersion = "0.3.0";
}
