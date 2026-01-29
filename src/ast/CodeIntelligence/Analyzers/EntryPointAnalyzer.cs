// -----------------------------------------------------------------------
// <copyright file="EntryPointAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code for entry points and lifecycle hooks.
/// </summary>
public sealed class EntryPointAnalyzer
{
    /// <summary>
    /// Analyzes a syntax tree for entry points.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected entry points.</returns>
    public IReadOnlyList<EntryPointInfo> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var entryPoints = new List<EntryPointInfo>();

        // Check for top-level statements
        var hasTopLevelStatements = root.DescendantNodes().OfType<GlobalStatementSyntax>().Any();
        if (hasTopLevelStatements)
        {
            entryPoints.Add(new EntryPointInfo
            {
                EntryPointType = EntryPointType.TopLevelStatements,
                FilePath = filePath,
                LineNumber = 1,
                ClassName = null,
                MethodName = null
            });
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax methodDecl:
                    var mainEntry = this.AnalyzeMainMethod(methodDecl, filePath);
                    if (mainEntry != null)
                    {
                        entryPoints.Add(mainEntry);
                    }

                    break;

                case ClassDeclarationSyntax classDecl:
                    entryPoints.AddRange(this.AnalyzeClassEntryPoints(classDecl, filePath));
                    break;
            }
        }

        return entryPoints;
    }

    private EntryPointInfo? AnalyzeMainMethod(MethodDeclarationSyntax methodDecl, string filePath)
    {
        if (methodDecl.Identifier.Text != "Main")
        {
            return null;
        }

        var isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        if (!isStatic)
        {
            return null;
        }

        var returnType = methodDecl.ReturnType.ToString();
        var validReturnTypes = new[] { "void", "int", "Task", "Task<int>" };
        if (!validReturnTypes.Contains(returnType))
        {
            return null;
        }

        var containingType = GetContainingTypeName(methodDecl);
        var isAsync = returnType.StartsWith("Task");
        var parameters = methodDecl.ParameterList.Parameters
            .Select(p => new ParameterInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "unknown"
            })
            .ToList();

        return new EntryPointInfo
        {
            EntryPointType = EntryPointType.ConsoleMain,
            ClassName = containingType,
            MethodName = "Main",
            ReturnType = returnType,
            IsAsync = isAsync,
            Parameters = parameters,
            FilePath = filePath,
            LineNumber = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1
        };
    }

    private IEnumerable<EntryPointInfo> AnalyzeClassEntryPoints(ClassDeclarationSyntax classDecl, string filePath)
    {
        var className = classDecl.Identifier.Text;
        var namespaceName = GetNamespace(classDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? className : $"{namespaceName}.{className}";
        var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Check for convention-based middleware first (doesn't require base list)
        var hasInvokeMethod = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "Invoke" || m.Identifier.Text == "InvokeAsync");

        var implementsIMiddleware = classDecl.BaseList?.Types.Any(t => t.Type.ToString() == "IMiddleware") == true;

        if (hasInvokeMethod && !implementsIMiddleware)
        {
            yield return new EntryPointInfo
            {
                EntryPointType = EntryPointType.Middleware,
                ClassName = fullName,
                MiddlewareType = "Convention",
                FilePath = filePath,
                LineNumber = lineNumber
            };
        }

        if (classDecl.BaseList == null)
        {
            yield break;
        }

        foreach (var baseType in classDecl.BaseList.Types)
        {
            var baseTypeName = baseType.Type.ToString();

            // BackgroundService
            if (baseTypeName == "BackgroundService")
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.BackgroundService,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    LifecycleMethods = GetLifecycleMethods(classDecl)
                };
            }

            // IHostedService
            if (baseTypeName == "IHostedService")
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.HostedService,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    LifecycleMethods = GetLifecycleMethods(classDecl)
                };
            }

            // Windows Service
            if (baseTypeName == "ServiceBase" || baseTypeName.Contains("WinSvcHost") || baseTypeName.Contains("WinServiceHost"))
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.WindowsService,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    LifecycleMethods = GetLifecycleMethods(classDecl)
                };
            }

            // Service Fabric
            if (baseTypeName == "StatelessService" || baseTypeName == "StatefulService")
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.ServiceFabric,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    FilePath = filePath,
                    LineNumber = lineNumber
                };
            }

            // Generic IService<T> or IWebApiService<T>
            if (baseTypeName.Contains("IService<") || baseTypeName.StartsWith("IService`") ||
                baseTypeName.Contains("IWebApiService<") || baseTypeName.StartsWith("IWebApiService`"))
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.HostedService,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    LifecycleMethods = GetLifecycleMethods(classDecl)
                };
            }

            // IMiddleware
            if (baseTypeName == "IMiddleware")
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.Middleware,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    MiddlewareType = "IMiddleware",
                    FilePath = filePath,
                    LineNumber = lineNumber
                };
            }

            // IStartupFilter
            if (baseTypeName == "IStartupFilter")
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.StartupHook,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    FilePath = filePath,
                    LineNumber = lineNumber
                };
            }

            // Quartz.NET IJob
            if (baseTypeName == "IJob")
            {
                yield return new EntryPointInfo
                {
                    EntryPointType = EntryPointType.ScheduledJob,
                    ClassName = fullName,
                    BaseType = baseTypeName,
                    ScheduleType = "Quartz",
                    FilePath = filePath,
                    LineNumber = lineNumber
                };
            }
        }
    }

    private static List<string> GetLifecycleMethods(ClassDeclarationSyntax classDecl)
    {
        var lifecycleMethods = new[] { "StartAsync", "StopAsync", "ExecuteAsync", "Initialize", "OnStart", "OnStop", "Dispose", "DisposeAsync" };

        return classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => lifecycleMethods.Contains(m.Identifier.Text))
            .Select(m => m.Identifier.Text)
            .ToList();
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
/// Information about a detected entry point.
/// </summary>
public record EntryPointInfo
{
    /// <summary>Gets the type of entry point.</summary>
    public required EntryPointType EntryPointType { get; init; }

