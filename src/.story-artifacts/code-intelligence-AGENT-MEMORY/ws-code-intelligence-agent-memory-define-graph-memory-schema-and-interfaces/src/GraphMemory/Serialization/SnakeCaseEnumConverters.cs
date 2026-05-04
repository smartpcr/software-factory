using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMemory.Models;

namespace GraphMemory.Serialization;

/// <summary>
/// Maps between enum values and their snake_case JSON wire representations.
/// All maps are bidirectional and exhaustive for every enum member.
/// </summary>
public static class SnakeCaseEnumMaps
{
    public static readonly IReadOnlyDictionary<NodeKind, string> NodeKindToString = new Dictionary<NodeKind, string>
    {
        [NodeKind.Repo]            = "repo",
        [NodeKind.Package]         = "package",
        [NodeKind.Module]          = "module",
        [NodeKind.Directory]       = "directory",
        [NodeKind.File]            = "file",
        [NodeKind.Class]           = "class",
        [NodeKind.Interface]       = "interface",
        [NodeKind.TypeAlias]       = "type_alias",
        [NodeKind.Function]        = "function",
        [NodeKind.Method]          = "method",
        [NodeKind.Variable]        = "variable",
        [NodeKind.Enum]            = "enum",
        [NodeKind.Symbol]          = "symbol",
        [NodeKind.SemanticCluster] = "semantic_cluster",
        [NodeKind.Topic]           = "topic",
        [NodeKind.ExpertiseNote]   = "expertise_note",
        [NodeKind.Pattern]         = "pattern",
        [NodeKind.Pitfall]         = "pitfall",
        [NodeKind.Preference]      = "preference",
        [NodeKind.Insight]         = "insight",
        [NodeKind.Workspace]       = "workspace",
    };

    public static readonly IReadOnlyDictionary<string, NodeKind> StringToNodeKind =
        NodeKindToString.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly IReadOnlyDictionary<EdgeKind, string> EdgeKindToString = new Dictionary<EdgeKind, string>
    {
        [EdgeKind.Contains]           = "contains",
        [EdgeKind.Calls]              = "calls",
        [EdgeKind.CalledBy]           = "called_by",
        [EdgeKind.Imports]            = "imports",
        [EdgeKind.ReExports]          = "re_exports",
        [EdgeKind.Exports]            = "exports",
        [EdgeKind.Inherits]           = "inherits",
        [EdgeKind.Overrides]          = "overrides",
        [EdgeKind.Implements]         = "implements",
        [EdgeKind.Instantiates]       = "instantiates",
        [EdgeKind.Expertise]          = "expertise",
        [EdgeKind.AppliesTo]          = "applies_to",
        [EdgeKind.SimilarTo]          = "similar_to",
        [EdgeKind.BelongsToCluster]   = "belongs_to_cluster",
        [EdgeKind.Supersedes]         = "supersedes",
        [EdgeKind.DerivedFrom]        = "derived_from",
        [EdgeKind.FrequentlyUsedWith] = "frequently_used_with",
        [EdgeKind.DependsOn]          = "depends_on",
        [EdgeKind.References]         = "references",
    };

    public static readonly IReadOnlyDictionary<string, EdgeKind> StringToEdgeKind =
        EdgeKindToString.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly IReadOnlyDictionary<Layer, string> LayerToString = new Dictionary<Layer, string>
    {
        [Layer.Structural] = "structural",
        [Layer.CallChain]  = "call_chain",
        [Layer.Semantic]   = "semantic",
        [Layer.Expertise]  = "expertise",
    };

    public static readonly IReadOnlyDictionary<string, Layer> StringToLayer =
        LayerToString.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly IReadOnlyDictionary<InheritanceMechanism, string> InheritanceMechanismToString = new Dictionary<InheritanceMechanism, string>
    {
        [InheritanceMechanism.Extends]    = "extends",
        [InheritanceMechanism.Implements] = "implements",
        [InheritanceMechanism.Mixin]      = "mixin",
    };

    public static readonly IReadOnlyDictionary<string, InheritanceMechanism> StringToInheritanceMechanism =
        InheritanceMechanismToString.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly IReadOnlyDictionary<SymbolKind, string> SymbolKindToString = new Dictionary<SymbolKind, string>
    {
        [SymbolKind.Enum]       = "enum",
        [SymbolKind.EnumMember] = "enum_member",
        [SymbolKind.Constant]   = "constant",
        [SymbolKind.Namespace]  = "namespace",
        [SymbolKind.Decorator]  = "decorator",
        [SymbolKind.Other]      = "other",
    };

    public static readonly IReadOnlyDictionary<string, SymbolKind> StringToSymbolKind =
        SymbolKindToString.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly IReadOnlyDictionary<Visibility, string> VisibilityToString = new Dictionary<Visibility, string>
    {
        [Visibility.Public]    = "public",
        [Visibility.Protected] = "protected",
        [Visibility.Private]   = "private",
    };

    public static readonly IReadOnlyDictionary<string, Visibility> StringToVisibility =
        VisibilityToString.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly IReadOnlyDictionary<VariableKind, string> VariableKindToString = new Dictionary<VariableKind, string>
    {
        [VariableKind.Const]    = "const",
        [VariableKind.Let]      = "let",
        [VariableKind.Var]      = "var",
        [VariableKind.Property] = "property",
    };

    public static readonly IReadOnlyDictionary<string, VariableKind> StringToVariableKind =
        VariableKindToString.ToDictionary(kv => kv.Value, kv => kv.Key);
}

// --- Typed converters for each enum ---

public sealed class NodeKindConverter : JsonConverter<NodeKind>
{
    public override NodeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToNodeKind[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, NodeKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.NodeKindToString[value]);
}

public sealed class EdgeKindConverter : JsonConverter<EdgeKind>
{
    public override EdgeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToEdgeKind[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, EdgeKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.EdgeKindToString[value]);
}

public sealed class LayerConverter : JsonConverter<Layer>
{
    public override Layer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToLayer[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, Layer value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.LayerToString[value]);
}

public sealed class InheritanceMechanismConverter : JsonConverter<InheritanceMechanism>
{
    public override InheritanceMechanism Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToInheritanceMechanism[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, InheritanceMechanism value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.InheritanceMechanismToString[value]);
}

public sealed class SymbolKindConverter : JsonConverter<SymbolKind>
{
    public override SymbolKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToSymbolKind[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, SymbolKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.SymbolKindToString[value]);
}

public sealed class VisibilityConverter : JsonConverter<Visibility>
{
    public override Visibility Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToVisibility[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, Visibility value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.VisibilityToString[value]);
}

public sealed class VariableKindConverter : JsonConverter<VariableKind>
{
    public override VariableKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SnakeCaseEnumMaps.StringToVariableKind[reader.GetString()!];

    public override void Write(Utf8JsonWriter writer, VariableKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnumMaps.VariableKindToString[value]);
}
