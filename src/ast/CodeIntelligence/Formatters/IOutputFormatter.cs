// -----------------------------------------------------------------------
// <copyright file="IOutputFormatter.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Formatters;

using CodeIntelligence.Services;

/// <summary>
/// Interface for output formatters.
/// </summary>
public interface IOutputFormatter
{
    /// <summary>
    /// Gets the format name.
    /// </summary>
    string FormatName { get; }

    /// <summary>
    /// Formats the analysis result.
    /// </summary>
    string Format(RepositoryAnalysisResult result);

    /// <summary>
    /// Formats the analysis summary.
    /// </summary>
    string FormatSummary(AnalysisSummary summary);
}
