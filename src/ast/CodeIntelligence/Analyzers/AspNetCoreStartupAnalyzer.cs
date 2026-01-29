// -----------------------------------------------------------------------
// <copyright file="AspNetCoreStartupAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes ASP.NET Core startup patterns including WebApplication.CreateBuilder,
/// Host.CreateDefaultBuilder, and middleware/service configuration chains.
/// </summary>
public sealed class AspNetCoreStartupAnalyzer
{
    private static readonly HashSet<string> BuilderMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateBuilder",
        "CreateDefaultBuilder",
        "CreateHostBuilder",
        "CreateSlimBuilder",
        "CreateEmptyBuilder"
    };

    private static readonly HashSet<string> ConfigurationMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConfigureServices",
        "ConfigureWebHostDefaults",
        "ConfigureHostConfiguration",
        "ConfigureAppConfiguration",
        "ConfigureLogging",
        "ConfigureContainer"
    };

    private static readonly HashSet<string> MiddlewareMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "UseRouting",
        "UseEndpoints",
        "UseAuthentication",
        "UseAuthorization",
        "UseCors",
        "UseHttpsRedirection",
        "UseStaticFiles",
        "UseSession",
        "UseResponseCaching",
        "UseResponseCompression",
        "UseExceptionHandler",
        "UseDeveloperExceptionPage",
        "UseHsts",
        "UseSwagger",
        "UseSwaggerUI"
    };

    /// <summary>
    /// Analyzes a syntax tree for ASP.NET Core startup patterns.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected ASP.NET Core startup patterns.</returns>
    public IReadOnlyList<AspNetCoreStartupInfo> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var startupInfos = new List<AspNetCoreStartupInfo>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var builderInfo = this.AnalyzeBuilderPattern(invocation, filePath);
            if (builderInfo != null)
            {
                startupInfos.Add(builderInfo);
            }
        }

        // Also detect Startup class pattern
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var startupClass = this.AnalyzeStartupClass(classDecl, filePath);
            if (startupClass != null)
            {
                startupInfos.Add(startupClass);
            }
        }

        return startupInfos;
    }

    private AspNetCoreStartupInfo? AnalyzeBuilderPattern(InvocationExpressionSyntax invocation, string filePath)
    {
        var methodName = GetMethodName(invocation);
        if (string.IsNullOrEmpty(methodName) || !BuilderMethods.Contains(methodName))
        {
            return null;
        }

        var receiverType = GetReceiverType(invocation);
        var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Determine startup type based on receiver
        var startupType = DetermineStartupType(receiverType, methodName);

        // Extract configuration chain
        var configurationChain = ExtractConfigurationChain(invocation);
        var middlewareChain = ExtractMiddlewareChain(invocation);

        return new AspNetCoreStartupInfo
        {
            StartupType = startupType,
            BuilderMethod = methodName,
            ReceiverType = receiverType,
            ConfigurationMethods = configurationChain,
            MiddlewareMethods = middlewareChain,
            FilePath = filePath,
            LineNumber = lineNumber,
            IsMinimalApi = startupType == AspNetCoreStartupType.MinimalApi
        };
    }

    private AspNetCoreStartupInfo? AnalyzeStartupClass(ClassDeclarationSyntax classDecl, string filePath)
    {
        var className = classDecl.Identifier.Text;

        // Check for conventional Startup class
        if (!className.Equals("Startup", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hasConfigureServices = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "ConfigureServices");

        var hasConfigure = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "Configure");

        if (!hasConfigureServices && !hasConfigure)
        {
            return null;
        }

        var namespaceName = GetNamespace(classDecl);
        var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Extract methods from Configure
        var middlewareMethods = new List<string>();
        var configureMethod = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Configure");

        if (configureMethod != null)
        {
            middlewareMethods = ExtractMiddlewareFromMethod(configureMethod);
        }

        return new AspNetCoreStartupInfo
        {
            StartupType = AspNetCoreStartupType.StartupClass,
            ClassName = string.IsNullOrEmpty(namespaceName) ? className : $"{namespaceName}.{className}",
            HasConfigureServices = hasConfigureServices,
            HasConfigure = hasConfigure,
            MiddlewareMethods = middlewareMethods,
            FilePath = filePath,
            LineNumber = lineNumber,
            IsMinimalApi = false
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

    private static string? GetReceiverType(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression.ToString();
        }

        return null;
    }

    private static AspNetCoreStartupType DetermineStartupType(string? receiverType, string methodName)
    {
        if (receiverType == null)
        {
            return AspNetCoreStartupType.Unknown;
        }

        // WebApplication.CreateBuilder -> Minimal API
        if (receiverType.Contains("WebApplication"))
        {
            return methodName == "CreateSlimBuilder" || methodName == "CreateEmptyBuilder"
                ? AspNetCoreStartupType.MinimalApiSlim
                : AspNetCoreStartupType.MinimalApi;
        }

        // WebHost.CreateDefaultBuilder -> Web Host (older) - check before Host
        if (receiverType.Contains("WebHost"))
        {
            return AspNetCoreStartupType.WebHost;
        }

        // Host.CreateDefaultBuilder -> Generic Host
        if (receiverType.Contains("Host"))
        {
            return AspNetCoreStartupType.GenericHost;
        }

        return AspNetCoreStartupType.Unknown;
    }

    private static List<string> ExtractConfigurationChain(InvocationExpressionSyntax invocation)
    {
        var methods = new List<string>();

        // Walk up the invocation chain
        var current = invocation.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax parentInvocation)
            {
                var methodName = GetMethodName(parentInvocation);
                if (!string.IsNullOrEmpty(methodName) && ConfigurationMethods.Contains(methodName))
                {
                    methods.Add(methodName);
                }
            }

            current = current.Parent;
        }

        // Also check for chained calls after the builder
        var chainedCalls = GetChainedMethodCalls(invocation);
        foreach (var call in chainedCalls)
        {
            if (ConfigurationMethods.Contains(call))
            {
                methods.Add(call);
            }
        }

        return methods;
    }

    private static List<string> ExtractMiddlewareChain(InvocationExpressionSyntax invocation)
    {
        var methods = new List<string>();
        var chainedCalls = GetChainedMethodCalls(invocation);

        foreach (var call in chainedCalls)
        {
            if (MiddlewareMethods.Contains(call) || call.StartsWith("Use"))
            {
                methods.Add(call);
            }
        }

        return methods;
    }

    private static List<string> GetChainedMethodCalls(SyntaxNode startNode)
    {
        var methods = new List<string>();

        // Find the statement containing this node
        var statement = startNode.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement == null)
        {
            return methods;
        }

        // Get all invocations in the same statement
        var invocations = statement.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            var methodName = GetMethodName(inv);
            if (!string.IsNullOrEmpty(methodName))
            {
                methods.Add(methodName);
            }
        }

        return methods;
    }

    private static List<string> ExtractMiddlewareFromMethod(MethodDeclarationSyntax method)
    {
        var methods = new List<string>();

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetMethodName(invocation);
            if (!string.IsNullOrEmpty(methodName) &&
                (MiddlewareMethods.Contains(methodName) || methodName.StartsWith("Use")))
            {
                methods.Add(methodName);
            }
        }

        return methods;
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
/// Information about ASP.NET Core startup patterns.
/// </summary>
public record AspNetCoreStartupInfo
{
    /// <summary>Gets the type of startup pattern.</summary>
    public required AspNetCoreStartupType StartupType { get; init; }

