// -----------------------------------------------------------------------
// <copyright file="ConfidenceCalculator.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Scoring;

using CodeIntelligence.Models;

/// <summary>
/// Calculates confidence scores for detected relationships based on
/// multiple evidence sources with weighted contributions.
/// </summary>
public sealed class ConfidenceCalculator
{
    private const float DefaultBaseScore = 0.5f;
    private const float MaxConfidence = 1.0f;
    private const float MinConfidence = 0.0f;

    /// <summary>
    /// Default evidence weights for different detection sources.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, float> DefaultWeights = new Dictionary<string, float>
    {
        ["ast"] = 0.35f,
        ["lsp"] = 0.25f,
        ["config"] = 0.25f,
        ["regex"] = 0.10f,
        ["manifest"] = 0.30f,
        ["inheritance"] = 0.20f,
        ["attribute"] = 0.25f,
        ["naming"] = 0.10f
    };

    private readonly IReadOnlyDictionary<string, float> weights;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfidenceCalculator"/> class.
    /// </summary>
    public ConfidenceCalculator()
        : this(DefaultWeights)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfidenceCalculator"/> class
    /// with custom weights.
    /// </summary>
    /// <param name="weights">Custom evidence weights.</param>
    public ConfidenceCalculator(IReadOnlyDictionary<string, float> weights)
    {
        this.weights = weights;
    }

    /// <summary>
    /// Calculates confidence score from multiple evidence sources.
    /// </summary>
    /// <param name="baseScore">The base confidence score.</param>
    /// <param name="evidenceChain">The evidence chain.</param>
    /// <returns>The calculated confidence score.</returns>
    public float Calculate(float baseScore, IReadOnlyList<Evidence> evidenceChain)
    {
        if (evidenceChain == null || evidenceChain.Count == 0)
        {
            return Clamp(baseScore);
        }

        // Start with base score
        var totalWeight = 0f;
        var weightedSum = 0f;

        foreach (var evidence in evidenceChain)
        {
            var weight = this.GetWeight(evidence.Source);
            var contribution = evidence.Weight * weight;

            weightedSum += contribution;
            totalWeight += weight;
        }

        // Combine base score with evidence contributions
        var evidenceContribution = totalWeight > 0 ? weightedSum / totalWeight : 0;
        var finalScore = (baseScore * 0.4f) + (evidenceContribution * 0.6f);

        return Clamp(finalScore);
    }

    /// <summary>
    /// Calculates confidence with boost from validation.
    /// </summary>
    /// <param name="baseScore">The base confidence score.</param>
    /// <param name="evidenceChain">The evidence chain.</param>
    /// <param name="validationBoost">Additional boost from validation (e.g., lifecycle methods found).</param>
    /// <returns>The calculated confidence score.</returns>
    public float CalculateWithBoost(float baseScore, IReadOnlyList<Evidence> evidenceChain, float validationBoost)
    {
        var calculated = this.Calculate(baseScore, evidenceChain);
        return Clamp(calculated + validationBoost);
    }