    /// <summary>Gets the class name containing the entry point.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the method name (for Main entry points).</summary>
    public string? MethodName { get; init; }

    /// <summary>Gets the return type of the entry method.</summary>
    public string? ReturnType { get; init; }

    /// <summary>Gets whether the entry point is async.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Gets the base type or interface implemented.</summary>
    public string? BaseType { get; init; }

    /// <summary>Gets the parameters of the entry method.</summary>
    public List<ParameterInfo> Parameters { get; init; } = new();

    /// <summary>Gets the lifecycle methods present.</summary>
    public List<string> LifecycleMethods { get; init; } = new();

    /// <summary>Gets the middleware type (for middleware entry points).</summary>
    public string? MiddlewareType { get; init; }

    /// <summary>Gets the schedule type (for scheduled jobs).</summary>
    public string? ScheduleType { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Information about a method parameter.
/// </summary>
public record ParameterInfo
{
    /// <summary>Gets the parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the parameter type.</summary>
    public required string Type { get; init; }
}

/// <summary>
/// Types of entry points.
/// </summary>
public enum EntryPointType
{
    /// <summary>Static Main method.</summary>
    ConsoleMain,

    /// <summary>Top-level statements (C# 9+).</summary>
    TopLevelStatements,

    /// <summary>ASP.NET Core web application.</summary>
    AspNetCore,

    /// <summary>Background service (BackgroundService class).</summary>
    BackgroundService,

    /// <summary>Hosted service (IHostedService).</summary>
    HostedService,

    /// <summary>Windows service (ServiceBase).</summary>
    WindowsService,

    /// <summary>Service Fabric service.</summary>
    ServiceFabric,

    /// <summary>Azure Function.</summary>
    AzureFunction,

    /// <summary>Middleware component.</summary>
    Middleware,

    /// <summary>Startup hook (IStartupFilter).</summary>
    StartupHook,

    /// <summary>Shutdown hook.</summary>
    ShutdownHook,

    /// <summary>Scheduled/background job.</summary>
    ScheduledJob,

    /// <summary>Worker service.</summary>
    WorkerService
}
