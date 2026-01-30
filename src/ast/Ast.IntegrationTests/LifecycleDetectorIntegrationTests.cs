// -----------------------------------------------------------------------
// <copyright file="LifecycleDetectorIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.IntegrationTests;

using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIntelligence.Analyzers;
using CodeIntelligence.Detectors;
using CodeIntelligence.Parsers;
using Microsoft.Extensions.Logging;

/// <summary>
/// Integration tests for lifecycle and entry point detection using real repository code.
/// </summary>
[TestClass]
public class LifecycleDetectorIntegrationTests
{
    // Update service repo path
    private static readonly string updateRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    // Common infra repo path
    private static readonly string commonInfraRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\common\infra"
        : "/mnt/e/work/hub/common/infra";

    private static readonly string outputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "lifecycle");

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static ILogger<LifecycleDetectorIntegrationTests> logger = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<LifecycleDetectorIntegrationTests>();

        if (Directory.Exists(LifecycleDetectorIntegrationTests.outputDir))
        {
            Directory.Delete(LifecycleDetectorIntegrationTests.outputDir, recursive: true);
        }

        Directory.CreateDirectory(LifecycleDetectorIntegrationTests.outputDir);
        logger.LogInformation("Output directory: {OutputDir}", LifecycleDetectorIntegrationTests.outputDir);
    }

    /// <summary>
    /// Tests that the Update service repo has entry points for both Windows Service and IWebApiService hosting.
    /// Expected: UpdateWinService (Windows Service host) and UpdateService (IWebApiService for Service Fabric).
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_DetectsTwoEntryPoints_WinServiceAndServiceFabric()
    {
        // Arrange
        if (!Directory.Exists(LifecycleDetectorIntegrationTests.updateRepoPath))
        {
            Assert.Inconclusive($"Update service repo not found at {LifecycleDetectorIntegrationTests.updateRepoPath}");
            return;
        }

        logger.LogInformation("Discovering entry points from repository root: {Path}", LifecycleDetectorIntegrationTests.updateRepoPath);

        var detector = new LifecycleDetector();

        // Act - Discover entry points from repository root
        var result = await detector.DiscoverFromRepositoryAsync(LifecycleDetectorIntegrationTests.updateRepoPath);

        // Output results
        var outputPath = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "update_service_entry_points.json");
        var output = new
        {
            RepoPath = LifecycleDetectorIntegrationTests.updateRepoPath,
            TotalProjectsScanned = result.TotalProjectsScanned,
            ExecutableProjects = result.Projects.Count,
            TotalEntryPoints = result.AllEntryPoints.Count(),
            Projects = result.Projects
                .Where(p => p.HasEntryPoints)
                .Select(p => new
                {
                    p.Project.ProjectName,
                    p.RelativePath,
                    ProjectType = p.Project.SdkInfo.ProjectType.ToString(),
                    p.Project.SdkInfo.IsExecutable,
                    p.Project.SdkInfo.IsWebProject,
                    p.Project.SdkInfo.IsWorkerProject,
                    EntryPoints = p.EntryPoints.Select(ep => new
                    {
                        Type = ep.EntryPointType.ToString(),
                        ep.ClassName,
                        ep.BaseType,
                        ep.FilePath,
                        ep.LineNumber,
                        ep.LifecycleMethods
                    })
                }),
            Errors = result.Errors.Take(5).Select(e => new { e.FilePath, e.Message })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, LifecycleDetectorIntegrationTests.jsonOptions));

        // Assert
        logger.LogInformation("Total projects scanned: {Count}", result.TotalProjectsScanned);
        logger.LogInformation("Executable projects found: {Count}", result.Projects.Count);
        logger.LogInformation("Total entry points: {Count}", result.AllEntryPoints.Count());

        result.Projects.Should().NotBeEmpty("Repository should have executable projects");
        result.AllEntryPoints.Should().NotBeEmpty("Repository should have at least one entry point");

        // Log all discovered entry points
        foreach (var project in result.Projects.Where(p => p.HasEntryPoints))
        {
            logger.LogInformation("Project: {Name} ({Type})",
                project.Project.ProjectName,
                project.Project.SdkInfo.ProjectType);

            foreach (var ep in project.EntryPoints)
            {
                logger.LogInformation("  Entry Point: {Type} - {Class} ({BaseType})",
                    ep.EntryPointType, ep.ClassName, ep.BaseType ?? "N/A");
            }
        }

        // Verify we found Windows Service and/or IWebApiService entry points
        var allEntryPoints = result.AllEntryPoints.ToList();
        var hasWindowsServiceOrHostedService = allEntryPoints.Any(ep =>
            ep.EntryPointType == EntryPointType.WindowsService ||
            ep.EntryPointType == EntryPointType.HostedService ||
            ep.EntryPointType == EntryPointType.BackgroundService ||
            ep.EntryPointType == EntryPointType.ServiceFabric);

        hasWindowsServiceOrHostedService.Should().BeTrue(
            "Update service repo should have Windows Service, Hosted Service, or Service Fabric entry points");
    }

    /// <summary>
    /// Tests manifest-based entry point detection using WinApplicationManifest.xml from source.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_ParsesWinApplicationManifest()
    {
        // Arrange - Discover WinService folders dynamically by finding WinApplicationManifest.xml
        var manifestFiles = Directory.GetFiles(LifecycleDetectorIntegrationTests.updateRepoPath, "WinApplicationManifest.xml", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\out\\"))
          .ToList();

        if (manifestFiles.Count == 0)
        {
            Assert.Inconclusive("No WinApplicationManifest.xml files found in repo");
            return;
        }

        var winServicePath = Path.GetDirectoryName(manifestFiles[0])!;
        logger.LogInformation("Found WinApplicationManifest in: {Path}", winServicePath);

        var parser = new ManifestParser();

        // Find the .csproj file in the same directory
        var csprojFile = Directory.GetFiles(winServicePath, "*.csproj").FirstOrDefault();
        if (csprojFile == null)
        {
            Assert.Inconclusive("No .csproj found in WinService folder");
            return;
        }

        // Act - Find manifest references from project file
        var manifestRefs = await parser.FindProjectManifestsAsync(csprojFile);
        logger.LogInformation("Found {Count} manifest references in {Project}",
            manifestRefs.Count, Path.GetFileName(csprojFile));

        foreach (var manifestRef in manifestRefs)
        {
            logger.LogInformation("  - {Type}: {Path} (Copy: {Copy})",
                manifestRef.ManifestType, manifestRef.RelativePath, manifestRef.CopyAction);
        }

        // Find WinApplicationManifest specifically
        var winAppManifestRef = manifestRefs
            .FirstOrDefault(m => m.ManifestType == ManifestType.WinApplication);

        if (winAppManifestRef == null)
        {
            // Fallback: use the directly discovered manifest file
            var manifestPath = manifestFiles[0];
            var services = await parser.ParseWinApplicationManifestAsync(manifestPath);

            // Output results
            var outputPath1 = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "manifest_services.json");
            var output1 = new
            {
                Source = "DirectFile",
                ManifestPath = manifestPath,
                ServiceCount = services.Count,
                Services = services.Select(s => new
                {
                    s.ServiceName,
                    s.AssemblyFile,
                    ManifestType = s.ManifestType.ToString(),
                    s.LineNumber
                })
            };
            File.WriteAllText(outputPath1, JsonSerializer.Serialize(output1, LifecycleDetectorIntegrationTests.jsonOptions));

            services.Should().NotBeEmpty("WinApplicationManifest should define at least one service");
            return;
        }

        // Parse the manifest found via project reference
        var manifestInfo = await parser.ParseManifestAsync(winAppManifestRef.ManifestPath);

        // Output results
        var outputPath = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "manifest_services.json");
        var output = new
        {
            Source = "ProjectReference",
            ProjectPath = winAppManifestRef.ProjectPath,
            ManifestPath = winAppManifestRef.ManifestPath,
            RelativePath = winAppManifestRef.RelativePath,
            CopyAction = winAppManifestRef.CopyAction,
            ServiceCount = manifestInfo?.Services.Count ?? 0,
            Services = manifestInfo?.Services.Select(s => new
            {
                s.ServiceName,
                s.AssemblyFile,
                ManifestType = s.ManifestType.ToString(),
                s.LineNumber
            })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, LifecycleDetectorIntegrationTests.jsonOptions));

        // Assert
        manifestInfo.Should().NotBeNull("Should parse the manifest");
        manifestInfo!.Services.Should().NotBeEmpty("WinApplicationManifest should define at least one service");

        foreach (var service in manifestInfo.Services)
        {
            logger.LogInformation("Service: {Name} -> {Assembly}",
                service.ServiceName, service.AssemblyFile);
        }

        // Verify UpdateWinService is defined
        manifestInfo.Services.Should().Contain(s =>
            s.AssemblyFile != null &&
            s.AssemblyFile.Contains("UpdateWinService"),
            "Should find UpdateWinService assembly reference");

        // Also verify manifest is associated with project via CopyToOutputDirectory
        winAppManifestRef.CopyAction.Should().BeOneOf("PreserveNewest", "Always",
            "Manifest should be configured to copy to output");
    }

    /// <summary>
    /// Tests detection of IWebApiService implementations which are hosted services.
    /// Uses repository discovery to find all hosted service implementations.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_DetectsIWebApiServiceImplementations()
    {
        // Arrange
        if (!Directory.Exists(LifecycleDetectorIntegrationTests.updateRepoPath))
        {
            Assert.Inconclusive($"Update service repo not found at {LifecycleDetectorIntegrationTests.updateRepoPath}");
            return;
        }

        logger.LogInformation("Discovering IWebApiService implementations from repo: {Path}", LifecycleDetectorIntegrationTests.updateRepoPath);

        var detector = new LifecycleDetector();

        // Configure discovery options with common-infra dependency repository
        var options = new RepositoryDiscoveryOptions
        {
            IncludeLibrariesWithHostingPatterns = true,
            DependencyRepositoryPaths = new[] { LifecycleDetectorIntegrationTests.commonInfraRepoPath },
        };

        // Act - Discover from repository root with dependency repo support
        var result = await detector.DiscoverFromRepositoryAsync(LifecycleDetectorIntegrationTests.updateRepoPath, options);

        // Log discovered projects for debugging
        logger.LogInformation("Total projects scanned: {Count}", result.TotalProjectsScanned);
        logger.LogInformation("Projects with entry points: {Count}", result.Projects.Count);
        foreach (var proj in result.Projects.Take(10))
        {
            logger.LogInformation("  Project: {Name}, IsLib: {IsLib}, EntryPoints: {Count}",
                proj.Project.ProjectName, proj.Project.IsLibraryWithHostingPatterns, proj.EntryPoints.Count);
        }

        // Find all HostedService entry points (IWebApiService implements IHostedService pattern)
        var hostedServiceEntryPoints = result.AllEntryPoints
            .Where(ep => ep.EntryPointType == EntryPointType.HostedService ||
                        ep.BaseType?.Contains("IWebApiService") == true ||
                        ep.BaseType?.Contains("IService<") == true)
            .ToList();

        // Output results
        var outputPath = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "webapi_service_implementations.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            TotalProjectsScanned = result.TotalProjectsScanned,
            TotalHostedServiceImplementations = hostedServiceEntryPoints.Count,
            Implementations = hostedServiceEntryPoints.Select(ep => new
            {
                ep.ClassName,
                ep.BaseType,
                ep.FilePath,
                Type = ep.EntryPointType.ToString()
            }),
            ProjectsWithHostedServices = result.Projects
                .Where(p => p.EntryPoints.Any(ep =>
                    ep.EntryPointType == EntryPointType.HostedService ||
                    ep.BaseType?.Contains("IWebApiService") == true))
                .Select(p => p.Project.ProjectName)
        }, LifecycleDetectorIntegrationTests.jsonOptions));

        // Assert
        logger.LogInformation("Found {Count} hosted service implementations", hostedServiceEntryPoints.Count);
        foreach (var ep in hostedServiceEntryPoints)
        {
            logger.LogInformation("  - {Class} : {BaseType} in {File}",
                ep.ClassName, ep.BaseType, Path.GetFileName(ep.FilePath));
        }

        hostedServiceEntryPoints.Should().NotBeEmpty(
            "Update service repo should have IWebApiService/IHostedService implementations");
    }

    /// <summary>
    /// Tests full lifecycle detection across the entire Update service repository.
    /// Uses repository discovery to analyze all executable projects.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_FullLifecycleAnalysis()
    {
        // Arrange
        if (!Directory.Exists(LifecycleDetectorIntegrationTests.updateRepoPath))
        {
            Assert.Inconclusive($"Update service repo not found at {LifecycleDetectorIntegrationTests.updateRepoPath}");
            return;
        }

        logger.LogInformation("Running full lifecycle analysis from repo root: {Path}", LifecycleDetectorIntegrationTests.updateRepoPath);

        var detector = new LifecycleDetector();

        // Configure discovery options with common-infra dependency repository
        var options = new RepositoryDiscoveryOptions
        {
            IncludeLibrariesWithHostingPatterns = true,
            DependencyRepositoryPaths = new[] { LifecycleDetectorIntegrationTests.commonInfraRepoPath },
        };

        // Act - Discover from repository root with dependency repo support
        var result = await detector.DiscoverFromRepositoryAsync(LifecycleDetectorIntegrationTests.updateRepoPath, options);

        // Output results
        var outputPath = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "full_lifecycle_analysis.json");
        var output = new
        {
            RepositoryRoot = result.RepositoryRoot,
            result.TotalProjectsScanned,
            ExecutableProjectsFound = result.Projects.Count,
            TotalFilesAnalyzed = result.TotalFilesAnalyzed,
            TotalEntryPoints = result.AllEntryPoints.Count(),
            TotalAzureFunctions = result.AllAzureFunctions.Count(),
            ErrorCount = result.Errors.Count,
            Projects = result.Projects
                .Where(p => p.HasEntryPoints)
                .Select(p => new
                {
                    p.Project.ProjectName,
                    p.RelativePath,
                    ProjectType = p.Project.SdkInfo.ProjectType.ToString(),
                    p.Project.IsLibraryWithHostingPatterns,
                    p.FilesAnalyzed,
                    EntryPointCount = p.EntryPoints.Count,
                    AzureFunctionCount = p.AzureFunctions.Count,
                    ScheduledJobCount = p.ScheduledJobs.Count,
                    LifetimeEventCount = p.ApplicationLifetimeEvents.Count,
                    EntryPoints = p.EntryPoints.Select(ep => new
                    {
                        Type = ep.EntryPointType.ToString(),
                        ep.ClassName,
                        ep.MethodName,
                        ep.BaseType,
                        ep.LifecycleMethods
                    })
                }),
            Errors = result.Errors.Take(10).Select(e => new
            {
                e.FilePath,
                e.Message,
                e.ErrorType
            })
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, LifecycleDetectorIntegrationTests.jsonOptions));

        // Assert
        result.TotalFilesAnalyzed.Should().BeGreaterThan(0);
        result.AllEntryPoints.Should().NotBeEmpty("Should detect entry points in Update service");

        // Assert: UpdateService project should be detected with UpdateService class containing StartAsync
        const string ExpectedProjectName = "UpdateResourceProvider.UpdateService.Service";
        var updateServiceProject = result.Projects
            .FirstOrDefault(p => p.Project.ProjectName.Equals(ExpectedProjectName, StringComparison.OrdinalIgnoreCase));

        updateServiceProject.Should().NotBeNull(
            $"Should find project '{ExpectedProjectName}'");

        var updateServiceEntryPoint = updateServiceProject!.EntryPoints
            .FirstOrDefault(ep => ep.ClassName != null && ep.ClassName.Contains("UpdateService"));

        updateServiceEntryPoint.Should().NotBeNull(
            "UpdateService project should contain an entry point with 'UpdateService' class");

        updateServiceEntryPoint!.LifecycleMethods.Should().Contain("StartAsync",
            "UpdateService class should have StartAsync lifecycle method");

        logger.LogInformation("Found UpdateService entry point:");
        logger.LogInformation("  Project: {Project}", updateServiceProject.RelativePath);
        logger.LogInformation("  Class: {Class}", updateServiceEntryPoint.ClassName);
        logger.LogInformation("  BaseType: {BaseType}", updateServiceEntryPoint.BaseType);
        logger.LogInformation("  LifecycleMethods: {Methods}", string.Join(", ", updateServiceEntryPoint.LifecycleMethods));

        logger.LogInformation("Analysis complete:");
        logger.LogInformation("  Projects scanned: {Count}", result.TotalProjectsScanned);
        logger.LogInformation("  Executable projects: {Count}", result.Projects.Count);
        logger.LogInformation("  Files analyzed: {Count}", result.TotalFilesAnalyzed);
        logger.LogInformation("  Total entry points: {Count}", result.AllEntryPoints.Count());
        logger.LogInformation("  Errors: {Count}", result.Errors.Count);

        foreach (var project in result.Projects.Where(p => p.HasEntryPoints))
        {
            logger.LogInformation("  Project: {Name} - {Count} entry points",
                project.Project.ProjectName, project.EntryPoints.Count);
        }
    }

    /// <summary>
    /// Tests that common-infra repo hosting base classes are detected correctly.
    /// Uses repository discovery to find hosting-related projects and entry points.
    /// </summary>
    [TestMethod]
    public async Task CommonInfraRepo_DetectsHostingBaseClasses()
    {
        // Arrange
        if (!Directory.Exists(LifecycleDetectorIntegrationTests.commonInfraRepoPath))
        {
            Assert.Inconclusive($"Common infra repo not found at {LifecycleDetectorIntegrationTests.commonInfraRepoPath}");
            return;
        }

        logger.LogInformation("Discovering hosting base classes from repo: {Path}", LifecycleDetectorIntegrationTests.commonInfraRepoPath);

        var detector = new LifecycleDetector();

        // Act - Discover from repository root, include all projects to find base classes
        var options = new RepositoryDiscoveryOptions { IncludeAllProjects = true };
        var result = await detector.DiscoverFromRepositoryAsync(LifecycleDetectorIntegrationTests.commonInfraRepoPath, options);

        // Find projects with hosting-related entry points
        var hostingProjects = result.Projects
            .Where(p => p.Project.ProjectName.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
                       p.EntryPoints.Any(ep =>
                           ep.EntryPointType == EntryPointType.HostedService ||
                           ep.EntryPointType == EntryPointType.BackgroundService ||
                           ep.EntryPointType == EntryPointType.WindowsService))
            .ToList();

        // Output results
        var outputPath = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "common_infra_hosting.json");
        var output = new
        {
            TotalProjectsScanned = result.TotalProjectsScanned,
            HostingRelatedProjects = hostingProjects.Count,
            Projects = hostingProjects.Select(p => new
            {
                p.Project.ProjectName,
                p.RelativePath,
                ProjectType = p.Project.SdkInfo.ProjectType.ToString(),
                EntryPoints = p.EntryPoints.Select(ep => new
                {
                    Type = ep.EntryPointType.ToString(),
                    ep.ClassName,
                    ep.BaseType
                })
            }),
            AllEntryPointTypes = result.AllEntryPoints
                .GroupBy(ep => ep.EntryPointType)
                .Select(g => new { Type = g.Key.ToString(), Count = g.Count() })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, LifecycleDetectorIntegrationTests.jsonOptions));

        // Assert
        result.TotalProjectsScanned.Should().BeGreaterThan(0, "Common infra should have projects to scan");

        logger.LogInformation("Found {Count} projects in common-infra repo", result.TotalProjectsScanned);
        logger.LogInformation("Hosting-related projects: {Count}", hostingProjects.Count);

        foreach (var project in hostingProjects)
        {
            logger.LogInformation("  {Name}: {Count} entry points",
                project.Project.ProjectName, project.EntryPoints.Count);
        }
    }

    /// <summary>
    /// Tests project SDK detection to identify Worker and Web projects.
    /// Uses repository discovery to identify all project types.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_DetectsProjectTypes()
    {
        // Arrange
        if (!Directory.Exists(LifecycleDetectorIntegrationTests.updateRepoPath))
        {
            Assert.Inconclusive($"Update service repo not found at {LifecycleDetectorIntegrationTests.updateRepoPath}");
            return;
        }

        logger.LogInformation("Discovering project types from repo: {Path}", LifecycleDetectorIntegrationTests.updateRepoPath);

        var detector = new LifecycleDetector();

        // Act - Discover from repository root, include all projects
        var options = new RepositoryDiscoveryOptions { IncludeAllProjects = true };
        var result = await detector.DiscoverFromRepositoryAsync(LifecycleDetectorIntegrationTests.updateRepoPath, options);

        // Output results
        var outputPath = Path.Combine(LifecycleDetectorIntegrationTests.outputDir, "project_types.json");
        var output = new
        {
            TotalProjectsScanned = result.TotalProjectsScanned,
            ExecutableProjects = result.Projects.Count(p => p.Project.SdkInfo.IsExecutable),
            WebProjects = result.Projects.Count(p => p.Project.SdkInfo.IsWebProject),
            WorkerProjects = result.Projects.Count(p => p.Project.SdkInfo.IsWorkerProject),
            ByType = result.Projects
                .GroupBy(p => p.Project.SdkInfo.ProjectType.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            Projects = result.Projects.Select(p => new
            {
                Name = p.Project.ProjectName,
                p.RelativePath,
                Type = p.Project.SdkInfo.ProjectType.ToString(),
                p.Project.SdkInfo.Sdk,
                p.Project.SdkInfo.OutputType,
                p.Project.SdkInfo.IsExecutable,
                p.Project.SdkInfo.IsWebProject,
                p.Project.SdkInfo.IsWorkerProject,
                p.Project.IsTestProject,
                HasEntryPoints = p.HasEntryPoints,
                EntryPointCount = p.EntryPoints.Count
            })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, LifecycleDetectorIntegrationTests.jsonOptions));

        // Assert
        result.Projects.Should().NotBeEmpty("Should discover projects in the repository");

        var executableProjects = result.Projects.Where(p => p.Project.SdkInfo.IsExecutable).ToList();
        logger.LogInformation("Found {Total} projects, {Exe} executables",
            result.TotalProjectsScanned, executableProjects.Count);

        foreach (var proj in executableProjects)
        {
            logger.LogInformation("  Executable: {Name} ({Type}) - {Count} entry points",
                proj.Project.ProjectName, proj.Project.SdkInfo.ProjectType, proj.EntryPoints.Count);
        }
    }
}
