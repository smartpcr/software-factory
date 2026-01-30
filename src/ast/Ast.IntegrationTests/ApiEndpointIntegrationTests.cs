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

        // Act - find all startup files
        var startupFiles = await service.FindStartupFilesAsync(UpdateRepoPath);

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
        var infraInfo = analyzer.Analyze(tree, startupPath);

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
        // Arrange
        var unityConfigPath = Path.Combine(
            UpdateRepoPath,
            "src", "UpdateResourceProvider", "UpdateService", "Service", "Source", "UnityConfig.cs");

        if (!File.Exists(unityConfigPath))
        {
            Assert.Inconclusive($"UnityConfig file not found: {unityConfigPath}");
            return;
        }

        var code = await File.ReadAllTextAsync(unityConfigPath);
        var tree = CSharpSyntaxTree.ParseText(code);
        var analyzer = new ApiInfrastructureAnalyzer();

        // Act
        var registrations = analyzer.AnalyzeControllerRegistrations(tree, unityConfigPath);

        // Assert
        Assert.IsTrue(registrations.Count > 0, "Should find controller registrations");

        logger.LogInformation("Controller DI registrations ({Count} total):\n{Registrations}",
            registrations.Count,
            string.Join("\n", registrations.Select(r =>
                $"  {r.InterfaceType} -> {r.ImplementationType} [{r.Lifetime ?? "default"}]")));

        // Should find key controllers
        Assert.IsTrue(registrations.Any(r => r.InterfaceType.Contains("Cache")),
            "Should register CacheController");
        Assert.IsTrue(registrations.Any(r => r.InterfaceType.Contains("UpdateResource")),
            "Should register UpdateResourceController");
        Assert.IsTrue(registrations.Any(r => r.InterfaceType.Contains("UpdateRun")),
            "Should register UpdateRunResourceController");

        await SaveResultsAsync("UnityConfig_ControllerRegistrations.json", registrations);
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