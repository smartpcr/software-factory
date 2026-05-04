using System.Text.Json.Serialization;
using GraphMemory.Serialization;

namespace GraphMemory.Models;

/// <summary>
/// A node in the code knowledge graph.
/// The <see cref="Kind"/> discriminator determines which concrete
/// <see cref="INodeProperties"/> subtype is stored in <see cref="Properties"/>.
/// </summary>
public sealed class GraphNode
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("kind")]
    public required NodeKind Kind { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("properties")]
    [JsonConverter(typeof(NodePropertiesConverter))]
    public required INodeProperties Properties { get; set; }

    [JsonPropertyName("qualifiedName")]
    public string? QualifiedName { get; set; }

    [JsonPropertyName("repoId")]
    public string? RepoId { get; set; }

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

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("embedding")]
    public double[]? Embedding { get; set; }

    /// <summary>
    /// Extension properties for downstream consumers.
    /// Keys must match the pattern <c>x-{vendor}</c> (e.g. "x-myTool", "x-neo4j").
    /// </summary>
    [JsonPropertyName("extensions")]
    public Dictionary<string, object>? Extensions { get; set; }
}
