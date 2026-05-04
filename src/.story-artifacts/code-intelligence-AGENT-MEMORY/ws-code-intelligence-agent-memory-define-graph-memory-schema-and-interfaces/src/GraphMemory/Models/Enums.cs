using System.Text.Json.Serialization;
using GraphMemory.Serialization;

namespace GraphMemory.Models;

/// <summary>
/// All recognised node kinds in the code knowledge graph.
/// Wire values are snake_case strings defined in <see cref="SnakeCaseEnumMaps"/>.
/// </summary>
[JsonConverter(typeof(NodeKindConverter))]
public enum NodeKind
{
    Repo,
    Package,
    Module,
    Directory,
    File,
    Class,
    Interface,
    TypeAlias,
    Function,
    Method,
    Variable,
    Enum,
    Symbol,
    SemanticCluster,
    Topic,
    ExpertiseNote,
    Pattern,
    Pitfall,
    Preference,
    Insight,
    Workspace,
}

/// <summary>All recognised edge kinds in the code knowledge graph.</summary>
[JsonConverter(typeof(EdgeKindConverter))]
public enum EdgeKind
{
    Contains,
    Calls,
    CalledBy,
    Imports,
    ReExports,
    Exports,
    Inherits,
    Overrides,
    Implements,
    Instantiates,
    Expertise,
    AppliesTo,
    SimilarTo,
    BelongsToCluster,
    Supersedes,
    DerivedFrom,
    FrequentlyUsedWith,
    DependsOn,
    References,
}

[JsonConverter(typeof(LayerConverter))]
public enum Layer
{
    Structural,
    CallChain,
    Semantic,
    Expertise,
}

[JsonConverter(typeof(InheritanceMechanismConverter))]
public enum InheritanceMechanism
{
    Extends,
    Implements,
    Mixin,
}

[JsonConverter(typeof(SymbolKindConverter))]
public enum SymbolKind
{
    Enum,
    EnumMember,
    Constant,
    Namespace,
    Decorator,
    Other,
}

[JsonConverter(typeof(VisibilityConverter))]
public enum Visibility
{
    Public,
    Protected,
    Private,
}

[JsonConverter(typeof(VariableKindConverter))]
public enum VariableKind
{
    Const,
    Let,
    Var,
    Property,
}