    /// <summary>Gets the builder method name.</summary>
    public string? BuilderMethod { get; init; }

    /// <summary>Gets the receiver type (e.g., WebApplication, Host).</summary>
    public string? ReceiverType { get; init; }

    /// <summary>Gets the class name (for Startup class pattern).</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets whether the startup has ConfigureServices method.</summary>
    public bool HasConfigureServices { get; init; }

    /// <summary>Gets whether the startup has Configure method.</summary>
    public bool HasConfigure { get; init; }

    /// <summary>Gets the configuration methods in the chain.</summary>
    public List<string> ConfigurationMethods { get; init; } = new();

    /// <summary>Gets the middleware methods in the chain.</summary>
    public List<string> MiddlewareMethods { get; init; } = new();

    /// <summary>Gets whether this is a minimal API pattern.</summary>
    public bool IsMinimalApi { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Types of ASP.NET Core startup patterns.
/// </summary>
public enum AspNetCoreStartupType
{
    /// <summary>Unknown startup type.</summary>
    Unknown,

    /// <summary>Minimal API with WebApplication.CreateBuilder.</summary>
    MinimalApi,

    /// <summary>Minimal API with CreateSlimBuilder or CreateEmptyBuilder.</summary>
    MinimalApiSlim,

    /// <summary>Generic Host with Host.CreateDefaultBuilder.</summary>
    GenericHost,

    /// <summary>Web Host with WebHost.CreateDefaultBuilder.</summary>
    WebHost,

    /// <summary>Traditional Startup class pattern.</summary>
    StartupClass
}
