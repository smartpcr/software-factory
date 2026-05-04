using System.Text.Json.Serialization;

namespace GraphMemory.Models;

/// <summary>Provenance records who/what produced a node or edge.</summary>
public sealed class Provenance
{
    [JsonPropertyName("analyzer")]
    public string? Analyzer { get; set; }

    [JsonPropertyName("analyzerVersion")]
    public string? AnalyzerVersion { get; set; }

    [JsonPropertyName("analyzedAt")]
    public string? AnalyzedAt { get; set; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; set; }
}

/// <summary>Tracks when a node/edge was introduced and last seen.</summary>
public sealed class VersionRange
{
    [JsonPropertyName("introducedIn")]
    public required string IntroducedIn { get; set; }

    [JsonPropertyName("lastSeenIn")]
    public required string LastSeenIn { get; set; }

    [JsonPropertyName("removedIn")]
    public string? RemovedIn { get; set; }
}

/// <summary>Source location within a file.</summary>
public sealed class SourceLocation
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("startLine")]
    public required int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public required int EndLine { get; set; }

    [JsonPropertyName("startColumn")]
    public int? StartColumn { get; set; }

    [JsonPropertyName("endColumn")]
    public int? EndColumn { get; set; }
}

/// <summary>A parameter of a function or method.</summary>
public sealed class FunctionParameter
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("typeAnnotation")]
    public string? TypeAnnotation { get; set; }

    [JsonPropertyName("isOptional")]
    public bool? IsOptional { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }
}

/// <summary>Location of a call within the calling function.</summary>
public sealed class CallSite
{
    [JsonPropertyName("line")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }
}

/// <summary>An imported (or re-exported) symbol.</summary>
public sealed class ImportedSymbol
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
}

/// <summary>A piece of evidence supporting an expertise edge.</summary>
public sealed class ExpertiseEvidence
{
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

/// <summary>Graph-level metadata.</summary>
public sealed class GraphMetadata
{
    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public required string UpdatedAt { get; set; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; set; }

    [JsonPropertyName("repositoryRoot")]
    public string? RepositoryRoot { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>
    /// Extension properties for downstream consumers.
    /// Keys must match the pattern <c>x-{vendor}</c> (e.g. "x-myTool", "x-neo4j").
    /// </summary>
    [JsonPropertyName("extensions")]
    public Dictionary<string, object>? Extensions { get; set; }
}
