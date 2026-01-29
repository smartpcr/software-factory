// -----------------------------------------------------------------------
// <copyright file="DependencyInjectionAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes dependency injection patterns in C# code.
/// </summary>
public sealed class DependencyInjectionAnalyzer
{
    private static readonly HashSet<string> RegistrationMethods = new(StringComparer.Ordinal)
    {
        // Microsoft.Extensions.DependencyInjection
        "AddScoped",
        "AddTransient",
        "AddSingleton",
        "AddHostedService",
        "AddKeyedScoped",
        "AddKeyedTransient",
        "AddKeyedSingleton",

        // Unity Container
        "RegisterType",
        "RegisterInstance",
        "RegisterFactory",
        "RegisterSingleton"
    };

    private static readonly HashSet<string> InjectionAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common injection attributes
        "Inject",
        "Autowired",
        "FromServices",
        "FromKeyedServices",

        // Unity Container
        "Dependency",
        "OptionalDependency"
    };

    /// <summary>
    /// Analyzes a syntax tree for dependency injection patterns.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected DI relationships.</returns>
    public IReadOnlyList<DependencyInjectionRelationship> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var relationships = new List<DependencyInjectionRelationship>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ConstructorDeclarationSyntax ctorDecl:
                    relationships.AddRange(this.AnalyzeConstructorInjection(ctorDecl, filePath));
                    break;

                case PropertyDeclarationSyntax propDecl:
                    var propInjection = this.AnalyzePropertyInjection(propDecl, filePath);
                    if (propInjection != null)
                    {
                        relationships.Add(propInjection);
                    }

                    break;

                case InvocationExpressionSyntax invocation:
                    var registration = this.AnalyzeServiceRegistration(invocation, filePath);
                    if (registration != null)
                    {
                        relationships.Add(registration);
                    }

                    break;
            }
        }

        return relationships;
    }

    private IEnumerable<DependencyInjectionRelationship> AnalyzeConstructorInjection(
        ConstructorDeclarationSyntax ctorDecl,
        string filePath)
    {
        if (ctorDecl.ParameterList.Parameters.Count == 0)
        {
            yield break;
        }

        var containingType = GetContainingTypeName(ctorDecl);
        var namespaceName = GetNamespace(ctorDecl);
        var fullTypeName = string.IsNullOrEmpty(namespaceName) ? containingType : $"{namespaceName}.{containingType}";

        foreach (var parameter in ctorDecl.ParameterList.Parameters)
        {
            var parameterType = parameter.Type?.ToString();
            if (string.IsNullOrEmpty(parameterType))
            {
                continue;
            }

            var parameterName = parameter.Identifier.Text;
            var isInterface = parameterType.StartsWith("I") && parameterType.Length > 1 && char.IsUpper(parameterType[1]);
            var isNullable = parameterType.EndsWith("?");
            var baseType = isNullable ? parameterType[..^1] : parameterType;

            yield return new DependencyInjectionRelationship
            {
                ConsumerType = fullTypeName,
                DependencyType = baseType,
                DependencyName = parameterName,
                InjectionMethod = InjectionMethod.Constructor,
                IsOptional = isNullable || HasDefaultValue(parameter),
                IsInterface = isInterface,
                FilePath = filePath,
                LineNumber = parameter.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }
    }

    private DependencyInjectionRelationship? AnalyzePropertyInjection(
        PropertyDeclarationSyntax propDecl,
        string filePath)
    {
        var attributes = propDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .Select(a => a.Name.ToString());

        var injectionAttribute = attributes.FirstOrDefault(a =>
            InjectionAttributes.Any(ia => a.EndsWith(ia) || a.Equals(ia, StringComparison.OrdinalIgnoreCase)));

        if (injectionAttribute == null)
        {
            return null;
        }

        var containingType = GetContainingTypeName(propDecl);
        var namespaceName = GetNamespace(propDecl);
        var fullTypeName = string.IsNullOrEmpty(namespaceName) ? containingType : $"{namespaceName}.{containingType}";
        var propertyType = propDecl.Type.ToString();
        var isNullable = propertyType.EndsWith("?");
        var baseType = isNullable ? propertyType[..^1] : propertyType;

        return new DependencyInjectionRelationship
        {
            ConsumerType = fullTypeName,
            DependencyType = baseType,
            DependencyName = propDecl.Identifier.Text,
            InjectionMethod = InjectionMethod.Property,
            InjectionAttribute = injectionAttribute,
            IsOptional = isNullable,
            IsInterface = baseType.StartsWith("I") && baseType.Length > 1 && char.IsUpper(baseType[1]),
            FilePath = filePath,
            LineNumber = propDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        };
    }

    private DependencyInjectionRelationship? AnalyzeServiceRegistration(
        InvocationExpressionSyntax invocation,
        string filePath)
    {
        var methodName = GetMethodName(invocation);
        if (methodName == null || !RegistrationMethods.Contains(methodName))
        {
            return null;
        }

        var (serviceType, implementationType) = ExtractRegistrationTypes(invocation);
        var lifetime = GetLifetime(methodName);
        var isKeyed = methodName.StartsWith("AddKeyed");

        return new DependencyInjectionRelationship
        {
            ConsumerType = "ServiceCollection",
            DependencyType = implementationType ?? serviceType ?? "Unknown",
            DependencyName = serviceType ?? "Unknown",
            InjectionMethod = InjectionMethod.Registration,
            ServiceType = serviceType,
            ImplementationType = implementationType,
            Lifetime = lifetime,
            IsKeyed = isKeyed,
            KeyedServiceKey = isKeyed ? ExtractKeyedServiceKey(invocation) : null,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        };
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.Text,
            _ => null
        };
    }

    private static (string? ServiceType, string? ImplementationType) ExtractRegistrationTypes(
        InvocationExpressionSyntax invocation)
    {
        // Check for generic type arguments first
        GenericNameSyntax? genericName = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            genericName = memberAccess.Name as GenericNameSyntax;
        }

        if (genericName?.TypeArgumentList.Arguments.Count > 0)
        {
            var args = genericName.TypeArgumentList.Arguments;
            var serviceType = args[0].ToString();
            var implementationType = args.Count > 1 ? args[1].ToString() : null;
            return (serviceType, implementationType);
        }

        // Check for typeof arguments
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count > 0)
        {
            var firstArg = arguments[0].Expression;
            if (firstArg is TypeOfExpressionSyntax typeOfExpr)
            {
                var serviceType = typeOfExpr.Type.ToString();
                string? implementationType = null;

                if (arguments.Count > 1 && arguments[1].Expression is TypeOfExpressionSyntax implTypeOf)
                {
                    implementationType = implTypeOf.Type.ToString();
                }

                return (serviceType, implementationType);
            }
        }

        return (null, null);
    }

    private static ServiceLifetime GetLifetime(string methodName)
    {
        if (methodName.Contains("Scoped"))
        {
            return ServiceLifetime.Scoped;
        }

        if (methodName.Contains("Transient"))
        {
            return ServiceLifetime.Transient;
        }

        return ServiceLifetime.Singleton;
    }

    private static string? ExtractKeyedServiceKey(InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments;
        foreach (var arg in arguments)
        {
            if (arg.Expression is LiteralExpressionSyntax literal)
            {
                return literal.Token.ValueText;
            }
        }

        return null;
    }

    private static string GetContainingTypeName(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
            {
                return typeDecl.Identifier.Text;
            }

            current = current.Parent;
        }

        return "Unknown";
    }

    private static string GetNamespace(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is BaseNamespaceDeclarationSyntax nsDecl)
            {
                return nsDecl.Name.ToString();
            }

            current = current.Parent;
        }

        return string.Empty;
    }

    private static bool HasDefaultValue(ParameterSyntax parameter)
    {
        return parameter.Default != null;
    }
}

