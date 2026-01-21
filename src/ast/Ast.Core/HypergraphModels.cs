// -----------------------------------------------------------------------
// <copyright file="HypergraphModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core;

/// <summary>
/// Types of hyperedges extracted from code.
/// </summary>
public enum HyperedgeType
{
    // C# specific
    ClassDefinition,
    InterfaceDefinition,
    StructDefinition,
    RecordDefinition,
    MethodDefinition,
    ConstructorDefinition,
    PropertyDefinition,
    MethodInvocation,
    ConstructorInvocation,
    UsingDirective,
    AttributeDecoration,
    LambdaExpression,
    LinqQuery,
    AsyncAwait,
    DependencyInjection,
    EventHandler,
    GenericTypeConstraint,

    // PowerShell specific
    FunctionDefinition,
    CmdletCall,
    Pipeline,
    ModuleImport,
    DotSource,
    ParamBlock,
    DscConfiguration,
    DscResource,

    // Common
    Assignment,
    ControlFlow,
    ExceptionHandling,
    DataFlow,

    // Cross-language
    CrossLanguageTypeReference,
    CrossLanguageMethodCall
}

/// <summary>
/// Represents a node in the code hypergraph.
/// </summary>
public class HypergraphNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string NodeType { get; init; }
    public required string Language { get; init; }
    public required string FilePath { get; init; }
    public string Scope { get; init; } = string.Empty;
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public List<string> Modifiers { get; init; } = [];
    public Dictionary<string, object?> Metadata { get; init; } = [];

    public override int GetHashCode() => this.Id.GetHashCode();
    public override bool Equals(object? obj) => obj is HypergraphNode node && this.Id == node.Id;

    /// <summary>
    /// Generate text suitable for vector embedding.
    /// </summary>
    public string GetEmbeddingText()
    {
        var parts = new List<string>
        {
            $"{this.NodeType}: {this.Name}",
            $"Language: {this.Language}",
            $"File: {this.FilePath}:{this.LineStart}"
        };

        if (!string.IsNullOrEmpty(this.Scope))
        {
            parts.Add($"Scope: {this.Scope}");
        }

        if (this.Modifiers.Count > 0)
        {
            parts.Add($"Modifiers: {string.Join(" ", this.Modifiers)}");
        }

        return string.Join("\n", parts);
    }
}

/// <summary>
/// Represents a hyperedge connecting multiple nodes.
/// Unlike traditional edges (pairwise), hyperedges connect ANY number of nodes,
/// preserving the full context of code relationships.
/// </summary>
public class Hyperedge
{
    public required string Id { get; init; }
    public required HyperedgeType EdgeType { get; init; }
    public required List<string> NodeIds { get; init; }
    public required Dictionary<string, string> Roles { get; init; }
    public List<string> Languages { get; init; } = [];
    public double Weight { get; init; } = 1.0;
    public Dictionary<string, object?> Metadata { get; init; } = [];

    public int Cardinality => this.NodeIds.Count;
    public bool IsCrossLanguage => this.Languages.Distinct().Count() > 1;

    /// <summary>
    /// Generate text suitable for vector embedding.
    /// </summary>
    public string GetEmbeddingText()
    {
        var parts = new List<string>
        {
            $"Edge: {this.EdgeType}",
            $"Nodes: {string.Join(", ", this.NodeIds)}",
            $"Roles: {string.Join(", ", this.Roles.Select(r => $"{r.Value}={r.Key}"))}"
        };

        if (this.IsCrossLanguage)
        {
            parts.Add($"Cross-language: {string.Join(", ", this.Languages)}");
        }

        return string.Join("\n", parts);
    }
}

/// <summary>
/// Container for hypergraph data from a repository.
/// </summary>
public class HypergraphData
{
    public Dictionary<string, HypergraphNode> Nodes { get; init; } = [];
    public List<Hyperedge> Hyperedges { get; init; } = [];
    public HypergraphMetadata Metadata { get; init; } = new();
}

public class HypergraphMetadata
{
    public string? RepoPath { get; set; }
    public int NumFiles { get; set; }
    public int NumCSharpNodes { get; set; }
    public int NumPowerShellNodes { get; set; }
    public int NumCrossLanguageEdges { get; set; }
    public List<string> Files { get; set; } = [];
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
}