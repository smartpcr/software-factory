// -----------------------------------------------------------------------
// <copyright file="AzureFunctionsAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes Azure Functions and their triggers/bindings.
/// </summary>
public sealed class AzureFunctionsAnalyzer
{
    private static readonly HashSet<string> FunctionAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Function",
        "FunctionName"
    };

    private static readonly HashSet<string> TriggerAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpTrigger",
        "TimerTrigger",
        "QueueTrigger",
        "BlobTrigger",
        "ServiceBusTrigger",
        "EventHubTrigger",
        "CosmosDBTrigger",
        "EventGridTrigger",
        "OrchestrationTrigger",
        "ActivityTrigger",
        "EntityTrigger",
        "SignalRTrigger",
        "KafkaTrigger"
    };

    private static readonly HashSet<string> BindingAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Blob",
        "Queue",
        "Table",
        "ServiceBus",
        "EventHub",
        "CosmosDB",
        "SignalR",
        "SendGrid",
        "Twilio"
    };

    /// <summary>
    /// Analyzes a syntax tree for Azure Functions.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected Azure Functions.</returns>
    public IReadOnlyList<AzureFunctionInfo> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var functions = new List<AzureFunctionInfo>();

        foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var functionInfo = this.AnalyzeMethod(methodDecl, filePath);
            if (functionInfo != null)
            {
                functions.Add(functionInfo);
            }
        }

        return functions;
    }

    private AzureFunctionInfo? AnalyzeMethod(MethodDeclarationSyntax methodDecl, string filePath)
    {
        // Check for Function/FunctionName attribute
        var functionAttribute = methodDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => FunctionAttributes.Any(fa =>
                a.Name.ToString().EndsWith(fa) || a.Name.ToString().Equals(fa)));

        if (functionAttribute == null)
        {
            return null;
        }

        // Extract function name from attribute
        var functionName = ExtractFunctionName(functionAttribute) ?? methodDecl.Identifier.Text;

        // Find trigger
        var triggerInfo = FindTrigger(methodDecl);

        // Find bindings
        var bindings = FindBindings(methodDecl);

        // Get containing class info
        var containingType = GetContainingTypeName(methodDecl);
        var isIsolated = IsIsolatedWorkerModel(methodDecl);

        return new AzureFunctionInfo
        {
            FunctionName = functionName,
            MethodName = methodDecl.Identifier.Text,
            ClassName = containingType,
            Trigger = triggerInfo,
            Bindings = bindings,
            IsAsync = IsAsyncMethod(methodDecl),
            ReturnType = methodDecl.ReturnType.ToString(),
            IsIsolatedWorker = isIsolated,
            FilePath = filePath,
            LineNumber = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        };
    }

    private static string? ExtractFunctionName(AttributeSyntax attribute)
    {
        var args = attribute.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return null;
        }

        var firstArg = args.Value[0].Expression;
        if (firstArg is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        if (firstArg is InvocationExpressionSyntax invocation &&
            invocation.Expression.ToString() == "nameof")
        {
            var nameofArg = invocation.ArgumentList.Arguments.FirstOrDefault();
            return nameofArg?.Expression.ToString();
        }

        return firstArg.ToString().Trim('"');
    }

    private static TriggerInfo? FindTrigger(MethodDeclarationSyntax methodDecl)
    {
        foreach (var parameter in methodDecl.ParameterList.Parameters)
        {
            var triggerAttr = parameter.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => TriggerAttributes.Any(ta =>
                    a.Name.ToString().EndsWith(ta) || a.Name.ToString().Equals(ta) ||
                    a.Name.ToString().EndsWith(ta + "Attribute")));

            if (triggerAttr != null)
            {
                var triggerType = ExtractTriggerType(triggerAttr.Name.ToString());
                return new TriggerInfo
                {
                    TriggerType = triggerType,
                    ParameterName = parameter.Identifier.Text,
                    ParameterType = parameter.Type?.ToString() ?? "unknown",
                    Configuration = ExtractAttributeConfiguration(triggerAttr)
                };
            }
        }

        return null;
    }

    private static string ExtractTriggerType(string attributeName)
    {
        var name = attributeName;
        if (name.EndsWith("Attribute"))
        {
            name = name[..^9];
        }

        if (name.EndsWith("Trigger"))
        {
            name = name[..^7];
        }

        return name;
    }

    private static List<BindingInfo> FindBindings(MethodDeclarationSyntax methodDecl)
    {
        var bindings = new List<BindingInfo>();

        // Check parameters for input bindings
        foreach (var parameter in methodDecl.ParameterList.Parameters)
        {
            foreach (var attr in parameter.AttributeLists.SelectMany(al => al.Attributes))
            {
                var attrName = attr.Name.ToString();
                if (BindingAttributes.Any(ba =>
                    attrName.EndsWith(ba) || attrName.Equals(ba) ||
                    attrName.EndsWith(ba + "Input")))
                {
                    bindings.Add(new BindingInfo
                    {
                        BindingType = ExtractBindingType(attrName),
                        Direction = BindingDirection.In,
                        ParameterName = parameter.Identifier.Text,
                        ParameterType = parameter.Type?.ToString() ?? "unknown",
                        Configuration = ExtractAttributeConfiguration(attr)
                    });
                }
            }
        }

        // Check return type for output bindings
        foreach (var attr in methodDecl.AttributeLists.SelectMany(al => al.Attributes))
        {
            var attrName = attr.Name.ToString();
            if (BindingAttributes.Any(ba =>
                attrName.EndsWith(ba + "Output") || attrName.EndsWith(ba)))
            {
                if (attrName.Contains("Output") || attrName.EndsWith("Attribute"))
                {
                    bindings.Add(new BindingInfo
                    {
                        BindingType = ExtractBindingType(attrName),
                        Direction = BindingDirection.Out,
                        Configuration = ExtractAttributeConfiguration(attr)
                    });
                }
            }
        }

        return bindings;
    }

    private static string ExtractBindingType(string attributeName)
    {
        var name = attributeName;
        if (name.EndsWith("Attribute"))
        {
            name = name[..^9];
        }

        if (name.EndsWith("Input") || name.EndsWith("Output"))
        {
            name = name[..^(name.EndsWith("Input") ? 5 : 6)];
        }

        return name;
    }

    private static Dictionary<string, string> ExtractAttributeConfiguration(AttributeSyntax attr)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (attr.ArgumentList == null)
        {
            return config;
        }

        foreach (var arg in attr.ArgumentList.Arguments)
        {
            var name = arg.NameEquals?.Name.ToString()
                ?? arg.NameColon?.Name.ToString()
                ?? $"arg{attr.ArgumentList.Arguments.IndexOf(arg)}";

            var value = arg.Expression switch
            {
                LiteralExpressionSyntax literal => literal.Token.ValueText,
                _ => arg.Expression.ToString()
            };

            config[name] = value;
        }

        return config;
    }

    private static bool IsAsyncMethod(MethodDeclarationSyntax methodDecl)
    {
        var returnType = methodDecl.ReturnType.ToString();
        return returnType.StartsWith("Task") ||
               returnType.StartsWith("ValueTask") ||
               methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
    }

    private static bool IsIsolatedWorkerModel(MethodDeclarationSyntax methodDecl)
    {
        // Check for isolated worker model indicators
        var root = methodDecl.SyntaxTree.GetRoot();
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>();

        return usings.Any(u =>
            u.Name?.ToString().Contains("Microsoft.Azure.Functions.Worker") == true);
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
/// Information about a detected Azure Function.
/// </summary>
public record AzureFunctionInfo
{
    /// <summary>Gets the function name (from attribute).</summary>
    public required string FunctionName { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the containing class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Gets the trigger information.</summary>
    public TriggerInfo? Trigger { get; init; }

    /// <summary>Gets the bindings.</summary>
    public List<BindingInfo> Bindings { get; init; } = new();

    /// <summary>Gets whether the function is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets the return type.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Gets whether using isolated worker model.</summary>
    public bool IsIsolatedWorker { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Information about a function trigger.
/// </summary>
public record TriggerInfo
{
    /// <summary>Gets the trigger type (Http, Timer, Queue, etc.).</summary>
    public required string TriggerType { get; init; }

    /// <summary>Gets the parameter name.</summary>
    public required string ParameterName { get; init; }

    /// <summary>Gets the parameter type.</summary>
    public required string ParameterType { get; init; }

    /// <summary>Gets the trigger configuration.</summary>
    public Dictionary<string, string> Configuration { get; init; } = new();
}

/// <summary>
/// Information about a function binding.
/// </summary>
public record BindingInfo
{
    /// <summary>Gets the binding type (Blob, Queue, etc.).</summary>
    public required string BindingType { get; init; }

    /// <summary>Gets the binding direction.</summary>
    public required BindingDirection Direction { get; init; }

    /// <summary>Gets the parameter name (for input bindings).</summary>
    public string? ParameterName { get; init; }

    /// <summary>Gets the parameter type.</summary>
    public string? ParameterType { get; init; }

    /// <summary>Gets the binding configuration.</summary>
    public Dictionary<string, string> Configuration { get; init; } = new();
}

/// <summary>
/// Direction of a binding.
/// </summary>
public enum BindingDirection
{
    /// <summary>Input binding.</summary>
    In,

    /// <summary>Output binding.</summary>
    Out,

    /// <summary>In/out binding.</summary>
    InOut
}
