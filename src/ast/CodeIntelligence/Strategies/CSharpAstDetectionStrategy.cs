// -----------------------------------------------------------------------
// <copyright file="CSharpAstDetectionStrategy.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Strategies;

using System.Collections.Concurrent;
using CodeIntelligence.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Detection strategy using Roslyn for C# AST analysis.
/// </summary>
public sealed class CSharpAstDetectionStrategy : IDetectionStrategy
{
    private readonly ConcurrentDictionary<string, SyntaxTree> syntaxTreeCache = new();
    private bool disposed;

    /// <inheritdoc/>
    public string Name => "ast";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedLanguages => ["csharp", "cs", "c#"];

    /// <inheritdoc/>
    public bool CanHandle(DetectionPattern pattern)
    {
        return pattern.Strategy.Equals("ast", StringComparison.OrdinalIgnoreCase) &&
               (pattern.Language == null || this.SupportedLanguages.Contains(pattern.Language.ToLowerInvariant()));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DetectedRelationship>> DetectAsync(
        DetectionRule rule,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var relationships = new List<DetectedRelationship>();

        foreach (var filePath in filePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var syntaxTree = await this.GetOrParseSyntaxTreeAsync(filePath, cancellationToken);
            if (syntaxTree == null)
            {
                continue;
            }

            var root = await syntaxTree.GetRootAsync(cancellationToken);

            foreach (var pattern in rule.Detection.Where(p => this.CanHandle(p)))
            {
                var detected = this.ApplyPattern(rule, pattern, root, filePath);
                relationships.AddRange(detected);
            }
        }

        return relationships;
    }

    /// <summary>
    /// Applies a detection pattern to a syntax tree root.
    /// </summary>
    private IEnumerable<DetectedRelationship> ApplyPattern(
        DetectionRule rule,
        DetectionPattern detectionPattern,
        SyntaxNode root,
        string filePath)
    {
        foreach (var pattern in detectionPattern.Patterns)
        {
            var nodes = this.FindMatchingNodes(root, pattern);

            foreach (var node in nodes)
            {
                if (this.EvaluateCondition(node, pattern.Condition))
                {
                    var extracted = this.ExtractProperties(node, pattern.Extract);
                    var relationship = this.CreateRelationship(rule, node, filePath, extracted);
                    if (relationship != null)
                    {
                        yield return relationship;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Finds nodes matching the specified node type.
    /// </summary>
    private IEnumerable<SyntaxNode> FindMatchingNodes(SyntaxNode root, PatternDefinition pattern)
    {
        if (string.IsNullOrEmpty(pattern.NodeType))
        {
            yield break;
        }

        var nodeType = pattern.NodeType;

        foreach (var node in root.DescendantNodes())
        {
            var typeName = node.GetType().Name;

            // Match by syntax type name
            if (typeName.Equals(nodeType, StringComparison.OrdinalIgnoreCase) ||
                typeName.Equals(nodeType + "Syntax", StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// Evaluates a condition against a syntax node.
    /// </summary>
    private bool EvaluateCondition(SyntaxNode node, string? condition)
    {
        if (string.IsNullOrEmpty(condition))
        {
            return true;
        }

        // Simple condition evaluation for common patterns
        // In full implementation, this would use a proper expression evaluator
        return node switch
        {
            ClassDeclarationSyntax classDecl => this.EvaluateClassCondition(classDecl, condition),
            MethodDeclarationSyntax methodDecl => this.EvaluateMethodCondition(methodDecl, condition),
            PropertyDeclarationSyntax propDecl => this.EvaluatePropertyCondition(propDecl, condition),
            InvocationExpressionSyntax invocation => this.EvaluateInvocationCondition(invocation, condition),
            _ => true
        };
    }

    private bool EvaluateClassCondition(ClassDeclarationSyntax classDecl, string condition)
    {
        // Check for inheritance conditions
        if (condition.Contains("inheritsFrom"))
        {
            var baseTypes = classDecl.BaseList?.Types
                .Select(t => t.Type.ToString())
                .ToList() ?? [];

            // Extract the base class name from condition like "inheritsFrom(\"Controller\")"
            var match = System.Text.RegularExpressions.Regex.Match(
                condition,
                @"inheritsFrom\([""']([^""']+)[""']\)");

            if (match.Success)
            {
                var targetBase = match.Groups[1].Value;
                return baseTypes.Any(b => b.Contains(targetBase, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Check for attribute conditions
        if (condition.Contains("hasAttribute"))
        {
            var attributes = classDecl.AttributeLists
                .SelectMany(al => al.Attributes)
                .Select(a => a.Name.ToString())
                .ToList();

            var match = System.Text.RegularExpressions.Regex.Match(
                condition,
                @"hasAttribute\([""']([^""']+)[""']\)");

            if (match.Success)
            {
                var targetAttr = match.Groups[1].Value;
                return attributes.Any(a =>
                    a.Equals(targetAttr, StringComparison.OrdinalIgnoreCase) ||
                    a.Equals(targetAttr + "Attribute", StringComparison.OrdinalIgnoreCase));
            }
        }

        // Check for implements conditions
        if (condition.Contains("implements"))
        {
            var interfaces = classDecl.BaseList?.Types
                .Select(t => t.Type.ToString())
                .Where(t => t.StartsWith("I", StringComparison.Ordinal))
                .ToList() ?? [];

            var match = System.Text.RegularExpressions.Regex.Match(
                condition,
                @"implements\([""']([^""']+)[""']\)");

            if (match.Success)
            {
                var targetInterface = match.Groups[1].Value;
                return interfaces.Any(i => i.Contains(targetInterface, StringComparison.OrdinalIgnoreCase));
            }
        }

        return true;
    }

    private bool EvaluateMethodCondition(MethodDeclarationSyntax methodDecl, string condition)
    {
        // Check for static modifier
        if (condition.Contains("static"))
        {
            var isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            if (condition.Contains("!static") || condition.Contains("not static"))
            {
                return !isStatic;
            }

            return isStatic;
        }

        // Check for method name
        if (condition.Contains("Identifier.Text"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                condition,
                @"Identifier\.Text\s*==\s*[""']([^""']+)[""']");

            if (match.Success)
            {
                return methodDecl.Identifier.Text.Equals(
                    match.Groups[1].Value,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    private bool EvaluatePropertyCondition(PropertyDeclarationSyntax propDecl, string condition)
    {
        // Check for type conditions
        if (condition.Contains("Type.ToString()"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                condition,
                @"Type\.ToString\(\)\.StartsWith\([""']([^""']+)[""']\)");

            if (match.Success)
            {
                return propDecl.Type.ToString().StartsWith(
                    match.Groups[1].Value,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    private bool EvaluateInvocationCondition(InvocationExpressionSyntax invocation, string condition)
    {
        var expression = invocation.Expression.ToString();

        // Check for method name patterns
        if (condition.Contains("matches"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                condition,
                @"matches\s+[""']([^""']+)[""']");

            if (match.Success)
            {
                var pattern = match.Groups[1].Value;
                return System.Text.RegularExpressions.Regex.IsMatch(expression, pattern);
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts properties from a syntax node based on extraction expressions.
    /// </summary>
    private Dictionary<string, object> ExtractProperties(
        SyntaxNode node,
        IReadOnlyDictionary<string, string>? extractExpressions)
    {
        var properties = new Dictionary<string, object>();

        if (extractExpressions == null)
        {
            return properties;
        }

        foreach (var (key, expression) in extractExpressions)
        {
            var value = this.EvaluateExtraction(node, expression);
            if (value != null)
            {
                properties[key] = value;
            }
        }

        return properties;
    }

    private object? EvaluateExtraction(SyntaxNode node, string expression)
    {
        // Simple extraction for common patterns
        return node switch
        {
            ClassDeclarationSyntax classDecl => this.ExtractFromClass(classDecl, expression),
            MethodDeclarationSyntax methodDecl => this.ExtractFromMethod(methodDecl, expression),
            _ => null
        };
    }

    private object? ExtractFromClass(ClassDeclarationSyntax classDecl, string expression)
    {
        if (expression.Contains("Identifier.Text"))
        {
            return classDecl.Identifier.Text;
        }

        if (expression.Contains("BaseList"))
        {
            return classDecl.BaseList?.Types.FirstOrDefault()?.Type.ToString();
        }

        return null;
    }

    private object? ExtractFromMethod(MethodDeclarationSyntax methodDecl, string expression)
    {
        if (expression.Contains("Identifier.Text"))
        {
            return methodDecl.Identifier.Text;
        }

        if (expression.Contains("ReturnType"))
        {
            return methodDecl.ReturnType.ToString();
        }

        return null;
    }

    /// <summary>
    /// Creates a relationship from a matched node.
    /// </summary>
    private DetectedRelationship? CreateRelationship(
        DetectionRule rule,
        SyntaxNode node,
        string filePath,
        Dictionary<string, object> extractedProperties)
    {
        var location = node.GetLocation().GetLineSpan();
        var (name, fullyQualifiedName) = this.GetNodeIdentifier(node);

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var sourceNode = new RelationshipNode
        {
            NodeType = rule.Output.SourceNodeType ?? rule.Output.NodeType ?? "Unknown",
            Name = name,
            FullyQualifiedName = fullyQualifiedName,
            FilePath = filePath,
            LineNumber = location.StartLinePosition.Line + 1,
            ColumnNumber = location.StartLinePosition.Character + 1
        };

        var evidence = new Evidence
        {
            Source = "ast",
            Description = $"Matched {node.GetType().Name} pattern",
            Weight = rule.Confidence.BaseScore,
            FilePath = filePath,
            LineNumber = location.StartLinePosition.Line + 1,
            MatchedText = node.ToString().Length > 100
                ? node.ToString()[..100] + "..."
                : node.ToString()
        };

        // Merge output properties with extracted properties
        var properties = new Dictionary<string, object>(extractedProperties);
        foreach (var prop in rule.Output.Properties)
        {
            if (prop.Value != null)
            {
                properties[prop.Name] = prop.Value;
            }
        }

        return new DetectedRelationship
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = rule.Category,
            RelationshipType = rule.RelationshipType,
            Source = sourceNode,
            Target = null,
            Confidence = rule.Confidence.BaseScore,
            EvidenceChain = [evidence],
            DetectionStrategy = this.Name,
            Properties = properties
        };
    }

    private (string name, string fullyQualifiedName) GetNodeIdentifier(SyntaxNode node)
    {
        var name = node switch
        {
            ClassDeclarationSyntax c => c.Identifier.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            InterfaceDeclarationSyntax i => i.Identifier.Text,
            StructDeclarationSyntax s => s.Identifier.Text,
            RecordDeclarationSyntax r => r.Identifier.Text,
            _ => string.Empty
        };

        // Build fully qualified name from ancestors
        var namespaceNode = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var namespaceName = namespaceNode?.Name.ToString() ?? string.Empty;

        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var typeName = containingType?.Identifier.Text ?? string.Empty;

        var fqn = string.Join(".", new[] { namespaceName, typeName, name }.Where(s => !string.IsNullOrEmpty(s)));

        return (name, fqn);
    }

    private async Task<SyntaxTree?> GetOrParseSyntaxTreeAsync(string filePath, CancellationToken cancellationToken)
    {
        if (this.syntaxTreeCache.TryGetValue(filePath, out var cachedTree))
        {
            return cachedTree;
        }

        try
        {
            var sourceText = await File.ReadAllTextAsync(filePath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath);
            this.syntaxTreeCache.TryAdd(filePath, tree);
            return tree;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            this.syntaxTreeCache.Clear();
            this.disposed = true;
        }
    }
}
