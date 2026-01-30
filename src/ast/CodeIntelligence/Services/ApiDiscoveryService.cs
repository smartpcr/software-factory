// -----------------------------------------------------------------------
// <copyright file="ApiDiscoveryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Services;

using System.Text.RegularExpressions;
using CodeIntelligence.Analyzers;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Service that discovers complete API infrastructure by tracing from startup/bootstrap code.
/// Unlike pattern-matching individual controllers, this traces the actual wiring.
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

    #region Public Methods

    /// <summary>
    /// Discovers the complete API surface of a repository by tracing from startup code.
    /// </summary>
    public async Task<ApiDiscoveryResult> DiscoverAsync(string repoRoot)
    {
        var result = new ApiDiscoveryResult { RepoRoot = repoRoot };

        // Step 1: Find startup/bootstrap files
        var startupFiles = await this.FindStartupFilesAsync(repoRoot);
        result.StartupFiles.AddRange(startupFiles);

        // Step 2: Analyze each startup file for infrastructure setup
        foreach (var file in startupFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var infraInfo = this.infrastructureAnalyzer.Analyze(tree, file);
            result.InfrastructureSetup.Add(infraInfo);
        }

        // Step 3: Find and analyze DI configuration files
        var diFiles = await this.FindDIConfigFilesAsync(repoRoot);
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
    /// Finds all files that set up HTTP configuration or OWIN pipeline.
    /// </summary>
    public async Task<List<string>> FindStartupFilesAsync(string repoRoot)
    {
        var startupFiles = new List<string>();
        var patterns = new[]
        {
            "*Startup*.cs",
            "*WebApiConfig*.cs",
            "*HttpConfig*.cs",
            "*ServiceConfig*.cs",
            "*Bootstrap*.cs"
        };

        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(repoRoot, pattern, SafeEnumerationOptions)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
                .ToList();

            startupFiles.AddRange(files);
        }

        // Also scan for files containing HttpConfiguration parameter
        var allCsFiles = Directory.GetFiles(repoRoot, "*.cs", SafeEnumerationOptions)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
            .Take(500); // Limit for performance

        foreach (var file in allCsFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                if (content.Contains("HttpConfiguration") && content.Contains("MapHttpAttributeRoutes"))
                {
                    if (!startupFiles.Contains(file))
                    {
                        startupFiles.Add(file);
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return startupFiles.Distinct().ToList();
    }

    /// <summary>
    /// Finds dependency injection configuration files.
    /// </summary>
    public async Task<List<string>> FindDIConfigFilesAsync(string repoRoot)
    {
        var diFiles = new List<string>();
        var patterns = new[]
        {
            "*UnityConfig*.cs",
            "*AutofacModule*.cs",
            "*ServiceCollection*.cs",
            "*DependencyConfig*.cs",
            "*ContainerConfig*.cs",
            "*Startup*.cs"
        };

        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(repoRoot, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
                .ToList();

            diFiles.AddRange(files);
        }

        // Scan for files with RegisterType calls
        var allCsFiles = Directory.GetFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
            .Take(500);

        foreach (var file in allCsFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                if (content.Contains("RegisterType<") && content.Contains("Controller"))
                {
                    if (!diFiles.Contains(file))
                    {
                        diFiles.Add(file);
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return diFiles.Distinct().ToList();
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
        var abstractControllerFiles = Directory.GetFiles(repoRoot, "*Controller.cs", SearchOption.AllDirectories)
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
        var candidates = Directory.GetFiles(repoRoot, pattern, SearchOption.AllDirectories)
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
            var files = Directory.GetFiles(repoRoot, pattern, SearchOption.AllDirectories)
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
        var sourceFiles = Directory.GetFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
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
