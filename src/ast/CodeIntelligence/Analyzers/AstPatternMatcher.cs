// -----------------------------------------------------------------------
// <copyright file="AstPatternMatcher.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using CodeIntelligence.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Advanced pattern matcher for AST nodes using declarative rule definitions.
/// </summary>
public sealed class AstPatternMatcher
{
    private readonly Dictionary<string, Func<SyntaxNode, PatternDefinition, MatchResult>> matchers;

    /// <summary>
    /// Initializes a new instance of the <see cref="AstPatternMatcher"/> class.
    /// </summary>
    public AstPatternMatcher()
    {
        this.matchers = new Dictionary<string, Func<SyntaxNode, PatternDefinition, MatchResult>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["ClassDeclaration"] = this.MatchClassDeclaration,
            ["InterfaceDeclaration"] = this.MatchInterfaceDeclaration,
            ["MethodDeclaration"] = this.MatchMethodDeclaration,
            ["PropertyDeclaration"] = this.MatchPropertyDeclaration,
            ["ConstructorDeclaration"] = this.MatchConstructorDeclaration,
            ["InvocationExpression"] = this.MatchInvocationExpression,
            ["ObjectCreationExpression"] = this.MatchObjectCreationExpression,
            ["AttributeDeclaration"] = this.MatchAttributeDeclaration,
            ["FieldDeclaration"] = this.MatchFieldDeclaration,
            ["UsingDirective"] = this.MatchUsingDirective,
            ["NamespaceDeclaration"] = this.MatchNamespaceDeclaration,
            ["RecordDeclaration"] = this.MatchRecordDeclaration,
            ["StructDeclaration"] = this.MatchStructDeclaration,
            ["EnumDeclaration"] = this.MatchEnumDeclaration
        };
    }

    /// <summary>
    /// Matches all patterns in a syntax tree.
    /// </summary>
    /// <param name="root">The syntax tree root.</param>
    /// <param name="pattern">The pattern to match.</param>
    /// <returns>All matching results.</returns>
    public IEnumerable<MatchResult> Match(SyntaxNode root, PatternDefinition pattern)
    {
        if (string.IsNullOrEmpty(pattern.NodeType))
        {
            yield break;
        }

        var nodeType = pattern.NodeType.Replace("Syntax", string.Empty);

        foreach (var node in root.DescendantNodes())
        {
            var typeName = node.GetType().Name.Replace("Syntax", string.Empty);

            if (!typeName.Equals(nodeType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (this.matchers.TryGetValue(nodeType, out var matcher))
            {
                var result = matcher(node, pattern);
                if (result.IsMatch)
                {
                    yield return result;
                }
            }
            else
            {
                // Generic matching for unsupported node types
                var result = this.MatchGeneric(node, pattern);
                if (result.IsMatch)
                {
                    yield return result;
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a condition expression against a syntax node.
    /// </summary>
    /// <param name="node">The node to evaluate.</param>
    /// <param name="condition">The condition expression.</param>
    /// <returns>True if the condition is satisfied.</returns>
    public bool EvaluateCondition(SyntaxNode node, string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var context = new ConditionContext(node);
        return this.EvaluateConditionExpression(context, condition);
    }

    private bool EvaluateConditionExpression(ConditionContext context, string condition)
    {
        // Handle logical operators
        if (condition.Contains("&&"))
        {
            var parts = condition.Split(new[] { "&&" }, 2, StringSplitOptions.TrimEntries);
            return this.EvaluateConditionExpression(context, parts[0]) &&
                   this.EvaluateConditionExpression(context, parts[1]);
        }

        if (condition.Contains("||"))
        {
            var parts = condition.Split(new[] { "||" }, 2, StringSplitOptions.TrimEntries);
            return this.EvaluateConditionExpression(context, parts[0]) ||
                   this.EvaluateConditionExpression(context, parts[1]);
        }

        // Handle negation
        if (condition.StartsWith("!") || condition.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
        {
            var inner = condition.StartsWith("!") ? condition[1..] : condition[4..];
            return !this.EvaluateConditionExpression(context, inner.Trim());
        }

        // Handle function calls
        return this.EvaluateFunctionCall(context, condition);
    }

    private bool EvaluateFunctionCall(ConditionContext context, string expression)
    {
        // inheritsFrom("BaseClass")
        var inheritsMatch = Regex.Match(expression, @"inheritsFrom\s*\(\s*[""']([^""']+)[""']\s*\)");
        if (inheritsMatch.Success)
        {
            return context.InheritsFrom(inheritsMatch.Groups[1].Value);
        }

        // implements("IInterface")
        var implementsMatch = Regex.Match(expression, @"implements\s*\(\s*[""']([^""']+)[""']\s*\)");
        if (implementsMatch.Success)
        {
            return context.Implements(implementsMatch.Groups[1].Value);
        }

        // hasAttribute("AttributeName")
        var attrMatch = Regex.Match(expression, @"hasAttribute\s*\(\s*[""']([^""']+)[""']\s*\)");
        if (attrMatch.Success)
        {
            return context.HasAttribute(attrMatch.Groups[1].Value);
        }

        // hasMethod("MethodName")
        var methodMatch = Regex.Match(expression, @"hasMethod\s*\(\s*[""']([^""']+)[""']\s*\)");
        if (methodMatch.Success)
        {
            return context.HasMethod(methodMatch.Groups[1].Value);
        }

        // hasProperty("PropertyName")
        var propMatch = Regex.Match(expression, @"hasProperty\s*\(\s*[""']([^""']+)[""']\s*\)");
        if (propMatch.Success)
        {
            return context.HasProperty(propMatch.Groups[1].Value);
        }

        // className matches "pattern"
        var nameMatchesMatch = Regex.Match(expression, @"className\s+matches\s+[""']([^""']+)[""']");
        if (nameMatchesMatch.Success)
        {
            return context.NameMatches(nameMatchesMatch.Groups[1].Value);
        }

        // isStatic
        if (expression.Trim().Equals("isStatic", StringComparison.OrdinalIgnoreCase))
        {
            return context.IsStatic;
        }

        // isAbstract
        if (expression.Trim().Equals("isAbstract", StringComparison.OrdinalIgnoreCase))
        {
            return context.IsAbstract;
        }

        // isPublic
        if (expression.Trim().Equals("isPublic", StringComparison.OrdinalIgnoreCase))
        {
            return context.IsPublic;
        }

        // isAsync
        if (expression.Trim().Equals("isAsync", StringComparison.OrdinalIgnoreCase))
        {
            return context.IsAsync;
        }

        // expression matches "pattern" (generic regex matching on string representation)
        var exprMatchesMatch = Regex.Match(expression, @"expression\s+matches\s+[""']([^""']+)[""']");
        if (exprMatchesMatch.Success)
        {
            var nodeText = context.Node.ToString();
            return Regex.IsMatch(nodeText, exprMatchesMatch.Groups[1].Value, RegexOptions.IgnoreCase);
        }

        // Default: try regex on node text
        try
        {
            return Regex.IsMatch(context.Node.ToString(), expression, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts properties from a syntax node based on extraction expressions.
    /// </summary>
    /// <param name="node">The syntax node.</param>
    /// <param name="extractExpressions">The extraction expressions.</param>
    /// <returns>The extracted properties.</returns>
    public Dictionary<string, object> ExtractProperties(
        SyntaxNode node,
        IReadOnlyDictionary<string, string>? extractExpressions)
    {
        var properties = new Dictionary<string, object>();

        if (extractExpressions == null)
        {
            return properties;
        }

        var context = new ExtractionContext(node);

        foreach (var (key, expression) in extractExpressions)
        {
            var value = this.EvaluateExtraction(context, expression);
            if (value != null)
            {
                properties[key] = value;
            }
        }

        return properties;
    }

    private object? EvaluateExtraction(ExtractionContext context, string expression)
    {
        // node.Identifier.Text
        if (expression.Contains("Identifier.Text"))
        {
            return context.GetIdentifier();
        }

        // node.BaseList.Types
        if (expression.Contains("BaseList"))
        {
            return context.GetBaseTypes();
        }

        // node.ReturnType
        if (expression.Contains("ReturnType"))
        {
            return context.GetReturnType();
        }

        // node.Modifiers
        if (expression.Contains("Modifiers"))
        {
            return context.GetModifiers();
        }

        // node.ParameterList.Parameters
        if (expression.Contains("ParameterList") || expression.Contains("Parameters"))
        {
            return context.GetParameters();
        }

        // node.AttributeLists
        if (expression.Contains("AttributeLists") || expression.Contains("Attributes"))
        {
            return context.GetAttributes();
        }

        // node.Type or Type.ToString()
        if (expression.Contains("Type"))
        {
            return context.GetType();
        }

        // Generic property access via reflection-like syntax
        if (expression.StartsWith("node."))
        {
            return this.ExtractByPath(context.Node, expression[5..]);
        }

        return null;
    }

    private object? ExtractByPath(SyntaxNode node, string path)
    {
        var parts = path.Split('.');
        object? current = node;

        foreach (var part in parts)
        {
            if (current == null)
            {
                return null;
            }

            var propName = part.Replace("()", string.Empty);
            var prop = current.GetType().GetProperty(propName);

            if (prop != null)
            {
                current = prop.GetValue(current);
            }
            else
            {
                var method = current.GetType().GetMethod(propName, Type.EmptyTypes);
                if (method != null)
                {
                    current = method.Invoke(current, null);
                }
                else
                {
                    return null;
                }
            }
        }

        return current?.ToString();
    }

    #region Specific Matchers

    private MatchResult MatchClassDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not ClassDeclarationSyntax classDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["className"] = classDecl.Identifier.Text;
        properties["namespace"] = this.GetNamespace(classDecl);

        if (classDecl.BaseList?.Types.Count > 0)
        {
            properties["baseTypes"] = classDecl.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        }

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchInterfaceDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not InterfaceDeclarationSyntax interfaceDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["interfaceName"] = interfaceDecl.Identifier.Text;
        properties["namespace"] = this.GetNamespace(interfaceDecl);

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchMethodDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not MethodDeclarationSyntax methodDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["methodName"] = methodDecl.Identifier.Text;
        properties["returnType"] = methodDecl.ReturnType.ToString();
        properties["parameters"] = methodDecl.ParameterList.Parameters
            .Select(p => new { type = p.Type?.ToString(), name = p.Identifier.Text })
            .ToList();

        var containingType = methodDecl.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType != null)
        {
            properties["containingType"] = containingType.Identifier.Text;
        }

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchPropertyDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not PropertyDeclarationSyntax propDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["propertyName"] = propDecl.Identifier.Text;
        properties["propertyType"] = propDecl.Type.ToString();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchConstructorDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not ConstructorDeclarationSyntax ctorDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["className"] = ctorDecl.Identifier.Text;
        properties["parameters"] = ctorDecl.ParameterList.Parameters
            .Select(p => new { type = p.Type?.ToString(), name = p.Identifier.Text })
            .ToList();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchInvocationExpression(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["expression"] = invocation.Expression.ToString();
        properties["arguments"] = invocation.ArgumentList.Arguments
            .Select(a => a.ToString())
            .ToList();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchObjectCreationExpression(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not ObjectCreationExpressionSyntax creation)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["type"] = creation.Type.ToString();
        properties["arguments"] = creation.ArgumentList?.Arguments
            .Select(a => a.ToString())
            .ToList() ?? new List<string>();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchAttributeDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not AttributeSyntax attr)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["attributeName"] = attr.Name.ToString();
        properties["arguments"] = attr.ArgumentList?.Arguments
            .Select(a => a.ToString())
            .ToList() ?? new List<string>();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchFieldDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not FieldDeclarationSyntax field)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["fieldType"] = field.Declaration.Type.ToString();
        properties["fieldNames"] = field.Declaration.Variables
            .Select(v => v.Identifier.Text)
            .ToList();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchUsingDirective(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not UsingDirectiveSyntax usingDirective)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["namespace"] = usingDirective.Name?.ToString() ?? string.Empty;
        properties["isStatic"] = usingDirective.StaticKeyword != default;
        properties["alias"] = usingDirective.Alias?.Name.ToString() ?? string.Empty;

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchNamespaceDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not BaseNamespaceDeclarationSyntax nsDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["namespace"] = nsDecl.Name.ToString();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchRecordDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not RecordDeclarationSyntax record)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["recordName"] = record.Identifier.Text;
        properties["namespace"] = this.GetNamespace(record);
        properties["parameters"] = (object)(record.ParameterList?.Parameters
            .Select(p => new { type = p.Type?.ToString(), name = p.Identifier.Text })
            .ToList()) ?? new List<object>();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchStructDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not StructDeclarationSyntax structDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["structName"] = structDecl.Identifier.Text;
        properties["namespace"] = this.GetNamespace(structDecl);

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchEnumDeclaration(SyntaxNode node, PatternDefinition pattern)
    {
        if (node is not EnumDeclarationSyntax enumDecl)
        {
            return MatchResult.NoMatch;
        }

        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["enumName"] = enumDecl.Identifier.Text;
        properties["members"] = enumDecl.Members.Select(m => m.Identifier.Text).ToList();

        return new MatchResult(true, node, properties);
    }

    private MatchResult MatchGeneric(SyntaxNode node, PatternDefinition pattern)
    {
        if (!this.EvaluateCondition(node, pattern.Condition))
        {
            return MatchResult.NoMatch;
        }

        var properties = this.ExtractProperties(node, pattern.Extract);
        properties["nodeType"] = node.GetType().Name;
        properties["text"] = node.ToString().Length > 200
            ? node.ToString()[..200] + "..."
            : node.ToString();

        return new MatchResult(true, node, properties);
    }

    private string GetNamespace(SyntaxNode node)
    {
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return ns?.Name.ToString() ?? string.Empty;
    }

    #endregion

    #region Helper Classes

    private sealed class ConditionContext
    {
        public SyntaxNode Node { get; }

        public ConditionContext(SyntaxNode node)
        {
            this.Node = node;
        }

        public bool InheritsFrom(string baseTypeName)
        {
            if (this.Node is TypeDeclarationSyntax typeDecl && typeDecl.BaseList != null)
            {
                return typeDecl.BaseList.Types.Any(t =>
                {
                    var typeName = t.Type.ToString();
                    return typeName.Equals(baseTypeName, StringComparison.OrdinalIgnoreCase) ||
                           typeName.Contains(baseTypeName, StringComparison.OrdinalIgnoreCase) ||
                           Regex.IsMatch(typeName, baseTypeName.Replace("*", ".*"), RegexOptions.IgnoreCase);
                });
            }

            return false;
        }

        public bool Implements(string interfaceName)
        {
            if (this.Node is TypeDeclarationSyntax typeDecl && typeDecl.BaseList != null)
            {
                return typeDecl.BaseList.Types.Any(t =>
                {
                    var typeName = t.Type.ToString();
                    return typeName.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) ||
                           typeName.StartsWith(interfaceName, StringComparison.OrdinalIgnoreCase) ||
                           Regex.IsMatch(typeName, interfaceName.Replace("*", ".*"), RegexOptions.IgnoreCase);
                });
            }

            return false;
        }

        public bool HasAttribute(string attributeName)
        {
            var attrLists = this.Node switch
            {
                MemberDeclarationSyntax member => member.AttributeLists,
                ParameterSyntax param => param.AttributeLists,
                TypeParameterSyntax typeParam => typeParam.AttributeLists,
                _ => default
            };

            if (attrLists.Count == 0)
            {
                return false;
            }

            return attrLists.SelectMany(al => al.Attributes).Any(a =>
            {
                var name = a.Name.ToString();
                return name.Equals(attributeName, StringComparison.OrdinalIgnoreCase) ||
                       name.Equals(attributeName + "Attribute", StringComparison.OrdinalIgnoreCase);
            });
        }

        public bool HasMethod(string methodName)
        {
            if (this.Node is TypeDeclarationSyntax typeDecl)
            {
                return typeDecl.Members.OfType<MethodDeclarationSyntax>()
                    .Any(m => m.Identifier.Text.Equals(methodName, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        public bool HasProperty(string propertyName)
        {
            if (this.Node is TypeDeclarationSyntax typeDecl)
            {
                return typeDecl.Members.OfType<PropertyDeclarationSyntax>()
                    .Any(p => p.Identifier.Text.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        public bool NameMatches(string pattern)
        {
            var identifier = this.Node switch
            {
                TypeDeclarationSyntax t => t.Identifier.Text,
                MethodDeclarationSyntax m => m.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                _ => string.Empty
            };

            return Regex.IsMatch(identifier, pattern.Replace("*", ".*"), RegexOptions.IgnoreCase);
        }

        public bool IsStatic => this.Node switch
        {
            MemberDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.StaticKeyword),
            _ => false
        };

        public bool IsAbstract => this.Node switch
        {
            MemberDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AbstractKeyword),
            _ => false
        };

        public bool IsPublic => this.Node switch
        {
            MemberDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.PublicKeyword),
            _ => false
        };

        public bool IsAsync => this.Node switch
        {
            MethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword),
            _ => false
        };
    }

    private sealed class ExtractionContext
    {
        public SyntaxNode Node { get; }

        public ExtractionContext(SyntaxNode node)
        {
            this.Node = node;
        }

        public string? GetIdentifier()
        {
            return this.Node switch
            {
                TypeDeclarationSyntax t => t.Identifier.Text,
                MethodDeclarationSyntax m => m.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                ConstructorDeclarationSyntax c => c.Identifier.Text,
                FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
                _ => null
            };
        }

        public IEnumerable<string>? GetBaseTypes()
        {
            if (this.Node is TypeDeclarationSyntax typeDecl)
            {
                return typeDecl.BaseList?.Types.Select(t => t.Type.ToString());
            }

            return null;
        }

        public string? GetReturnType()
        {
            return this.Node switch
            {
                MethodDeclarationSyntax m => m.ReturnType.ToString(),
                PropertyDeclarationSyntax p => p.Type.ToString(),
                _ => null
            };
        }

        public IEnumerable<string>? GetModifiers()
        {
            if (this.Node is MemberDeclarationSyntax member)
            {
                return member.Modifiers.Select(m => m.Text);
            }

            return null;
        }

        public IEnumerable<object>? GetParameters()
        {
            var paramList = this.Node switch
            {
                MethodDeclarationSyntax m => m.ParameterList,
                ConstructorDeclarationSyntax c => c.ParameterList,
                _ => null
            };

            return paramList?.Parameters.Select(p => new
            {
                type = p.Type?.ToString(),
                name = p.Identifier.Text
            });
        }

        public IEnumerable<string>? GetAttributes()
        {
            var attrLists = this.Node switch
            {
                MemberDeclarationSyntax member => member.AttributeLists,
                _ => default
            };

            if (attrLists.Count == 0)
            {
                return null;
            }

            return attrLists.SelectMany(al => al.Attributes).Select(a => a.Name.ToString());
        }

        public new string? GetType()
        {
            return this.Node switch
            {
                PropertyDeclarationSyntax p => p.Type.ToString(),
                FieldDeclarationSyntax f => f.Declaration.Type.ToString(),
                ParameterSyntax param => param.Type?.ToString(),
                _ => null
            };
        }
    }

    #endregion
}

/// <summary>
/// Result of a pattern match operation.
/// </summary>
public sealed class MatchResult
{
    /// <summary>
    /// Gets a result indicating no match.
    /// </summary>
    public static MatchResult NoMatch { get; } = new(false, null, new Dictionary<string, object>());

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchResult"/> class.
    /// </summary>
    /// <param name="isMatch">Whether the pattern matched.</param>
    /// <param name="node">The matched node.</param>
    /// <param name="properties">The extracted properties.</param>
    public MatchResult(bool isMatch, SyntaxNode? node, Dictionary<string, object> properties)
    {
        this.IsMatch = isMatch;
        this.Node = node;
        this.Properties = properties;
    }

    /// <summary>
    /// Gets whether the pattern matched.
    /// </summary>
    public bool IsMatch { get; }

    /// <summary>
    /// Gets the matched syntax node.
    /// </summary>
    public SyntaxNode? Node { get; }

    /// <summary>
    /// Gets the extracted properties.
    /// </summary>
    public Dictionary<string, object> Properties { get; }
}
