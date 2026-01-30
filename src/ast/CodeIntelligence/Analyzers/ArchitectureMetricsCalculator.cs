// -----------------------------------------------------------------------
// <copyright file="ArchitectureMetricsCalculator.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System;
using System.Collections.Generic;
using System.Linq;

using static CodeIntelligence.Analyzers.LayerClassificationAnalyzer;

/// <summary>
/// Calculates architecture metrics including coupling, cohesion, and layer adherence.
/// </summary>
public class ArchitectureMetricsCalculator
{
    /// <summary>
    /// Calculates comprehensive architecture metrics.
    /// </summary>
    public ArchitectureMetrics CalculateMetrics(
        IEnumerable<LayerClassification> classifications,
        IEnumerable<LayerInteractionInfo> interactions,
        IEnumerable<LayerViolation> violations)
    {
        var classificationList = classifications.ToList();
        var interactionList = interactions.ToList();
        var violationList = violations.ToList();

        var layerSummaries = this.CalculateLayerSummaries(classificationList, interactionList);
        var couplingScore = this.CalculateCouplingScore(interactionList, classificationList);
        var cohesionScore = this.CalculateCohesionScore(classificationList, interactionList);
        var adherenceScore = this.CalculateLayerAdherence(interactionList, violationList);

        return new ArchitectureMetrics
        {
            TotalLayers = layerSummaries.Count,
            TotalComponents = classificationList.Count,
            TotalInteractions = interactionList.Count,
            ViolationCount = violationList.Count,
            CouplingScore = couplingScore,
            CohesionScore = cohesionScore,
            LayerAdherence = adherenceScore,
            LayerSummaries = layerSummaries,
            InteractionMatrix = this.BuildInteractionMatrix(interactionList),
        };
    }

    /// <summary>
    /// Calculates summaries for each layer.
    /// </summary>
    public Dictionary<ArchitectureLayer, LayerSummary> CalculateLayerSummaries(
        IEnumerable<LayerClassification> classifications,
        IEnumerable<LayerInteractionInfo> interactions)
    {
        var classificationList = classifications.ToList();
        var interactionList = interactions.ToList();
        var result = new Dictionary<ArchitectureLayer, LayerSummary>();

        foreach (ArchitectureLayer layer in Enum.GetValues(typeof(ArchitectureLayer)))
        {
            if (layer == ArchitectureLayer.Unknown)
            {
                continue;
            }

            var layerTypes = classificationList.Where(c => c.Layer == layer).ToList();
            if (layerTypes.Count == 0)
            {
                continue;
            }

            var namespaces = layerTypes
                .Where(t => t.Namespace != null)
                .Select(t => t.Namespace!)
                .Distinct()
                .ToList();

            var incomingInteractions = interactionList.Count(i => i.TargetLayer == layer);
            var outgoingInteractions = interactionList.Count(i => i.SourceLayer == layer);

            result[layer] = new LayerSummary
            {
                LayerName = layer.ToString(),
                ComponentCount = layerTypes.Count,
                ClassCount = layerTypes.Count(t => !t.IsInterface && !t.IsAbstract),
                InterfaceCount = layerTypes.Count(t => t.IsInterface),
                AbstractCount = layerTypes.Count(t => t.IsAbstract && !t.IsInterface),
                Namespaces = namespaces,
                IncomingDependencies = incomingInteractions,
                OutgoingDependencies = outgoingInteractions,
                TopComponents = layerTypes
                    .OrderByDescending(t => t.Confidence)
                    .Take(5)
                    .Select(t => t.TypeName)
                    .ToList(),
            };
        }

        return result;
    }

    /// <summary>
    /// Calculates coupling score (0-1, lower is better).
    /// Coupling = (actual cross-layer dependencies) / (maximum possible dependencies)
    /// </summary>
    public float CalculateCouplingScore(
        IEnumerable<LayerInteractionInfo> interactions,
        IEnumerable<LayerClassification> classifications)
    {
        var classificationList = classifications.ToList();
        var interactionList = interactions.ToList();

        if (classificationList.Count == 0)
        {
            return 0f;
        }

        // Count unique cross-layer type pairs
        var uniqueCrossLayerPairs = interactionList
            .Where(i => i.SourceLayer != i.TargetLayer)
            .Select(i => (i.SourceType, i.TargetType))
            .Distinct()
            .Count();

        // Maximum possible is every type depending on every other type in different layers
        var totalTypes = classificationList.Count;
        var maxPossible = totalTypes * (totalTypes - 1);

        if (maxPossible == 0)
        {
            return 0f;
        }

        return Math.Min(1f, uniqueCrossLayerPairs / (float)maxPossible);
    }

