using System.Text.Json.Serialization;
using GraphMemory.Serialization;

namespace GraphMemory.Models;

/// <summary>
/// An edge in the code knowledge graph.
/// The <see cref="Kind"/> discriminator determines which concrete
/// <see cref="IEdgeProperties"/> subtype is stored in <see cref="Properties"/>.
/// </summary>
public sealed class GraphEdge
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("kind")]
    public required EdgeKind Kind { get; set; }

    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("target")]
    public required string Target { get; set; }

    [JsonPropertyName("properties")]
    [JsonConverter(typeof(EdgePropertiesConverter))]
    public required IEdgeProperties Properties { get; set; }

    [JsonPropertyName("provenance")]
    public Provenance? Provenance { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("layer")]
    public Layer? Layer { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("versionRange")]
    public VersionRange? VersionRange { get; set; }

    [JsonPropertyName("snapshotId")]
    public string? SnapshotId { get; set; }

    [JsonPropertyName("stale")]
    public bool? Stale { get; set; }

    [JsonPropertyName("sourceRepoId")]
    public string? SourceRepoId { get; set; }

    [JsonPropertyName("targetRepoId")]
    public string? TargetRepoId { get; set; }

    /// <summary>
    /// Extension properties for downstream consumers.
    /// Keys must match the pattern <c>x-{vendor}</c> (e.g. "x-myTool", "x-neo4j").
    /// </summary>
    [JsonPropertyName("extensions")]
    public Dictionary<string, object>? Extensions { get; set; }
}
