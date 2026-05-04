using GraphMemory.Models;

namespace GraphMemory.Serialization;

/// <summary>Fast string-to-enum lookup matching JSON Schema wire values.</summary>
internal static class EnumLookup
{
    public static NodeKind ParseNodeKind(string value) =>
        SnakeCaseEnumMaps.StringToNodeKind.TryGetValue(value, out var kind)
            ? kind
            : throw new ArgumentException($"Unknown node kind: '{value}'");

    public static EdgeKind ParseEdgeKind(string value) =>
        SnakeCaseEnumMaps.StringToEdgeKind.TryGetValue(value, out var kind)
            ? kind
            : throw new ArgumentException($"Unknown edge kind: '{value}'");
}