/// <summary>
/// Represents a dependency injection relationship.
/// </summary>
public record DependencyInjectionRelationship
{
    /// <summary>Gets the type that consumes the dependency.</summary>
    public required string ConsumerType { get; init; }

    /// <summary>Gets the type of the dependency.</summary>
    public required string DependencyType { get; init; }

    /// <summary>Gets the name of the dependency (parameter/property name).</summary>
    public required string DependencyName { get; init; }

    /// <summary>Gets the injection method.</summary>
    public required InjectionMethod InjectionMethod { get; init; }

    /// <summary>Gets the injection attribute if property injection.</summary>
    public string? InjectionAttribute { get; init; }

    /// <summary>Gets whether this dependency is optional.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Gets whether the dependency type is an interface.</summary>
    public bool IsInterface { get; init; }

    /// <summary>Gets the service type for registrations.</summary>
    public string? ServiceType { get; init; }

    /// <summary>Gets the implementation type for registrations.</summary>
    public string? ImplementationType { get; init; }

    /// <summary>Gets the service lifetime for registrations.</summary>
    public ServiceLifetime? Lifetime { get; init; }

    /// <summary>Gets whether this is a keyed service.</summary>
    public bool IsKeyed { get; init; }

    /// <summary>Gets the key for keyed services.</summary>
    public string? KeyedServiceKey { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Methods of dependency injection.
/// </summary>
public enum InjectionMethod
{
    /// <summary>Injection via constructor parameter.</summary>
    Constructor,

    /// <summary>Injection via property with attribute.</summary>
    Property,

    /// <summary>Injection via method parameter.</summary>
    Method,

    /// <summary>Service registration in DI container.</summary>
    Registration
}

/// <summary>
/// Service lifetime for DI registrations.
/// </summary>
public enum ServiceLifetime
{
    /// <summary>A new instance is created each time.</summary>
    Transient,

    /// <summary>A new instance is created per scope.</summary>
    Scoped,

    /// <summary>A single instance is shared.</summary>
    Singleton
}
