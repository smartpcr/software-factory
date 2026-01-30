// -----------------------------------------------------------------------
// <copyright file="IDetectionStrategy.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Strategies;

using CodeIntelligence.Models;

/// <summary>
/// Represents a strategy for detecting relationships in code.
/// </summary>
public interface IDetectionStrategy : IDisposable
{
    /// <summary>
    /// Gets the name of this strategy (e.g., "ast", "lsp", "config", "regex").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the supported languages for this strategy.
    /// </summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>
    /// Detects relationships in the given files using the specified rule.
    /// </summary>
    /// <param name="rule">The detection rule to apply.</param>
    /// <param name="filePaths">The file paths to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of detected relationships.</returns>
    Task<IReadOnlyList<DetectedRelationship>> DetectAsync(
        DetectionRule rule,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if this strategy can handle the given detection pattern.
    /// </summary>
    /// <param name="pattern">The pattern to check.</param>
    /// <returns>True if this strategy can handle the pattern.</returns>
    bool CanHandle(DetectionPattern pattern);
}

/// <summary>
/// Context provided to detection strategies during execution.
/// </summary>
public record DetectionContext
{
    /// <summary>
    /// Gets the root path of the repository being analyzed.
    /// </summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>
    /// Gets the solution file path, if any.
    /// </summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Gets the project file paths to analyze.
    /// </summary>
    public required IReadOnlyList<string> ProjectPaths { get; init; }

    /// <summary>
    /// Gets whether to include test projects.
    /// </summary>
    public bool IncludeTests { get; init; } = false;

    /// <summary>
    /// Gets additional options for the detection.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Options { get; init; }
}

/// <summary>
/// Result of applying a detection strategy.
/// </summary>
public record DetectionResult
{
    /// <summary>
    /// Gets the detected relationships.
    /// </summary>
    public required IReadOnlyList<DetectedRelationship> Relationships { get; init; }

    /// <summary>
    /// Gets any errors that occurred during detection.
    /// </summary>
    public IReadOnlyList<DetectionError>? Errors { get; init; }

    /// <summary>
    /// Gets the files that were analyzed.
    /// </summary>
    public required IReadOnlyList<string> AnalyzedFiles { get; init; }

    /// <summary>
    /// Gets the time taken for detection.
    /// </summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Represents an error during detection.
/// </summary>
public record DetectionError
{
    /// <summary>
    /// Gets the file path where the error occurred.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Gets the error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the exception, if any.
    /// </summary>
    public Exception? Exception { get; init; }
}
