// -----------------------------------------------------------------------
// <copyright file="ApiDiscoveryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Services;

using System.Text.RegularExpressions;
using CodeIntelligence.Analyzers;
using CodeIntelligence.Detectors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Service that discovers complete API infrastructure by tracing from entry points.
/// Instead of pattern-matching file names, this traces from actual entry points
/// (IWebApiService, Program.cs, Startup.cs) to find how routes are registered.
/// </summary>
public partial class ApiDiscoveryService
{
    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly ApiInfrastructureAnalyzer infrastructureAnalyzer = new();
    private readonly HttpEndpointAnalyzer endpointAnalyzer = new();
    private readonly HttpClientAnalyzer clientAnalyzer = new();
    private readonly EntryPointAnalyzer entryPointAnalyzer = new();

    #region Public Methods

    /// <summary>
    /// Discovers the complete API surface of a repository by tracing from entry points.
    /// </summary>
    /// <param name="repoRoot">Repository root path.</param>
    /// <param name="dependencyRepoPaths">Optional paths to dependency repositories for interface resolution.</param>
    public async Task<ApiDiscoveryResult> DiscoverAsync(string repoRoot, IEnumerable<string>? dependencyRepoPaths = null)
    {
        var result = new ApiDiscoveryResult { RepoRoot = repoRoot };

        // Step 1: Find startup files by tracing from entry points
        var startupFiles = await this.FindStartupFilesAsync(repoRoot, dependencyRepoPaths);
        result.StartupFiles.AddRange(startupFiles);

        // Step 2: Analyze each startup file for infrastructure setup
        foreach (var file in startupFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var infraInfo = this.infrastructureAnalyzer.Analyze(tree, file);
            result.InfrastructureSetup.Add(infraInfo);
        }

        // Step 3: Find and analyze DI configuration files by tracing from startup files
        var diFiles = await this.FindDIConfigFilesAsync(repoRoot, startupFiles);
        foreach (var file in diFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var registrations = this.infrastructureAnalyzer.AnalyzeControllerRegistrations(tree, file);
            result.ControllerRegistrations.AddRange(registrations);
        }

        // Step 4: Trace controller implementations
        await this.TraceControllerImplementationsAsync(repoRoot, result);

        // Step 5: Find HTTP client registrations
        await this.DiscoverHttpClientsAsync(repoRoot, result);

        // Step 6: Trace external service calls
        await this.TraceExternalCallsAsync(repoRoot, result);

        return result;
    }

