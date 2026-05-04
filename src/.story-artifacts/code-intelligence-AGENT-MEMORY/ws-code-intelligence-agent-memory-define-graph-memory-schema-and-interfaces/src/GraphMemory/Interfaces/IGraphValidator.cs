using GraphMemory.Models;

namespace GraphMemory.Interfaces;

/// <summary>
/// Validates a <see cref="CodeGraph"/> for structural and semantic correctness.
/// </summary>
public interface IGraphValidator
{
    ValidationResult Validate(CodeGraph graph);
}

/// <summary>Outcome of a graph validation pass.</summary>
public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<ValidationError> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class ValidationError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? ElementId { get; init; }
}
