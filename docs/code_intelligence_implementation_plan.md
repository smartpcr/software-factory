# Code Intelligence Enhancement Implementation Plan

> Last Updated: 2026-01-24

This document outlines enhancements to the AST HyperGraph Reasoning system for improved repo-level code intelligence, based on analysis of OpenCode's successful discovery patterns where LSP failed.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Problem Statement](#problem-statement)
3. [OpenCode's Success Patterns](#opencodes-success-patterns)
4. [Enhancement Areas](#enhancement-areas)
   - [Entry Point Detection](#1-entry-point-detection)
   - [API Endpoint Discovery](#2-api-endpoint-discovery)
   - [Configuration-Based Discovery](#3-configuration-based-discovery)
   - [Cross-Reference Analysis](#4-cross-reference-analysis)
5. [Implementation Phases](#implementation-phases)
6. [Technical Specifications](#technical-specifications)
7. [Integration with Existing Infrastructure](#integration-with-existing-infrastructure)
8. [Success Criteria](#success-criteria)

---

## Executive Summary

### Current Gap

LSP-based code intelligence fails for certain architectural patterns:
- **Service Fabric hosting**: External host with reflection-based assembly loading
- **Dynamic routing**: Custom route providers, ARM-style URL generation
- **Plugin architectures**: MEF, runtime assembly loading
- **Configuration-driven behavior**: Settings.xml, manifest files

### Proposed Solution

Implement a **multi-strategy code intelligence system** that combines:
1. LSP (when available and accurate)
2. Pattern-based heuristics
3. Configuration file analysis
4. Hypergraph relationship traversal
5. Embedding-based semantic discovery

---

## Problem Statement

### Why LSP Fails for Service Fabric

**Traditional Entry Point (LSP works):**
```
Main() → Service.Start() → Controller
 └── Compile-time call chain → LSP can trace
```

**Service Fabric Pattern (LSP fails):**
```
External Host (ServiceFabricHost.exe)
        │
        ▼
Settings.xml → "ServiceAssembly=MyService.dll"
        │
        ▼ (Reflection at runtime)
UpdateService<T> → Initialize() → StartAsync()
        │
        ▼
IWebApiService<T>.Register() → Controllers
```

**Root Cause**: No compile-time linkage between host and service assembly. The connection exists only in configuration files and through reflection-based interfaces.

### Impact on Code Understanding

| Capability | LSP Result | Actual Behavior |
|------------|------------|-----------------|
| Find entry point | ❌ No Main() found | `UpdateService<T>.StartAsync()` |
| Trace service lifecycle | ❌ Can't follow reflection | Initialize → StartAsync → Register → StopAsync |
| Discover API routes | ❌ Static analysis misses dynamic routes | ARM-style URLs via custom provider |
| Find callers | ❌ Cross-assembly refs hidden | Service Fabric runtime invocation |

---

## OpenCode's Success Patterns

Analysis of OpenCode session log (`/mnt/e/work/hci/update/docs/opencode/service.md`) reveals four key strategies:

### 1. Pattern-Based Discovery

OpenCode searches for known hosting patterns:
```
Search patterns:
- "StatefulService" / "StatelessService"
- "IWebApiService" / "ServiceFabricHost"
- "IIntegrationTestService" / "Initialize"
- Assembly loading patterns (Activator.CreateInstance, Assembly.Load)
```

### 2. Configuration File Analysis

OpenCode parses Service Fabric manifests:
```
Files analyzed:
- ServiceManifest.xml → <ServiceTypes>, <CodePackage>
- Settings.xml → ServiceAssembly, ServiceType entries
- ApplicationManifest.xml → Service instances
- *.csproj → PackageReference for framework detection
```

### 3. Inheritance Tracing

OpenCode follows interface hierarchies:
```
IWebApiService<T>
    └── UpdateService<TDependency>
            └── Implements: Initialize(), StartAsync(), Register(), StopAsync()
                    └── Register() → OwinConfiguration → Controllers
```

### 4. Explicit Filtering

OpenCode excludes false positives:
```
Exclusions:
- Test assemblies (*Tests.dll, *Test.dll)
- Mock implementations (MockService*, FakeService*)
- Abstract base classes
- Interfaces without concrete implementations
```

---

## Enhancement Areas

### 1. Entry Point Detection

#### Current State
- LSP `textDocument/documentSymbol` finds static `Main()` methods
- Fails for framework-hosted, reflection-loaded, or configuration-driven entry points

#### Proposed Enhancement

**New Component: `EntryPointDiscoveryService`**

```csharp
namespace Ast.Discovery;

public class EntryPointDiscoveryService
{
    private readonly ILspClient _lspClient;
    private readonly IGraphStore _graphStore;
    private readonly EntryPointHeuristics _heuristics;
    private readonly ConfigurationParser _configParser;

    public async Task<EntryPointAnalysis> DiscoverEntryPointsAsync(
        string repositoryPath,
        CancellationToken ct = default)
    {
        var analysis = new EntryPointAnalysis();

        // Strategy 1: Conventional LSP discovery
        var lspResults = await TryLspDiscoveryAsync(repositoryPath, ct);
        analysis.AddResults(lspResults, DiscoveryStrategy.LSP);

        // Strategy 2: Configuration-based discovery
        var configResults = await DiscoverFromConfigurationAsync(repositoryPath, ct);
        analysis.AddResults(configResults, DiscoveryStrategy.Configuration);

        // Strategy 3: Pattern heuristics
        var patternResults = await DiscoverFromPatternsAsync(repositoryPath, ct);
        analysis.AddResults(patternResults, DiscoveryStrategy.Pattern);

        // Strategy 4: Test file inference
        var testResults = await InferFromTestsAsync(repositoryPath, ct);
        analysis.AddResults(testResults, DiscoveryStrategy.TestInference);

        // Rank and deduplicate
        return analysis.Consolidate();
    }
}

public record EntryPointCandidate
{
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required EntryPointType Type { get; init; }
    public required DiscoveryStrategy Strategy { get; init; }
    public required float Confidence { get; init; }
    public string? FrameworkHint { get; init; }
    public IReadOnlyList<string> LifecycleMethods { get; init; } = [];
}

public enum EntryPointType
{
    ConsoleMain,           // static void Main()
    WebApiStartup,         // Startup.Configure()
    ServiceFabricService,  // StatelessService/StatefulService
    WorkerService,         // BackgroundService/IHostedService
    AzureFunction,         // [FunctionName] attribute
    AspNetCoreMinimal,     // WebApplication.CreateBuilder()
    PluginEntry,           // IPlugin, MEF exports
    TestHost               // Test service host
}

public enum DiscoveryStrategy
{
    LSP,
    Configuration,
    Pattern,
    TestInference,
    GraphTraversal,
    Embedding
}
```

**Pattern Heuristics Implementation:**

```csharp
namespace Ast.Discovery;

public class EntryPointHeuristics
{
    // Service Fabric patterns
    private static readonly PatternRule[] ServiceFabricRules =
    [
        new("StatelessService", "CreateServiceInstanceListeners", EntryPointType.ServiceFabricService, 0.95f),
        new("StatefulService", "CreateServiceReplicaListeners", EntryPointType.ServiceFabricService, 0.95f),
        new("IWebApiService", "Register", EntryPointType.ServiceFabricService, 0.90f),
        new("ICommunicationListener", "OpenAsync", EntryPointType.ServiceFabricService, 0.85f),
    ];

    // ASP.NET Core patterns
    private static readonly PatternRule[] AspNetCoreRules =
    [
        new("IHostBuilder", "Build", EntryPointType.WebApiStartup, 0.90f),
        new("WebApplication", "CreateBuilder", EntryPointType.AspNetCoreMinimal, 0.95f),
        new("IApplicationBuilder", "UseEndpoints", EntryPointType.WebApiStartup, 0.85f),
        new("Startup", "Configure", EntryPointType.WebApiStartup, 0.80f),
    ];

    // Worker/Background service patterns
    private static readonly PatternRule[] WorkerRules =
    [
        new("BackgroundService", "ExecuteAsync", EntryPointType.WorkerService, 0.90f),
        new("IHostedService", "StartAsync", EntryPointType.WorkerService, 0.85f),
    ];

    // Azure Functions patterns
    private static readonly PatternRule[] AzureFunctionRules =
    [
        new("[FunctionName", null, EntryPointType.AzureFunction, 0.95f),
        new("IFunctionsHostBuilder", "Configure", EntryPointType.AzureFunction, 0.90f),
    ];

    public IEnumerable<EntryPointCandidate> Analyze(HypergraphNode node)
    {
        var allRules = ServiceFabricRules
            .Concat(AspNetCoreRules)
            .Concat(WorkerRules)
            .Concat(AzureFunctionRules);

        foreach (var rule in allRules)
        {
            if (MatchesRule(node, rule))
            {
                yield return CreateCandidate(node, rule);
            }
        }
    }

    private bool MatchesRule(HypergraphNode node, PatternRule rule)
    {
        // Check inheritance/implementation
        if (node.Metadata.TryGetValue("BaseTypes", out var baseTypes))
        {
            if (baseTypes.Contains(rule.BaseTypeOrAttribute))
                return true;
        }

        // Check method names
        if (rule.MethodName != null && node.Name == rule.MethodName)
        {
            var parentClass = GetParentClass(node);
            if (parentClass?.Metadata.TryGetValue("BaseTypes", out var parentBases) == true)
            {
                if (parentBases.Contains(rule.BaseTypeOrAttribute))
                    return true;
            }
        }

        // Check attributes
        if (node.Metadata.TryGetValue("Attributes", out var attrs))
        {
            if (attrs.Contains(rule.BaseTypeOrAttribute))
                return true;
        }

        return false;
    }
}

public record PatternRule(
    string BaseTypeOrAttribute,
    string? MethodName,
    EntryPointType Type,
    float Confidence);
```

---

### 2. API Endpoint Discovery

#### Current State
- CSharpHyperedgeExtractor captures `[Route]` and `[HttpGet/Post/etc]` attributes
- Limited support for custom routing, ARM-style patterns, or dynamic route registration

#### Proposed Enhancement

**New Component: `ApiEndpointDiscoveryService`**

```csharp
namespace Ast.Discovery;

public class ApiEndpointDiscoveryService
{
    public async Task<ApiEndpointAnalysis> DiscoverEndpointsAsync(
        string repositoryPath,
        CancellationToken ct = default)
    {
        var analysis = new ApiEndpointAnalysis();

        // Strategy 1: Attribute-based routes
        var attributeRoutes = await DiscoverAttributeRoutesAsync(repositoryPath, ct);
        analysis.AddRoutes(attributeRoutes, RouteDiscoveryStrategy.Attribute);

        // Strategy 2: Convention-based routes
        var conventionRoutes = await DiscoverConventionRoutesAsync(repositoryPath, ct);
        analysis.AddRoutes(conventionRoutes, RouteDiscoveryStrategy.Convention);

        // Strategy 3: Custom route providers
        var customRoutes = await DiscoverCustomRouteProvidersAsync(repositoryPath, ct);
        analysis.AddRoutes(customRoutes, RouteDiscoveryStrategy.CustomProvider);

        // Strategy 4: Minimal API endpoints
        var minimalRoutes = await DiscoverMinimalApiEndpointsAsync(repositoryPath, ct);
        analysis.AddRoutes(minimalRoutes, RouteDiscoveryStrategy.MinimalApi);

        // Strategy 5: Dynamic/ARM-style routes
        var dynamicRoutes = await DiscoverDynamicRoutesAsync(repositoryPath, ct);
        analysis.AddRoutes(dynamicRoutes, RouteDiscoveryStrategy.Dynamic);

        return analysis.Consolidate();
    }
}

public record ApiEndpoint
{
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public required string ControllerName { get; init; }
    public required string ActionName { get; init; }
    public required string HttpMethod { get; init; }
    public required string RouteTemplate { get; init; }
    public string? ResolvedRoute { get; init; }
    public required RouteDiscoveryStrategy Strategy { get; init; }
    public required float Confidence { get; init; }
    public IReadOnlyList<ApiParameter> Parameters { get; init; } = [];
    public string? ReturnType { get; init; }
    public IReadOnlyList<string> Filters { get; init; } = [];
    public IReadOnlyList<string> Produces { get; init; } = [];
    public IReadOnlyList<string> Consumes { get; init; } = [];
}

public record ApiParameter(
    string Name,
    string Type,
    ParameterSource Source,  // Route, Query, Body, Header, Form
    bool IsRequired,
    string? DefaultValue);

public enum RouteDiscoveryStrategy
{
    Attribute,       // [Route], [HttpGet], etc.
    Convention,      // MapControllerRoute conventions
    CustomProvider,  // IDirectRouteProvider implementations
    MinimalApi,      // app.MapGet(), app.MapPost(), etc.
    Dynamic,         // Runtime-generated routes (ARM, OData)
    Configuration    // Routes defined in config files
}
```

**Attribute Route Discovery:**

```csharp
namespace Ast.Discovery;

public class AttributeRouteDiscoverer
{
    private readonly HypergraphData _hypergraph;

    public IEnumerable<ApiEndpoint> DiscoverRoutes()
    {
        // Find all controller classes
        var controllers = _hypergraph.Nodes
            .Where(n => n.NodeType == NodeType.Class)
            .Where(IsController);

        foreach (var controller in controllers)
        {
            var controllerRoute = ExtractControllerRoute(controller);

            // Find action methods
            var actions = GetMethodsInClass(controller)
                .Where(HasHttpMethodAttribute);

            foreach (var action in actions)
            {
                var endpoint = BuildEndpoint(controller, action, controllerRoute);
                yield return endpoint;
            }
        }
    }

    private bool IsController(HypergraphNode node)
    {
        // Check naming convention
        if (node.Name.EndsWith("Controller"))
            return true;

        // Check base class
        if (node.Metadata.TryGetValue("BaseTypes", out var bases))
        {
            if (bases.Contains("Controller") ||
                bases.Contains("ControllerBase") ||
                bases.Contains("ApiController"))
                return true;
        }

        // Check attributes
        if (node.Metadata.TryGetValue("Attributes", out var attrs))
        {
            if (attrs.Contains("[ApiController]") ||
                attrs.Contains("[Controller]"))
                return true;
        }

        return false;
    }

    private string? ExtractControllerRoute(HypergraphNode controller)
    {
        if (!controller.Metadata.TryGetValue("Attributes", out var attrs))
            return null;

        // [Route("api/[controller]")]
        var routeMatch = Regex.Match(attrs, @"\[Route(?:Prefix)?\s*\(\s*""([^""]+)""\s*\)\]");
        if (routeMatch.Success)
        {
            var template = routeMatch.Groups[1].Value;
            // Replace [controller] token
            template = template.Replace("[controller]",
                controller.Name.Replace("Controller", ""),
                StringComparison.OrdinalIgnoreCase);
            return template;
        }

        return null;
    }

    private ApiEndpoint BuildEndpoint(
        HypergraphNode controller,
        HypergraphNode action,
        string? controllerRoute)
    {
        var httpMethod = ExtractHttpMethod(action);
        var actionRoute = ExtractActionRoute(action);
        var fullRoute = CombineRoutes(controllerRoute, actionRoute);

        return new ApiEndpoint
        {
            FilePath = action.FilePath,
            LineNumber = action.LineStart,
            ControllerName = controller.Name,
            ActionName = action.Name,
            HttpMethod = httpMethod,
            RouteTemplate = fullRoute,
            ResolvedRoute = ResolveRouteTokens(fullRoute, action),
            Strategy = RouteDiscoveryStrategy.Attribute,
            Confidence = 0.95f,
            Parameters = ExtractParameters(action),
            ReturnType = action.Metadata.GetValueOrDefault("ReturnType"),
            Filters = ExtractFilters(action),
            Produces = ExtractProducesTypes(action),
            Consumes = ExtractConsumesTypes(action)
        };
    }

    private string ExtractHttpMethod(HypergraphNode action)
    {
        if (!action.Metadata.TryGetValue("Attributes", out var attrs))
            return "GET"; // Default

        if (attrs.Contains("[HttpGet")) return "GET";
        if (attrs.Contains("[HttpPost")) return "POST";
        if (attrs.Contains("[HttpPut")) return "PUT";
        if (attrs.Contains("[HttpDelete")) return "DELETE";
        if (attrs.Contains("[HttpPatch")) return "PATCH";
        if (attrs.Contains("[HttpHead")) return "HEAD";
        if (attrs.Contains("[HttpOptions")) return "OPTIONS";

        return "GET";
    }

    private IReadOnlyList<ApiParameter> ExtractParameters(HypergraphNode action)
    {
        var parameters = new List<ApiParameter>();

        if (!action.Metadata.TryGetValue("Parameters", out var paramStr))
            return parameters;

        // Parse parameter list: "string id, [FromBody] CreateRequest request, CancellationToken ct"
        var paramParts = SplitParameters(paramStr);

        foreach (var param in paramParts)
        {
            var source = DetermineParameterSource(param);
            var (type, name) = ParseTypeAndName(param);
            var isRequired = !param.Contains("?") && !param.Contains("= ");
            var defaultValue = ExtractDefaultValue(param);

            parameters.Add(new ApiParameter(name, type, source, isRequired, defaultValue));
        }

        return parameters;
    }

    private ParameterSource DetermineParameterSource(string param)
    {
        if (param.Contains("[FromBody]")) return ParameterSource.Body;
        if (param.Contains("[FromQuery]")) return ParameterSource.Query;
        if (param.Contains("[FromRoute]")) return ParameterSource.Route;
        if (param.Contains("[FromHeader]")) return ParameterSource.Header;
        if (param.Contains("[FromForm]")) return ParameterSource.Form;
        if (param.Contains("[FromServices]")) return ParameterSource.Services;

        // Infer from type for complex types
        var type = ParseTypeAndName(param).Type;
        if (IsComplexType(type))
            return ParameterSource.Body;

        return ParameterSource.Query; // Default for simple types
    }
}

public enum ParameterSource
{
    Route,
    Query,
    Body,
    Header,
    Form,
    Services
}
```

**Custom Route Provider Discovery:**

This handles patterns like `ResourceProviderDirectRouteProvider` for ARM-style URLs:

```csharp
namespace Ast.Discovery;

public class CustomRouteProviderDiscoverer
{
    private readonly HypergraphData _hypergraph;

    public IEnumerable<CustomRouteProviderInfo> DiscoverProviders()
    {
        // Find IDirectRouteProvider implementations
        var providers = _hypergraph.Nodes
            .Where(n => n.NodeType == NodeType.Class)
            .Where(ImplementsDirectRouteProvider);

        foreach (var provider in providers)
        {
            yield return AnalyzeProvider(provider);
        }
    }

    private bool ImplementsDirectRouteProvider(HypergraphNode node)
    {
        if (!node.Metadata.TryGetValue("BaseTypes", out var bases))
            return false;

        return bases.Contains("IDirectRouteProvider") ||
               bases.Contains("IDirectRouteFactory") ||
               bases.Contains("DirectRouteProviderBase");
    }

    private CustomRouteProviderInfo AnalyzeProvider(HypergraphNode provider)
    {
        var info = new CustomRouteProviderInfo
        {
            ClassName = provider.Name,
            FilePath = provider.FilePath,
            LineNumber = provider.LineStart
        };

        // Find GetDirectRoutes method
        var getRoutesMethod = FindMethod(provider, "GetDirectRoutes");
        if (getRoutesMethod != null)
        {
            info.RoutePatterns = ExtractRoutePatterns(getRoutesMethod);
        }

        // Find route attributes used
        info.CustomAttributes = FindCustomRouteAttributes(provider);

        // Determine route type
        info.RouteType = InferRouteType(provider, info.RoutePatterns);

        return info;
    }

    private RouteType InferRouteType(
        HypergraphNode provider,
        IReadOnlyList<string> patterns)
    {
        var code = provider.SourceCode ?? "";

        // ARM-style detection
        if (code.Contains("subscriptions") &&
            code.Contains("resourceGroups") &&
            code.Contains("providers"))
            return RouteType.AzureResourceManager;

        // OData detection
        if (code.Contains("ODataRoute") ||
            code.Contains("EnableQuery"))
            return RouteType.OData;

        // GraphQL detection
        if (code.Contains("GraphQL") ||
            code.Contains("Query(") ||
            code.Contains("Mutation("))
            return RouteType.GraphQL;

        return RouteType.Custom;
    }
}

public record CustomRouteProviderInfo
{
    public required string ClassName { get; init; }
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public IReadOnlyList<string> RoutePatterns { get; set; } = [];
    public IReadOnlyList<string> CustomAttributes { get; set; } = [];
    public RouteType RouteType { get; set; }
}

public enum RouteType
{
    Standard,
    AzureResourceManager,  // /subscriptions/{subId}/resourceGroups/{rg}/providers/...
    OData,                 // OData conventions
    GraphQL,               // GraphQL schema-driven
    Custom                 // Other custom patterns
}
```

**ARM-Style Route Analysis:**

```csharp
namespace Ast.Discovery;

public class ArmRouteAnalyzer
{
    // Pattern for ARM resource URLs
    private static readonly Regex ArmPattern = new(
        @"/subscriptions/\{[^}]+\}/resourceGroups/\{[^}]+\}/providers/([^/]+)/([^/]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ArmRouteInfo? AnalyzeRoute(string routeTemplate)
    {
        var match = ArmPattern.Match(routeTemplate);
        if (!match.Success)
            return null;

        return new ArmRouteInfo
        {
            ProviderNamespace = match.Groups[1].Value,
            ResourceType = match.Groups[2].Value,
            FullTemplate = routeTemplate,
            SegmentCount = routeTemplate.Count(c => c == '/'),
            HasNestedResource = routeTemplate.Count(s => s == '/') > 8,
            OperationType = InferOperationType(routeTemplate)
        };
    }

    private ArmOperationType InferOperationType(string template)
    {
        if (template.EndsWith("/listKeys", StringComparison.OrdinalIgnoreCase))
            return ArmOperationType.Action;
        if (template.Contains("{") && template.Split('/').Length % 2 == 0)
            return ArmOperationType.Resource;
        return ArmOperationType.Collection;
    }
}

public record ArmRouteInfo
{
    public required string ProviderNamespace { get; init; }
    public required string ResourceType { get; init; }
    public required string FullTemplate { get; init; }
    public int SegmentCount { get; init; }
    public bool HasNestedResource { get; init; }
    public ArmOperationType OperationType { get; init; }
}

public enum ArmOperationType
{
    Collection,  // GET /resources
    Resource,    // GET /resources/{id}
    Action       // POST /resources/{id}/action
}
```

**Minimal API Discovery:**

```csharp
namespace Ast.Discovery;

public class MinimalApiDiscoverer
{
    private readonly HypergraphData _hypergraph;

    public IEnumerable<ApiEndpoint> DiscoverEndpoints()
    {
        // Find app.Map* calls
        var mapCalls = _hypergraph.Edges
            .Where(e => e.Type == HyperedgeType.MethodInvocation)
            .Where(e => IsMapMethod(e));

        foreach (var call in mapCalls)
        {
            var endpoint = ParseMapCall(call);
            if (endpoint != null)
                yield return endpoint;
        }
    }

    private bool IsMapMethod(Hyperedge edge)
    {
        var callee = edge.GetNodeByRole("callee");
        if (callee == null) return false;

        var name = callee.Name;
        return name.StartsWith("Map") && (
            name == "MapGet" ||
            name == "MapPost" ||
            name == "MapPut" ||
            name == "MapDelete" ||
            name == "MapPatch" ||
            name == "MapMethods" ||
            name == "Map"
        );
    }

    private ApiEndpoint? ParseMapCall(Hyperedge edge)
    {
        var callee = edge.GetNodeByRole("callee");
        if (callee == null) return null;

        var httpMethod = callee.Name switch
        {
            "MapGet" => "GET",
            "MapPost" => "POST",
            "MapPut" => "PUT",
            "MapDelete" => "DELETE",
            "MapPatch" => "PATCH",
            _ => "ANY"
        };

        // Extract route from first argument
        var routeArg = edge.GetNodeByRole("arg_0");
        var route = routeArg?.SourceCode?.Trim('"') ?? "";

        // Extract handler from second argument
        var handlerArg = edge.GetNodeByRole("arg_1");
        var handlerInfo = AnalyzeHandler(handlerArg);

        return new ApiEndpoint
        {
            FilePath = edge.FilePath,
            LineNumber = edge.LineStart ?? 0,
            ControllerName = "MinimalApi",
            ActionName = handlerInfo?.Name ?? "Anonymous",
            HttpMethod = httpMethod,
            RouteTemplate = route,
            Strategy = RouteDiscoveryStrategy.MinimalApi,
            Confidence = 0.90f,
            Parameters = handlerInfo?.Parameters ?? [],
            ReturnType = handlerInfo?.ReturnType
        };
    }
}
```

---

### 3. Configuration-Based Discovery

#### Service Fabric Configuration Parser

```csharp
namespace Ast.Discovery;

public class ServiceFabricConfigParser
{
    public ServiceFabricManifestInfo? ParseServiceManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return null;

        var doc = XDocument.Load(manifestPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        return new ServiceFabricManifestInfo
        {
            ManifestPath = manifestPath,
            ServiceTypes = doc.Descendants(ns + "StatelessServiceType")
                .Concat(doc.Descendants(ns + "StatefulServiceType"))
                .Select(e => new ServiceTypeInfo
                {
                    Name = e.Attribute("ServiceTypeName")?.Value ?? "",
                    IsStateful = e.Name.LocalName == "StatefulServiceType"
                })
                .ToList(),
            CodePackages = doc.Descendants(ns + "CodePackage")
                .Select(e => new CodePackageInfo
                {
                    Name = e.Attribute("Name")?.Value ?? "",
                    Version = e.Attribute("Version")?.Value ?? "",
                    EntryPoint = e.Descendants(ns + "ExeHost")
                        .Select(h => h.Element(ns + "Program")?.Value)
                        .FirstOrDefault()
                })
                .ToList()
        };
    }

    public SettingsInfo? ParseSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath))
            return null;

        var doc = XDocument.Load(settingsPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var sections = doc.Descendants(ns + "Section")
            .ToDictionary(
                s => s.Attribute("Name")?.Value ?? "",
                s => s.Descendants(ns + "Parameter")
                    .ToDictionary(
                        p => p.Attribute("Name")?.Value ?? "",
                        p => p.Attribute("Value")?.Value ?? ""));

        return new SettingsInfo
        {
            SettingsPath = settingsPath,
            Sections = sections
        };
    }

    public IEnumerable<EntryPointCandidate> InferEntryPoints(
        ServiceFabricManifestInfo manifest,
        SettingsInfo? settings)
    {
        foreach (var serviceType in manifest.ServiceTypes)
        {
            // Look for ServiceAssembly setting
            var assemblyName = settings?.Sections
                .GetValueOrDefault("Service")?
                .GetValueOrDefault("ServiceAssembly");

            yield return new EntryPointCandidate
            {
                FilePath = manifest.ManifestPath,
                LineNumber = 0,
                ClassName = serviceType.Name,
                MethodName = "CreateServiceInstanceListeners",
                Type = EntryPointType.ServiceFabricService,
                Strategy = DiscoveryStrategy.Configuration,
                Confidence = 0.85f,
                FrameworkHint = "ServiceFabric",
                LifecycleMethods =
                [
                    "CreateServiceInstanceListeners",
                    "RunAsync",
                    "OnOpenAsync",
                    "OnCloseAsync"
                ]
            };
        }
    }
}

public record ServiceFabricManifestInfo
{
    public required string ManifestPath { get; init; }
    public IReadOnlyList<ServiceTypeInfo> ServiceTypes { get; init; } = [];
    public IReadOnlyList<CodePackageInfo> CodePackages { get; init; } = [];
}

public record ServiceTypeInfo
{
    public required string Name { get; init; }
    public bool IsStateful { get; init; }
}

public record CodePackageInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? EntryPoint { get; init; }
}

public record SettingsInfo
{
    public required string SettingsPath { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Sections { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();
}
```

#### Kubernetes/Docker Configuration Parser

```csharp
namespace Ast.Discovery;

public class ContainerConfigParser
{
    public ContainerEntryPointInfo? ParseDockerfile(string dockerfilePath)
    {
        if (!File.Exists(dockerfilePath))
            return null;

        var lines = File.ReadAllLines(dockerfilePath);
        var entrypoint = lines
            .FirstOrDefault(l => l.TrimStart().StartsWith("ENTRYPOINT", StringComparison.OrdinalIgnoreCase));
        var cmd = lines
            .FirstOrDefault(l => l.TrimStart().StartsWith("CMD", StringComparison.OrdinalIgnoreCase));

        return new ContainerEntryPointInfo
        {
            FilePath = dockerfilePath,
            EntryPoint = ParseDockerInstruction(entrypoint),
            Command = ParseDockerInstruction(cmd)
        };
    }

    public KubernetesDeploymentInfo? ParseDeployment(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            return null;

        // Parse YAML (using YamlDotNet or similar)
        var yaml = File.ReadAllText(yamlPath);
        // ... parse containers, commands, args, ports

        return new KubernetesDeploymentInfo
        {
            FilePath = yamlPath,
            // ... parsed info
        };
    }
}
```

---

### 4. Cross-Reference Analysis

#### Graph-Based Discovery

```csharp
namespace Ast.Discovery;

public class GraphBasedDiscoverer
{
    private readonly IGraphStore _graphStore;

    public async Task<IReadOnlyList<EntryPointCandidate>> DiscoverFromGraphAsync(
        string repositoryPath,
        CancellationToken ct = default)
    {
        var candidates = new List<EntryPointCandidate>();

        // Find nodes with no incoming calls (potential entry points)
        var rootNodes = await FindRootNodesAsync(ct);

        foreach (var node in rootNodes)
        {
            // Filter out obvious non-entry points
            if (IsLikelyEntryPoint(node))
            {
                candidates.Add(CreateCandidate(node));
            }
        }

        return candidates;
    }

    private async Task<IReadOnlyList<HypergraphNode>> FindRootNodesAsync(
        CancellationToken ct)
    {
        // Cypher query: Find classes with no incoming method calls
        var query = @"
            MATCH (n:CodeNode {type: 'Class'})
            WHERE NOT EXISTS {
                MATCH (caller:CodeNode)-[:PARTICIPATES_IN]->(:Hyperedge {type: 'MethodInvocation'})
                      -[:PARTICIPATES_IN]->(n)
            }
            AND n.name ENDS WITH 'Service' OR n.name ENDS WITH 'Host' OR n.name ENDS WITH 'Startup'
            RETURN n
        ";

        return await _graphStore.ExecuteQueryAsync<HypergraphNode>(query, ct);
    }

    private bool IsLikelyEntryPoint(HypergraphNode node)
    {
        // Has lifecycle methods
        var methods = GetMethodsInClass(node);
        var hasLifecycle = methods.Any(m =>
            m.Name is "Main" or "StartAsync" or "RunAsync" or
                      "Configure" or "Initialize" or "CreateHostBuilder");

        // Not a test class
        var isTest = node.Name.Contains("Test") ||
                     node.FilePath.Contains("Test");

        // Not abstract
        var isAbstract = node.Metadata.GetValueOrDefault("Modifiers")?.Contains("abstract") ?? false;

        return hasLifecycle && !isTest && !isAbstract;
    }
}
```

#### Embedding-Based Discovery

```csharp
namespace Ast.Discovery;

public class EmbeddingBasedDiscoverer
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingGenerator _embedder;

    public async Task<IReadOnlyList<EntryPointCandidate>> DiscoverSimilarToKnownPatternsAsync(
        CancellationToken ct = default)
    {
        // Known entry point patterns to search for
        var patterns = new[]
        {
            "public static void Main(string[] args) { CreateHostBuilder(args).Build().Run(); }",
            "public class Startup { public void Configure(IApplicationBuilder app) { } }",
            "protected override async Task RunAsync(CancellationToken cancellationToken)",
            "public override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()"
        };

        var candidates = new List<EntryPointCandidate>();

        foreach (var pattern in patterns)
        {
            var embedding = await _embedder.GenerateEmbeddingAsync(pattern, ct);
            var similar = await _vectorStore.SearchSimilarNodesAsync(
                embedding,
                topK: 5,
                filter: new NodeFilter { Type = NodeType.Method },
                ct: ct);

            foreach (var result in similar.Where(r => r.Score > 0.85f))
            {
                candidates.Add(new EntryPointCandidate
                {
                    FilePath = result.Node.FilePath,
                    LineNumber = result.Node.LineStart,
                    ClassName = result.Node.Scope ?? "Unknown",
                    MethodName = result.Node.Name,
                    Type = InferEntryPointType(pattern),
                    Strategy = DiscoveryStrategy.Embedding,
                    Confidence = result.Score
                });
            }
        }

        return candidates.DistinctBy(c => $"{c.FilePath}:{c.LineNumber}").ToList();
    }
}
```

---

## Implementation Phases

### Phase 1: Entry Point Detection (Week 1-2)

**Priority: HIGH**

| Task | Effort | Dependencies |
|------|--------|--------------|
| Create `EntryPointDiscoveryService` | 2 days | None |
| Implement `EntryPointHeuristics` | 2 days | None |
| Add Service Fabric config parser | 2 days | None |
| Integrate with existing extractors | 1 day | Above |
| Unit tests | 2 days | Above |

**Deliverables:**
- [ ] `Ast.Discovery` project created
- [ ] Entry point detection for Service Fabric, ASP.NET Core, Worker Services
- [ ] Configuration file parsing (ServiceManifest.xml, Settings.xml)
- [ ] 30+ unit tests

### Phase 2: API Endpoint Discovery (Week 3-4)

**Priority: HIGH**

| Task | Effort | Dependencies |
|------|--------|--------------|
| Create `ApiEndpointDiscoveryService` | 2 days | Phase 1 |
| Implement attribute route discovery | 2 days | None |
| Implement minimal API discovery | 2 days | None |
| Add custom route provider analysis | 3 days | None |
| ARM-style route analysis | 2 days | Above |
| Unit tests | 2 days | Above |

**Deliverables:**
- [ ] Full API endpoint discovery
- [ ] Support for attribute routes, minimal APIs, custom providers
- [ ] ARM-style URL pattern recognition
- [ ] 40+ unit tests

### Phase 3: Graph Integration (Week 5)

**Priority: MEDIUM**

| Task | Effort | Dependencies |
|------|--------|--------------|
| Add discovery results to HypergraphNode metadata | 1 day | Phase 1, 2 |
| Create specialized hyperedge types | 1 day | Above |
| Neo4j schema updates | 1 day | Above |
| Query service enhancements | 2 days | Above |

**Deliverables:**
- [ ] Entry points and endpoints stored in graph
- [ ] New edge types: `EntryPointOf`, `ServesEndpoint`, `RoutesTo`
- [ ] Enhanced query capabilities

### Phase 4: CLI Integration (Week 6)

**Priority: MEDIUM**

| Task | Effort | Dependencies |
|------|--------|--------------|
| Add `discover` command to CLI | 2 days | Phase 1, 2 |
| Add endpoint listing command | 1 day | Phase 2 |
| JSON/table output formats | 1 day | Above |
| Documentation | 1 day | Above |

**Deliverables:**
- [ ] `ast-cli discover entrypoints <repo>` command
- [ ] `ast-cli discover endpoints <repo>` command
- [ ] Structured output for tooling integration

---

## Technical Specifications

### New Project: Ast.Discovery

```
src/ast/Ast.Discovery/
├── Ast.Discovery.csproj
├── EntryPointDiscoveryService.cs
├── ApiEndpointDiscoveryService.cs
├── Heuristics/
│   ├── EntryPointHeuristics.cs
│   ├── PatternRules.cs
│   └── FrameworkDetector.cs
├── Parsers/
│   ├── ServiceFabricConfigParser.cs
│   ├── KubernetesConfigParser.cs
│   └── DockerfileParser.cs
├── Routes/
│   ├── AttributeRouteDiscoverer.cs
│   ├── MinimalApiDiscoverer.cs
│   ├── CustomRouteProviderDiscoverer.cs
│   └── ArmRouteAnalyzer.cs
├── Graph/
│   ├── GraphBasedDiscoverer.cs
│   └── EmbeddingBasedDiscoverer.cs
├── Models/
│   ├── EntryPointCandidate.cs
│   ├── ApiEndpoint.cs
│   └── DiscoveryAnalysis.cs
└── ServiceCollectionExtensions.cs
```

### Dependencies

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ast.Core\Ast.Core.csproj" />
    <ProjectReference Include="..\Ast.GraphDb\Ast.GraphDb.csproj" />
    <ProjectReference Include="..\Ast.VectorDb\Ast.VectorDb.csproj" />
    <ProjectReference Include="..\Ast.Embedding\Ast.Embedding.csproj" />
    <ProjectReference Include="..\Ast.Lsp\Ast.Lsp.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="YamlDotNet" Version="15.1.0" />
  </ItemGroup>
</Project>
```

### Configuration

```csharp
public class DiscoveryConfiguration
{
    public bool UseGraphTraversal { get; set; } = true;
    public bool UseEmbeddingSearch { get; set; } = true;
    public bool ParseConfigurationFiles { get; set; } = true;
    public float MinimumConfidenceThreshold { get; set; } = 0.70f;

    public EntryPointOptions EntryPoints { get; set; } = new();
    public EndpointOptions Endpoints { get; set; } = new();
}

public class EntryPointOptions
{
    public string[] ExcludePatterns { get; set; } = ["*Test*", "*Mock*", "*Fake*"];
    public string[] FrameworkHints { get; set; } = ["ServiceFabric", "AspNetCore", "AzureFunctions"];
}

public class EndpointOptions
{
    public bool IncludeInternalRoutes { get; set; } = false;
    public bool ResolveRouteTokens { get; set; } = true;
    public string[] ExcludeControllers { get; set; } = ["Health", "Diagnostics"];
}
```

---

## Integration with Existing Infrastructure

### HypergraphNode Extensions

Add new metadata fields to support discovery results:

```csharp
// Extend HypergraphNode metadata
public static class HypergraphNodeExtensions
{
    public static bool IsEntryPoint(this HypergraphNode node)
        => node.Metadata.ContainsKey("IsEntryPoint") &&
           node.Metadata["IsEntryPoint"] == "true";

    public static EntryPointType? GetEntryPointType(this HypergraphNode node)
    {
        if (node.Metadata.TryGetValue("EntryPointType", out var type))
            return Enum.Parse<EntryPointType>(type);
        return null;
    }

    public static IReadOnlyList<string> GetServedEndpoints(this HypergraphNode node)
    {
        if (node.Metadata.TryGetValue("ServedEndpoints", out var endpoints))
            return endpoints.Split(',');
        return [];
    }
}
```

### New Hyperedge Types

```csharp
// Add to HyperedgeType enum
public enum HyperedgeType
{
    // ... existing types ...

    // Discovery-related edges
    EntryPointOf,       // Service → Application
    ServesEndpoint,     // Controller → Endpoint
    RoutesTo,           // Route → Controller.Action
    ConfiguresRouting,  // RouteProvider → Routes
    HostedBy,           // Service → Host (Service Fabric, k8s, etc.)
}
```

### Query Enhancements

```csharp
// Add to ICodeQuery interface
public interface ICodeQuery
{
    // ... existing methods ...

    INodeQuery EntryPoints();
    INodeQuery Endpoints();
    INodeQuery ForRoute(string routePattern);
    INodeQuery InController(string controllerName);
}

// Usage example
var results = await query
    .Endpoints()
    .OfHttpMethod("POST")
    .InController("Update*")
    .WithParameter("subscriptionId")
    .ExecuteAsync();
```

---

## Success Criteria

### Functional Requirements

| Requirement | Metric | Target |
|-------------|--------|--------|
| Entry point detection accuracy | Precision | > 90% |
| API endpoint discovery | Recall | > 95% |
| False positive rate | Entry points | < 10% |
| Configuration parsing | File types supported | 5+ |
| Framework support | Frameworks recognized | 8+ |

### Supported Frameworks

- [ ] ASP.NET Core (Web API, MVC, Minimal APIs)
- [ ] Service Fabric (Stateless, Stateful)
- [ ] Azure Functions
- [ ] Worker Services / BackgroundService
- [ ] gRPC Services
- [ ] SignalR Hubs
- [ ] OData Endpoints
- [ ] ARM Resource Provider APIs

### Performance Requirements

| Operation | Target |
|-----------|--------|
| Entry point discovery (medium repo) | < 5 seconds |
| Full endpoint discovery | < 10 seconds |
| Incremental update (single file) | < 500ms |

---

## References

- [OpenCode Session Log Analysis](/mnt/e/work/hci/update/docs/opencode/service.md)
- [AST Implementation Plan](./AST_IMPLEMENTATION_PLAN.md)
- [LSP Migration Plan](./LSP_MIGRATION_PLAN.md)
- [Embedding Model Approach](./embedding_model_approach.md)
- [Code Understanding Comparison](./code_understanding_comparison.md)
