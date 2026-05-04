using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMemory.Models;

namespace GraphMemory.Serialization;

/// <summary>
/// Polymorphic converter for <see cref="INodeProperties"/>.
/// On write: serialises the concrete type directly.
/// On read: requires the parent <see cref="GraphNode.Kind"/> to select the correct type.
/// This converter is attached to <see cref="GraphNode.Properties"/> via attribute.
/// </summary>
public sealed class NodePropertiesConverter : JsonConverter<INodeProperties>
{
    public static readonly Dictionary<NodeKind, Type> KindToType = new()
    {
        [NodeKind.Repo]            = typeof(RepoProperties),
        [NodeKind.Package]         = typeof(PackageProperties),
        [NodeKind.Module]          = typeof(ModuleProperties),
        [NodeKind.Directory]       = typeof(DirectoryProperties),
        [NodeKind.File]            = typeof(FileProperties),
        [NodeKind.Class]           = typeof(ClassProperties),
        [NodeKind.Interface]       = typeof(InterfaceProperties),
        [NodeKind.TypeAlias]       = typeof(TypeAliasProperties),
        [NodeKind.Function]        = typeof(FunctionProperties),
        [NodeKind.Method]          = typeof(MethodProperties),
        [NodeKind.Variable]        = typeof(VariableProperties),
        [NodeKind.Enum]            = typeof(EnumProperties),
        [NodeKind.Symbol]          = typeof(SymbolProperties),
        [NodeKind.SemanticCluster] = typeof(SemanticClusterProperties),
        [NodeKind.Topic]           = typeof(TopicProperties),
        [NodeKind.ExpertiseNote]   = typeof(ExpertiseNoteProperties),
        [NodeKind.Pattern]         = typeof(PatternProperties),
        [NodeKind.Pitfall]         = typeof(PitfallProperties),
        [NodeKind.Preference]      = typeof(PreferenceProperties),
        [NodeKind.Insight]         = typeof(InsightProperties),
        [NodeKind.Workspace]       = typeof(WorkspaceProperties),
    };

    public override INodeProperties? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Peek-ahead not possible here; we rely on GraphNodeConverter for full deserialization.
        // If called standalone, fall back to reading as JsonElement and returning null.
        throw new NotSupportedException(
            "Use GraphNodeConverter or CodeGraphConverter for deserialization. " +
            "Standalone INodeProperties deserialization requires knowing the node kind.");
    }

    public override void Write(Utf8JsonWriter writer, INodeProperties value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

/// <summary>
/// Polymorphic converter for <see cref="IEdgeProperties"/>.
/// </summary>
public sealed class EdgePropertiesConverter : JsonConverter<IEdgeProperties>
{
    public static readonly Dictionary<EdgeKind, Type> KindToType = new()
    {
        [EdgeKind.Contains]           = typeof(ContainsProperties),
        [EdgeKind.Calls]              = typeof(CallsProperties),
        [EdgeKind.CalledBy]           = typeof(CalledByProperties),
        [EdgeKind.Imports]            = typeof(ImportsProperties),
        [EdgeKind.ReExports]          = typeof(ReExportsProperties),
        [EdgeKind.Exports]            = typeof(ExportsProperties),
        [EdgeKind.Inherits]           = typeof(InheritsProperties),
        [EdgeKind.Overrides]          = typeof(OverridesProperties),
        [EdgeKind.Implements]         = typeof(ImplementsProperties),
        [EdgeKind.Instantiates]       = typeof(InstantiatesProperties),
        [EdgeKind.Expertise]          = typeof(ExpertiseProperties),
        [EdgeKind.AppliesTo]          = typeof(AppliesToProperties),
        [EdgeKind.SimilarTo]          = typeof(SimilarToProperties),
        [EdgeKind.BelongsToCluster]   = typeof(BelongsToClusterProperties),
        [EdgeKind.Supersedes]         = typeof(SupersedesProperties),
        [EdgeKind.DerivedFrom]        = typeof(DerivedFromProperties),
        [EdgeKind.FrequentlyUsedWith] = typeof(FrequentlyUsedWithProperties),
        [EdgeKind.DependsOn]          = typeof(DependsOnProperties),
        [EdgeKind.References]         = typeof(ReferencesProperties),
    };

    public override IEdgeProperties? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException(
            "Use GraphEdgeConverter or CodeGraphConverter for deserialization.");
    }

    public override void Write(Utf8JsonWriter writer, IEdgeProperties value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
