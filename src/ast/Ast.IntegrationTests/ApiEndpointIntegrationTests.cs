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
using Microsoft.CodeAnalysis;
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
        // Arrange - discover startup files dynamically
        var service = new ApiDiscoveryService();
        var startupFiles = await service.FindStartupFilesAsync(UpdateRepoPath, new[] { CommonInfraRepoPath });

        if (startupFiles.Count == 0)
        {
            Assert.Inconclusive("No startup files discovered in repo");
            return;
        }

        var analyzer = new ApiInfrastructureAnalyzer();
        var allAuthHandlers = new List<AuthHandlerInfo>();

        // Act - analyze all startup files for auth handlers
        foreach (var startupPath in startupFiles)
        {
            var code = await File.ReadAllTextAsync(startupPath);
            var tree = CSharpSyntaxTree.ParseText(code);
            var authHandlers = analyzer.AnalyzeAuthHandlers(tree, startupPath);
            allAuthHandlers.AddRange(authHandlers);
        }

        // Assert
        Assert.IsTrue(allAuthHandlers.Count > 0, "Should detect auth handlers");

        logger.LogInformation("Authentication handlers:\n{Handlers}",
            string.Join("\n", allAuthHandlers.Select(h =>
                $"  [{h.HandlerType}] {h.Expression} (line {h.LineNumber})")));

        // Should detect tracing and auth handlers
        Assert.IsTrue(allAuthHandlers.Any(h => h.HandlerType == "Tracing"),
            "Should detect TracingHandler");
        Assert.IsTrue(allAuthHandlers.Any(h => h.HandlerType == "MultiAuth" || h.HandlerType == "CertificateBased"),
            "Should detect authentication handler");

        await SaveResultsAsync("AuthenticationHandlers.json", allAuthHandlers);
    }

    #endregion

    #region HTTP Client Discovery Tests

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_DiscoverHttpClientRegistrations()
    {
        // Arrange - parse all source files from both update repo and common-infra for complete tracing
        var updateCsFiles = Directory.GetFiles(UpdateRepoPath, "*.cs", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                      !f.Contains("\\out\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
          .ToList();

        // Include common-infra source files for complete HTTP client wrapper chain tracing
        var commonInfraCsFiles = new List<string>();
        if (Directory.Exists(CommonInfraRepoPath))
        {
            commonInfraCsFiles = Directory.GetFiles(CommonInfraRepoPath, "*.cs", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                          !f.Contains("\\out\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
              .ToList();
        }

        var allCsFiles = updateCsFiles.Concat(commonInfraCsFiles).ToList();
        logger.LogInformation("Scanning {Count} C# files for HTTP client DI registrations ({UpdateCount} update + {InfraCount} common-infra)",
            allCsFiles.Count, updateCsFiles.Count, commonInfraCsFiles.Count);

        var analyzer = new ApiInfrastructureAnalyzer();
        var allRegistrations = new List<HttpClientRegistration>();
        var syntaxTrees = new List<SyntaxTree>();
        var fileToTree = new Dictionary<string, SyntaxTree>();

        // First pass: parse all files
        foreach (var file in allCsFiles)
        {
            try
            {
                var code = await File.ReadAllTextAsync(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                syntaxTrees.Add(tree);
                fileToTree[file] = tree;
            }
            catch
            {
                // Skip unreadable files
            }
        }

        // Build HTTP client wrapper chain
        var httpClientWrappers = analyzer.BuildHttpClientWrapperChain(syntaxTrees);

        // Second pass: find files with DI registration patterns using AST
        // Look for RegisterType, RegisterInstance, AddHttpClient, AddSingleton, etc.
        var diRegistrationMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RegisterType", "RegisterInstance", "RegisterSingleton",
            "AddHttpClient", "AddSingleton", "AddScoped", "AddTransient",
            "Register", "RegisterComponents",
        };

        var diConfigFiles = new List<string>();
        foreach (var (file, tree) in fileToTree)
        {
            var root = tree.GetRoot();
            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            // Check if this file contains DI registration method calls
            var hasDiRegistrations = invocations.Any(inv =>
            {
                var methodName = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                return methodName != null && diRegistrationMethods.Contains(methodName);
            });

            if (hasDiRegistrations)
            {
                diConfigFiles.Add(file);
            }
        }

        logger.LogInformation("Found {Count} files with DI registrations via AST analysis", diConfigFiles.Count);

        // Act - analyze ALL files for HTTP client registrations (using wrapper chain)
        // The wrapper chain already identified which classes are HTTP clients
        foreach (var (file, tree) in fileToTree)
        {
            var registrations = analyzer.AnalyzeHttpClientRegistrations(tree, file, httpClientWrappers);
            allRegistrations.AddRange(registrations);
        }

        // Assert - should detect HTTP client registrations
        Assert.IsTrue(allRegistrations.Count > 0 || httpClientWrappers.Count > 0,
            "Should detect HTTP client registrations or wrappers");

        logger.LogInformation("HTTP Client Registrations ({Count} total):", allRegistrations.Count);
        foreach (var reg in allRegistrations)
        {
            logger.LogInformation("  [{Type}] {Class}.{Member} -> {ClientType}",
                reg.RegistrationType,
                reg.ClassName ?? "(no class)",
                reg.FieldName ?? reg.MethodName ?? "(no member)",
                reg.TypedClientType ?? reg.RawExpression?.Substring(0, Math.Min(50, reg.RawExpression?.Length ?? 0)) ?? "");
        }

        // Verify expected DI registrations from UnityConfig.cs
        // These are the clients registered via RegisterInstance
        var diRegistrations = allRegistrations.Where(r => r.RegistrationType == "DIContainer").ToList();
        logger.LogInformation("\nDI Container Registrations ({Count}):", diRegistrations.Count);
        foreach (var reg in diRegistrations)
        {
            logger.LogInformation("  {Type} in {Class}.{Method}",
                reg.TypedClientType, reg.ClassName, reg.MethodName);
        }

        // Expected clients from UnityConfig.cs analysis:
        // 1. IEnterpriseCloudEngineClient (BufferedECEClient) - line 289
        // 2. ICauClient (CauClient) - line 292
        // 3. IDownloadServiceClientManager - line 311
        // 4. IDrpClient (DrpClient) - line 331
        // 5. IFileDownloader (HttpFileDownloader) - line 351
        var expectedDiClients = new[]
        {
            "IEnterpriseCloudEngineClient",
            "ICauClient",
            "IDownloadServiceClientManager",
            "IDrpClient",
        };

        var foundDiClients = diRegistrations.Select(r => r.TypedClientType).Where(t => t != null).ToHashSet();
        logger.LogInformation("\nExpected DI Clients:");
        foreach (var expected in expectedDiClients)
        {
            var found = foundDiClients.Any(f => f!.Contains(expected.Replace("I", "")));
            logger.LogInformation("  {Status} {Client}", found ? "✓" : "✗", expected);
        }

        // The wrapper chain provides comprehensive HTTP client detection
        // Key clients should be in the wrapper chain
        httpClientWrappers.Should().Contain("HttpClientSingleton",
            "HttpClientSingleton should be in wrapper chain");

        // Verify discovered wrapper types include key clients
        logger.LogInformation("\nDiscovered HTTP client wrapper types ({Count}):", httpClientWrappers.Count);
        var keyClients = new[] { "HttpClientSingleton", "DrpClient", "HttpFileDownloader", "BufferedECEClient", "CauClient" };
        foreach (var client in keyClients)
        {
            if (httpClientWrappers.Contains(client))
            {
                logger.LogInformation("  ✓ {Client}", client);
            }
            else
            {
                logger.LogInformation("  ✗ {Client} (not found in wrapper chain)", client);
            }
        }

        // Verify key wrapper types are in the wrapper chain
        httpClientWrappers.Should().Contain("DrpClient",
            "DrpClient should be in wrapper chain (uses HttpClientSingleton)");
        httpClientWrappers.Should().Contain("HttpFileDownloader",
            "HttpFileDownloader should be in wrapper chain (uses HttpClientSingleton)");

        // Verify EnterpriseCloudEngineClient is detected (uses IRequestFactory/IExecuteRequest from common-infra)
        httpClientWrappers.Should().Contain("EnterpriseCloudEngineClient",
            "EnterpriseCloudEngineClient should be in wrapper chain (uses IExecuteRequest which wraps HttpClient)");

        // Verify other key clients that use common-infra HTTP abstractions
        httpClientWrappers.Should().Contain("DownloadServiceClient",
            "DownloadServiceClient should be in wrapper chain (uses common-infra HTTP)");
        httpClientWrappers.Should().Contain("LcmUpdateServiceClient",
            "LcmUpdateServiceClient should be in wrapper chain (uses common-infra HTTP)");

        // Verify DI registrations include expected client interfaces
        diRegistrations.Should().Contain(r => r.TypedClientType != null &&
            r.TypedClientType.Contains("EnterpriseCloudEngineClient"),
            "IEnterpriseCloudEngineClient should be registered in DI container");

        await SaveResultsAsync("HttpClientRegistrations.json", new
        {
            TotalRegistrations = allRegistrations.Count,
            DiContainerRegistrations = diRegistrations.Count,
            DiConfigFilesFound = diConfigFiles.Count,
            WrapperTypesDiscovered = httpClientWrappers.Count,
            KeyClientsInWrapperChain = keyClients.Where(c => httpClientWrappers.Contains(c)).ToList(),
            ExpectedDiClients = expectedDiClients,
            FoundDiClients = foundDiClients.ToList(),
            DiConfigFiles = diConfigFiles.Select(f => Path.GetRelativePath(UpdateRepoPath, f)).ToList(),
            Registrations = allRegistrations.Select(r => new
            {
                r.RegistrationType,
                r.ClassName,
                Member = r.FieldName ?? r.MethodName,
                r.TypedClientType,
                Expression = r.RawExpression?.Substring(0, Math.Min(100, r.RawExpression?.Length ?? 0)),
                File = Path.GetRelativePath(UpdateRepoPath, r.FilePath),
            }),
        });
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_TracesClientFactoryPatterns()
    {
        // Arrange - find client factory files dynamically
        var factoryFiles = Directory.GetFiles(UpdateRepoPath, "*ClientFactory*.cs", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
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

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateServiceRepo_DiscoverHttpClientUsageByType()
    {
        // Arrange - scan all C# files for HTTP client usage using AST-based type analysis
        var updateCsFiles = Directory.GetFiles(UpdateRepoPath, "*.cs", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                      !f.Contains("\\out\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
          .ToList();

        // Include common-infra source files for complete HTTP client wrapper chain tracing
        var commonInfraCsFiles = new List<string>();
        if (Directory.Exists(CommonInfraRepoPath))
        {
            commonInfraCsFiles = Directory.GetFiles(CommonInfraRepoPath, "*.cs", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") &&
                          !f.Contains("\\out\\") && !f.Contains("Test", StringComparison.OrdinalIgnoreCase))
              .ToList();
        }

        var allCsFiles = updateCsFiles.Concat(commonInfraCsFiles).ToList();
        logger.LogInformation("Scanning {Count} C# files for HTTP client usage ({UpdateCount} update + {InfraCount} common-infra)",
            allCsFiles.Count, updateCsFiles.Count, commonInfraCsFiles.Count);

        var analyzer = new ApiInfrastructureAnalyzer();
        var allUsages = new List<HttpClientUsage>();

        // First pass: parse all files and build type inheritance map
        var syntaxTrees = new List<SyntaxTree>();
        var fileToTree = new Dictionary<string, SyntaxTree>();
        foreach (var file in allCsFiles)
        {
            try
            {
                var code = await File.ReadAllTextAsync(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                syntaxTrees.Add(tree);
                fileToTree[file] = tree;
            }
            catch
            {
                // Skip unreadable files
            }
        }

        // Build type inheritance map from all trees
        var typeInheritanceMap = analyzer.BuildTypeInheritanceMap(syntaxTrees);
        logger.LogInformation("Built type inheritance map with {Count} types", typeInheritanceMap.Count);

        // Build HTTP client wrapper chain by tracing constructor dependencies
        var httpClientWrappers = analyzer.BuildHttpClientWrapperChain(syntaxTrees);
        logger.LogInformation("Discovered {Count} HTTP client wrapper types via dependency chain", httpClientWrappers.Count);

        // Log some discovered wrappers
        var discoveredWrappers = httpClientWrappers
            .Except(new[] { "HttpClient", "WebClient", "IHttpClientFactory", "HttpMessageHandler", "DelegatingHandler" })
            .Take(20)
            .ToList();
        if (discoveredWrappers.Count > 0)
        {
            logger.LogInformation("Sample discovered wrappers: {Wrappers}", string.Join(", ", discoveredWrappers));
        }

        // Act - analyze all files for HTTP client usage by type, using inheritance map and wrapper chain
        foreach (var (file, tree) in fileToTree)
        {
            var usages = analyzer.AnalyzeHttpClientUsageByType(tree, file, typeInheritanceMap, httpClientWrappers);
            allUsages.AddRange(usages);
        }

        // Assert
        Assert.IsTrue(allUsages.Count > 0, "Should detect HTTP client usage");

        // Group by usage type
        var usagesByType = allUsages.GroupBy(u => u.UsageType).OrderBy(g => g.Key);

        logger.LogInformation("HTTP Client Usage Summary:");
        foreach (var group in usagesByType)
        {
            logger.LogInformation("  {Type}: {Count} usages", group.Key, group.Count());
        }

        // Log details by category
        logger.LogInformation("\n=== Singleton Patterns ===");
        foreach (var usage in allUsages.Where(u => u.UsageType == "Singleton").Take(10))
        {
            logger.LogInformation("  {Class}.{Member} ({Type})",
                usage.ClassName, usage.MemberName, usage.ClientType);
        }

        logger.LogInformation("\n=== Factory Methods ===");
        foreach (var usage in allUsages.Where(u => u.UsageType == "FactoryMethod").Take(10))
        {
            logger.LogInformation("  {Class}.{Method}() -> {Type}",
                usage.ClassName, usage.FactoryMethod, usage.ClientType);
        }

        logger.LogInformation("\n=== Constructor Injection ===");
        foreach (var usage in allUsages.Where(u => u.UsageType == "ConstructorInjection").Take(10))
        {
            logger.LogInformation("  {Class}({Type} {Param})",
                usage.ClassName, usage.ClientType, usage.MemberName);
        }

        logger.LogInformation("\n=== Client Wrappers ===");
        foreach (var usage in allUsages.Where(u => u.UsageType == "Wrapper").Take(10))
        {
            logger.LogInformation("  {Class} wraps {Wrapped}",
                usage.ClassName, usage.WrappedClientType);
        }

        logger.LogInformation("\n=== HTTP Method Calls ===");
        foreach (var usage in allUsages.Where(u => u.UsageType == "HttpCalls").Take(10))
        {
            logger.LogInformation("  {Class}: {Methods}",
                usage.ClassName, string.Join(", ", usage.HttpMethodsUsed));
        }

        // Verify expected HTTP clients from opencode analysis
        // See docs/examples/http_client_detection.md for full list
        var classNames = allUsages.Select(u => u.ClassName).ToHashSet();

        // 1. HttpClientSingleton - the core singleton pattern
        allUsages.Should().Contain(u => u.ClassName == "HttpClientSingleton",
            "Should detect HttpClientSingleton (core singleton)");

        // 2. Classes that use HttpClientSingleton.Instance (static access)
        httpClientWrappers.Should().Contain("HttpFileDownloader",
            "HttpFileDownloader should be detected in wrapper chain (uses HttpClientSingleton)");
        httpClientWrappers.Should().Contain("DrpClient",
            "DrpClient should be detected in wrapper chain (uses HttpClientSingleton)");
        httpClientWrappers.Should().Contain("SimpleHttpDownloader",
            "SimpleHttpDownloader should be detected in wrapper chain (uses HttpClientSingleton)");

        // 3. Injected HttpClient users
        allUsages.Should().Contain(u => u.ClassName == "ArcTokenStore",
            "Should detect ArcTokenStore (injected HttpClient for Azure Arc Identity)");

        // 4. Direct HttpClient instantiation
        httpClientWrappers.Should().Contain("DiscoveryManifestHelper",
            "DiscoveryManifestHelper should be detected in wrapper chain (new HttpClient())");

        // 5. WebClient users
        allUsages.Should().Contain(u => u.ClassName == "WebDownloader",
            "Should detect WebDownloader (WebClient for HTTP downloads)");
        httpClientWrappers.Should().Contain("GetAzSUpdateManifestCommand",
            "GetAzSUpdateManifestCommand should be detected in wrapper chain (uses WebClient)");

        // 6. Wrapper chain detection - verify wrapper relationships
        allUsages.Should().Contain(u => u.ClassName == "BufferedECEClient" && u.WrappedClientType == "IEnterpriseCloudEngineClient",
            "BufferedECEClient should wrap IEnterpriseCloudEngineClient");
        allUsages.Should().Contain(u => u.ClassName == "CauClient" && u.WrappedClientType == "IEnterpriseCloudEngineClient",
            "CauClient should wrap IEnterpriseCloudEngineClient");
        allUsages.Should().Contain(u => u.ClassName == "DownloadServiceClient",
            "DownloadServiceClient should be detected as a wrapper");

        // Note: EnterpriseCloudEngineClient, EnterpriseCloudEngineReliableClient, and LcmUpdateServiceClient
        // use IExecuteRequest/IServiceTypeExecuteRequest from external common-infra NuGet packages
        // which aren't in the source tree, so they can't be detected via AST analysis alone.
        // However, we CAN detect classes that WRAP them (BufferedECEClient, CauClient, etc.)

        // Log discovered vs expected for direct HttpClient/WebClient usage
        var directHttpClients = new[]
        {
            "HttpClientSingleton", "ArcTokenStore", "WebDownloader",
        };
        var wrapperChainClients = new[]
        {
            "HttpFileDownloader", "DrpClient", "SimpleHttpDownloader",
            "DiscoveryManifestHelper", "GetAzSUpdateManifestCommand",
        };
        var wrapperClients = new[]
        {
            "BufferedECEClient", "CauClient", "DownloadServiceClient",
        };

        logger.LogInformation("\n=== HTTP Client Detection Results ===");
        logger.LogInformation("Direct HTTP clients detected: {Clients}",
            string.Join(", ", directHttpClients.Where(c => classNames.Contains(c))));
        logger.LogInformation("Wrapper chain clients detected: {Clients}",
            string.Join(", ", wrapperChainClients.Where(c => httpClientWrappers.Contains(c))));
        logger.LogInformation("Wrapper clients detected: {Clients}",
            string.Join(", ", wrapperClients.Where(c => classNames.Contains(c))));

        await SaveResultsAsync("HttpClientUsageByType.json", new
        {
            TotalUsages = allUsages.Count,
            ByType = usagesByType.Select(g => new { Type = g.Key, Count = g.Count() }),
            DiscoveredWrappers = httpClientWrappers.Count,
            WrapperChainTypes = httpClientWrappers.ToList(),
            DirectHttpClients = directHttpClients.Where(c => classNames.Contains(c)).ToList(),
            WrapperChainClients = wrapperChainClients.Where(c => httpClientWrappers.Contains(c)).ToList(),
            WrapperClients = wrapperClients.Where(c => classNames.Contains(c)).ToList(),
            Usages = allUsages.Select(u => new
            {
                u.UsageType,
                u.ClientType,
                u.ClassName,
                u.MemberName,
                u.FactoryMethod,
                u.WrappedClientType,
                u.HttpMethodsUsed,
                File = Path.GetRelativePath(UpdateRepoPath, u.FilePath),
                u.LineNumber,
            })
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

        var mockControllerFiles = Directory.GetFiles(UpdateRepoPath, "*Mock*Controller*.cs", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
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