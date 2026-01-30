// -----------------------------------------------------------------------
// <copyright file="LayerViolationDetector.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System;
using System.Collections.Generic;
using System.Linq;

using static CodeIntelligence.Analyzers.LayerClassificationAnalyzer;

/// <summary>
/// Detects architectural layer violations based on layered architecture rules.
/// </summary>
public class LayerViolationDetector
{
    /// <summary>
    /// Defines allowed layer interactions. Key is source layer, value is list of allowed target layers.
    /// </summary>
    private static readonly Dictionary<ArchitectureLayer, HashSet<ArchitectureLayer>> AllowedInteractions = new()
    {
        // Presentation can call API and Application
        [ArchitectureLayer.Presentation] = new HashSet<ArchitectureLayer>
        {
            ArchitectureLayer.Api,
            ArchitectureLayer.Application,
            ArchitectureLayer.CrossCutting,
        },

        // API can call Application and Domain
        [ArchitectureLayer.Api] = new HashSet<ArchitectureLayer>
        {
            ArchitectureLayer.Application,
            ArchitectureLayer.Domain,
            ArchitectureLayer.CrossCutting,
        },

        // Application can call Domain and Infrastructure
        [ArchitectureLayer.Application] = new HashSet<ArchitectureLayer>
        {
            ArchitectureLayer.Domain,
            ArchitectureLayer.Infrastructure,
            ArchitectureLayer.CrossCutting,
        },

        // Domain should have no outward dependencies (except cross-cutting)
        [ArchitectureLayer.Domain] = new HashSet<ArchitectureLayer>
        {
            ArchitectureLayer.CrossCutting,
        },

        // Infrastructure can reference Domain (implements interfaces)
        [ArchitectureLayer.Infrastructure] = new HashSet<ArchitectureLayer>
        {
            ArchitectureLayer.Domain,
            ArchitectureLayer.CrossCutting,
        },

        // CrossCutting can reference Domain types
        [ArchitectureLayer.CrossCutting] = new HashSet<ArchitectureLayer>
        {
            ArchitectureLayer.Domain,
        },
    };

    /// <summary>
    /// Violation severity definitions.
    /// </summary>
    private static readonly Dictionary<(ArchitectureLayer Source, ArchitectureLayer Target), ViolationSeverity> ViolationSeverities = new()
    {
        // Critical violations - break dependency inversion
        [(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure)] = ViolationSeverity.Critical,
        [(ArchitectureLayer.Domain, ArchitectureLayer.Application)] = ViolationSeverity.Critical,
        [(ArchitectureLayer.Domain, ArchitectureLayer.Api)] = ViolationSeverity.Critical,
        [(ArchitectureLayer.Domain, ArchitectureLayer.Presentation)] = ViolationSeverity.Critical,

        // High severity - bypass service layer
        [(ArchitectureLayer.Presentation, ArchitectureLayer.Infrastructure)] = ViolationSeverity.High,
        [(ArchitectureLayer.Presentation, ArchitectureLayer.Domain)] = ViolationSeverity.High,
        [(ArchitectureLayer.Api, ArchitectureLayer.Infrastructure)] = ViolationSeverity.Medium,

        // Medium severity - skip layers
        [(ArchitectureLayer.Application, ArchitectureLayer.Api)] = ViolationSeverity.Medium,
        [(ArchitectureLayer.Application, ArchitectureLayer.Presentation)] = ViolationSeverity.Medium,

        // Low severity - unusual but not critical
        [(ArchitectureLayer.Infrastructure, ArchitectureLayer.Application)] = ViolationSeverity.Low,
        [(ArchitectureLayer.Infrastructure, ArchitectureLayer.Api)] = ViolationSeverity.Low,
    };

    /// <summary>
    /// Violation messages and recommendations.
    /// </summary>
    private static readonly Dictionary<(ArchitectureLayer Source, ArchitectureLayer Target), (string Message, string Recommendation)> ViolationMessages = new()
    {
        [(ArchitectureLayer.Domain, ArchitectureLayer.Infrastructure)] = (
            "Domain should not depend on infrastructure (Dependency Inversion Principle)",
            "Define an interface in Domain layer and implement it in Infrastructure"),

        [(ArchitectureLayer.Domain, ArchitectureLayer.Application)] = (
            "Domain should not depend on application layer",
            "Domain should be self-contained; move shared logic to Domain layer"),

        [(ArchitectureLayer.Domain, ArchitectureLayer.Api)] = (
            "Domain should not depend on API layer",
            "Domain must be isolated from transport concerns"),

        [(ArchitectureLayer.Presentation, ArchitectureLayer.Infrastructure)] = (
            "UI should not directly access data layer",
            "Use services or API endpoints to access data"),

        [(ArchitectureLayer.Presentation, ArchitectureLayer.Domain)] = (
            "UI should not directly use domain entities",
            "Use DTOs or ViewModels to transfer data"),

        [(ArchitectureLayer.Api, ArchitectureLayer.Infrastructure)] = (
            "Controllers should use services, not repositories directly",
            "Inject services into controllers; repositories into services"),

        [(ArchitectureLayer.Application, ArchitectureLayer.Api)] = (
            "Application layer should not depend on API layer",
            "Application services should be transport-agnostic"),

        [(ArchitectureLayer.Infrastructure, ArchitectureLayer.Application)] = (
            "Infrastructure should not depend on application layer",
            "Infrastructure implements domain interfaces, not application interfaces"),
    };

    /// <summary>
    /// Detects violations in a list of layer interactions.
    /// </summary>
    public List<LayerViolation> DetectViolations(IEnumerable<LayerInteractionInfo> interactions)
    {
        var violations = new List<LayerViolation>();

        foreach (var interaction in interactions)
        {
            if (this.IsViolation(interaction.SourceLayer, interaction.TargetLayer))
            {
                violations.Add(this.CreateViolation(interaction));
            }
        }

        return violations;
    }

