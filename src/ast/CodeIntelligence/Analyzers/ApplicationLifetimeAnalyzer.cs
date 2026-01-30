// -----------------------------------------------------------------------
// <copyright file="ApplicationLifetimeAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes application lifetime events including IHostApplicationLifetime,
/// IApplicationLifetime, and related startup/shutdown hooks.
/// </summary>
public sealed class ApplicationLifetimeAnalyzer
{
    private static readonly HashSet<string> LifetimeInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "IHostApplicationLifetime",
        "IApplicationLifetime",
        "IHostLifetime"
    };

    private static readonly HashSet<string> LifetimeEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationStarted",
        "ApplicationStopping",
        "ApplicationStopped"
    };

    private static readonly HashSet<string> LifetimeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "StopApplication",
        "WaitForStartAsync",
        "WaitForShutdownAsync"
    };

    /// <summary>
    /// Analyzes a syntax tree for application lifetime patterns.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected application lifetime usages.</returns>
    public IReadOnlyList<ApplicationLifetimeInfo> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var lifetimeInfos = new List<ApplicationLifetimeInfo>();

        // Analyze member access for lifetime event registrations
        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var eventRegistration = this.AnalyzeEventRegistration(memberAccess, filePath);
            if (eventRegistration != null)
            {
                lifetimeInfos.Add(eventRegistration);
            }
        }

        // Analyze invocations for lifetime method calls
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodCall = this.AnalyzeLifetimeMethodCall(invocation, filePath);
            if (methodCall != null)
            {
                lifetimeInfos.Add(methodCall);
            }
        }

        // Analyze constructor parameters for lifetime injection
        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            var injection = this.AnalyzeLifetimeInjection(constructor, filePath);
            if (injection != null)
            {
                lifetimeInfos.Add(injection);
            }
        }

        return lifetimeInfos;
    }

    private ApplicationLifetimeInfo? AnalyzeEventRegistration(MemberAccessExpressionSyntax memberAccess, string filePath)
    {
        var memberName = memberAccess.Name.Identifier.Text;

        // Check if this is a lifetime event
        if (!LifetimeEvents.Contains(memberName))
        {
            return null;
        }

        // Check if the receiver is a lifetime interface
        var receiverText = memberAccess.Expression.ToString();
        var isLifetimeReceiver = LifetimeInterfaces.Any(i => receiverText.Contains(i)) ||
                                  receiverText.Contains("lifetime") ||
                                  receiverText.Contains("appLifetime") ||
                                  receiverText.Contains("_lifetime") ||
                                  receiverText.Contains("applicationLifetime");

        if (!isLifetimeReceiver)
        {
            return null;
        }

        var lineNumber = memberAccess.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var containingClass = GetContainingTypeName(memberAccess);

        // Check if this is a registration (.Register call)
        var isRegistration = memberAccess.Parent is MemberAccessExpressionSyntax parentAccess &&
                              parentAccess.Name.Identifier.Text == "Register";

        // Try to extract the callback
        string? callback = null;
        if (isRegistration && memberAccess.Parent?.Parent is InvocationExpressionSyntax invocation)
        {
            callback = ExtractCallback(invocation);
        }

        return new ApplicationLifetimeInfo
        {
            EventType = ParseEventType(memberName),
            EventName = memberName,
            ClassName = containingClass,
            IsRegistration = isRegistration,
            CallbackMethod = callback,
            ReceiverType = receiverText,
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private ApplicationLifetimeInfo? AnalyzeLifetimeMethodCall(InvocationExpressionSyntax invocation, string filePath)
    {
        var methodName = GetMethodName(invocation);
        if (string.IsNullOrEmpty(methodName) || !LifetimeMethods.Contains(methodName))
        {
            return null;
        }

        var receiver = GetReceiverName(invocation);
        if (string.IsNullOrEmpty(receiver))
        {
            return null;
        }

        // Check if receiver is a lifetime interface
        var isLifetimeReceiver = LifetimeInterfaces.Any(i => receiver.Contains(i)) ||
                                  receiver.Contains("lifetime") ||
                                  receiver.Contains("appLifetime");

        if (!isLifetimeReceiver)
        {
            return null;
        }

        var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var containingClass = GetContainingTypeName(invocation);

        return new ApplicationLifetimeInfo
        {
            EventType = ApplicationLifetimeEventType.MethodCall,
            MethodName = methodName,
            ClassName = containingClass,
            ReceiverType = receiver,
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private ApplicationLifetimeInfo? AnalyzeLifetimeInjection(ConstructorDeclarationSyntax constructor, string filePath)
    {
        var lifetimeParam = constructor.ParameterList.Parameters
            .FirstOrDefault(p => LifetimeInterfaces.Any(i =>
                p.Type?.ToString().Contains(i) == true));

        if (lifetimeParam == null)
        {
            return null;
        }

        var lineNumber = constructor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var containingClass = GetContainingTypeName(constructor);

        return new ApplicationLifetimeInfo
        {
            EventType = ApplicationLifetimeEventType.Injection,
            ClassName = containingClass,
            ParameterName = lifetimeParam.Identifier.Text,
            ParameterType = lifetimeParam.Type?.ToString(),
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private static ApplicationLifetimeEventType ParseEventType(string eventName)
    {
        return eventName switch
        {
            "ApplicationStarted" => ApplicationLifetimeEventType.Started,
            "ApplicationStopping" => ApplicationLifetimeEventType.Stopping,
            "ApplicationStopped" => ApplicationLifetimeEventType.Stopped,
            _ => ApplicationLifetimeEventType.Unknown
        };
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static string? GetReceiverName(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression.ToString();
        }

        return null;
    }

    private static string? ExtractCallback(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList == null || !invocation.ArgumentList.Arguments.Any())
        {
            return null;
        }

        var arg = invocation.ArgumentList.Arguments.First().Expression;

        return arg switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            SimpleLambdaExpressionSyntax => "<lambda>",
            ParenthesizedLambdaExpressionSyntax => "<lambda>",
            _ => null
        };
    }

    private static string GetContainingTypeName(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
            {
                var namespaceName = GetNamespace(typeDecl);
                var typeName = typeDecl.Identifier.Text;
                return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
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
}

/// <summary>
/// Information about application lifetime usage.
/// </summary>
public record ApplicationLifetimeInfo
{
    /// <summary>Gets the event type.</summary>
    public required ApplicationLifetimeEventType EventType { get; init; }

    /// <summary>Gets the event name.</summary>
    public string? EventName { get; init; }

    /// <summary>Gets the method name for method calls.</summary>
    public string? MethodName { get; init; }

    /// <summary>Gets the containing class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets whether this is a registration.</summary>
    public bool IsRegistration { get; init; }

    /// <summary>Gets the callback method name.</summary>
    public string? CallbackMethod { get; init; }

    /// <summary>Gets the receiver type.</summary>
    public string? ReceiverType { get; init; }

    /// <summary>Gets the parameter name for injections.</summary>
    public string? ParameterName { get; init; }

    /// <summary>Gets the parameter type for injections.</summary>
    public string? ParameterType { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Types of application lifetime events.
/// </summary>
public enum ApplicationLifetimeEventType
{
    /// <summary>Unknown event type.</summary>
    Unknown,

    /// <summary>ApplicationStarted event.</summary>
    Started,

    /// <summary>ApplicationStopping event.</summary>
    Stopping,

    /// <summary>ApplicationStopped event.</summary>
    Stopped,

    /// <summary>Lifetime method call.</summary>
    MethodCall,

    /// <summary>Lifetime dependency injection.</summary>
    Injection
}