    /// <summary>
    /// Finds files that configure HTTP endpoints by tracing from entry points.
    /// Instead of pattern matching file names, this:
    /// 1. Finds entry points (IWebApiService, Startup, Program.cs)
    /// 2. Locates their Register/Configure methods
    /// 3. Returns files that actually configure routes
    /// </summary>
    /// <param name="repoRoot">Repository root path.</param>
    /// <param name="dependencyRepoPaths">Optional dependency repo paths for interface resolution.</param>
    public async Task<List<string>> FindStartupFilesAsync(string repoRoot, IEnumerable<string>? dependencyRepoPaths = null)
    {
        var startupFiles = new List<string>();
        var analyzedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var csFiles = Directory.GetFiles(repoRoot, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in csFiles)
        {
            if (analyzedFiles.Contains(file))
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(file);
                var syntaxTree = CSharpSyntaxTree.ParseText(content, path: file);

                // Method 1: Find entry points that expose HTTP endpoints
                var entryPoints = this.entryPointAnalyzer.Analyze(syntaxTree, file);
                foreach (var ep in entryPoints)
                {
                    // IWebApiService implementations have a Register method for HTTP config
                    if (ep.BaseType?.Contains("IWebApiService") == true ||
                        ep.BaseType?.Contains("IService<") == true)
                    {
                        // Find the Register method in this class
                        var registerMethod = this.FindHttpConfigurationMethod(syntaxTree);
                        if (registerMethod != null)
                        {
                            startupFiles.Add(file);
                            analyzedFiles.Add(file);
                            break;
                        }
                    }
                }

                // Method 2: Find classes with Configure/Register methods that take HttpConfiguration
                if (!analyzedFiles.Contains(file))
                {
                    var hasHttpConfig = this.HasHttpConfigurationSetup(syntaxTree);
                    if (hasHttpConfig)
                    {
                        startupFiles.Add(file);
                        analyzedFiles.Add(file);
                    }
                }

                // Method 3: Find ASP.NET Core Startup classes with ConfigureServices/Configure
                if (!analyzedFiles.Contains(file))
                {
                    var hasAspNetCoreStartup = this.HasAspNetCoreStartupPattern(syntaxTree);
                    if (hasAspNetCoreStartup)
                    {
                        startupFiles.Add(file);
                        analyzedFiles.Add(file);
                    }
                }

                // Method 4: Find minimal API patterns (WebApplication.CreateBuilder)
                if (!analyzedFiles.Contains(file))
                {
                    var hasMinimalApi = this.HasMinimalApiPattern(content);
                    if (hasMinimalApi)
                    {
                        startupFiles.Add(file);
                        analyzedFiles.Add(file);
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return startupFiles;
    }

    /// <summary>
    /// Finds a method that configures HttpConfiguration (e.g., Register method in IWebApiService).
    /// </summary>
    private MethodDeclarationSyntax? FindHttpConfigurationMethod(Microsoft.CodeAnalysis.SyntaxTree syntaxTree)
    {
        var root = syntaxTree.GetRoot();

        // Look for methods with HttpConfiguration parameter
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.ParameterList.Parameters.Any(p =>
                p.Type?.ToString().Contains("HttpConfiguration") == true ||
                p.Type?.ToString().Contains("IApplicationBuilder") == true ||
                p.Type?.ToString().Contains("IEndpointRouteBuilder") == true));

        return methods.FirstOrDefault();
    }

    /// <summary>
    /// Checks if a syntax tree contains HttpConfiguration setup patterns.
    /// </summary>
    private bool HasHttpConfigurationSetup(Microsoft.CodeAnalysis.SyntaxTree syntaxTree)
    {
        var root = syntaxTree.GetRoot();

        // Find methods that configure HTTP routes
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            // Check for HttpConfiguration parameter
            var hasHttpConfigParam = method.ParameterList.Parameters.Any(p =>
                p.Type?.ToString().Contains("HttpConfiguration") == true);

            if (hasHttpConfigParam)
            {
                // Check if the method body contains route configuration
                var body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? string.Empty;
                if (body.Contains("MapHttpAttributeRoutes") ||
                    body.Contains("MapRoute") ||
                    body.Contains("Routes.MapHttpRoute") ||
                    body.Contains("config.Routes"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a syntax tree contains ASP.NET Core Startup class patterns.
    /// </summary>
    private bool HasAspNetCoreStartupPattern(Microsoft.CodeAnalysis.SyntaxTree syntaxTree)
    {
        var root = syntaxTree.GetRoot();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>();
            var methodNames = methods.Select(m => m.Identifier.Text).ToList();

            // ASP.NET Core Startup has ConfigureServices and Configure methods
            if (methodNames.Contains("ConfigureServices") && methodNames.Contains("Configure"))
            {
                return true;
            }

            // Or just Configure with IApplicationBuilder
            var configureMethod = methods.FirstOrDefault(m => m.Identifier.Text == "Configure");
            if (configureMethod != null)
            {
                var hasAppBuilder = configureMethod.ParameterList.Parameters.Any(p =>
                    p.Type?.ToString().Contains("IApplicationBuilder") == true);
                if (hasAppBuilder)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if content contains minimal API patterns.
    /// </summary>
    private bool HasMinimalApiPattern(string content)
    {
        // Minimal API patterns
        return (content.Contains("WebApplication.CreateBuilder") ||
                content.Contains("Host.CreateDefaultBuilder") ||
                content.Contains("CreateHostBuilder")) &&
               (content.Contains(".MapGet(") ||
                content.Contains(".MapPost(") ||
                content.Contains(".MapControllers(") ||
                content.Contains(".UseEndpoints("));
    }

    /// <summary>
    /// Finds all files that set up HTTP configuration (legacy method for compatibility).
    /// </summary>
    [Obsolete("Use FindStartupFilesAsync(repoRoot, dependencyRepoPaths) instead")]
    public Task<List<string>> FindStartupFilesAsync(string repoRoot)
    {
        return this.FindStartupFilesAsync(repoRoot, null);
    }

    /// <summary>
    /// Finds dependency injection configuration files by tracing from entry points.
    /// This method:
    /// 1. Identifies the DI container type used (Unity, ServiceCollection, Autofac)
    /// 2. Traces how the container is configured from entry points
    /// 3. Follows method calls that receive the container to find registration sites
    /// </summary>
    /// <param name="repoRoot">Repository root path.</param>
    /// <param name="startupFiles">Already discovered startup files to trace from.</param>
    public async Task<List<string>> FindDIConfigFilesAsync(string repoRoot, IEnumerable<string> startupFiles)
    {
        var diFiles = new List<string>();
        var analyzedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Step 1: Detect DI framework and find container variable usage in startup files
        var containerTraces = new List<DIContainerTrace>();

        foreach (var startupFile in startupFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(startupFile);
                var syntaxTree = CSharpSyntaxTree.ParseText(content, path: startupFile);
                var root = syntaxTree.GetRoot();

                // Detect container types and trace their usage
                var traces = this.DetectContainerUsage(root, startupFile);
                containerTraces.AddRange(traces);

                // If startup file has container usage with registrations, add it
                if (traces.Any(t => t.HasRegistrations))
                {
                    diFiles.Add(startupFile);
                    analyzedFiles.Add(startupFile);
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        // Step 2: Find files that receive the container and perform registrations
        var csFiles = Directory.GetFiles(repoRoot, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Get all types that are called from startup files (DI config classes)
        var typesToTrace = containerTraces
            .SelectMany(t => t.CalledTypes)
            .Distinct()
            .ToList();

        // If no types to trace found, return what we have
        if (typesToTrace.Count == 0)
        {
            return diFiles;
        }

        foreach (var file in csFiles)
        {
            if (analyzedFiles.Contains(file))
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(file);
                var syntaxTree = CSharpSyntaxTree.ParseText(content, path: file);
                var root = syntaxTree.GetRoot();

                // Check if this file defines any of the types we're tracing
                var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classDecl in classDecls)
                {
                    var className = classDecl.Identifier.Text;

                    if (typesToTrace.Contains(className))
                    {
                        // This class is called from startup - check if it has container registrations
                        var classTraces = this.DetectContainerUsage(classDecl, file);
                        if (classTraces.Any(t => t.HasRegistrations))
                        {
                            diFiles.Add(file);
                            analyzedFiles.Add(file);

                            // Add any types this class calls to our trace list
                            foreach (var trace in classTraces)
                            {
                                foreach (var calledType in trace.CalledTypes)
                                {
                                    if (!typesToTrace.Contains(calledType))
                                    {
                                        typesToTrace.Add(calledType);
                                    }
                                }
                            }

                            break;
                        }
                    }
                }

                // Also check for methods that receive container parameters
                if (!analyzedFiles.Contains(file))
                {
                    var fileTraces = this.DetectContainerUsage(root, file);
                    if (fileTraces.Any(t => t.HasRegistrations && t.ContainerPassedAsParameter))
                    {
                        diFiles.Add(file);
                        analyzedFiles.Add(file);
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return diFiles;
    }

    /// <summary>
    /// Detects DI container usage patterns in a syntax node using AST analysis.
    /// Traces container flow by:
    /// 1. Finding properties/fields with container types
    /// 2. Tracking assignments to find methods that create/configure containers
    /// 3. Finding methods that receive container as parameter
    /// </summary>
    private List<DIContainerTrace> DetectContainerUsage(SyntaxNode node, string filePath)
    {
        var traces = new List<DIContainerTrace>();
        var calledTypes = new HashSet<string>();

        // Step 1: Find properties/fields with container types in the class
        var containerMembers = new Dictionary<string, DIContainerType>();

        var propertyDecls = node.DescendantNodes().OfType<PropertyDeclarationSyntax>();
        foreach (var prop in propertyDecls)
        {
            var propType = prop.Type.ToString();
            var containerType = this.GetContainerType(propType);
            if (containerType != DIContainerType.Unknown)
            {
                containerMembers[prop.Identifier.Text] = containerType;
            }
        }

        var fieldDecls = node.DescendantNodes().OfType<FieldDeclarationSyntax>();
        foreach (var field in fieldDecls)
        {
            var fieldType = field.Declaration.Type.ToString();
            var containerType = this.GetContainerType(fieldType);
            if (containerType != DIContainerType.Unknown)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    containerMembers[variable.Identifier.Text] = containerType;
                }
            }
        }

        // Step 2: Find methods and analyze container usage
        var methods = node.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var trace = new DIContainerTrace { FilePath = filePath, MethodName = method.Identifier.Text };

            // Check method parameters for container types
            foreach (var param in method.ParameterList.Parameters)
            {
                var paramType = param.Type?.ToString() ?? string.Empty;
                var containerType = this.GetContainerType(paramType);
                if (containerType != DIContainerType.Unknown)
                {
                    trace.ContainerType = containerType;
                    trace.ContainerPassedAsParameter = true;
                    trace.ContainerVariableName = param.Identifier.Text;
                }
            }

            // Check method return type - methods that return containers are factory methods
            var returnType = method.ReturnType.ToString();
            var returnContainerType = this.GetContainerType(returnType);
            if (returnContainerType != DIContainerType.Unknown)
            {
                trace.ContainerType = returnContainerType;
                trace.IsContainerFactory = true;
            }

            // Check local variable declarations for container types
            var variableDecls = method.DescendantNodes().OfType<VariableDeclarationSyntax>();
            foreach (var varDecl in variableDecls)
            {
                var varType = varDecl.Type.ToString();
                var containerType = this.GetContainerType(varType);
                if (containerType != DIContainerType.Unknown)
                {
                    trace.ContainerType = containerType;
                    trace.ContainerVariableName = varDecl.Variables.FirstOrDefault()?.Identifier.Text ?? string.Empty;
                }
            }

            // Analyze assignments to container properties/fields
            var assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>();
            foreach (var assignment in assignments)
            {
                // Get the target of the assignment (property or field name)
                var leftSide = assignment.Left.ToString();
                var targetName = leftSide.Replace("this.", string.Empty);

                // Check if assigning to a container member
                if (containerMembers.TryGetValue(targetName, out var memberContainerType))
                {
                    trace.ContainerType = memberContainerType;

                    // If the right side is a method call, trace to that method's class
                    if (assignment.Right is InvocationExpressionSyntax invocation)
                    {
                        var sourceClass = this.ExtractClassFromInvocation(invocation);
                        if (sourceClass != null)
                        {
                            calledTypes.Add(sourceClass);
                        }
                    }
                }
            }

            // Find all invocations and check for container registration calls and delegation
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                // Check for registration method calls on container
                if (trace.ContainerType != DIContainerType.Unknown && !string.IsNullOrEmpty(trace.ContainerVariableName))
                {
                    var expr = invocation.Expression.ToString();

                    // Check if calling method on our container variable
                    if (expr.StartsWith(trace.ContainerVariableName + ".") ||
                        expr.StartsWith("this." + trace.ContainerVariableName + "."))
                    {
                        // Detect actual registration calls based on container type
                        trace.HasRegistrations = this.IsRegistrationCall(invocation, trace.ContainerType);
                    }
                }

                // Track method calls that pass container as argument
                var args = invocation.ArgumentList.Arguments;
                foreach (var arg in args)
                {
                    var argExpr = arg.Expression.ToString().Replace("this.", string.Empty);

                    // Check if passing a container member or variable
                    if (containerMembers.ContainsKey(argExpr) ||
                        argExpr == trace.ContainerVariableName)
                    {
                        var targetClass = this.ExtractClassFromInvocation(invocation);
                        if (targetClass != null)
                        {
                            calledTypes.Add(targetClass);
                        }
                    }
                }
            }

            // Add all called types to trace
            foreach (var calledType in calledTypes)
            {
                if (!trace.CalledTypes.Contains(calledType))
                {
                    trace.CalledTypes.Add(calledType);
                }
            }

            // Only add trace if we found container usage
            if (trace.ContainerType != DIContainerType.Unknown || calledTypes.Count > 0)
            {
                traces.Add(trace);
            }
        }

        return traces;
    }

    /// <summary>
    /// Gets the container type from a type string.
    /// </summary>
    private DIContainerType GetContainerType(string typeString)
    {
        if (typeString.Contains("IUnityContainer") || typeString.Contains("UnityContainer"))
        {
            return DIContainerType.Unity;
        }

        if (typeString.Contains("IServiceCollection") || typeString.Contains("ServiceCollection") ||
            typeString.Contains("IServiceProvider") || typeString.Contains("ServiceProvider"))
        {
            return DIContainerType.ServiceCollection;
        }

        if (typeString.Contains("ContainerBuilder") || typeString.Contains("ILifetimeScope") ||
            typeString.Contains("IContainer"))
        {
            return DIContainerType.Autofac;
        }

        return DIContainerType.Unknown;
    }

    /// <summary>
    /// Extracts the class name from an invocation expression (for static method calls).
    /// </summary>
    private string? ExtractClassFromInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Expression is IdentifierNameSyntax identifier)
        {
            var name = identifier.Identifier.Text;
            // Only return if it looks like a class name (starts with uppercase)
            if (!string.IsNullOrEmpty(name) && char.IsUpper(name[0]))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines if an invocation is a DI registration call based on container type.
    /// Uses AST to check the method being called on the container.
    /// </summary>
    private bool IsRegistrationCall(InvocationExpressionSyntax invocation, DIContainerType containerType)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        var methodName = memberAccess.Name.Identifier.Text;

        return containerType switch
        {
            DIContainerType.Unity => methodName is "RegisterType" or "RegisterInstance" or "RegisterFactory" or "RegisterSingleton",
            DIContainerType.ServiceCollection => methodName is "AddScoped" or "AddSingleton" or "AddTransient" or "AddControllers" or "AddControllersWithViews",
            DIContainerType.Autofac => methodName is "RegisterType" or "Register" or "RegisterInstance" or "RegisterModule",
            _ => false
        };
    }

    /// <summary>
    /// Represents a trace of DI container usage.
    /// </summary>
    private class DIContainerTrace
    {
        public string FilePath { get; set; } = string.Empty;

        public string MethodName { get; set; } = string.Empty;

        public DIContainerType ContainerType { get; set; }

        public string? ContainerVariableName { get; set; }

        public bool ContainerPassedAsParameter { get; set; }

        public bool IsContainerFactory { get; set; }

        public bool HasRegistrations { get; set; }

        public List<string> CalledTypes { get; } = new();
    }

    /// <summary>
    /// Types of DI containers supported.
    /// </summary>
    private enum DIContainerType
    {
        Unknown,
        Unity,
        ServiceCollection,
        Autofac,
    }

    /// <summary>
    /// Finds dependency injection configuration files by scanning for patterns.
    /// </summary>
    /// <remarks>
    /// Use the overload with startupFiles parameter for entry-point based discovery.
    /// </remarks>
    [Obsolete("Use FindDIConfigFilesAsync(repoRoot, startupFiles) for entry-point based discovery")]
    public async Task<List<string>> FindDIConfigFilesAsync(string repoRoot)
    {
        var diFiles = new List<string>();
        var analyzedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var csFiles = Directory.GetFiles(repoRoot, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in csFiles)
        {
            if (analyzedFiles.Contains(file))
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(file);
                var syntaxTree = CSharpSyntaxTree.ParseText(content, path: file);
                var root = syntaxTree.GetRoot();

                // Use container-based detection
                var traces = this.DetectContainerUsage(root, file);
                if (traces.Any(t => t.HasRegistrations))
                {
                    diFiles.Add(file);
                    analyzedFiles.Add(file);
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return diFiles;
    }

    #endregion

    #region Private Methods - Controller Tracing

    private async Task TraceControllerImplementationsAsync(string repoRoot, ApiDiscoveryResult result)
    {
        // For each controller registration, find and analyze the implementation
        foreach (var registration in result.ControllerRegistrations)
        {
            var implName = registration.ImplementationType;

            // Find the implementation file
            var implFile = await this.FindImplementationFileAsync(repoRoot, implName);
            if (implFile == null)
            {
                continue;
            }

            var code = await File.ReadAllTextAsync(implFile);
            var tree = CSharpSyntaxTree.ParseText(code);

            // Extract endpoints from the implementation
            var endpoints = this.endpointAnalyzer.Analyze(tree, implFile);

            foreach (var endpoint in endpoints)
            {
                result.Endpoints.Add(new DiscoveredEndpoint
                {
                    ControllerInterface = registration.InterfaceType,
                    ControllerImplementation = registration.ImplementationType,
                    ActionName = endpoint.ActionName,
                    HttpMethod = endpoint.HttpMethod,
                    RouteTemplate = endpoint.RouteTemplate,
                    Parameters = endpoint.Parameters,
                    RequiresAuth = endpoint.RequiresAuthorization,
                    AllowsAnonymous = endpoint.AllowsAnonymous,
                    FilePath = implFile,
                    LineNumber = endpoint.LineNumber,
                    // Link back to infrastructure setup
                    AuthHandlers = result.InfrastructureSetup
                        .SelectMany(i => i.MessageHandlers)
                        .Where(h => h.HandlerExpression.Contains("Auth") ||
                                   h.HandlerExpression.Contains("Certificate"))
                        .Select(h => h.HandlerExpression)
                        .ToList()
                });
            }
        }

        // Also find controllers that weren't registered in DI (abstract base controllers)
        var abstractControllerFiles = Directory.GetFiles(repoRoot, "*Controller.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                       f.Contains("Contract") || f.Contains("Abstract"))
            .ToList();

        foreach (var file in abstractControllerFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var endpoints = this.endpointAnalyzer.Analyze(tree, file);

            foreach (var endpoint in endpoints)
            {
                // Check if this endpoint is already discovered via implementation
                var alreadyExists = result.Endpoints.Any(e =>
                    e.ActionName == endpoint.ActionName &&
                    e.HttpMethod == endpoint.HttpMethod);

                if (!alreadyExists)
                {
                    result.AbstractEndpoints.Add(new DiscoveredEndpoint
                    {
                        ControllerInterface = endpoint.ControllerName ?? "",
                        ControllerImplementation = "(abstract)",
                        ActionName = endpoint.ActionName,
                        HttpMethod = endpoint.HttpMethod,
                        RouteTemplate = endpoint.RouteTemplate,
                        Parameters = endpoint.Parameters,
                        RequiresAuth = endpoint.RequiresAuthorization,
                        AllowsAnonymous = endpoint.AllowsAnonymous,
                        FilePath = file,
                        LineNumber = endpoint.LineNumber,
                        AuthHandlers = []
                    });
                }
            }
        }
    }

    private async Task<string?> FindImplementationFileAsync(string repoRoot, string typeName)
    {
        // Search for a file containing the class definition
        var pattern = $"*{typeName}*.cs";
        var candidates = Directory.GetFiles(repoRoot, pattern, SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        foreach (var file in candidates)
        {
            var content = await File.ReadAllTextAsync(file);
            if (content.Contains($"class {typeName}"))
            {
                return file;
            }
        }

        return null;
    }

    #endregion

    #region Private Methods - HTTP Client Discovery

    private async Task DiscoverHttpClientsAsync(string repoRoot, ApiDiscoveryResult result)
    {
        // Find HTTP client singleton/factory files
        var clientPatterns = new[] { "*HttpClient*.cs", "*Client.cs", "*Factory.cs" };

        foreach (var pattern in clientPatterns)
        {
            var files = Directory.GetFiles(repoRoot, pattern, SafeEnumerationOptions)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
                .Take(50)
                .ToList();

            foreach (var file in files)
            {
                var code = await File.ReadAllTextAsync(file);
                if (!code.Contains("HttpClient"))
                {
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(code);
                var registrations = this.infrastructureAnalyzer.AnalyzeHttpClientRegistrations(tree, file);
                result.HttpClientRegistrations.AddRange(registrations);

                // Also analyze auth handlers for this file
                var authHandlers = this.infrastructureAnalyzer.AnalyzeAuthHandlers(tree, file);
                result.AuthHandlers.AddRange(authHandlers);
            }
        }
    }

    private async Task TraceExternalCallsAsync(string repoRoot, ApiDiscoveryResult result)
    {
        // Find all files with HTTP client usage patterns
        var sourceFiles = Directory.GetFiles(repoRoot, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
            .ToList();

        foreach (var file in sourceFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);

                // Quick filter - only parse files with HTTP patterns
                if (!content.Contains("PostAsync") && !content.Contains("GetAsync") &&
                    !content.Contains("SendAsync") && !content.Contains("HttpClient") &&
                    !content.Contains("RestClient"))
                {
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(content);
                var calls = this.clientAnalyzer.Analyze(tree, file);

                foreach (var call in calls)
                {
                    // Try to link the call back to a client registration
                    var clientReg = result.HttpClientRegistrations.FirstOrDefault(r =>
                        r.ClassName != null &&
                        (content.Contains(r.ClassName) || file.Contains(r.ClassName)));

                    result.ExternalCalls.Add(new ExternalServiceCall
                    {
                        CallingClass = this.ExtractClassName(file, content),
                        CallingMethod = call.MethodName,
                        ClientType = call.ClientType.ToString(),
                        TargetExpression = call.TargetUrl ?? "(dynamic)",
                        HttpMethod = this.InferHttpMethod(call.MethodName),
                        FilePath = file,
                        LineNumber = call.LineNumber,
                        // Link to client registration
                        ClientRegistration = clientReg?.ClassName,
                        // Check if auth is configured
                        HasAuthConfiguration = clientReg?.HandlerConfiguration != null ||
                            result.AuthHandlers.Any(h => content.Contains(h.Expression))
                    });
                }
            }
            catch
            {
                // Skip problematic files
            }
        }
    }

    private string ExtractClassName(string filePath, string content)
    {
        var classMatch = ClassNameRegex().Match(content);
        return classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);
    }

    private string InferHttpMethod(string methodName)
    {
        return methodName switch
        {
            "GetAsync" or "GetStringAsync" => "GET",
            "PostAsync" or "PostAsJsonAsync" => "POST",
            "PutAsync" or "PutAsJsonAsync" => "PUT",
            "DeleteAsync" => "DELETE",
            "PatchAsync" => "PATCH",
            "SendAsync" => "UNKNOWN", // Could be any method
            _ => "UNKNOWN"
        };
    }

    [GeneratedRegex(@"class\s+(\w+)")]
    private static partial Regex ClassNameRegex();

    #endregion
}

#region Result Models

/// <summary>
/// Complete API discovery result for a repository.
/// </summary>
public record ApiDiscoveryResult
{
    public required string RepoRoot { get; init; }
    public List<string> StartupFiles { get; init; } = [];
    public List<ApiInfrastructureInfo> InfrastructureSetup { get; init; } = [];
    public List<ControllerRegistration> ControllerRegistrations { get; init; } = [];
    public List<DiscoveredEndpoint> Endpoints { get; init; } = [];
    public List<DiscoveredEndpoint> AbstractEndpoints { get; init; } = [];
    public List<HttpClientRegistration> HttpClientRegistrations { get; init; } = [];
    public List<AuthHandlerInfo> AuthHandlers { get; init; } = [];
    public List<ExternalServiceCall> ExternalCalls { get; init; } = [];
}

/// <summary>
/// An API endpoint discovered by tracing from DI registration.
/// </summary>
public record DiscoveredEndpoint
{
    public required string ControllerInterface { get; init; }
    public required string ControllerImplementation { get; init; }
    public required string ActionName { get; init; }
    public required string HttpMethod { get; init; }
    public required string RouteTemplate { get; init; }
    public IReadOnlyList<EndpointParameter> Parameters { get; init; } = [];
    public bool RequiresAuth { get; init; }
    public bool AllowsAnonymous { get; init; }
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public List<string> AuthHandlers { get; init; } = [];
}

/// <summary>
/// An external service call traced from the codebase.
/// </summary>
public record ExternalServiceCall
{
    public required string CallingClass { get; init; }
    public required string CallingMethod { get; init; }
    public required string ClientType { get; init; }
    public required string TargetExpression { get; init; }
    public required string HttpMethod { get; init; }
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public string? ClientRegistration { get; init; }
    public bool HasAuthConfiguration { get; init; }
}

#endregion

