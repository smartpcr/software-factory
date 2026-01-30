// -----------------------------------------------------------------------
// <copyright file="JsonFormatter.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Formatters;

using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIntelligence.Services;

/// <summary>
/// JSON output formatter.
/// </summary>
public class JsonFormatter : IOutputFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc/>
    public string FormatName => "json";

    /// <inheritdoc/>
    public string Format(RepositoryAnalysisResult result)
    {
        return JsonSerializer.Serialize(result, Options);
    }

    /// <inheritdoc/>
    public string FormatSummary(AnalysisSummary summary)
    {
        return JsonSerializer.Serialize(summary, Options);
    }
}