    /// <summary>
    /// Creates an evidence object for AST detection.
    /// </summary>
    /// <param name="description">Evidence description.</param>
    /// <param name="filePath">File path where detected.</param>
    /// <param name="lineNumber">Line number.</param>
    /// <returns>Evidence object.</returns>
    public Evidence CreateAstEvidence(string description, string? filePath = null, int? lineNumber = null)
    {
        return new Evidence
        {
            Source = "ast",
            Description = description,
            Weight = this.GetWeight("ast"),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    /// <summary>
    /// Creates an evidence object for LSP detection.
    /// </summary>
    /// <param name="description">Evidence description.</param>
    /// <param name="filePath">File path where detected.</param>
    /// <param name="lineNumber">Line number.</param>
    /// <returns>Evidence object.</returns>
    public Evidence CreateLspEvidence(string description, string? filePath = null, int? lineNumber = null)
    {
        return new Evidence
        {
            Source = "lsp",
            Description = description,
            Weight = this.GetWeight("lsp"),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    /// <summary>
    /// Creates an evidence object for config/manifest detection.
    /// </summary>
    /// <param name="description">Evidence description.</param>
    /// <param name="filePath">File path where detected.</param>
    /// <param name="lineNumber">Line number.</param>
    /// <returns>Evidence object.</returns>
    public Evidence CreateConfigEvidence(string description, string? filePath = null, int? lineNumber = null)
    {
        return new Evidence
        {
            Source = "config",
            Description = description,
            Weight = this.GetWeight("config"),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    /// <summary>
    /// Creates an evidence object for manifest detection.
    /// </summary>
    /// <param name="description">Evidence description.</param>
    /// <param name="filePath">File path where detected.</param>
    /// <param name="lineNumber">Line number.</param>
    /// <returns>Evidence object.</returns>
    public Evidence CreateManifestEvidence(string description, string? filePath = null, int? lineNumber = null)
    {
        return new Evidence
        {
            Source = "manifest",
            Description = description,
            Weight = this.GetWeight("manifest"),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    /// <summary>
    /// Creates an evidence object for inheritance detection.
    /// </summary>
    /// <param name="baseTypeName">The base type name.</param>
    /// <param name="filePath">File path where detected.</param>
    /// <param name="lineNumber">Line number.</param>
    /// <returns>Evidence object.</returns>
    public Evidence CreateInheritanceEvidence(string baseTypeName, string? filePath = null, int? lineNumber = null)
    {
        return new Evidence
        {
            Source = "inheritance",
            Description = $"Inherits from {baseTypeName}",
            Weight = this.GetWeight("inheritance"),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    /// <summary>
    /// Creates an evidence object for attribute detection.
    /// </summary>
    /// <param name="attributeName">The attribute name.</param>
    /// <param name="filePath">File path where detected.</param>
    /// <param name="lineNumber">Line number.</param>
    /// <returns>Evidence object.</returns>
    public Evidence CreateAttributeEvidence(string attributeName, string? filePath = null, int? lineNumber = null)
    {
        return new Evidence
        {
            Source = "attribute",
            Description = $"Has [{attributeName}] attribute",
            Weight = this.GetWeight("attribute"),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    /// <summary>
    /// Correlates multiple detection results and calculates combined confidence.
    /// </summary>
    /// <param name="results">Multiple detection results from different strategies.</param>
    /// <returns>Correlated result with combined confidence.</returns>
    public CorrelatedResult Correlate(IReadOnlyList<DetectionResult> results)
    {
        if (results == null || results.Count == 0)
        {
            return new CorrelatedResult
            {
                FinalConfidence = 0,
                CombinedEvidence = [],
                StrategiesUsed = []
            };
        }

        var combinedEvidence = new List<Evidence>();
        var strategies = new HashSet<string>();
        var baseScoreSum = 0f;

        foreach (var result in results)
        {
            combinedEvidence.AddRange(result.EvidenceChain);
            strategies.Add(result.Strategy);
            baseScoreSum += result.BaseScore;
        }

        var averageBase = baseScoreSum / results.Count;
        var finalConfidence = this.Calculate(averageBase, combinedEvidence);

        // Multi-strategy bonus: higher confidence when multiple strategies agree
        if (strategies.Count > 1)
        {
            var multiStrategyBonus = (strategies.Count - 1) * 0.05f;
            finalConfidence = Clamp(finalConfidence + multiStrategyBonus);
        }

        return new CorrelatedResult
        {
            FinalConfidence = finalConfidence,
            CombinedEvidence = combinedEvidence,
            StrategiesUsed = strategies.ToList()
        };
    }

    /// <summary>
    /// Determines if a confidence score meets the minimum threshold.
    /// </summary>
    /// <param name="confidence">The confidence score.</param>
    /// <param name="threshold">The minimum threshold (default 0.7).</param>
    /// <returns>True if confidence meets threshold.</returns>
    public static bool MeetsThreshold(float confidence, float threshold = 0.7f)
    {
        return confidence >= threshold;
    }

    /// <summary>
    /// Gets the confidence level category.
    /// </summary>
    /// <param name="confidence">The confidence score.</param>
    /// <returns>The confidence level.</returns>
    public static ConfidenceLevel GetLevel(float confidence)
    {
        return confidence switch
        {
            >= 0.9f => ConfidenceLevel.VeryHigh,
            >= 0.8f => ConfidenceLevel.High,
            >= 0.7f => ConfidenceLevel.Medium,
            >= 0.5f => ConfidenceLevel.Low,
            _ => ConfidenceLevel.VeryLow
        };
    }

    private float GetWeight(string source)
    {
        return this.weights.TryGetValue(source.ToLowerInvariant(), out var weight)
            ? weight
            : 0.1f; // Default weight for unknown sources
    }

    private static float Clamp(float value)
    {
        return Math.Max(MinConfidence, Math.Min(MaxConfidence, value));
    }
}

/// <summary>
/// Result from a single detection strategy.
/// </summary>
public record DetectionResult
{
    /// <summary>Gets the strategy name.</summary>
    public required string Strategy { get; init; }

    /// <summary>Gets the base confidence score.</summary>
    public required float BaseScore { get; init; }

    /// <summary>Gets the evidence chain.</summary>
    public required IReadOnlyList<Evidence> EvidenceChain { get; init; }
}

/// <summary>
/// Result from correlating multiple detection strategies.
/// </summary>
public record CorrelatedResult
{
    /// <summary>Gets the final combined confidence.</summary>
    public required float FinalConfidence { get; init; }

    /// <summary>Gets the combined evidence from all strategies.</summary>
    public required IReadOnlyList<Evidence> CombinedEvidence { get; init; }

    /// <summary>Gets the strategies that contributed.</summary>
    public required IReadOnlyList<string> StrategiesUsed { get; init; }
}

/// <summary>
/// Confidence level categories.
/// </summary>
public enum ConfidenceLevel
{
    /// <summary>Very low confidence (less than 0.5).</summary>
    VeryLow,

    /// <summary>Low confidence (0.5 to 0.7).</summary>
    Low,

    /// <summary>Medium confidence (0.7 to 0.8).</summary>
    Medium,

    /// <summary>High confidence (0.8 - 0.9).</summary>
    High,

    /// <summary>Very high confidence (>= 0.9).</summary>
    VeryHigh
}
