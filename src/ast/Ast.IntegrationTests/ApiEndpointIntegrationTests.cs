// -----------------------------------------------------------------------
// <copyright file="ApiEndpointIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.IntegrationTests;

using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIntelligence.Analyzers;
using CodeIntelligence.Services;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

/// <summary>
/// Integration tests for API discovery by tracing from startup/bootstrap code.
/// This tests the REAL API wiring, not just pattern matching on controllers.
/// </summary>
[TestClass]
public class ApiEndpointIntegrationTests
{
    // Update service repo path
    private static readonly string UpdateRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    // Common infra repo path for dependency resolution
    private static readonly string CommonInfraRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\common\infra"
        : "/mnt/e/work/hub/common/infra";

    private static readonly string OutputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "api-discovery");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static ILogger<ApiEndpointIntegrationTests> logger = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<ApiEndpointIntegrationTests>();

        if (!Directory.Exists(OutputDir))
        {
            Directory.CreateDirectory(OutputDir);
        }
    }

    #region API Infrastructure Discovery Tests

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_FindsStartupConfiguration()
    {
        // Arrange
        var service = new ApiDiscoveryService();

        // Act - find all startup files using dependency repos for better detection
        var startupFiles = await service.FindStartupFilesAsync(UpdateRepoPath, new[] { CommonInfraRepoPath });

        // Assert
        Assert.IsTrue(startupFiles.Count > 0, "Should find startup configuration files");

        logger.LogInformation("Found {Count} startup files:\n{Files}",
            startupFiles.Count,
            string.Join("\n", startupFiles.Select(f => Path.GetRelativePath(UpdateRepoPath, f))));

        // Should find the main UpdateServiceStartup
        Assert.IsTrue(startupFiles.Any(f => f.Contains("UpdateServiceStartup")),
            "Should find UpdateServiceStartup.cs");

        await SaveResultsAsync("StartupFiles.json", new
        {
            Count = startupFiles.Count,
            Files = startupFiles.Select(f => Path.GetRelativePath(UpdateRepoPath, f))
        });
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_AnalyzesHttpConfigurationSetup()
    {
        // Arrange - discover startup files dynamically instead of hardcoding
        var service = new ApiDiscoveryService();
        var startupFiles = await service.FindStartupFilesAsync(UpdateRepoPath, new[] { CommonInfraRepoPath });

        if (startupFiles.Count == 0)
        {
            Assert.Inconclusive("No startup files discovered in repo");
            return;
        }

        logger.LogInformation("Discovered {Count} startup files", startupFiles.Count);

        // Find a startup file with full HTTP infrastructure (config methods + routes + handlers)
        string? startupPath = null;
        ApiInfrastructureInfo? infraInfo = null;
        var analyzer = new ApiInfrastructureAnalyzer();

        foreach (var candidate in startupFiles)
        {
            var code = await File.ReadAllTextAsync(candidate);
            var tree = CSharpSyntaxTree.ParseText(code);
            var candidateInfo = analyzer.Analyze(tree, candidate);

            // Look for a complete startup file with HttpConfiguration, routes, and handlers
            if (candidateInfo.HttpConfigurationMethods.Count > 0 &&
                candidateInfo.RouteRegistrations.Count > 0)
            {
                startupPath = candidate;
                infraInfo = candidateInfo;
                logger.LogInformation("Found complete startup with HttpConfiguration: {Path}", candidate);
                break;
            }
        }

        if (startupPath == null || infraInfo == null)
        {
            Assert.Inconclusive("No startup file with HttpConfiguration and route registrations found");
            return;
        }

        // Assert
        logger.LogInformation("HttpConfiguration methods: {Methods}",
            string.Join(", ", infraInfo.HttpConfigurationMethods.Select(m => m.MethodName)));

        Assert.IsTrue(infraInfo.HttpConfigurationMethods.Count > 0,
            "Should detect HttpConfiguration setup methods");

        // Should detect route registration
        Assert.IsTrue(infraInfo.RouteRegistrations.Count > 0,
            "Should detect route registrations");

        logger.LogInformation("Route registrations:\n{Routes}",
            string.Join("\n", infraInfo.RouteRegistrations.Select(r =>
                $"  {r.RegistrationType}: {r.RouteProvider ?? r.RawExpression}")));

        // Should detect message handlers (auth pipeline)
        Assert.IsTrue(infraInfo.MessageHandlers.Count >= 2,
            $"Expected at least 2 message handlers (tracing + auth), found {infraInfo.MessageHandlers.Count}");

        logger.LogInformation("Message handlers (auth pipeline):\n{Handlers}",
            string.Join("\n", infraInfo.MessageHandlers.Select(h =>
                $"  [{h.InsertIndex}] {h.HandlerExpression}")));

        // Should detect service replacements (custom resolvers)
        Assert.IsTrue(infraInfo.ServiceReplacements.Count > 0,
            "Should detect service replacements for custom resolvers");

        logger.LogInformation("Service replacements:\n{Replacements}",
            string.Join("\n", infraInfo.ServiceReplacements.Select(s =>
                $"  {s.ServiceType} -> {s.Implementation}")));

        await SaveResultsAsync("UpdateServiceStartup_Infrastructure.json", infraInfo);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_TracesControllerDIRegistrations()
    {
        // Arrange - discover startup files and DI config files by tracing from entry points
        var service = new ApiDiscoveryService();
        var startupFiles = await service.FindStartupFilesAsync(UpdateRepoPath, new[] { CommonInfraRepoPath });

        if (startupFiles.Count == 0)
        {
            Assert.Inconclusive("No startup files discovered in repo");
            return;
        }

        logger.LogInformation("Discovered {Count} startup files", startupFiles.Count);

        // Find DI config files by tracing from startup files
        var diConfigFiles = await service.FindDIConfigFilesAsync(UpdateRepoPath, startupFiles);

        if (diConfigFiles.Count == 0)
        {
            Assert.Inconclusive("No DI configuration files discovered");
            return;
        }

        var analyzer = new ApiInfrastructureAnalyzer();

        // ========================================
        // Step 1: Analyze Route Registration Mechanism
        // ========================================
        AttributeRoutingSetup? routingSetup = null;
        foreach (var startupFile in startupFiles)
        {
            var code = await File.ReadAllTextAsync(startupFile);
            var tree = CSharpSyntaxTree.ParseText(code);
            routingSetup = analyzer.AnalyzeAttributeRoutingSetup(tree, startupFile);
            if (routingSetup != null)
            {
                break;
            }
        }

        Assert.IsNotNull(routingSetup, "Should find MapHttpAttributeRoutes setup");
        logger.LogInformation("Route Registration: MapHttpAttributeRoutes at line {Line}", routingSetup.LineNumber);

        Assert.IsNotNull(routingSetup.CustomRouteProviderType, "Should use custom route provider");
        routingSetup.CustomRouteProviderType.Should().Contain("ResourceProviderDirectRouteProvider",
            "Should use ResourceProviderDirectRouteProvider for dynamic route building");

        Assert.IsNotNull(routingSetup.BaseControllerType, "Should specify base controller type");
        routingSetup.BaseControllerType.Should().Contain("UnauthorizedResourceControllerBase",
            "Should use UnauthorizedResourceControllerBase as the base controller");

        logger.LogInformation("Route Provider: {Provider} with base type {BaseType}",
            routingSetup.CustomRouteProviderType, routingSetup.BaseControllerType);

        // ========================================
        // Step 2: Analyze Controller Registrations
        // ========================================
        var allRegistrations = new List<ControllerRegistration>();
        foreach (var diFile in diConfigFiles)
        {
            var code = await File.ReadAllTextAsync(diFile);
            var tree = CSharpSyntaxTree.ParseText(code);
            var registrations = analyzer.AnalyzeControllerRegistrations(tree, diFile);
            allRegistrations.AddRange(registrations);
        }

        logger.LogInformation("Found {Count} DI config files with {RegCount} controller registrations",
            diConfigFiles.Count, allRegistrations.Count);

        // Assert expected controller registrations
        var expectedControllers = new[]
        {
            ("UpdateLocationResourceController", "UpdateLocationResourceControllerImplementation"),
            ("UpdateResourceController", "UpdateResourceControllerImplementation"),
            ("UpdateRunResourceController", "UpdateRunResourceControllerImplementation"),
            ("DiscoveryDiagnosticController", "DiscoveryDiagnosticControllerImplementation"),
            ("CacheController", "CacheControllerImplementation"),
            ("UpdatePrepareController", "UpdatePrepareControllerImplementation"),
            ("DynamicConfigurationController", "DynamicConfigurationControllerImplementation"),
            ("DiscoveryConfigController", "DiscoveryConfigControllerImplementation"),
            ("FeatureFlagController", "FeatureFlagControllerImplementation"),
            ("UrpOperationMetadataController", "UrpOperationMetadataControllerImplementation"),
        };

        foreach (var (contract, impl) in expectedControllers)
        {
            allRegistrations.Should().Contain(r => r.InterfaceType.Contains(contract),
                $"Should register {contract}");
            allRegistrations.Should().Contain(r => r.ImplementationType.Contains(impl),
                $"Should have implementation {impl}");
        }

        logger.LogInformation("Controller Hierarchy:\n{Hierarchy}",
            string.Join("\n", expectedControllers.Select(c => $"  {c.Item1} → {c.Item2}")));

        // ========================================
        // Step 3: Analyze Controller Endpoints
        // ========================================
        // Trace controller files from DI registrations instead of pattern matching on file names
        var allEndpoints = new List<EndpointInfo>();
        var allControllerInfo = new List<ControllerInfo>();

        // Get implementation type names from DI registrations
        var implementationTypes = allRegistrations
            .Select(r => r.ImplementationType)
            .Distinct()
            .ToHashSet();

        // Also get interface types (controller contracts)
        var interfaceTypes = allRegistrations
            .Select(r => r.InterfaceType)
            .Distinct()
            .ToHashSet();

        // Scan all C# files and check if they define any of the registered types
        var csFiles = Directory.GetFiles(UpdateRepoPath, "*.cs", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
          .ToList();

        var controllerFiles = new List<string>();

        foreach (var file in csFiles)
        {
            try
            {
                var code = await File.ReadAllTextAsync(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                // Check if this file contains any of our registered controller types
                var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                foreach (var classDecl in classDecls)
                {
                    var className = classDecl.Identifier.Text;

                    // Check if this class is a registered implementation or interface
                    if (implementationTypes.Any(t => t.Contains(className)) ||
                        interfaceTypes.Any(t => t.Contains(className)))
                    {
                        controllerFiles.Add(file);
                        break;
                    }

                    // Also check if it inherits from UnauthorizedResourceControllerBase (from route setup)
                    if (classDecl.BaseList != null)
                    {
                        var inheritsFromBase = classDecl.BaseList.Types
                            .Any(t => t.Type.ToString().Contains(routingSetup.BaseControllerType ?? "ResourceControllerBase"));

                        if (inheritsFromBase)
                        {
                            controllerFiles.Add(file);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        logger.LogInformation("Found {Count} controller files by tracing from DI registrations", controllerFiles.Count);

        foreach (var controllerFile in controllerFiles.Distinct())
        {
            var code = await File.ReadAllTextAsync(controllerFile);
            var tree = CSharpSyntaxTree.ParseText(code);
            var endpointInfo = analyzer.AnalyzeControllerEndpoints(tree, controllerFile);

            foreach (var controller in endpointInfo.Controllers)
            {
                allControllerInfo.Add(controller);
                allEndpoints.AddRange(controller.Endpoints);
            }
        }

        logger.LogInformation("Found {Count} controllers with {EndpointCount} endpoints",
            allControllerInfo.Count, allEndpoints.Count);

        // ========================================
        // Step 3b: Build ARM Routes for Controllers
        // ========================================
        // Scan model files to get ResourceTypeName constants
        var modelFiles = csFiles
            .Where(f => f.Contains("\\Models\\") || f.Contains("\\Contract\\"))
            .ToList();

        var modelFileTuples = new List<(Microsoft.CodeAnalysis.SyntaxTree, string)>();
        foreach (var modelFile in modelFiles)
        {
            try
            {
                var code = await File.ReadAllTextAsync(modelFile);
                var tree = CSharpSyntaxTree.ParseText(code);
                modelFileTuples.Add((tree, modelFile));
            }
            catch
            {
                // Skip unreadable files
            }
        }

        var resourceTypeMap = analyzer.BuildResourceTypeNameMap(modelFileTuples);
        logger.LogInformation("Built resource type map with {Count} entries", resourceTypeMap.Count);

        // Scan constants files (e.g., UrpConstants.cs)
        var constantFiles = csFiles
            .Where(f => f.Contains("Constants.cs") || f.Contains("Common"))
            .ToList();

        var constantFileTuples = new List<(Microsoft.CodeAnalysis.SyntaxTree, string)>();
        foreach (var constantFile in constantFiles)
        {
            try
            {
                var code = await File.ReadAllTextAsync(constantFile);
                var tree = CSharpSyntaxTree.ParseText(code);
                constantFileTuples.Add((tree, constantFile));
            }
            catch
            {
                // Skip unreadable files
            }
        }

        var constantMap = analyzer.BuildConstantMap(constantFileTuples);
        logger.LogInformation("Built constant map with {Count} entries", constantMap.Count);

        // Build full ARM routes for each controller's endpoints
        foreach (var controller in allControllerInfo)
        {
            // Resolve namespace from constant expression
            if (controller.ResourceRouting != null)
            {
                var resolvedNamespace = analyzer.ResolveNamespace(
                    controller.ResourceRouting.NamespaceExpression, constantMap);
                if (!string.IsNullOrEmpty(resolvedNamespace) && resolvedNamespace != controller.ResourceRouting.Namespace)
                {
                    controller.ResourceRouting.Namespace = resolvedNamespace;
                }
            }

            // Resolve RoutePrefix constants
            if (!string.IsNullOrEmpty(controller.RoutePrefix))
            {
                controller.RoutePrefix = analyzer.ResolveRouteConstants(controller.RoutePrefix, constantMap);
            }

            foreach (var endpoint in controller.Endpoints)
            {
                // Resolve operation name constants
                if (!string.IsNullOrEmpty(endpoint.OperationName))
                {
                    endpoint.OperationName = analyzer.ResolveRouteConstants(endpoint.OperationName, constantMap);
                }

                // Resolve route template constants
                if (!string.IsNullOrEmpty(endpoint.RouteTemplate))
                {
                    endpoint.RouteTemplate = analyzer.ResolveRouteConstants(endpoint.RouteTemplate, constantMap);
                }

                var fullRoute = analyzer.BuildArmEndpointRoute(
                    controller, endpoint, allControllerInfo, resourceTypeMap);

                if (!string.IsNullOrEmpty(fullRoute))
                {
                    // Also resolve any remaining constants in the full route
                    endpoint.FullRoute = analyzer.ResolveRouteConstants(fullRoute, constantMap);
                }
            }
        }

        // Assert we found controllers that inherit from UnauthorizedResourceControllerBase
        var baseControllerDerived = allControllerInfo
            .Where(c => c.BaseType?.Contains("UnauthorizedResourceControllerBase") == true ||
                        c.BaseType?.Contains("ResourceControllerBase") == true)
            .ToList();

        baseControllerDerived.Should().NotBeEmpty(
            "Should find controllers derived from UnauthorizedResourceControllerBase");

        logger.LogInformation("Controllers derived from UnauthorizedResourceControllerBase:\n{Controllers}",
            string.Join("\n", baseControllerDerived.Select(c => $"  {c.ClassName} : {c.BaseType}")));

        // Assert we found endpoints with various HTTP methods
        var getEndpoints = allEndpoints.Where(e => e.HttpMethod == "GET").ToList();
        var postEndpoints = allEndpoints.Where(e => e.HttpMethod == "POST").ToList();
        var putEndpoints = allEndpoints.Where(e => e.HttpMethod == "PUT").ToList();
        var deleteEndpoints = allEndpoints.Where(e => e.HttpMethod == "DELETE").ToList();

        getEndpoints.Should().NotBeEmpty("Should have GET endpoints");
        postEndpoints.Should().NotBeEmpty("Should have POST endpoints (operations)");

        logger.LogInformation("Endpoint summary: GET={Get}, POST={Post}, PUT={Put}, DELETE={Delete}",
            getEndpoints.Count, postEndpoints.Count, putEndpoints.Count, deleteEndpoints.Count);

        // Assert custom route attributes are detected
        var customRouteEndpoints = allEndpoints
            .Where(e => !string.IsNullOrEmpty(e.CustomRouteAttribute))
            .ToList();

        logger.LogInformation("Endpoints with custom route attributes: {Count}", customRouteEndpoints.Count);

        // ========================================
        // Step 4: Assert specific route-to-controller-method mappings
        // ========================================
        // Note: Some methods like GetAllResources, GetResource are in the base class UnauthorizedResourceControllerBase
        // and are dynamically routed by ResourceProviderDirectRouteProvider at runtime.
        // Here we verify the methods explicitly defined in each controller.

        var expectedGetEndpoints = new (string Controller, string Method)[]
        {
            // UpdateResource shortcuts
            ("UpdateResourceController", "GetAllResourcesByShortCut"),
            // UpdateRun shortcuts
            ("UpdateRunResourceController", "GetAllResourcesByShortCut"),
            // Operations metadata
            ("UrpOperationMetadataController", "GetOperationsMetadata"),
            // Cache endpoints
            ("CacheController", "GetCachedItem"),
            ("CacheController", "GetCachedItems"),
            ("CacheController", "ExportCacheSettings"),
            ("CacheController", "ReportStats"),
            ("CacheController", "GetPackageVersions"),
            // Other endpoints
            ("UpdatePrepareController", "GetPrepareWorkflow"),
            ("DynamicConfigurationController", "GetDynamicConfigurationValues"),
            ("DiscoveryConfigController", "GetDiscoveryConfig"),
            ("FeatureFlagController", "GetFeatureFlags"),
        };

        var expectedPostEndpoints = new (string Controller, string Method)[]
        {
            // Operations (defined in Implementation classes)
            ("UpdateLocationResourceController", "CheckHealth"),
            ("UpdateResourceController", "ApplyUpdate"),
            ("UpdateResourceController", "Prepare"),
            ("UpdateResourceController", "CheckHealth"),
            ("UpdateRunResourceController", "Rerun"),
            // Cache
            ("CacheController", "ImportCacheSettings"),
            // Configuration
            ("DynamicConfigurationController", "SetDynamicConfigurationValue"),
            ("DiscoveryConfigController", "SetDiscoveryConfig"),
            ("FeatureFlagController", "SetFeatureFlag"),
        };

        var expectedPutEndpoints = new (string Controller, string Method)[]
        {
            ("UpdateResourceController", "AddUpdate"),
            ("CacheController", "SetStampVersion"),
            ("CacheController", "SetSbeVersion"),
        };

        var expectedDeleteEndpoints = new (string Controller, string Method)[]
        {
            ("CacheController", "ClearCachedItems"),
            ("UpdatePrepareController", "ClearPrepareWorkflow"),
        };

        // Verify GET endpoints
        // Log actual endpoint details for debugging before assertions
        logger.LogInformation("Detected GET endpoints:\n{Endpoints}",
            string.Join("\n", getEndpoints.Select(e =>
                $"  {e.ControllerClass}.{e.MethodName} -> {e.FullRoute ?? e.RouteTemplate ?? "(dynamic)"}")));

        // Verify GET endpoints - note that base class methods may be in the base class or Implementation class
        logger.LogInformation("Verifying GET endpoint mappings...");
        foreach (var (controller, method) in expectedGetEndpoints)
        {
            // Check both the controller name and its implementation
            var found = getEndpoints.Any(e =>
                (e.ControllerClass?.Contains(controller) == true ||
                 e.ControllerClass?.Contains(controller.Replace("Controller", "ControllerImplementation")) == true ||
                 e.ControllerClass?.Contains("ResourceControllerBase") == true) &&
                e.MethodName == method);

            found.Should().BeTrue(
                $"GET endpoint {controller}.{method} should be detected");
        }

        logger.LogInformation("All {Count} expected GET endpoints verified", expectedGetEndpoints.Length);

        // Verify POST endpoints
        logger.LogInformation("Verifying POST endpoint mappings...");
        logger.LogInformation("Detected POST endpoints:\n{Endpoints}",
            string.Join("\n", postEndpoints.Select(e =>
                $"  {e.ControllerClass}.{e.MethodName} -> {e.FullRoute ?? e.RouteTemplate ?? "(dynamic)"}")));

        foreach (var (controller, method) in expectedPostEndpoints)
        {
            var found = postEndpoints.Any(e =>
                (e.ControllerClass?.Contains(controller) == true ||
                 e.ControllerClass?.Contains(controller.Replace("Controller", "ControllerImplementation")) == true) &&
                e.MethodName == method);

            found.Should().BeTrue(
                $"POST endpoint {controller}.{method} should be detected");
        }

        logger.LogInformation("All {Count} expected POST endpoints verified", expectedPostEndpoints.Length);

        // Verify PUT endpoints
        logger.LogInformation("Verifying PUT endpoint mappings...");
        logger.LogInformation("Detected PUT endpoints:\n{Endpoints}",
            string.Join("\n", putEndpoints.Select(e =>
                $"  {e.ControllerClass}.{e.MethodName} -> {e.FullRoute ?? e.RouteTemplate ?? "(dynamic)"}")));

        foreach (var (controller, method) in expectedPutEndpoints)
        {
            var found = putEndpoints.Any(e =>
                (e.ControllerClass?.Contains(controller) == true ||
                 e.ControllerClass?.Contains(controller.Replace("Controller", "ControllerImplementation")) == true) &&
                e.MethodName == method);

            found.Should().BeTrue(
                $"PUT endpoint {controller}.{method} should be detected");
        }

        logger.LogInformation("All {Count} expected PUT endpoints verified", expectedPutEndpoints.Length);

        // Verify DELETE endpoints
        logger.LogInformation("Verifying DELETE endpoint mappings...");
        logger.LogInformation("Detected DELETE endpoints:\n{Endpoints}",
            string.Join("\n", deleteEndpoints.Select(e =>
                $"  {e.ControllerClass}.{e.MethodName} -> {e.FullRoute ?? e.RouteTemplate ?? "(dynamic)"}")));

        foreach (var (controller, method) in expectedDeleteEndpoints)
        {
            var found = deleteEndpoints.Any(e =>
                (e.ControllerClass?.Contains(controller) == true ||
                 e.ControllerClass?.Contains(controller.Replace("Controller", "ControllerImplementation")) == true) &&
                e.MethodName == method);

            found.Should().BeTrue(
                $"DELETE endpoint {controller}.{method} should be detected");
        }

        logger.LogInformation("All {Count} expected DELETE endpoints verified", expectedDeleteEndpoints.Length);

        // ========================================
        // Step 5: Assert Concrete Route Patterns
        // ========================================
        // Verify specific ARM routes are built correctly following ResourceProviderDirectRouteProvider pattern
        logger.LogInformation("Verifying concrete ARM route patterns...");

        var expectedRoutePatterns = new (string Controller, string Method, string RoutePattern)[]
        {
            // UpdateLocation endpoints
            ("UpdateLocationResourceController", "CheckHealth", "providers/Microsoft.Update.Admin/updateLocations/{name}/checkHealth"),

            // Update endpoints (with parent: updateLocations)
            ("UpdateResourceController", "GetAllResourcesByShortCut", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates"),
            ("UpdateResourceController", "AddUpdate", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates/{name}"),
            ("UpdateResourceController", "ApplyUpdate", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates/{name}/apply"),
            ("UpdateResourceController", "Prepare", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates/{name}/prepare"),
            ("UpdateResourceController", "CheckHealth", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates/{name}/checkHealth"),

            // UpdateRun endpoints (with grandparent: updateLocations, parent: updates)
            ("UpdateRunResourceController", "GetAllResourcesByShortCut", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates/{name}/updateRuns"),
            ("UpdateRunResourceController", "Rerun", "providers/Microsoft.Update.Admin/updateLocations/{name}/updates/{name}/updateRuns/{name}/rerun"),

            // Non-ARM routes with resolved constants
            ("CacheController", "ClearCachedItems", "caches/{typeName}"),
            ("CacheController", "GetCachedItem", "caches/{typeName}"),
            ("CacheController", "ReportStats", "caches/stats"),
            ("UpdatePrepareController", "GetPrepareWorkflow", "updatePrep/{name}"),
            ("UpdatePrepareController", "ClearPrepareWorkflow", "updatePrep/{name}"),
            ("DynamicConfigurationController", "GetDynamicConfigurationValues", "dynamicConfiguration"),
            ("DiscoveryConfigController", "GetDiscoveryConfig", "discoveryConfig"),
            ("FeatureFlagController", "GetFeatureFlags", "featureFlag"),
        };

        foreach (var (controller, method, expectedRoute) in expectedRoutePatterns)
        {
            var endpoint = allEndpoints.FirstOrDefault(e =>
                (e.ControllerClass?.Contains(controller) == true ||
                 e.ControllerClass?.Contains(controller.Replace("Controller", "ControllerImplementation")) == true) &&
                e.MethodName == method);

            if (endpoint != null)
            {
                endpoint.FullRoute.Should().Be(expectedRoute,
                    $"Route for {controller}.{method} should match expected ARM pattern");
            }
        }

        logger.LogInformation("All concrete ARM route patterns verified");

        // ========================================
        // Save comprehensive results
        // ========================================
        var results = new
        {
            RouteRegistration = new
            {
                Mechanism = "MapHttpAttributeRoutes",
                Location = $"Line {routingSetup.LineNumber}",
                CustomRouteProvider = routingSetup.CustomRouteProviderType,
                BaseControllerType = routingSetup.BaseControllerType,
            },
            ControllerRegistrations = allRegistrations.Select(r => new
            {
                Contract = r.InterfaceType,
                Implementation = r.ImplementationType,
                Lifetime = r.Lifetime ?? "default",
            }),
            Controllers = allControllerInfo.Select(c => new
            {
                c.ClassName,
                c.BaseType,
                c.RoutePrefix,
                ResourceRouting = c.ResourceRouting,
                EndpointCount = c.Endpoints.Count,
            }),
            Endpoints = allEndpoints.Select(e => new
            {
                e.ControllerClass,
                e.MethodName,
                e.HttpMethod,
                Route = e.FullRoute ?? e.RouteTemplate,
                e.CustomRouteAttribute,
                e.OperationName,
            }),
            Summary = new
            {
                TotalControllers = allControllerInfo.Count,
                TotalEndpoints = allEndpoints.Count,
                GetEndpoints = getEndpoints.Count,
                PostEndpoints = postEndpoints.Count,
                PutEndpoints = putEndpoints.Count,
                DeleteEndpoints = deleteEndpoints.Count,
            },
        };

        await SaveResultsAsync("UpdateService_FullApiDiscovery.json", results);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_AnalyzesAuthenticationHandlers()
    {
        // Arrange
        var startupPath = Path.Combine(
            UpdateRepoPath,
            "src", "UpdateResourceProvider", "UpdateService", "Service", "Source", "UpdateServiceStartup.cs");

        if (!File.Exists(startupPath))
        {
            Assert.Inconclusive($"Startup file not found: {startupPath}");
            return;
        }

        var code = await File.ReadAllTextAsync(startupPath);
        var tree = CSharpSyntaxTree.ParseText(code);
        var analyzer = new ApiInfrastructureAnalyzer();

        // Act
        var authHandlers = analyzer.AnalyzeAuthHandlers(tree, startupPath);

        // Assert
        Assert.IsTrue(authHandlers.Count > 0, "Should detect auth handlers");

        logger.LogInformation("Authentication handlers:\n{Handlers}",
            string.Join("\n", authHandlers.Select(h =>
                $"  [{h.HandlerType}] {h.Expression} (line {h.LineNumber})")));

        // Should detect tracing and auth handlers
        Assert.IsTrue(authHandlers.Any(h => h.HandlerType == "Tracing"),
            "Should detect TracingHandler");
        Assert.IsTrue(authHandlers.Any(h => h.HandlerType == "MultiAuth" || h.HandlerType == "CertificateBased"),
            "Should detect authentication handler");

        await SaveResultsAsync("AuthenticationHandlers.json", authHandlers);
    }

    #endregion

    #region HTTP Client Discovery Tests

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_DiscoverHttpClientRegistrations()
    {
        // Arrange
        var httpClientSingletonPath = Path.Combine(
            UpdateRepoPath,
            "src", "UpdateResourceProvider", "UpdateService", "Core", "Source", "Helpers", "HttpClientSingleton.cs");

        if (!File.Exists(httpClientSingletonPath))
        {
            Assert.Inconclusive($"HttpClientSingleton file not found: {httpClientSingletonPath}");
            return;
        }

        var code = await File.ReadAllTextAsync(httpClientSingletonPath);
        var tree = CSharpSyntaxTree.ParseText(code);
        var analyzer = new ApiInfrastructureAnalyzer();

        // Act
        var registrations = analyzer.AnalyzeHttpClientRegistrations(tree, httpClientSingletonPath);

        // Assert
        Assert.IsTrue(registrations.Count > 0, "Should detect HttpClient registration");

        logger.LogInformation("HttpClient registrations:\n{Registrations}",
            string.Join("\n", registrations.Select(r =>
                $"  [{r.RegistrationType}] {r.ClassName}.{r.FieldName ?? r.MethodName}")));

        // Should detect singleton pattern
        Assert.IsTrue(registrations.Any(r => r.RegistrationType == "Singleton"),
            "Should detect singleton pattern");

        await SaveResultsAsync("HttpClientRegistrations.json", registrations);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_TracesClientFactoryPatterns()
    {
        // Arrange - find client factory files
        var clientFactoryDir = Path.Combine(UpdateRepoPath, "src");
        var factoryFiles = Directory.GetFiles(clientFactoryDir, "*ClientFactory*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        if (factoryFiles.Count == 0)
        {
            Assert.Inconclusive("No client factory files found");
            return;
        }

        var allRegistrations = new List<HttpClientRegistration>();
        var analyzer = new ApiInfrastructureAnalyzer();

        // Act
        foreach (var file in factoryFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var registrations = analyzer.AnalyzeHttpClientRegistrations(tree, file);
            allRegistrations.AddRange(registrations);
        }

        // Assert
        logger.LogInformation("Found {FileCount} factory files with {RegCount} registrations",
            factoryFiles.Count, allRegistrations.Count);

        foreach (var reg in allRegistrations)
        {
            logger.LogInformation("  [{Type}] {Class}.{Method}",
                reg.RegistrationType, reg.ClassName, reg.MethodName ?? reg.FieldName);
        }

        await SaveResultsAsync("ClientFactoryRegistrations.json", new
        {
            FactoryFiles = factoryFiles.Select(f => Path.GetRelativePath(UpdateRepoPath, f)),
            Registrations = allRegistrations
        });
    }

    #endregion

    #region Full API Discovery Test

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_FullApiDiscovery()
    {
        // Arrange
        var service = new ApiDiscoveryService();

        // Act - full discovery
        var result = await service.DiscoverAsync(UpdateRepoPath);

        // Assert & Log
        logger.LogInformation("=== API Discovery Results ===");

        logger.LogInformation("Startup files found: {Count}", result.StartupFiles.Count);
        foreach (var file in result.StartupFiles.Take(5))
        {
            logger.LogInformation("  - {File}", Path.GetRelativePath(UpdateRepoPath, file));
        }

        logger.LogInformation("Infrastructure setup files: {Count}", result.InfrastructureSetup.Count);
        foreach (var infra in result.InfrastructureSetup)
        {
            logger.LogInformation("  - {File}: {Routes} routes, {Handlers} handlers",
                Path.GetFileName(infra.FilePath),
                infra.RouteRegistrations.Count,
                infra.MessageHandlers.Count);
        }

        logger.LogInformation("Controller DI registrations: {Count}", result.ControllerRegistrations.Count);

        logger.LogInformation("Discovered endpoints: {Count}", result.Endpoints.Count);
        logger.LogInformation("Abstract endpoints (contracts): {Count}", result.AbstractEndpoints.Count);

        // Log endpoint summary
        var endpointsByMethod = result.Endpoints.Concat(result.AbstractEndpoints)
            .GroupBy(e => e.HttpMethod)
            .ToDictionary(g => g.Key, g => g.Count());

        logger.LogInformation("Endpoints by HTTP method: GET={Get}, POST={Post}, PUT={Put}, DELETE={Delete}",
            endpointsByMethod.GetValueOrDefault("GET", 0),
            endpointsByMethod.GetValueOrDefault("POST", 0),
            endpointsByMethod.GetValueOrDefault("PUT", 0),
            endpointsByMethod.GetValueOrDefault("DELETE", 0));

        logger.LogInformation("HttpClient registrations: {Count}", result.HttpClientRegistrations.Count);
        logger.LogInformation("Auth handlers: {Count}", result.AuthHandlers.Count);
        logger.LogInformation("External service calls: {Count}", result.ExternalCalls.Count);

        // Assertions
        Assert.IsTrue(result.StartupFiles.Count > 0, "Should find startup files");
        Assert.IsTrue(result.InfrastructureSetup.Count > 0, "Should analyze infrastructure");
        Assert.IsTrue(result.ControllerRegistrations.Count > 0, "Should find controller DI registrations");

        // The key difference from pattern matching: we trace from DI -> implementation -> endpoints
        Assert.IsTrue(result.Endpoints.Count > 0 || result.AbstractEndpoints.Count > 0,
            "Should discover endpoints through DI tracing or abstract contracts");

        // Save comprehensive results
        await SaveResultsAsync("FullApiDiscovery.json", new
        {
            result.RepoRoot,
            DiscoveredAt = DateTime.UtcNow,
            Summary = new
            {
                StartupFiles = result.StartupFiles.Count,
                InfrastructureSetup = result.InfrastructureSetup.Count,
                ControllerRegistrations = result.ControllerRegistrations.Count,
                Endpoints = result.Endpoints.Count,
                AbstractEndpoints = result.AbstractEndpoints.Count,
                HttpClientRegistrations = result.HttpClientRegistrations.Count,
                AuthHandlers = result.AuthHandlers.Count,
                ExternalCalls = result.ExternalCalls.Count
            },
            Infrastructure = new
            {
                StartupFiles = result.StartupFiles.Select(f => Path.GetRelativePath(UpdateRepoPath, f)),
                RouteRegistrations = result.InfrastructureSetup.SelectMany(i => i.RouteRegistrations),
                MessageHandlers = result.InfrastructureSetup.SelectMany(i => i.MessageHandlers),
                ServiceReplacements = result.InfrastructureSetup.SelectMany(i => i.ServiceReplacements)
            },
            Controllers = result.ControllerRegistrations,
            Endpoints = result.Endpoints.Concat(result.AbstractEndpoints).Select(e => new
            {
                e.ControllerInterface,
                e.ControllerImplementation,
                e.ActionName,
                e.HttpMethod,
                e.RouteTemplate,
                e.RequiresAuth,
                e.AllowsAnonymous,
                File = Path.GetRelativePath(UpdateRepoPath, e.FilePath),
                e.LineNumber
            }),
            HttpClients = result.HttpClientRegistrations,
            Authentication = result.AuthHandlers,
            ExternalCalls = result.ExternalCalls.Select(c => new
            {
                c.CallingClass,
                c.CallingMethod,
                c.ClientType,
                c.TargetExpression,
                c.HttpMethod,
                c.HasAuthConfiguration,
                File = Path.GetRelativePath(UpdateRepoPath, c.FilePath),
                c.LineNumber
            })
        });
    }

    #endregion

    #region Mock Controller Detection (for test scenarios)

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_IdentifiesMockControllers()
    {
        // Mock controllers should be identified separately (like EceMockController)
        // These are used in integration tests, not real API endpoints

        var mockControllerFiles = Directory.GetFiles(
            Path.Combine(UpdateRepoPath, "src"),
            "*Mock*Controller*.cs",
            SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        logger.LogInformation("Found {Count} mock controller files", mockControllerFiles.Count);

        foreach (var file in mockControllerFiles)
        {
            logger.LogInformation("  MOCK: {File}", Path.GetRelativePath(UpdateRepoPath, file));
        }

        // These should NOT be counted as production endpoints
        var code = mockControllerFiles.Count > 0
            ? await File.ReadAllTextAsync(mockControllerFiles[0])
            : "";

        Assert.IsTrue(mockControllerFiles.Any(f => f.Contains("EceMockController")),
            "Should find EceMockController as a mock");

        await SaveResultsAsync("MockControllers.json", new
        {
            Count = mockControllerFiles.Count,
            Files = mockControllerFiles.Select(f => Path.GetRelativePath(UpdateRepoPath, f))
        });
    }

    #endregion

    #region Helper Methods

    private static async Task SaveResultsAsync(string fileName, object data)
    {
        var outputPath = Path.Combine(OutputDir, fileName);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json);
        logger.LogInformation("Results saved to: {Path}", outputPath);
    }

    #endregion
}