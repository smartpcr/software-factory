using System.Text.Json.Serialization;
using CodeIntelligence.GraphMemory.Serialization;

namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Strongly typed enumeration of node kinds in the code knowledge graph.
/// New members may be added in minor versions; consumers must handle <see cref="Unknown"/>.
/// Unknown values from newer producers deserialize to <see cref="Unknown"/>
/// via <see cref="LenientEnumConverter{T}"/>.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<NodeKind>))]
public enum NodeKind
{
    /// <summary>Reserved for forward-compatibility. Consumers must not reject unknown values.</summary>
    Unknown = 0,

    /// <summary>Top-level repository node.</summary>
    Repo = 1,

    /// <summary>Logical module or namespace grouping.</summary>
    Module = 2,

    /// <summary>Source file on disk.</summary>
    File = 3,

    /// <summary>Class, struct, interface, or other type declaration.</summary>
    Class = 4,

    /// <summary>Function or method declaration.</summary>
    Function = 5,

    /// <summary>Field, property, constant, or local variable.</summary>
    Variable = 6,
}
