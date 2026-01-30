// -----------------------------------------------------------------------
// <copyright file="DetectedRelationship.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Models;

/// <summary>
/// Represents a detected relationship between code elements.
/// </summary>
public record DetectedRelationship
{
    /// <summary>
    /// Gets the unique identifier for this relationship detection.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the category of the relationship (e.g., structural, lifecycle, api).
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Gets the specific type of relationship (e.g., Inheritance, HttpEndpoint).
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Gets the source node of the relationship.
    /// </summary>
    public required RelationshipNode Source { get; init; }

    /// <summary>
    /// Gets the target node of the relationship (null for leaf nodes).
    /// </summary>
    public RelationshipNode? Target { get; init; }

    /// <summary>
    /// Gets the confidence score for this detection (0.0 to 1.0).
    /// </summary>
    public required float Confidence { get; init; }

    /// <summary>
    /// Gets the evidence chain supporting this detection.
    /// </summary>
    public required IReadOnlyList<Evidence> EvidenceChain { get; init; }

    /// <summary>
    /// Gets the detection strategy that found this relationship.
    /// </summary>
    public required string DetectionStrategy { get; init; }

    /// <summary>
    /// Gets additional properties specific to this relationship type.
    /// </summary>
    public required IReadOnlyDictionary<string, object> Properties { get; init; }
}

/// <summary>
/// Represents a node in a relationship (source or target).
/// </summary>
public record RelationshipNode
{
    /// <summary>
    /// Gets the type of node (e.g., Class, Method, Interface).
    /// </summary>
    public required string NodeType { get; init; }

    /// <summary>
    /// Gets the fully qualified name of the node.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Gets the simple name of the node.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the file path where this node is defined.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Gets the line number where this node starts.
    /// </summary>
    public required int LineNumber { get; init; }

    /// <summary>
    /// Gets the column number where this node starts.
    /// </summary>
    public int ColumnNumber { get; init; }
}

/// <summary>
/// Represents a piece of evidence supporting a detection.
/// </summary>
public record Evidence
{
    /// <summary>
    /// Gets the source of this evidence (e.g., "ast", "lsp", "config", "regex").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Gets a human-readable description of what was found.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the weight of this evidence in confidence calculation.
    /// </summary>
    public required float Weight { get; init; }

    /// <summary>
    /// Gets the file path where this evidence was found.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the line number where this evidence was found.
    /// </summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// Gets the raw matched text or pattern.
    /// </summary>
    public string? MatchedText { get; init; }
}
