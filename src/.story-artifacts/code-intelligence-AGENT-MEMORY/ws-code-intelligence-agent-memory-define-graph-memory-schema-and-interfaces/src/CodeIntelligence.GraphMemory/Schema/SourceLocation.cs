namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// Pinpoints a symbol's location within a source file.
/// </summary>
public sealed record SourceLocation
{
    /// <summary>Repository-relative file path using forward-slash separators.</summary>
    public required string FilePath { get; init; }

    public int? StartLine { get; init; }
    public int? StartColumn { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}
