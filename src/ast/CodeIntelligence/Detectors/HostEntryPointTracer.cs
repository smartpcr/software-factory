// -----------------------------------------------------------------------
// <copyright file="HostEntryPointTracer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Detectors;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Traces from definitive host entry points to find loaded services.
/// Follows the approach: Find Host → Trace Service Loading → Report Service Entry Points.
/// </summary>
/// <remarks>
/// <para>
/// Definitive host entry points include:
/// - Program.cs with static Main method (console/Windows Service hosts)
/// - Classes extending StatefulService/StatelessService (Service Fabric)
/// - Classes extending ServiceBase (Windows Service)
/// - Classes with HostBuilder patterns (Generic Host)
/// </para>
/// <para>
/// The tracer then analyzes how services are loaded (e.g., ServiceInitializer.InitializeServiceFromAssembly)
/// to find the actual service classes that implement IService&lt;T&gt; or similar contracts.
/// </para>
/// </remarks>
public sealed class HostEntryPointTracer
{
    private readonly List<string> searchPaths = new();
    private readonly Dictionary<string, HostInfo> discoveredHosts = new();
    private readonly Dictionary<string, ServiceContractInfo> discoveredContracts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HostEntryPointTracer"/> class.
    /// </summary>
    /// <param name="repositoryRoot">The root path of the main repository.</param>
    /// <param name="dependencyRepoPaths">Optional paths to dependency repositories (e.g., common-infra).</param>
    public HostEntryPointTracer(string repositoryRoot, IEnumerable<string>? dependencyRepoPaths = null)
    {
        this.searchPaths.Add(repositoryRoot);
        if (dependencyRepoPaths != null)
        {
            this.searchPaths.AddRange(dependencyRepoPaths.Where(Directory.Exists));
        }
    }

    /// <summary>
    /// Discovers host entry points in the dependency repositories.
    /// This finds Program.cs, StatefulService subclasses, etc. that host services.
    /// </summary>
    /// <returns>List of discovered host information.</returns>
    public IReadOnlyList<HostInfo> DiscoverHosts()
    {
        var hosts = new List<HostInfo>();

        foreach (var searchPath in this.searchPaths)
        {
            try
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                };

                var csFiles = Directory.GetFiles(searchPath, "*.cs", enumerationOptions)
                    .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                               !f.Contains("\\test", StringComparison.OrdinalIgnoreCase));