    /// <summary>
    /// Calculates cohesion score (0-1, higher is better).
    /// Cohesion = (same-layer interactions) / (total interactions)
    /// </summary>
    public float CalculateCohesionScore(
        IEnumerable<LayerClassification> classifications,
        IEnumerable<LayerInteractionInfo> interactions)
    {
        var interactionList = interactions.ToList();

        if (interactionList.Count == 0)
        {
            return 1f; // No interactions means perfect cohesion
        }

        // Count interactions within the same layer (we only track cross-layer, so this is inverse)
        var crossLayerCount = interactionList.Count;

        // Estimate total interactions (assume similar number within layers)
        var classificationList = classifications.ToList();
        var typesPerLayer = classificationList
            .Where(c => c.Layer != ArchitectureLayer.Unknown)
            .GroupBy(c => c.Layer)
            .ToDictionary(g => g.Key, g => g.Count());

        // Estimate intra-layer interactions based on types per layer
        var estimatedIntraLayer = typesPerLayer.Values.Sum(count => count * (count - 1) / 2);
        var totalEstimated = crossLayerCount + estimatedIntraLayer;

        if (totalEstimated == 0)
        {
            return 1f;
        }

        return Math.Min(1f, estimatedIntraLayer / (float)totalEstimated);
    }

    /// <summary>
    /// Calculates layer adherence percentage (valid interactions / total interactions).
    /// </summary>
    public float CalculateLayerAdherence(
        IEnumerable<LayerInteractionInfo> interactions,
        IEnumerable<LayerViolation> violations)
    {
        var interactionCount = interactions.Count();
        var violationCount = violations.Count();

        if (interactionCount == 0)
        {
            return 1f; // No interactions means 100% adherence
        }

        var validInteractions = interactionCount - violationCount;
        return Math.Max(0f, validInteractions / (float)interactionCount);
    }

    /// <summary>
    /// Builds an interaction matrix showing counts between layer pairs.
    /// </summary>
    public Dictionary<(ArchitectureLayer Source, ArchitectureLayer Target), int> BuildInteractionMatrix(
        IEnumerable<LayerInteractionInfo> interactions)
    {
        return interactions
            .GroupBy(i => (i.SourceLayer, i.TargetLayer))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Formats the interaction matrix as a text table.
    /// </summary>
    public string FormatInteractionMatrix(Dictionary<(ArchitectureLayer Source, ArchitectureLayer Target), int> matrix)
    {
        var layers = Enum.GetValues(typeof(ArchitectureLayer))
            .Cast<ArchitectureLayer>()
            .Where(l => l != ArchitectureLayer.Unknown)
            .ToList();

        var sb = new System.Text.StringBuilder();

        // Header
        sb.Append("                  ");
        foreach (var layer in layers)
        {
            sb.Append($"│ {layer.ToString()[..Math.Min(4, layer.ToString().Length)],-4} ");
        }

        sb.AppendLine("│");

        // Separator
        sb.Append("──────────────────");
        foreach (var _ in layers)
        {
            sb.Append("┼──────");
        }

        sb.AppendLine("┤");

        // Rows
        foreach (var sourceLayer in layers)
        {
            sb.Append($"{sourceLayer,-18}");
            foreach (var targetLayer in layers)
            {
                if (sourceLayer == targetLayer)
                {
                    sb.Append("│  -   ");
                }
                else
                {
                    var count = matrix.GetValueOrDefault((sourceLayer, targetLayer), 0);
                    var marker = count > 0 ? $"{count,4}" : "   0";
                    sb.Append($"│{marker}  ");
                }
            }

            sb.AppendLine("│");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Comprehensive architecture metrics.
/// </summary>
public record ArchitectureMetrics
{
    /// <summary>Gets the total number of detected layers.</summary>
    public required int TotalLayers { get; init; }

    /// <summary>Gets the total number of components.</summary>
    public required int TotalComponents { get; init; }

    /// <summary>Gets the total number of cross-layer interactions.</summary>
    public required int TotalInteractions { get; init; }

    /// <summary>Gets the number of violations.</summary>
    public required int ViolationCount { get; init; }

    /// <summary>Gets the coupling score (0-1, lower is better).</summary>
    public required float CouplingScore { get; init; }

    /// <summary>Gets the cohesion score (0-1, higher is better).</summary>
    public required float CohesionScore { get; init; }

    /// <summary>Gets the layer adherence percentage (0-1).</summary>
    public required float LayerAdherence { get; init; }

    /// <summary>Gets summaries for each layer.</summary>
    public required Dictionary<ArchitectureLayer, LayerSummary> LayerSummaries { get; init; }

    /// <summary>Gets the interaction count matrix.</summary>
    public required Dictionary<(ArchitectureLayer Source, ArchitectureLayer Target), int> InteractionMatrix { get; init; }
}

/// <summary>
/// Summary of a single architectural layer.
/// </summary>
public record LayerSummary
{
    /// <summary>Gets the layer name.</summary>
    public required string LayerName { get; init; }

    /// <summary>Gets the total component count.</summary>
    public required int ComponentCount { get; init; }

    /// <summary>Gets the class count.</summary>
    public required int ClassCount { get; init; }

    /// <summary>Gets the interface count.</summary>
    public required int InterfaceCount { get; init; }

    /// <summary>Gets the abstract class count.</summary>
    public required int AbstractCount { get; init; }

    /// <summary>Gets the namespaces in this layer.</summary>
    public required List<string> Namespaces { get; init; }

    /// <summary>Gets the incoming dependency count.</summary>
    public required int IncomingDependencies { get; init; }

    /// <summary>Gets the outgoing dependency count.</summary>
    public required int OutgoingDependencies { get; init; }

    /// <summary>Gets top components in this layer.</summary>
    public required List<string> TopComponents { get; init; }
}
