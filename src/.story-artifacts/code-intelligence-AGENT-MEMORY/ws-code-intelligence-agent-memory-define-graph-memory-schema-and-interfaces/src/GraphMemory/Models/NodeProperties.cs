using System.Text.Json.Serialization;

namespace GraphMemory.Models;

/// <summary>Marker interface for typed per-kind node property bags.</summary>
public interface INodeProperties { }

// ---------------------------------------------------------------------------
// Structural layer
// ---------------------------------------------------------------------------

public sealed class RepoProperties : INodeProperties
{
    [JsonPropertyName("languages")]
    public required string[] Languages { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }
}

public sealed class PackageProperties : INodeProperties
{
    [JsonPropertyName("packageManager")]
    public required string PackageManager { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("registryUrl")]
    public string? RegistryUrl { get; set; }
}

public sealed class ModuleProperties : INodeProperties
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }
}

public sealed class DirectoryProperties : INodeProperties
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }
}

public sealed class FileProperties : INodeProperties
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("loc")]
    public int? Loc { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}

public sealed class ClassProperties : INodeProperties
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

    [JsonPropertyName("isAbstract")]
    public bool? IsAbstract { get; set; }

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }
}

public sealed class InterfaceProperties : INodeProperties
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

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }
}

public sealed class TypeAliasProperties : INodeProperties
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

    [JsonPropertyName("typeExpression")]
    public string? TypeExpression { get; set; }

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }
}

public sealed class FunctionProperties : INodeProperties
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

    [JsonPropertyName("parameters")]
    public required FunctionParameter[] Parameters { get; set; }

    [JsonPropertyName("isAsync")]
    public bool? IsAsync { get; set; }

    [JsonPropertyName("isGenerator")]
    public bool? IsGenerator { get; set; }

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }
}

public sealed class MethodProperties : INodeProperties
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

    [JsonPropertyName("parameters")]
    public required FunctionParameter[] Parameters { get; set; }

    [JsonPropertyName("isAsync")]
    public bool? IsAsync { get; set; }

    [JsonPropertyName("isStatic")]
    public bool? IsStatic { get; set; }

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }

    [JsonPropertyName("visibility")]
    public Visibility? Visibility { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }
}

public sealed class VariableProperties : INodeProperties
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

    [JsonPropertyName("variableKind")]
    public required VariableKind VariableKind { get; set; }

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }

    [JsonPropertyName("typeAnnotation")]
    public string? TypeAnnotation { get; set; }
}

public sealed class EnumProperties : INodeProperties
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

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }

    [JsonPropertyName("members")]
    public string[]? Members { get; set; }
}

public sealed class SymbolProperties : INodeProperties
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

    [JsonPropertyName("symbolKind")]
    public required SymbolKind SymbolKind { get; set; }

    [JsonPropertyName("isExported")]
    public bool? IsExported { get; set; }
}

// ---------------------------------------------------------------------------
// Semantic layer
// ---------------------------------------------------------------------------

public sealed class SemanticClusterProperties : INodeProperties
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("centroid")]
    public double[]? Centroid { get; set; }

    [JsonPropertyName("memberCount")]
    public int? MemberCount { get; set; }
}

public sealed class TopicProperties : INodeProperties
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("keywords")]
    public string[]? Keywords { get; set; }
}

// ---------------------------------------------------------------------------
// Expertise layer
// ---------------------------------------------------------------------------

public sealed class ExpertiseNoteProperties : INodeProperties
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }
}

public sealed class PatternProperties : INodeProperties
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }
}

public sealed class PitfallProperties : INodeProperties
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }
}

public sealed class PreferenceProperties : INodeProperties
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }
}

public sealed class InsightProperties : INodeProperties
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }
}

public sealed class WorkspaceProperties : INodeProperties
{
    [JsonPropertyName("rootPath")]
    public required string RootPath { get; set; }

    [JsonPropertyName("repoIds")]
    public string[]? RepoIds { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
