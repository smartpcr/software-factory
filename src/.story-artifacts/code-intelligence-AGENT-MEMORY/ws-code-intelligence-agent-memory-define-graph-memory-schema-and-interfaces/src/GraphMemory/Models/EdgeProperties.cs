using System.Text.Json.Serialization;

namespace GraphMemory.Models;

/// <summary>Marker interface for typed per-kind edge property bags.</summary>
public interface IEdgeProperties { }

public sealed class ContainsProperties : IEdgeProperties
{
    [JsonPropertyName("order")]
    public int? Order { get; set; }
}

public sealed class CallsProperties : IEdgeProperties
{
    [JsonPropertyName("callSite")]
    public CallSite? CallSite { get; set; }

    [JsonPropertyName("isConditional")]
    public bool? IsConditional { get; set; }

    [JsonPropertyName("isDynamic")]
    public bool? IsDynamic { get; set; }

    [JsonPropertyName("isAsync")]
    public bool? IsAsync { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class CalledByProperties : IEdgeProperties
{
    [JsonPropertyName("callSite")]
    public CallSite? CallSite { get; set; }

    [JsonPropertyName("isConditional")]
    public bool? IsConditional { get; set; }

    [JsonPropertyName("isDynamic")]
    public bool? IsDynamic { get; set; }

    [JsonPropertyName("isAsync")]
    public bool? IsAsync { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class ImportsProperties : IEdgeProperties
{
    [JsonPropertyName("importedSymbols")]
    public ImportedSymbol[]? ImportedSymbols { get; set; }

    [JsonPropertyName("isTypeOnly")]
    public bool? IsTypeOnly { get; set; }

    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; set; }

    [JsonPropertyName("isReExport")]
    public bool? IsReExport { get; set; }
}

public sealed class ReExportsProperties : IEdgeProperties
{
    [JsonPropertyName("importedSymbols")]
    public ImportedSymbol[]? ImportedSymbols { get; set; }

    [JsonPropertyName("isTypeOnly")]
    public bool? IsTypeOnly { get; set; }
}

public sealed class ExportsProperties : IEdgeProperties
{
    [JsonPropertyName("exportedSymbols")]
    public ImportedSymbol[]? ExportedSymbols { get; set; }

    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; set; }

    [JsonPropertyName("isTypeOnly")]
    public bool? IsTypeOnly { get; set; }
}

public sealed class InheritsProperties : IEdgeProperties
{
    [JsonPropertyName("mechanism")]
    public required InheritanceMechanism Mechanism { get; set; }
}

public sealed class OverridesProperties : IEdgeProperties
{
    [JsonPropertyName("callsSuper")]
    public bool? CallsSuper { get; set; }
}

public sealed class ImplementsProperties : IEdgeProperties
{
    [JsonPropertyName("isPartial")]
    public bool? IsPartial { get; set; }
}

public sealed class InstantiatesProperties : IEdgeProperties
{
    [JsonPropertyName("typeArguments")]
    public string[]? TypeArguments { get; set; }
}

public sealed class ExpertiseProperties : IEdgeProperties
{
    [JsonPropertyName("weight")]
    public required double Weight { get; set; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; set; }

    [JsonPropertyName("insight")]
    public string? Insight { get; set; }

    [JsonPropertyName("reinforcementCount")]
    public int? ReinforcementCount { get; set; }

    [JsonPropertyName("relationship")]
    public string? Relationship { get; set; }

    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }

    [JsonPropertyName("observationCount")]
    public int? ObservationCount { get; set; }

    [JsonPropertyName("sourceAgent")]
    public string? SourceAgent { get; set; }

    [JsonPropertyName("evidence")]
    public ExpertiseEvidence[]? Evidence { get; set; }
}

public sealed class AppliesToProperties : IEdgeProperties
{
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public sealed class SimilarToProperties : IEdgeProperties
{
    [JsonPropertyName("cosine_similarity")]
    public required double CosineSimilarity { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }
}

public sealed class BelongsToClusterProperties : IEdgeProperties
{
    [JsonPropertyName("strength")]
    public double? Strength { get; set; }
}

public sealed class SupersedesProperties : IEdgeProperties
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed class DerivedFromProperties : IEdgeProperties
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }
}

public sealed class FrequentlyUsedWithProperties : IEdgeProperties
{
    [JsonPropertyName("coOccurrenceCount")]
    public int? CoOccurrenceCount { get; set; }

    [JsonPropertyName("strength")]
    public double? Strength { get; set; }

    [JsonPropertyName("window")]
    public string? Window { get; set; }
}

public sealed class DependsOnProperties : IEdgeProperties
{
    [JsonPropertyName("dependencyType")]
    public string? DependencyType { get; set; }

    [JsonPropertyName("versionConstraint")]
    public string? VersionConstraint { get; set; }

    [JsonPropertyName("isDev")]
    public bool? IsDev { get; set; }
}

public sealed class ReferencesProperties : IEdgeProperties
{
    [JsonPropertyName("referenceKind")]
    public string? ReferenceKind { get; set; }

    [JsonPropertyName("isWeak")]
    public bool? IsWeak { get; set; }
}
