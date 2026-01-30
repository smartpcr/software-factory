// -----------------------------------------------------------------------
// <copyright file="LayerInteractionAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes cross-layer interactions and dependencies.
/// </summary>
public class LayerInteractionAnalyzer
{
    private readonly LayerClassificationAnalyzer classificationAnalyzer;
    private Dictionary<string, LayerClassificationAnalyzer.ArchitectureLayer>? typeLayerCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayerInteractionAnalyzer"/> class.
    /// </summary>
    public LayerInteractionAnalyzer()
    {
        this.classificationAnalyzer = new LayerClassificationAnalyzer();
    }

    /// <summary>
    /// Builds the type-to-layer mapping from all syntax trees.
    /// </summary>
    public void BuildTypeLayerCache(IEnumerable<(SyntaxTree Tree, string FilePath)> trees)
    {
        this.typeLayerCache = new Dictionary<string, LayerClassificationAnalyzer.ArchitectureLayer>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tree, filePath) in trees)
        {
            var classifications = this.classificationAnalyzer.ClassifyTypes(tree, filePath);
            foreach (var classification in classifications)
            {
                var key = classification.Namespace != null
                    ? $"{classification.Namespace}.{classification.TypeName}"
                    : classification.TypeName;

                if (!this.typeLayerCache.ContainsKey(key))
                {
                    this.typeLayerCache[key] = classification.Layer;
                }

                // Also add simple name for matching
                if (!this.typeLayerCache.ContainsKey(classification.TypeName))
                {
                    this.typeLayerCache[classification.TypeName] = classification.Layer;
                }
            }
        }
    }

    /// <summary>
    /// Analyzes cross-layer interactions in a syntax tree.
    /// </summary>
    public List<LayerInteractionInfo> AnalyzeInteractions(SyntaxTree tree, string filePath)
    {
        var results = new List<LayerInteractionInfo>();
        var root = tree.GetRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var sourceClassification = this.classificationAnalyzer.ClassifyType(typeDecl, filePath);
            if (sourceClassification == null || sourceClassification.Layer == LayerClassificationAnalyzer.ArchitectureLayer.Unknown)
            {
                continue;
            }

            // Analyze constructor injections
            results.AddRange(this.AnalyzeConstructorInjections(typeDecl, sourceClassification, filePath));

            // Analyze field/property types
            results.AddRange(this.AnalyzeFieldTypes(typeDecl, sourceClassification, filePath));

            // Analyze method calls
            results.AddRange(this.AnalyzeMethodCalls(typeDecl, sourceClassification, filePath));

            // Analyze inheritance
            results.AddRange(this.AnalyzeInheritance(typeDecl, sourceClassification, filePath));
        }

        return results;
    }

    /// <summary>
    /// Analyzes constructor parameter injections for cross-layer dependencies.
    /// </summary>
    private List<LayerInteractionInfo> AnalyzeConstructorInjections(
        TypeDeclarationSyntax typeDecl,
        LayerClassification sourceClassification,
        string filePath)
    {
        var results = new List<LayerInteractionInfo>();

        foreach (var constructor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var param in constructor.ParameterList.Parameters)
            {
                if (param.Type == null)
                {
                    continue;
                }

                var targetTypeName = this.GetTypeName(param.Type);
                var targetLayer = this.GetLayerForType(targetTypeName);

                if (targetLayer != LayerClassificationAnalyzer.ArchitectureLayer.Unknown &&
                    targetLayer != sourceClassification.Layer)
                {
                    results.Add(new LayerInteractionInfo
                    {
                        SourceType = sourceClassification.TypeName,
                        SourceLayer = sourceClassification.Layer,
                        TargetType = targetTypeName,
                        TargetLayer = targetLayer,
                        InteractionType = InteractionType.ConstructorInjection,
                        FilePath = filePath,
                        LineNumber = param.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes field and property types for cross-layer dependencies.
    /// </summary>
    private List<LayerInteractionInfo> AnalyzeFieldTypes(
        TypeDeclarationSyntax typeDecl,
        LayerClassification sourceClassification,
        string filePath)
    {
        var results = new List<LayerInteractionInfo>();

        // Check fields
        foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
        {
            var targetTypeName = this.GetTypeName(field.Declaration.Type);
            var targetLayer = this.GetLayerForType(targetTypeName);

            if (targetLayer != LayerClassificationAnalyzer.ArchitectureLayer.Unknown &&
                targetLayer != sourceClassification.Layer)
            {
                results.Add(new LayerInteractionInfo
                {
                    SourceType = sourceClassification.TypeName,
                    SourceLayer = sourceClassification.Layer,
                    TargetType = targetTypeName,
                    TargetLayer = targetLayer,
                    InteractionType = InteractionType.FieldDependency,
                    FilePath = filePath,
                    LineNumber = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                });
            }
        }

        // Check properties
        foreach (var property in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            var targetTypeName = this.GetTypeName(property.Type);
            var targetLayer = this.GetLayerForType(targetTypeName);

            if (targetLayer != LayerClassificationAnalyzer.ArchitectureLayer.Unknown &&
                targetLayer != sourceClassification.Layer)
            {
                results.Add(new LayerInteractionInfo
                {
                    SourceType = sourceClassification.TypeName,
                    SourceLayer = sourceClassification.Layer,
                    TargetType = targetTypeName,
                    TargetLayer = targetLayer,
                    InteractionType = InteractionType.PropertyDependency,
                    FilePath = filePath,
                    LineNumber = property.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes method invocations for cross-layer calls.
    /// </summary>
    private List<LayerInteractionInfo> AnalyzeMethodCalls(
        TypeDeclarationSyntax typeDecl,
        LayerClassification sourceClassification,
        string filePath)
    {
        var results = new List<LayerInteractionInfo>();

        foreach (var invocation in typeDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var targetTypeName = this.GetInvocationTargetType(invocation);
            if (string.IsNullOrEmpty(targetTypeName))
            {
                continue;
            }

            var targetLayer = this.GetLayerForType(targetTypeName);

            if (targetLayer != LayerClassificationAnalyzer.ArchitectureLayer.Unknown &&
                targetLayer != sourceClassification.Layer)
            {
                results.Add(new LayerInteractionInfo
                {
                    SourceType = sourceClassification.TypeName,
                    SourceLayer = sourceClassification.Layer,
                    TargetType = targetTypeName,
                    TargetLayer = targetLayer,
                    InteractionType = InteractionType.MethodCall,
                    FilePath = filePath,
                    LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes inheritance for cross-layer relationships.
    /// </summary>
    private List<LayerInteractionInfo> AnalyzeInheritance(
        TypeDeclarationSyntax typeDecl,
        LayerClassification sourceClassification,
        string filePath)
    {
        var results = new List<LayerInteractionInfo>();

        if (typeDecl.BaseList == null)
        {
            return results;
        }

        foreach (var baseType in typeDecl.BaseList.Types)
        {
            var targetTypeName = this.GetTypeName(baseType.Type);
            var targetLayer = this.GetLayerForType(targetTypeName);

            if (targetLayer != LayerClassificationAnalyzer.ArchitectureLayer.Unknown &&
                targetLayer != sourceClassification.Layer)
            {
                results.Add(new LayerInteractionInfo
                {
                    SourceType = sourceClassification.TypeName,
                    SourceLayer = sourceClassification.Layer,
                    TargetType = targetTypeName,
                    TargetLayer = targetLayer,
                    InteractionType = InteractionType.Inheritance,
                    FilePath = filePath,
                    LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Aggregates interactions by source-target layer pairs.
    /// </summary>
    public List<LayerInteractionSummary> AggregateInteractions(IEnumerable<LayerInteractionInfo> interactions)
    {
        return interactions
            .GroupBy(i => (i.SourceLayer, i.TargetLayer, i.InteractionType))
            .Select(g => new LayerInteractionSummary
            {
                SourceLayer = g.Key.SourceLayer,
                TargetLayer = g.Key.TargetLayer,
                InteractionType = g.Key.InteractionType,
                Count = g.Count(),
                Examples = g.Take(5).Select(i => $"{i.SourceType} -> {i.TargetType}").ToList(),
            })
            .OrderByDescending(s => s.Count)
            .ToList();
    }

    private LayerClassificationAnalyzer.ArchitectureLayer GetLayerForType(string typeName)
    {
        if (this.typeLayerCache != null && this.typeLayerCache.TryGetValue(typeName, out var layer))
        {
            return layer;
        }

        // Try to infer layer from type name conventions
        return this.InferLayerFromTypeName(typeName);
    }

    private LayerClassificationAnalyzer.ArchitectureLayer InferLayerFromTypeName(string typeName)
    {
        if (typeName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return LayerClassificationAnalyzer.ArchitectureLayer.Api;
        }

        if (typeName.EndsWith("Service", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Contains("Hosted", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Contains("Background", StringComparison.OrdinalIgnoreCase))
        {
            return LayerClassificationAnalyzer.ArchitectureLayer.Application;
        }

        if (typeName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("RepositoryBase", StringComparison.OrdinalIgnoreCase))
        {
            return LayerClassificationAnalyzer.ArchitectureLayer.Infrastructure;
        }

        if (typeName.EndsWith("Client", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Gateway", StringComparison.OrdinalIgnoreCase))
        {
            return LayerClassificationAnalyzer.ArchitectureLayer.Infrastructure;
        }

        // Domain base types
        if (typeName.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("EntityBase", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("AggregateRoot", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("ValueObject", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("DomainEvent", StringComparison.OrdinalIgnoreCase))
        {
            return LayerClassificationAnalyzer.ArchitectureLayer.Domain;
        }

        if (typeName.StartsWith("I") && typeName.Length > 1 && char.IsUpper(typeName[1]))
        {
            // Interface - try to infer from base name
            return this.InferLayerFromTypeName(typeName.Substring(1));
        }

        return LayerClassificationAnalyzer.ArchitectureLayer.Unknown;
    }

    private string GetTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => this.GetTypeName(qualified.Right),
            NullableTypeSyntax nullable => this.GetTypeName(nullable.ElementType),
            _ => type.ToString(),
        };
    }

    private string? GetInvocationTargetType(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is IdentifierNameSyntax id)
            {
                // Check if it's a field/property access like _repository.GetById()
                var fieldName = id.Identifier.Text;
                if (fieldName.StartsWith("_"))
                {
                    // Try to find the field type from parent class
                    var typeDecl = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    if (typeDecl != null)
                    {
                        var field = typeDecl.Members.OfType<FieldDeclarationSyntax>()
                            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));
                        if (field != null)
                        {
                            return this.GetTypeName(field.Declaration.Type);
                        }
                    }
                }

                return id.Identifier.Text;
            }
        }

        return null;
    }
}

/// <summary>
/// Types of cross-layer interactions.
/// </summary>
public enum InteractionType
{
    /// <summary>Constructor parameter injection.</summary>
    ConstructorInjection,

    /// <summary>Field type dependency.</summary>
    FieldDependency,

    /// <summary>Property type dependency.</summary>
    PropertyDependency,

    /// <summary>Method invocation.</summary>
    MethodCall,

    /// <summary>Class inheritance or interface implementation.</summary>
    Inheritance,
}

/// <summary>
/// Represents a cross-layer interaction.
/// </summary>
public record LayerInteractionInfo
{
    /// <summary>Gets the source type name.</summary>
    public required string SourceType { get; init; }

    /// <summary>Gets the source layer.</summary>
    public required LayerClassificationAnalyzer.ArchitectureLayer SourceLayer { get; init; }

    /// <summary>Gets the target type name.</summary>
    public required string TargetType { get; init; }

    /// <summary>Gets the target layer.</summary>
    public required LayerClassificationAnalyzer.ArchitectureLayer TargetLayer { get; init; }

    /// <summary>Gets the interaction type.</summary>
    public required InteractionType InteractionType { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Aggregated summary of layer interactions.
/// </summary>
public record LayerInteractionSummary
{
    /// <summary>Gets the source layer.</summary>
    public required LayerClassificationAnalyzer.ArchitectureLayer SourceLayer { get; init; }

    /// <summary>Gets the target layer.</summary>
    public required LayerClassificationAnalyzer.ArchitectureLayer TargetLayer { get; init; }

    /// <summary>Gets the interaction type.</summary>
    public required InteractionType InteractionType { get; init; }

    /// <summary>Gets the count of interactions.</summary>
    public required int Count { get; init; }

    /// <summary>Gets example interactions.</summary>
    public required List<string> Examples { get; init; }
}
