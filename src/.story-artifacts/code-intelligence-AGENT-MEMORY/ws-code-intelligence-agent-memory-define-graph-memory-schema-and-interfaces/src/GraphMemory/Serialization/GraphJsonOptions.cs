using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphMemory.Serialization;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> configured for the graph memory schema.
/// </summary>
public static class GraphJsonOptions
{
    /// <summary>
    /// Default options: camelCase naming, ignore nulls, enum strings, full converters.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var opts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        opts.Converters.Add(new GraphNodeConverter());
        opts.Converters.Add(new GraphEdgeConverter());
        return opts;
    }
}
