// -----------------------------------------------------------------------
// <copyright file="DetectionRule.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Models;

/// <summary>
/// Represents a detection rule loaded from YAML configuration.
/// </summary>
public record DetectionRule
{
    /// <summary>
    /// Gets the unique identifier for this rule.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the human-readable name of the rule.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the category this rule belongs to (e.g., structural, lifecycle).
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Gets the specific relationship type this rule detects.
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Gets whether this rule is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets the priority of this rule (higher runs first).
    /// </summary>
    public int Priority { get; init; } = 50;

    /// <summary>
    /// Gets the detection patterns for this rule.
    /// </summary>
    public required IReadOnlyList<DetectionPattern> Detection { get; init; }

    /// <summary>
    /// Gets the validation criteria for confirming detections.
    /// </summary>
    public ValidationCriteria? Validation { get; init; }

    /// <summary>
    /// Gets the output specification for graph representation.
    /// </summary>
    public required OutputSpecification Output { get; init; }

    /// <summary>
    /// Gets the confidence calculation parameters.
    /// </summary>
    public required ConfidenceSpecification Confidence { get; init; }
}

/// <summary>
/// Represents a detection pattern within a rule.
/// </summary>
public record DetectionPattern
{
    /// <summary>
    /// Gets the strategy to use (ast, lsp, config, regex).
    /// </summary>
    public required string Strategy { get; init; }

    /// <summary>
    /// Gets the language for AST patterns (e.g., csharp).
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the patterns to match.
    /// </summary>
    public required IReadOnlyList<PatternDefinition> Patterns { get; init; }
}

/// <summary>
/// Represents a single pattern definition.
/// </summary>
public record PatternDefinition
{
    /// <summary>
    /// Gets the AST node type to match.
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// Gets the condition expression for matching.
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>
    /// Gets the extraction expressions for capturing data.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Extract { get; init; }

    /// <summary>
    /// Gets the file glob pattern for config/regex strategies.
    /// </summary>
    public string? FilePattern { get; init; }

    /// <summary>
    /// Gets the regex pattern for regex strategy.
    /// </summary>
    public string? Regex { get; init; }

    /// <summary>
    /// Gets the XPath for config strategy.
    /// </summary>
    public string? XPath { get; init; }

    /// <summary>
    /// Gets the JSON path for config strategy.
    /// </summary>
    public string? JsonPath { get; init; }
}

/// <summary>
/// Represents validation criteria for confirming detections.
/// </summary>
public record ValidationCriteria
{
    /// <summary>
    /// Gets the interface that must be implemented.
    /// </summary>
    public string? Interface { get; init; }

    /// <summary>
    /// Gets the base class that must be inherited.
    /// </summary>
    public string? BaseClass { get; init; }

    /// <summary>
    /// Gets the attributes that must be present.
    /// </summary>
    public IReadOnlyList<string>? Attributes { get; init; }

    /// <summary>
    /// Gets the methods that must be present.
    /// </summary>
    public IReadOnlyList<MethodSignature>? Methods { get; init; }
}

/// <summary>
/// Represents a method signature for validation.
/// </summary>
public record MethodSignature
{
    /// <summary>
    /// Gets the method name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the return type.
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Gets whether the method is required.
    /// </summary>
    public bool Required { get; init; } = true;
}

/// <summary>
/// Represents the output specification for graph representation.
/// </summary>
public record OutputSpecification
{
    /// <summary>
    /// Gets the node type for the source.
    /// </summary>
    public string? SourceNodeType { get; init; }

    /// <summary>
    /// Gets the node type for the target.
    /// </summary>
    public string? TargetNodeType { get; init; }

    /// <summary>
    /// Gets the node type (when no source/target distinction).
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// Gets the edge type for the relationship.
    /// </summary>
    public string? EdgeType { get; init; }

    /// <summary>
    /// Gets the properties to include in the output.
    /// </summary>
    public required IReadOnlyList<PropertySpecification> Properties { get; init; }
}

/// <summary>
/// Represents a property to extract and include in output.
/// </summary>
public record PropertySpecification
{
    /// <summary>
    /// Gets the property name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets a static value for the property.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets an expression to derive the value.
    /// </summary>
    public string? Derive { get; init; }
}

/// <summary>
/// Represents confidence calculation parameters.
/// </summary>
public record ConfidenceSpecification
{
    /// <summary>
    /// Gets the base confidence score.
    /// </summary>
    public float BaseScore { get; init; } = 0.5f;

    /// <summary>
    /// Gets the weights for different evidence types.
    /// </summary>
    public IReadOnlyDictionary<string, float>? EvidenceWeights { get; init; }

    /// <summary>
    /// Gets the minimum threshold for reporting.
    /// </summary>
    public float MinimumThreshold { get; init; } = 0.7f;
}