    /// <summary>
    /// Checks if an interaction between two layers is a violation.
    /// </summary>
    public bool IsViolation(ArchitectureLayer sourceLayer, ArchitectureLayer targetLayer)
    {
        // Same layer is never a violation
        if (sourceLayer == targetLayer)
        {
            return false;
        }

        // Unknown layers can't be violations
        if (sourceLayer == ArchitectureLayer.Unknown || targetLayer == ArchitectureLayer.Unknown)
        {
            return false;
        }

        // Check if target is in allowed list
        if (AllowedInteractions.TryGetValue(sourceLayer, out var allowed))
        {
            return !allowed.Contains(targetLayer);
        }

        // If not defined, assume violation
        return true;
    }

    /// <summary>
    /// Gets the severity of a violation.
    /// </summary>
    public ViolationSeverity GetSeverity(ArchitectureLayer sourceLayer, ArchitectureLayer targetLayer)
    {
        if (ViolationSeverities.TryGetValue((sourceLayer, targetLayer), out var severity))
        {
            return severity;
        }

        return ViolationSeverity.Medium; // Default
    }

    /// <summary>
    /// Gets violation message and recommendation.
    /// </summary>
    public (string Message, string Recommendation) GetViolationDetails(
        ArchitectureLayer sourceLayer,
        ArchitectureLayer targetLayer)
    {
        if (ViolationMessages.TryGetValue((sourceLayer, targetLayer), out var details))
        {
            return details;
        }

        return (
            $"Unexpected dependency from {sourceLayer} to {targetLayer}",
            "Review architecture and consider introducing an abstraction layer");
    }

    /// <summary>
    /// Creates a violation record from an interaction.
    /// </summary>
    private LayerViolation CreateViolation(LayerInteractionInfo interaction)
    {
        var severity = this.GetSeverity(interaction.SourceLayer, interaction.TargetLayer);
        var (message, recommendation) = this.GetViolationDetails(interaction.SourceLayer, interaction.TargetLayer);

        return new LayerViolation
        {
            SourceLayer = interaction.SourceLayer,
            TargetLayer = interaction.TargetLayer,
            SourceComponent = interaction.SourceType,
            TargetComponent = interaction.TargetType,
            FilePath = interaction.FilePath,
            LineNumber = interaction.LineNumber,
            Severity = severity,
            Message = message,
            Recommendation = recommendation,
            InteractionType = interaction.InteractionType,
        };
    }

    /// <summary>
    /// Groups violations by severity.
    /// </summary>
    public Dictionary<ViolationSeverity, List<LayerViolation>> GroupBySeverity(IEnumerable<LayerViolation> violations)
    {
        return violations
            .GroupBy(v => v.Severity)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Gets summary of violations.
    /// </summary>
    public ViolationSummary GetSummary(IEnumerable<LayerViolation> violations)
    {
        var list = violations.ToList();
        var bySeverity = this.GroupBySeverity(list);

        return new ViolationSummary
        {
            TotalCount = list.Count,
            CriticalCount = bySeverity.GetValueOrDefault(ViolationSeverity.Critical)?.Count ?? 0,
            HighCount = bySeverity.GetValueOrDefault(ViolationSeverity.High)?.Count ?? 0,
            MediumCount = bySeverity.GetValueOrDefault(ViolationSeverity.Medium)?.Count ?? 0,
            LowCount = bySeverity.GetValueOrDefault(ViolationSeverity.Low)?.Count ?? 0,
            TopViolatingTypes = list
                .GroupBy(v => v.SourceComponent)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => (g.Key, g.Count()))
                .ToList(),
        };
    }
}

/// <summary>
/// Violation severity levels.
/// </summary>
public enum ViolationSeverity
{
    /// <summary>Critical violation - breaks architectural principles.</summary>
    Critical,

    /// <summary>High severity - significant architectural smell.</summary>
    High,

    /// <summary>Medium severity - unusual but may be acceptable.</summary>
    Medium,

    /// <summary>Low severity - minor concern.</summary>
    Low,
}

/// <summary>
/// Represents an architectural layer violation.
/// </summary>
public record LayerViolation
{
    /// <summary>Gets the source layer.</summary>
    public required ArchitectureLayer SourceLayer { get; init; }

    /// <summary>Gets the target layer.</summary>
    public required ArchitectureLayer TargetLayer { get; init; }

    /// <summary>Gets the source component name.</summary>
    public required string SourceComponent { get; init; }

    /// <summary>Gets the target component name.</summary>
    public required string TargetComponent { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the severity.</summary>
    public required ViolationSeverity Severity { get; init; }

    /// <summary>Gets the violation message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the recommendation.</summary>
    public required string Recommendation { get; init; }

    /// <summary>Gets the interaction type that caused the violation.</summary>
    public required InteractionType InteractionType { get; init; }
}

/// <summary>
/// Summary of violations.
/// </summary>
public record ViolationSummary
{
    /// <summary>Gets total violation count.</summary>
    public required int TotalCount { get; init; }

    /// <summary>Gets critical violation count.</summary>
    public required int CriticalCount { get; init; }

    /// <summary>Gets high violation count.</summary>
    public required int HighCount { get; init; }

    /// <summary>Gets medium violation count.</summary>
    public required int MediumCount { get; init; }

    /// <summary>Gets low violation count.</summary>
    public required int LowCount { get; init; }

    /// <summary>Gets top violating types.</summary>
    public required List<(string TypeName, int Count)> TopViolatingTypes { get; init; }
}
