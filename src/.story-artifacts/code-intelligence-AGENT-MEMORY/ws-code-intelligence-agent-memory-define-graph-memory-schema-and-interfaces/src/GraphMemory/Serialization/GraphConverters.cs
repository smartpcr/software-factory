using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMemory.Models;

namespace GraphMemory.Serialization;

/// <summary>
/// Reads/writes a <see cref="GraphNode"/> with kind-aware property deserialization.
/// </summary>
public sealed class GraphNodeConverter : JsonConverter<GraphNode>
{
    public override GraphNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var kindStr = root.GetProperty("kind").GetString()
            ?? throw new JsonException("Node missing 'kind'.");

        var kind = EnumLookup.ParseNodeKind(kindStr);

        if (!NodePropertiesConverter.KindToType.TryGetValue(kind, out var propsType))
            throw new JsonException($"Unknown node kind '{kindStr}'.");

        var propsElement = root.GetProperty("properties");
        var props = (INodeProperties?)JsonSerializer.Deserialize(propsElement.GetRawText(), propsType, options)
            ?? throw new JsonException($"Failed to deserialize properties for node kind '{kindStr}'.");

        var node = new GraphNode
        {
            Id = root.GetProperty("id").GetString()!,
            Kind = kind,
            Name = root.GetProperty("name").GetString()!,
            Properties = props,
        };

        if (root.TryGetProperty("qualifiedName", out var qn)) node.QualifiedName = qn.GetString();
        if (root.TryGetProperty("repoId", out var ri)) node.RepoId = ri.GetString();
        if (root.TryGetProperty("provenance", out var prov))
            node.Provenance = JsonSerializer.Deserialize<Provenance>(prov.GetRawText(), options);
        if (root.TryGetProperty("version", out var ver)) node.Version = ver.GetInt32();
        if (root.TryGetProperty("layer", out var lay))
            node.Layer = JsonSerializer.Deserialize<Layer>(lay.GetRawText(), options);
        if (root.TryGetProperty("createdAt", out var ca)) node.CreatedAt = ca.GetString();
        if (root.TryGetProperty("updatedAt", out var ua)) node.UpdatedAt = ua.GetString();
        if (root.TryGetProperty("versionRange", out var vr))
            node.VersionRange = JsonSerializer.Deserialize<VersionRange>(vr.GetRawText(), options);
        if (root.TryGetProperty("snapshotId", out var sid)) node.SnapshotId = sid.GetString();
        if (root.TryGetProperty("stale", out var st)) node.Stale = st.GetBoolean();
        if (root.TryGetProperty("contentHash", out var ch)) node.ContentHash = ch.GetString();
        if (root.TryGetProperty("embedding", out var emb))
        {
            var arr = new List<double>();
            foreach (var el in emb.EnumerateArray()) arr.Add(el.GetDouble());
            node.Embedding = arr.ToArray();
        }
        if (root.TryGetProperty("extensions", out var ext))
            node.Extensions = JsonSerializer.Deserialize<Dictionary<string, object>>(ext.GetRawText(), options);

        return node;
    }

    public override void Write(Utf8JsonWriter writer, GraphNode value, JsonSerializerOptions options)
    {
        var bareOptions = new JsonSerializerOptions(options);
        var convertersToRemove = bareOptions.Converters
            .Where(c => c is GraphNodeConverter)
            .ToList();
        foreach (var c in convertersToRemove)
            bareOptions.Converters.Remove(c);
        JsonSerializer.Serialize(writer, value, bareOptions);
    }
}

/// <summary>
/// Reads/writes a <see cref="GraphEdge"/> with kind-aware property deserialization.
/// </summary>
public sealed class GraphEdgeConverter : JsonConverter<GraphEdge>
{
    public override GraphEdge? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var kindStr = root.GetProperty("kind").GetString()
            ?? throw new JsonException("Edge missing 'kind'.");

        var kind = EnumLookup.ParseEdgeKind(kindStr);

        if (!EdgePropertiesConverter.KindToType.TryGetValue(kind, out var propsType))
            throw new JsonException($"Unknown edge kind '{kindStr}'.");

        var propsElement = root.GetProperty("properties");
        var props = (IEdgeProperties?)JsonSerializer.Deserialize(propsElement.GetRawText(), propsType, options)
            ?? throw new JsonException($"Failed to deserialize properties for edge kind '{kindStr}'.");

        var edge = new GraphEdge
        {
            Id = root.GetProperty("id").GetString()!,
            Kind = kind,
            Source = root.GetProperty("source").GetString()!,
            Target = root.GetProperty("target").GetString()!,
            Properties = props,
        };

        if (root.TryGetProperty("provenance", out var prov))
            edge.Provenance = JsonSerializer.Deserialize<Provenance>(prov.GetRawText(), options);
        if (root.TryGetProperty("version", out var ver)) edge.Version = ver.GetInt32();
        if (root.TryGetProperty("layer", out var lay))
            edge.Layer = JsonSerializer.Deserialize<Layer>(lay.GetRawText(), options);
        if (root.TryGetProperty("createdAt", out var ca)) edge.CreatedAt = ca.GetString();
        if (root.TryGetProperty("updatedAt", out var ua)) edge.UpdatedAt = ua.GetString();
        if (root.TryGetProperty("versionRange", out var vr))
            edge.VersionRange = JsonSerializer.Deserialize<VersionRange>(vr.GetRawText(), options);
        if (root.TryGetProperty("snapshotId", out var sid)) edge.SnapshotId = sid.GetString();
        if (root.TryGetProperty("stale", out var st)) edge.Stale = st.GetBoolean();
        if (root.TryGetProperty("sourceRepoId", out var sri)) edge.SourceRepoId = sri.GetString();
        if (root.TryGetProperty("targetRepoId", out var tri)) edge.TargetRepoId = tri.GetString();
        if (root.TryGetProperty("extensions", out var ext))
            edge.Extensions = JsonSerializer.Deserialize<Dictionary<string, object>>(ext.GetRawText(), options);

        return edge;
    }

    public override void Write(Utf8JsonWriter writer, GraphEdge value, JsonSerializerOptions options)
    {
        var bareOptions = new JsonSerializerOptions(options);
        var convertersToRemove = bareOptions.Converters
            .Where(c => c is GraphEdgeConverter)
            .ToList();
        foreach (var c in convertersToRemove)
            bareOptions.Converters.Remove(c);
        JsonSerializer.Serialize(writer, value, bareOptions);
    }
}