                foreach (var csFile in csFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(csFile);
                        var fileHosts = this.AnalyzeFileForHosts(content, csFile);
                        hosts.AddRange(fileHosts);
                    }
                    catch
                    {
                        // Skip unreadable files
                    }
                }
            }
            catch
            {
                // Skip inaccessible paths
            }
        }

        return hosts;
    }

    /// <summary>
    /// Discovers service contracts (IService, IWebApiService, etc.) in the dependency repositories.
    /// These are the interfaces that define the lifecycle contract for services.
    /// </summary>
    /// <returns>List of discovered service contracts.</returns>
    public IReadOnlyList<ServiceContractInfo> DiscoverServiceContracts()
    {
        var contracts = new List<ServiceContractInfo>();

        foreach (var searchPath in this.searchPaths)
        {
            try
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                };

                // Look for interface files that might define service contracts
                var csFiles = Directory.GetFiles(searchPath, "I*.cs", enumerationOptions)
                    .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                               !f.Contains("\\test", StringComparison.OrdinalIgnoreCase));

                foreach (var csFile in csFiles)
                {
                    try
                    {
                        var content = File.ReadAllText(csFile);
                        var fileContracts = this.AnalyzeFileForServiceContracts(content, csFile);
                        contracts.AddRange(fileContracts);
                    }
                    catch
                    {
                        // Skip unreadable files
                    }
                }
            }
            catch
            {
                // Skip inaccessible paths
            }
        }

        return contracts;
    }

    /// <summary>
    /// Checks if a class implements a known service contract with high confidence.
    /// </summary>
    /// <param name="filePath">Path to the C# file to analyze.</param>
    /// <returns>Service implementation info if found, null otherwise.</returns>
    public ServiceImplementationInfo? AnalyzeServiceImplementation(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return this.AnalyzeServiceImplementation(content, filePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a class implements a known service contract with high confidence.
    /// </summary>
    /// <param name="content">The C# source code content.</param>
    /// <param name="filePath">The file path for reporting.</param>
    /// <returns>Service implementation info if found, null otherwise.</returns>
    public ServiceImplementationInfo? AnalyzeServiceImplementation(string content, string filePath)
    {
        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(content, path: filePath);
            var root = syntaxTree.GetRoot();

            var classDeclarations = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.BaseList != null && c.BaseList.Types.Count > 0);

            foreach (var classDecl in classDeclarations)
            {
                var result = this.AnalyzeClassForServiceContract(classDecl, content, filePath);
                if (result != null && result.Confidence >= 0.8f)
                {
                    return result;
                }
            }
        }
        catch
        {
            // Parse error
        }

        return null;
    }

    /// <summary>
    /// Analyzes a file for host patterns (Program.cs with Main, StatefulService, etc.).
    /// </summary>
    private List<HostInfo> AnalyzeFileForHosts(string content, string filePath)
    {
        var hosts = new List<HostInfo>();

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(content, path: filePath);
            var root = syntaxTree.GetRoot();

            // Check for static Main method (console/Windows Service entry point)
            var mainMethods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == "Main" &&
                           m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));

            foreach (var mainMethod in mainMethods)
            {
                var containingClass = mainMethod.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                if (containingClass != null)
                {
                    // Analyze what this Main method does - does it load services?
                    var serviceLoadingPattern = this.DetectServiceLoadingPattern(mainMethod, content);

                    hosts.Add(new HostInfo
                    {
                        HostType = HostType.ConsoleOrWindowsService,
                        ClassName = containingClass.Identifier.Text,
                        MethodName = "Main",
                        FilePath = filePath,
                        LineNumber = mainMethod.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        ServiceLoadingPattern = serviceLoadingPattern,
                        Confidence = serviceLoadingPattern != null ? 0.95f : 0.5f,
                    });
                }
            }

            // Check for Service Fabric host patterns
            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                if (classDecl.BaseList == null)
                {
                    continue;
                }

                foreach (var baseType in classDecl.BaseList.Types)
                {
                    var baseTypeName = baseType.Type.ToString();

                    // StatefulService / StatelessService are definitive Service Fabric hosts
                    if (baseTypeName == "StatefulService" || baseTypeName.Contains("StatefulService"))
                    {
                        var serviceLoadingPattern = this.DetectServiceLoadingPatternInClass(classDecl, content);
                        hosts.Add(new HostInfo
                        {
                            HostType = HostType.ServiceFabricStateful,
                            ClassName = classDecl.Identifier.Text,
                            FilePath = filePath,
                            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            BaseType = baseTypeName,
                            ServiceLoadingPattern = serviceLoadingPattern,
                            Confidence = 0.95f,
                        });
                    }
                    else if (baseTypeName == "StatelessService" || baseTypeName.Contains("StatelessService"))
                    {
                        var serviceLoadingPattern = this.DetectServiceLoadingPatternInClass(classDecl, content);
                        hosts.Add(new HostInfo
                        {
                            HostType = HostType.ServiceFabricStateless,
                            ClassName = classDecl.Identifier.Text,
                            FilePath = filePath,
                            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            BaseType = baseTypeName,
                            ServiceLoadingPattern = serviceLoadingPattern,
                            Confidence = 0.95f,
                        });
                    }
                    else if (baseTypeName == "ServiceBase")
                    {
                        hosts.Add(new HostInfo
                        {
                            HostType = HostType.WindowsService,
                            ClassName = classDecl.Identifier.Text,
                            FilePath = filePath,
                            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            BaseType = baseTypeName,
                            Confidence = 0.9f,
                        });
                    }
                }
            }
        }
        catch
        {
            // Parse error
        }

        return hosts;
    }

    /// <summary>
    /// Analyzes a file for service contract interfaces (IService, IWebApiService, etc.).
    /// </summary>
    private List<ServiceContractInfo> AnalyzeFileForServiceContracts(string content, string filePath)
    {
        var contracts = new List<ServiceContractInfo>();

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(content, path: filePath);
            var root = syntaxTree.GetRoot();

            var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();

            foreach (var interfaceDecl in interfaces)
            {
                // Get methods defined in this interface
                var methods = interfaceDecl.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Select(m => m.Identifier.Text)
                    .ToList();

                // Check if this looks like a service contract
                var hasLifecycleMethods = methods.Any(m =>
                    m == "Initialize" || m == "StartAsync" || m == "StopAsync" ||
                    m == "ExecuteAsync" || m == "Start" || m == "Stop");

                // Check if interface documentation mentions "entry point" or "service"
                var trivia = interfaceDecl.GetLeadingTrivia().ToString();
                var hasEntryPointDoc = trivia.Contains("entry point", StringComparison.OrdinalIgnoreCase) ||
                                       trivia.Contains("service", StringComparison.OrdinalIgnoreCase);

                if (hasLifecycleMethods || hasEntryPointDoc)
                {
                    var namespaceName = GetNamespace(interfaceDecl);

                    contracts.Add(new ServiceContractInfo
                    {
                        InterfaceName = interfaceDecl.Identifier.Text,
                        FullName = string.IsNullOrEmpty(namespaceName)
                            ? interfaceDecl.Identifier.Text
                            : $"{namespaceName}.{interfaceDecl.Identifier.Text}",
                        FilePath = filePath,
                        LineNumber = interfaceDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        LifecycleMethods = methods.Where(m =>
                            m == "Initialize" || m == "StartAsync" || m == "StopAsync" ||
                            m == "ExecuteAsync" || m == "Start" || m == "Stop" || m == "Register").ToList(),
                        HasEntryPointDocumentation = hasEntryPointDoc,
                        Confidence = hasEntryPointDoc ? 0.95f : (hasLifecycleMethods ? 0.8f : 0.5f),
                    });
                }
            }
        }
        catch
        {
            // Parse error
        }

        return contracts;
    }

    /// <summary>
    /// Analyzes a class to determine if it implements a service contract.
    /// </summary>
    private ServiceImplementationInfo? AnalyzeClassForServiceContract(
        ClassDeclarationSyntax classDecl,
        string content,
        string filePath)
    {
        if (classDecl.BaseList == null)
        {
            return null;
        }

        var className = classDecl.Identifier.Text;
        var namespaceName = GetNamespace(classDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? className : $"{namespaceName}.{className}";

        // Get implemented methods
        var implementedMethods = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.Text)
            .ToList();

        // Check each base type
        foreach (var baseType in classDecl.BaseList.Types)
        {
            var baseTypeName = baseType.Type.ToString();

            // Check if this is a known service contract pattern
            var contractMatch = this.MatchServiceContract(baseTypeName, implementedMethods, content);
            if (contractMatch != null)
            {
                return new ServiceImplementationInfo
                {
                    ClassName = className,
                    FullName = fullName,
                    FilePath = filePath,
                    LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ImplementedContract = baseTypeName,
                    ImplementedMethods = implementedMethods,
                    LifecycleMethods = contractMatch.Value.lifecycleMethods,
                    Confidence = contractMatch.Value.confidence,
                    EvidenceChain = contractMatch.Value.evidence,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Matches a base type against known service contract patterns.
    /// Returns lifecycle methods and confidence if it's a service contract.
    /// </summary>
    private (List<string> lifecycleMethods, float confidence, List<string> evidence)? MatchServiceContract(
        string baseTypeName,
        List<string> implementedMethods,
        string content)
    {
        var evidence = new List<string>();

        // Extract base name without generic parameters
        var baseName = baseTypeName;
        if (baseTypeName.Contains('<'))
        {
            baseName = baseTypeName.Substring(0, baseTypeName.IndexOf('<'));
        }

        // Check for generic service interface patterns
        // These are high confidence because they follow the IService<T> / IWebApiService<T> pattern
        if (baseName.StartsWith("IService") || baseName.StartsWith("IWebApiService") ||
            baseName.EndsWith("Service") && baseName.StartsWith("I"))
        {
            // Try to resolve the interface from dependency repos
            var resolvedContract = this.TryResolveContract(baseName);
            if (resolvedContract != null)
            {
                evidence.Add($"Implements {baseName} interface");
                evidence.Add($"Contract defined in: {resolvedContract.FilePath}");

                if (resolvedContract.HasEntryPointDocumentation)
                {
                    evidence.Add("Interface documentation indicates entry point");
                }

                var lifecycleMethods = implementedMethods
                    .Where(m => resolvedContract.LifecycleMethods.Contains(m))
                    .ToList();

                evidence.Add($"Lifecycle methods: {string.Join(", ", lifecycleMethods)}");

                return (lifecycleMethods, resolvedContract.Confidence, evidence);
            }
            else
            {
                // Interface not found in dependency repos, but pattern matches
                // Check if class has lifecycle methods
                var commonLifecycleMethods = new[] { "Initialize", "StartAsync", "StopAsync", "ExecuteAsync", "Register" };
                var foundLifecycle = implementedMethods.Where(m => commonLifecycleMethods.Contains(m)).ToList();

                if (foundLifecycle.Count >= 2)
                {
                    evidence.Add($"Implements {baseName} interface (contract not resolved)");
                    evidence.Add($"Has lifecycle methods: {string.Join(", ", foundLifecycle)}");

                    return (foundLifecycle, 0.7f, evidence);
                }
            }
        }

        // Check for well-known base classes
        if (baseName == "BackgroundService" || baseName == "IHostedService")
        {
            var lifecycleMethods = implementedMethods
                .Where(m => m == "ExecuteAsync" || m == "StartAsync" || m == "StopAsync")
                .ToList();

            evidence.Add($"Extends {baseName}");
            evidence.Add($"Lifecycle methods: {string.Join(", ", lifecycleMethods)}");

            return (lifecycleMethods, 0.95f, evidence);
        }

        return null;
    }

    /// <summary>
    /// Tries to resolve a contract interface from the discovered contracts.
    /// </summary>
    private ServiceContractInfo? TryResolveContract(string interfaceName)
    {
        // First check cache
        if (this.discoveredContracts.TryGetValue(interfaceName, out var cached))
        {
            return cached;
        }

        // Discover contracts if not already done
        if (this.discoveredContracts.Count == 0)
        {
            var contracts = this.DiscoverServiceContracts();
            foreach (var contract in contracts)
            {
                this.discoveredContracts[contract.InterfaceName] = contract;
            }
        }

        this.discoveredContracts.TryGetValue(interfaceName, out var result);
        return result;
    }

    /// <summary>
    /// Detects how a Main method loads services.
    /// </summary>
    private ServiceLoadingPattern? DetectServiceLoadingPattern(MethodDeclarationSyntax mainMethod, string content)
    {
        var body = mainMethod.Body?.ToString() ?? mainMethod.ExpressionBody?.ToString() ?? string.Empty;

        // Look for ServiceInitializer pattern
        if (body.Contains("ServiceInitializer") || body.Contains("InitializeServiceFromAssembly"))
        {
            return new ServiceLoadingPattern
            {
                PatternType = "DynamicAssemblyLoad",
                Description = "Uses ServiceInitializer to dynamically load service from assembly",
            };
        }

        // Look for HostBuilder pattern
        if (body.Contains("HostBuilder") || body.Contains("CreateHostBuilder") || body.Contains("Host.CreateDefaultBuilder"))
        {
            return new ServiceLoadingPattern
            {
                PatternType = "GenericHost",
                Description = "Uses .NET Generic Host to configure and run services",
            };
        }

        // Look for WebApplication pattern
        if (body.Contains("WebApplication") || body.Contains("CreateBuilder"))
        {
            return new ServiceLoadingPattern
            {
                PatternType = "MinimalApi",
                Description = "Uses ASP.NET Core minimal API pattern",
            };
        }

        return null;
    }

    /// <summary>
    /// Detects service loading pattern in a class (e.g., StatefulService).
    /// </summary>
    private ServiceLoadingPattern? DetectServiceLoadingPatternInClass(ClassDeclarationSyntax classDecl, string content)
    {
        var classBody = classDecl.ToString();

        // Look for ServiceLifecycle or ServiceInitializer usage
        if (classBody.Contains("ServiceInitializer") || classBody.Contains("InitializeServiceFromAssembly"))
        {
            return new ServiceLoadingPattern
            {
                PatternType = "DynamicAssemblyLoad",
                Description = "Dynamically loads service implementation from assembly",
            };
        }

        // Look for ReplicaLifeCycleHandler pattern (Azure Stack specific)
        if (classBody.Contains("LifeCycleHandler") || classBody.Contains("ServiceLifecycle"))
        {
            return new ServiceLoadingPattern
            {
                PatternType = "LifecycleHandler",
                Description = "Uses lifecycle handler to manage service instances",
            };
        }

        return null;
    }

    private static string GetNamespace(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is NamespaceDeclarationSyntax namespaceDecl)
            {
                return namespaceDecl.Name.ToString();
            }

            if (current is FileScopedNamespaceDeclarationSyntax fileScopedNs)
            {
                return fileScopedNs.Name.ToString();
            }

            current = current.Parent;
        }

        return string.Empty;
    }
}

/// <summary>
/// Types of host entry points.
/// </summary>
public enum HostType
{
    /// <summary>Console application or Windows Service with Main method.</summary>
    ConsoleOrWindowsService,

    /// <summary>Windows Service extending ServiceBase.</summary>
    WindowsService,

    /// <summary>Service Fabric stateful service.</summary>
    ServiceFabricStateful,

    /// <summary>Service Fabric stateless service.</summary>
    ServiceFabricStateless,

    /// <summary>.NET Generic Host.</summary>
    GenericHost,

    /// <summary>ASP.NET Core application.</summary>
    AspNetCore,
}

/// <summary>
/// Information about a discovered host entry point.
/// </summary>
public class HostInfo
{
    /// <summary>Gets or sets the host type.</summary>
    public required HostType HostType { get; set; }

    /// <summary>Gets or sets the class name.</summary>
    public required string ClassName { get; set; }

    /// <summary>Gets or sets the method name (for Main entry points).</summary>
    public string? MethodName { get; set; }

    /// <summary>Gets or sets the base type (for class-based hosts).</summary>
    public string? BaseType { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public required string FilePath { get; set; }

    /// <summary>Gets or sets the line number.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the service loading pattern detected.</summary>
    public ServiceLoadingPattern? ServiceLoadingPattern { get; set; }

    /// <summary>Gets or sets the confidence level (0-1).</summary>
    public float Confidence { get; set; }
}

/// <summary>
/// Information about how a host loads services.
/// </summary>
public class ServiceLoadingPattern
{
    /// <summary>Gets or sets the pattern type.</summary>
    public required string PatternType { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public required string Description { get; set; }
}

/// <summary>
/// Information about a service contract interface.
/// </summary>
public class ServiceContractInfo
{
    /// <summary>Gets or sets the interface name.</summary>
    public required string InterfaceName { get; set; }

    /// <summary>Gets or sets the fully qualified name.</summary>
    public required string FullName { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public required string FilePath { get; set; }

    /// <summary>Gets or sets the line number.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the lifecycle methods defined.</summary>
    public required List<string> LifecycleMethods { get; set; }

    /// <summary>Gets or sets whether documentation indicates this is an entry point.</summary>
    public bool HasEntryPointDocumentation { get; set; }

    /// <summary>Gets or sets the confidence level (0-1).</summary>
    public float Confidence { get; set; }
}

/// <summary>
/// Information about a class that implements a service contract.
/// </summary>
public class ServiceImplementationInfo
{
    /// <summary>Gets or sets the class name.</summary>
    public required string ClassName { get; set; }

    /// <summary>Gets or sets the fully qualified name.</summary>
    public required string FullName { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public required string FilePath { get; set; }

    /// <summary>Gets or sets the line number.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the implemented contract interface.</summary>
    public required string ImplementedContract { get; set; }

    /// <summary>Gets or sets all implemented methods.</summary>
    public required List<string> ImplementedMethods { get; set; }

    /// <summary>Gets or sets the lifecycle methods from the contract.</summary>
    public required List<string> LifecycleMethods { get; set; }

    /// <summary>Gets or sets the confidence level (0-1).</summary>
    public float Confidence { get; set; }

    /// <summary>Gets or sets the evidence chain for the detection.</summary>
    public required List<string> EvidenceChain { get; set; }
}
