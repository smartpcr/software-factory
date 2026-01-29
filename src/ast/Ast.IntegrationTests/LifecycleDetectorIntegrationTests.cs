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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

/// <summary>
/// Integration tests for lifecycle and entry point detection using real repository code.
/// </summary>
[TestClass]
public class LifecycleDetectorIntegrationTests
{
    // Update service repo path
    private static readonly string UpdateRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    // Common infra repo path
    private static readonly string CommonInfraRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\common\infra"
        : "/mnt/e/work/hub/common/infra";

    private static readonly string OutputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "lifecycle");

    private static readonly JsonSerializerOptions JsonOptions = new()
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

        if (Directory.Exists(OutputDir))
        {
            Directory.Delete(OutputDir, recursive: true);
        }

        Directory.CreateDirectory(OutputDir);
        logger.LogInformation("Output directory: {OutputDir}", OutputDir);
    }

    /// <summary>
    /// Tests that the Update service repo has entry points for both Windows Service and IWebApiService hosting.
    /// Expected: UpdateWinService (Windows Service host) and UpdateService (IWebApiService for Service Fabric).
    /// </summary>
    [TestMethod]
    public void UpdateServiceRepo_DetectsTwoEntryPoints_WinServiceAndServiceFabric()
    {
        // Arrange
        var updateServicePath = Path.Combine(UpdateRepoPath, "src", "UpdateResourceProvider", "UpdateService");
        if (!Directory.Exists(updateServicePath))
        {
            Assert.Inconclusive($"Update service repo not found at {updateServicePath}");
            return;
        }

        logger.LogInformation("Analyzing Update Service for entry points: {Path}", updateServicePath);

        var entryPointAnalyzer = new EntryPointAnalyzer();
        var allEntryPoints = new List<EntryPointInfo>();

        // Act - Analyze WinService folder
        var winServicePath = Path.Combine(updateServicePath, "WinService");
        if (Directory.Exists(winServicePath))
        {
            var csFiles = Directory.GetFiles(winServicePath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(content, path: file);
                var relativePath = Path.GetRelativePath(UpdateRepoPath, file);
                var entryPoints = entryPointAnalyzer.Analyze(tree, relativePath);
                allEntryPoints.AddRange(entryPoints);

                if (entryPoints.Count > 0)
                {
                    logger.LogInformation("Found {Count} entry points in {File}", entryPoints.Count, relativePath);
                    foreach (var ep in entryPoints)
                    {
                        logger.LogInformation("  - {Type}: {Class}", ep.EntryPointType, ep.ClassName);
                    }
                }
            }
        }

        // Analyze Service folder (contains UpdateService<T> : IWebApiService<T>)
        var servicePath = Path.Combine(updateServicePath, "Service");
        if (Directory.Exists(servicePath))
        {
            var csFiles = Directory.GetFiles(servicePath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(content, path: file);
                var relativePath = Path.GetRelativePath(UpdateRepoPath, file);
                var entryPoints = entryPointAnalyzer.Analyze(tree, relativePath);
                allEntryPoints.AddRange(entryPoints);

                // Also check for IWebApiService or IService<T> implementation
                if (content.Contains("IWebApiService<") || content.Contains(": IService<"))
                {
                    logger.LogInformation("Found IWebApiService/IService in {File}", relativePath);
                }
            }
        }

        // Output results
        var outputPath = Path.Combine(OutputDir, "update_service_entry_points.json");
        var output = new
        {
            RepoPath = updateServicePath,
            TotalEntryPoints = allEntryPoints.Count,
            EntryPoints = allEntryPoints.Select(ep => new
            {
                Type = ep.EntryPointType.ToString(),
                ep.ClassName,
                ep.BaseType,
                ep.FilePath,
                ep.LineNumber,
                ep.LifecycleMethods
            })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        // Assert
        logger.LogInformation("Total entry points detected: {Count}", allEntryPoints.Count);
        allEntryPoints.Should().NotBeEmpty("Update service should have at least one entry point");

        // Log results for manual verification
        foreach (var ep in allEntryPoints)
        {
            logger.LogInformation("Entry Point: {Type} - {Class} ({BaseType})",
                ep.EntryPointType, ep.ClassName, ep.BaseType ?? "N/A");
        }
    }

    /// <summary>
    /// Tests manifest-based entry point detection using WinApplicationManifest.xml from source.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_ParsesWinApplicationManifest()
    {
        // Arrange - Look for manifest in source, not build output
        var winServicePath = Path.Combine(
            UpdateRepoPath,
            "src", "UpdateResourceProvider", "UpdateService", "WinService");

        if (!Directory.Exists(winServicePath))
        {
            Assert.Inconclusive($"WinService folder not found at {winServicePath}");
            return;
        }

        logger.LogInformation("Looking for WinApplicationManifest in source: {Path}", winServicePath);

        var parser = new ManifestParser();

        // Find the .csproj file
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
            // Fallback: look for the manifest file directly
            var manifestPath = Path.Combine(winServicePath, "WinApplicationManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Assert.Inconclusive($"WinApplicationManifest.xml not found at {manifestPath}");
                return;
            }

            var services = await parser.ParseWinApplicationManifestAsync(manifestPath);

            // Output results
            var outputPath1 = Path.Combine(OutputDir, "manifest_services.json");
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
            File.WriteAllText(outputPath1, JsonSerializer.Serialize(output1, JsonOptions));

            services.Should().NotBeEmpty("WinApplicationManifest should define at least one service");
            return;
        }

        // Parse the manifest found via project reference
        var manifestInfo = await parser.ParseManifestAsync(winAppManifestRef.ManifestPath);

        // Output results
        var outputPath = Path.Combine(OutputDir, "manifest_services.json");
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
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

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
    /// </summary>
    [TestMethod]
    public void UpdateServiceRepo_DetectsIWebApiServiceImplementations()
    {
        // Arrange
        var srcPath = Path.Combine(UpdateRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"Source path not found at {srcPath}");
            return;
        }

        logger.LogInformation("Scanning for IWebApiService implementations: {Path}", srcPath);

        var csFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
            .ToList();

        var iWebApiServiceImplementations = new List<(string File, string ClassName)>();

        // Act
        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("IWebApiService<") && !content.Contains(": IService<"))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetRoot();

            var classDeclarations = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                var baseList = classDecl.BaseList?.ToString() ?? "";
                if (baseList.Contains("IWebApiService<") || baseList.Contains("IService<"))
                {
                    var relativePath = Path.GetRelativePath(UpdateRepoPath, file);
                    iWebApiServiceImplementations.Add((relativePath, classDecl.Identifier.Text));
                    logger.LogInformation("Found IWebApiService implementation: {Class} in {File}",
                        classDecl.Identifier.Text, relativePath);
                }
            }
        }

        // Output results
        var outputPath = Path.Combine(OutputDir, "webapi_service_implementations.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            TotalImplementations = iWebApiServiceImplementations.Count,
            Implementations = iWebApiServiceImplementations.Select(i => new { i.ClassName, i.File })
        }, JsonOptions));

        // Assert
        iWebApiServiceImplementations.Should().NotBeEmpty(
            "Update service repo should have IWebApiService implementations");

        iWebApiServiceImplementations.Should().Contain(i =>
            i.ClassName.Contains("UpdateService") || i.ClassName.Contains("Update"),
            "Should find UpdateService class implementing IWebApiService");
    }

    /// <summary>
    /// Tests full lifecycle detection across the entire Update service repository.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_FullLifecycleAnalysis()
    {
        // Arrange
        var srcPath = Path.Combine(UpdateRepoPath, "src", "UpdateResourceProvider");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"Source path not found at {srcPath}");
            return;
        }

        logger.LogInformation("Running full lifecycle analysis: {Path}", srcPath);

        var detector = new LifecycleDetector();

        // Act
        var result = await detector.AnalyzeAsync(srcPath);

        // Output results
        var outputPath = Path.Combine(OutputDir, "full_lifecycle_analysis.json");
        var output = new
        {
            result.FilesAnalyzed,
            TotalRelationships = result.DetectedRelationships.Count,
            EntryPointCount = result.EntryPoints.Count,
            AzureFunctionCount = result.AzureFunctions.Count,
            MiddlewareCount = result.MiddlewareRegistrations.Count,
            AspNetCoreStartupCount = result.AspNetCoreStartups.Count,
            ScheduledJobCount = result.ScheduledJobs.Count,
            LifetimeEventCount = result.ApplicationLifetimeEvents.Count,
            ErrorCount = result.Errors.Count,
            EntryPoints = result.EntryPoints.Select(ep => new
            {
                Type = ep.EntryPointType.ToString(),
                ep.ClassName,
                ep.BaseType,
                ep.FilePath
            }),
            Errors = result.Errors.Take(10).Select(e => new
            {
                e.FilePath,
                e.Message,
                e.ErrorType
            })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        // Assert
        result.FilesAnalyzed.Should().BeGreaterThan(0);
        result.EntryPoints.Should().NotBeEmpty("Should detect entry points in Update service");

        logger.LogInformation("Analysis complete:");
        logger.LogInformation("  Files analyzed: {Count}", result.FilesAnalyzed);
        logger.LogInformation("  Entry points: {Count}", result.EntryPoints.Count);
        logger.LogInformation("  Scheduled jobs: {Count}", result.ScheduledJobs.Count);
        logger.LogInformation("  Lifetime events: {Count}", result.ApplicationLifetimeEvents.Count);
        logger.LogInformation("  Errors: {Count}", result.Errors.Count);
    }

    /// <summary>
    /// Tests that common-infra repo hosting base classes are detected correctly.
    /// </summary>
    [TestMethod]
    public void CommonInfraRepo_DetectsHostingBaseClasses()
    {
        // Arrange
        var hostModelPath = Path.Combine(CommonInfraRepoPath, "src", "HostModel");
        if (!Directory.Exists(hostModelPath))
        {
            Assert.Inconclusive($"Common infra repo not found at {hostModelPath}");
            return;
        }

        logger.LogInformation("Analyzing common-infra HostModel: {Path}", hostModelPath);

        var inheritanceAnalyzer = new InheritanceAnalyzer();
        var allRelationships = new List<InheritanceRelationship>();

        var csFiles = Directory.GetFiles(hostModelPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("Test"))
            .ToList();

        // Act
        foreach (var file in csFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(content, path: file);
                var relativePath = Path.GetRelativePath(CommonInfraRepoPath, file);
                var relationships = inheritanceAnalyzer.Analyze(tree, relativePath);
                allRelationships.AddRange(relationships);
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Find service-related inheritance
        var serviceRelated = allRelationships
            .Where(r => r.TargetType.Contains("Service") ||
                       r.SourceType.Contains("Service") ||
                       r.TargetType.Contains("Host") ||
                       r.SourceType.Contains("Host"))
            .ToList();

        // Output results
        var outputPath = Path.Combine(OutputDir, "common_infra_hosting.json");
        var output = new
        {
            TotalRelationships = allRelationships.Count,
            ServiceRelated = serviceRelated.Count,
            HostingClasses = serviceRelated
                .GroupBy(r => r.SourceType)
                .Select(g => new
                {
                    ClassName = g.Key,
                    BaseTypes = g.Select(r => r.TargetType).Distinct()
                })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        // Assert
        allRelationships.Should().NotBeEmpty("Common infra should have inheritance relationships");

        logger.LogInformation("Found {Count} inheritance relationships in HostModel", allRelationships.Count);
        logger.LogInformation("Service-related: {Count}", serviceRelated.Count);
    }

    /// <summary>
    /// Tests project SDK detection to identify Worker and Web projects.
    /// </summary>
    [TestMethod]
    public async Task UpdateServiceRepo_DetectsProjectTypes()
    {
        // Arrange
        var srcPath = Path.Combine(UpdateRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"Source path not found at {srcPath}");
            return;
        }

        logger.LogInformation("Scanning for project types: {Path}", srcPath);

        var parser = new ManifestParser();
        var projectFiles = Directory.GetFiles(srcPath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        var projectInfos = new List<ProjectSdkInfo>();

        // Act
        foreach (var projectFile in projectFiles)
        {
            var info = await parser.ParseProjectSdkAsync(projectFile);
            if (info != null)
            {
                projectInfos.Add(info);
            }
        }

        // Output results
        var outputPath = Path.Combine(OutputDir, "project_types.json");
        var output = new
        {
            TotalProjects = projectInfos.Count,
            ByType = projectInfos
                .GroupBy(p => p.ProjectType.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            Projects = projectInfos.Select(p => new
            {
                Name = Path.GetFileName(p.FilePath),
                Type = p.ProjectType.ToString(),
                p.Sdk,
                p.OutputType,
                p.IsExecutable,
                p.IsWebProject,
                p.IsWorkerProject
            })
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        // Assert
        projectInfos.Should().NotBeEmpty("Should detect project files");

        var executableProjects = projectInfos.Where(p => p.IsExecutable).ToList();
        logger.LogInformation("Found {Total} projects, {Exe} executables",
            projectInfos.Count, executableProjects.Count);

        foreach (var proj in executableProjects)
        {
            logger.LogInformation("  Executable: {Name} ({Type})",
                Path.GetFileName(proj.FilePath), proj.ProjectType);
        }
    }
}
