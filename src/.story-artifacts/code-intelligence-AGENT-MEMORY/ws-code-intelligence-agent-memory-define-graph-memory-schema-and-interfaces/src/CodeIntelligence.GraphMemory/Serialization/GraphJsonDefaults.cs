using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIntelligence.GraphMemory.Schema;

namespace CodeIntelligence.GraphMemory.Serialization;

/// <summary>
/// Canonical <see cref="JsonSerializerOptions"/> for the code knowledge graph.
/// All producers and consumers must use these options to ensure wire compatibility.
/// </summary>
public static class GraphJsonDefaults
{
    /// <summary>Shared, thread-safe serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Tolerate unknown JSON properties for forward compatibility.
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        // Enum converters are applied via [JsonConverter] attributes on
        // NodeKind / EdgeKind using LenientEnumConverter<T>.
        // Do NOT add a JsonStringEnumConverter here — options-level converters
        // take precedence over type-level attributes in System.Text.Json,
        // which would bypass the lenient fallback-to-Unknown behavior.

        return opts;
    }

    /// <summary>Serialize a graph to UTF-8 JSON.</summary>
    public static string Serialize(CodeKnowledgeGraph graph)
        => JsonSerializer.Serialize(graph, Options);

    /// <summary>Deserialize a graph from JSON.</summary>
    public static CodeKnowledgeGraph? Deserialize(string json)
        => JsonSerializer.Deserialize<CodeKnowledgeGraph>(json, Options);
}
