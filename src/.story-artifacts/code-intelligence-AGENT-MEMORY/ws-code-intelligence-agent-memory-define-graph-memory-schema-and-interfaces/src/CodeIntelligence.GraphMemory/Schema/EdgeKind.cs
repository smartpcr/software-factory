using System.Text.Json.Serialization;
using CodeIntelligence.GraphMemory.Serialization;

namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Strongly typed enumeration of edge kinds in the code knowledge graph.
/// New members may be added in minor versions; consumers must handle <see cref="Unknown"/>.
/// Unknown values from newer producers deserialize to <see cref="Unknown"/>
/// via <see cref="LenientEnumConverter{T}"/>.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<EdgeKind>))]
public enum EdgeKind
{
    /// <summary>Reserved for forward-compatibility.</summary>
    Unknown = 0,

    /// <summary>Parent contains child (repo→module, module→file, file→class, etc.).</summary>
    Contains = 1,

    /// <summary>Caller invokes callee.</summary>
    Calls = 2,

    /// <summary>Source imports / references target.</summary>
    Imports = 3,

    /// <summary>Derived type inherits or implements base type.</summary>
    Inherits = 4,

    /// <summary>Agent-learned expertise relationship (weighted).</summary>
    Expertise = 5,

    /// <summary>
    /// Semantic similarity edge. Must carry a <c>cosine_similarity</c>
    /// property in [0.0, 1.0] within the edge's PropertyBag.
    /// </summary>
    SimilarTo = 6,
}
