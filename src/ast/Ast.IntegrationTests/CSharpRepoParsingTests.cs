// -----------------------------------------------------------------------
// <copyright file="CSharpRepoParsingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.IntegrationTests;

using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Core;
using Ast.Csharp;
using Ast.PowerShell;
using Microsoft.Extensions.Logging;

[TestClass]
public class CSharpRepoParsingTests
{
    // Windows path: E:\work\hci\update, WSL path: /mnt/e/work/hci/update
    private static readonly string csharpRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";
    private static readonly string outputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "csharp");

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static ILogger<CSharpRepoParsingTests> logger = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // Initialize logging with TestContext for colored console output
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<CSharpRepoParsingTests>();

        // Create output directory
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Output directory: {OutputDir}", outputDir);
    }

    [TestMethod]
    public void ParseCSharpRepo_ExtractsAllCSharpFiles()
    {
        // Arrange
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"C# repo not found at {srcPath}");
            return;
        }

        var csFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        logger.LogInformation("Found {FileCount} C# files to parse", csFiles.Count);

        // Act
        var results = new List<(string FilePath, HypergraphData Data)>();
        var errors = new List<(string FilePath, string Error)>();
        var filesProcessed = 0;

        foreach (var file in csFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);
                var extractor = new CSharpHyperedgeExtractor(relativePath);
                var data = extractor.ExtractToHypergraph(sourceCode);
                results.Add((relativePath, data));

                filesProcessed++;
                if (filesProcessed % 50 == 0 || filesProcessed == csFiles.Count)
                {
                    logger.LogInformation("Extraction progress: {Current}/{Total} files ({Percent:P0})",
                        filesProcessed, csFiles.Count, (double)filesProcessed / csFiles.Count);
                }
            }
            catch (Exception ex)
            {
                errors.Add((file, ex.Message));
                logger.LogWarning("Failed to parse {FilePath}: {Error}", file, ex.Message);
            }
        }

        // Assert
        results.Should().NotBeEmpty($"Should have parsed at least some files from {csFiles.Count} .cs files");

        // Output individual file results
        logger.LogInformation("Writing {ResultCount} output files...", results.Count);
        foreach (var (filePath, data) in results)
        {
            var safeFileName = filePath.Replace("\\", "_").Replace("/", "_").Replace(":", "");
            var outputPath = Path.Combine(outputDir, $"{safeFileName}.json");
            var json = JsonSerializer.Serialize(data, jsonOptions);
            File.WriteAllText(outputPath, json);
        }

        // Output summary
        var summary = new
        {
            TotalFiles = csFiles.Count,
            ParsedSuccessfully = results.Count,
            ParseErrors = errors.Count,
            TotalNodes = results.Sum(r => r.Data.Nodes.Count),
            TotalHyperedges = results.Sum(r => r.Data.Hyperedges.Count),
            HyperedgesByType = results
                .SelectMany(r => r.Data.Hyperedges)
                .GroupBy(e => e.EdgeType)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
            Errors = errors.Take(10).Select(e => new { e.FilePath, e.Error })
        };

        var summaryPath = Path.Combine(outputDir, "_summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, jsonOptions));

        logger.LogInformation("Parsed {Parsed}/{Total} C# files", results.Count, csFiles.Count);
        logger.LogInformation("Total nodes: {TotalNodes}", summary.TotalNodes);
        logger.LogInformation("Total hyperedges: {TotalHyperedges}", summary.TotalHyperedges);
        logger.LogInformation("Output written to: {OutputDir}", outputDir);

        if (errors.Count > 0)
        {
            logger.LogWarning("Encountered {ErrorCount} parse errors", errors.Count);
        }
    }

    [TestMethod]
    public void ParseCSharpRepo_ExtractsServiceRegistrations()
    {
        // Arrange
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"C# repo not found at {srcPath}");
            return;
        }

        // Look for Startup.cs or Program.cs files
        var startupFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .Where(f => f.EndsWith("Startup.cs") || f.EndsWith("Program.cs") ||
                       f.Contains("ServiceCollection") || f.Contains("DependencyInjection"))
            .ToList();

        logger.LogInformation("Found {FileCount} startup/DI files to analyze", startupFiles.Count);

        // Act
        var allServiceRegistrations = new List<Hyperedge>();
        var filesProcessed = 0;

        foreach (var file in startupFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);
                var extractor = new CSharpHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var registrations = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.ServiceRegistration)
                    .ToList();

                allServiceRegistrations.AddRange(registrations);

                filesProcessed++;
                if (filesProcessed % 10 == 0 || filesProcessed == startupFiles.Count)
                {
                    logger.LogDebug("Extraction progress: {Current}/{Total} files", filesProcessed, startupFiles.Count);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output service registrations
        var outputPath = Path.Combine(outputDir, "_service_registrations.json");
        var output = new
        {
            TotalRegistrations = allServiceRegistrations.Count,
            ByLifetime = allServiceRegistrations
                .GroupBy(r => r.Metadata.GetValueOrDefault("lifetime")?.ToString() ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            Registrations = allServiceRegistrations.Select(r => new
            {
                ServiceType = r.Metadata.GetValueOrDefault("service_type"),
                ImplementationType = r.Metadata.GetValueOrDefault("implementation_type"),
                Lifetime = r.Metadata.GetValueOrDefault("lifetime"),
                Method = r.Metadata.GetValueOrDefault("method")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} service registrations", allServiceRegistrations.Count);
    }

    [TestMethod]
    public void ParseCSharpRepo_ExtractsApiEndpoints()
    {
        // Arrange
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"C# repo not found at {srcPath}");
            return;
        }

        // Look for Controller files
        var controllerFiles = Directory.GetFiles(srcPath, "*Controller*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        logger.LogInformation("Found {FileCount} controller files to analyze", controllerFiles.Count);

        // Act
        var allEndpoints = new List<(string File, Hyperedge Endpoint)>();
        var filesProcessed = 0;

        foreach (var file in controllerFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);
                var extractor = new CSharpHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var endpoints = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.ApiEndpoint)
                    .ToList();

                foreach (var endpoint in endpoints)
                {
                    allEndpoints.Add((relativePath, endpoint));
                }

                filesProcessed++;
                if (filesProcessed % 10 == 0 || filesProcessed == controllerFiles.Count)
                {
                    logger.LogDebug("Extraction progress: {Current}/{Total} files", filesProcessed, controllerFiles.Count);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output API endpoints
        var outputPath = Path.Combine(outputDir, "_api_endpoints.json");
        var output = new
        {
            TotalEndpoints = allEndpoints.Count,
            ByHttpMethod = allEndpoints
                .GroupBy(e => e.Endpoint.Metadata.GetValueOrDefault("http_method")?.ToString() ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            Endpoints = allEndpoints.Select(e => new
            {
                File = e.File,
                HttpMethod = e.Endpoint.Metadata.GetValueOrDefault("http_method"),
                Route = e.Endpoint.Metadata.GetValueOrDefault("route"),
                Controller = e.Endpoint.Metadata.GetValueOrDefault("controller"),
                Action = e.Endpoint.Metadata.GetValueOrDefault("action")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} API endpoints", allEndpoints.Count);
    }

    [TestMethod]
    public void ParseCSharpRepo_ExtractsLoggingCalls()
    {
        // Arrange
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"C# repo not found at {srcPath}");
            return;
        }

        var csFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} C# files for logging calls", csFiles.Count);

        // Act
        var allLoggingCalls = new List<(string File, Hyperedge LogCall)>();
        var filesProcessed = 0;
        var filesWithLogs = 0;

        foreach (var file in csFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                if (!sourceCode.Contains("Log"))
                    continue;

                filesWithLogs++;
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);
                var extractor = new CSharpHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var logCalls = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.LoggingCall)
                    .ToList();

                foreach (var logCall in logCalls)
                {
                    allLoggingCalls.Add((relativePath, logCall));
                }

                filesProcessed++;
                if (filesProcessed % 50 == 0)
                {
                    logger.LogDebug("Extraction progress: {Current} files with logs processed", filesProcessed);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output logging analysis
        var outputPath = Path.Combine(outputDir, "_logging_analysis.json");
        var output = new
        {
            TotalLogCalls = allLoggingCalls.Count,
            ByLevel = allLoggingCalls
                .GroupBy(l => l.LogCall.Metadata.GetValueOrDefault("log_level")?.ToString() ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            SampleCalls = allLoggingCalls.Take(50).Select(l => new
            {
                File = l.File,
                Level = l.LogCall.Metadata.GetValueOrDefault("log_level"),
                Scope = l.LogCall.Metadata.GetValueOrDefault("scope"),
                HasException = l.LogCall.Metadata.GetValueOrDefault("has_exception"),
                StructuredFields = l.LogCall.Metadata.GetValueOrDefault("structured_fields")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} logging calls in {FileCount} files", allLoggingCalls.Count, filesWithLogs);
    }

    [TestMethod]
    public void ParseCSharpRepo_ExtractsExternalDependencies()
    {
        // Arrange
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"C# repo not found at {srcPath}");
            return;
        }

        var csFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} C# files for external dependencies", csFiles.Count);

        // Act
        var httpClientUsages = new List<(string File, Hyperedge Usage)>();
        var databaseAccesses = new List<(string File, Hyperedge Access)>();
        var fileSystemAccesses = new List<(string File, Hyperedge Access)>();
        var filesProcessed = 0;

        foreach (var file in csFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);
                var extractor = new CSharpHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                foreach (var edge in hyperedges)
                {
                    switch (edge.EdgeType)
                    {
                        case HyperedgeType.HttpClientUsage:
                            httpClientUsages.Add((relativePath, edge));
                            break;
                        case HyperedgeType.DatabaseAccess:
                            databaseAccesses.Add((relativePath, edge));
                            break;
                        case HyperedgeType.FileSystemAccess:
                            fileSystemAccesses.Add((relativePath, edge));
                            break;
                    }
                }

                filesProcessed++;
                if (filesProcessed % 50 == 0 || filesProcessed == csFiles.Count)
                {
                    logger.LogDebug("Extraction progress: {Current}/{Total} files ({Percent:P0})",
                        filesProcessed, csFiles.Count, (double)filesProcessed / csFiles.Count);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output external dependencies
        var outputPath = Path.Combine(outputDir, "_external_dependencies.json");
        var output = new
        {
            HttpClientUsages = new
            {
                Total = httpClientUsages.Count,
                ByMethod = httpClientUsages
                    .GroupBy(h => h.Usage.Metadata.GetValueOrDefault("http_method")?.ToString() ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                Samples = httpClientUsages.Take(20).Select(h => new
                {
                    File = h.File,
                    Method = h.Usage.Metadata.GetValueOrDefault("http_method"),
                    Url = h.Usage.Metadata.GetValueOrDefault("url")
                })
            },
            DatabaseAccesses = new
            {
                Total = databaseAccesses.Count,
                ByTechnology = databaseAccesses
                    .GroupBy(d => d.Access.Metadata.GetValueOrDefault("technology")?.ToString() ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                Samples = databaseAccesses.Take(20).Select(d => new
                {
                    File = d.File,
                    DbType = d.Access.Metadata.GetValueOrDefault("db_type"),
                    Technology = d.Access.Metadata.GetValueOrDefault("technology")
                })
            },
            FileSystemAccesses = new
            {
                Total = fileSystemAccesses.Count,
                ByOperation = fileSystemAccesses
                    .GroupBy(f => f.Access.Metadata.GetValueOrDefault("operation")?.ToString() ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                Samples = fileSystemAccesses.Take(20).Select(f => new
                {
                    File = f.File,
                    Operation = f.Access.Metadata.GetValueOrDefault("operation"),
                    Method = f.Access.Metadata.GetValueOrDefault("method")
                })
            }
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {HttpCount} HTTP client usages", httpClientUsages.Count);
        logger.LogInformation("Found {DbCount} database accesses", databaseAccesses.Count);
        logger.LogInformation("Found {FsCount} file system accesses", fileSystemAccesses.Count);
    }

    [TestMethod]
    public void ParseCSharpRepo_GeneratesNamedRepoGraph()
    {
        // Arrange
        if (!Directory.Exists(csharpRepoPath))
        {
            Assert.Inconclusive($"C# repo not found at {csharpRepoPath}");
            return;
        }

        logger.LogInformation("Processing repository: {RepoPath}", csharpRepoPath);

        // Act - Use RepositoryProcessor for full repo processing
        var processor = new RepositoryProcessor(csharpRepoPath);
        var filesProcessed = 0;
        var totalFiles = Directory.GetFiles(Path.Combine(csharpRepoPath, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .Count();

        processor.CSharpExtractor = (path, source) =>
        {
            filesProcessed++;
            if (filesProcessed % 50 == 0 || filesProcessed == totalFiles)
            {
                logger.LogDebug("Extraction progress: {Current}/{Total} files ({Percent:P0})",
                    filesProcessed, totalFiles, (double)filesProcessed / totalFiles);
            }

            var extractor = new CSharpHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        var data = processor.Process(verbose: true);

        // Assert - Verify metadata was extracted
        data.Metadata.Repository.Should().NotBeNull();
        data.Metadata.Repository!.Name.Should().NotBeNullOrEmpty();
        data.Metadata.Projects.Should().NotBeEmpty();

        // Output repo-level graph with repo name
        var repoName = data.Metadata.Repository.Name;
        var repoGraphPath = Path.Combine(outputDir, $"{repoName}.graph.json");
        HypergraphSerializer.SaveToJson(data, repoGraphPath);

        logger.LogInformation("Repository: {RepoName}", repoName);
        logger.LogInformation("  Languages: {Languages}", string.Join(", ", data.Metadata.Repository.Languages));
        logger.LogInformation("  Projects: {ProjectCount}", data.Metadata.Projects.Count);
        logger.LogInformation("  Total nodes: {NodeCount}", data.Nodes.Count);
        logger.LogInformation("  Total hyperedges: {EdgeCount}", data.Hyperedges.Count);
        logger.LogInformation("  Output: {OutputPath}", repoGraphPath);

        // Output individual project graphs
        foreach (var project in data.Metadata.Projects)
        {
            var projectNodes = data.Nodes.Values
                .Where(n => project.SourceFiles.Any(f => n.FilePath == f))
                .ToDictionary(n => n.Id);

            var projectNodeIds = projectNodes.Keys.ToHashSet();
            var projectEdges = data.Hyperedges
                .Where(e => e.NodeIds.Any(id => projectNodeIds.Contains(id)))
                .ToList();

            var projectData = new HypergraphData
            {
                Nodes = projectNodes,
                Hyperedges = projectEdges,
                Metadata = new HypergraphMetadata
                {
                    RepoPath = data.Metadata.RepoPath,
                    NumFiles = project.SourceFiles.Count,
                    NumCSharpNodes = projectNodes.Values.Count(n => n.Language == "csharp"),
                    NumPowerShellNodes = projectNodes.Values.Count(n => n.Language == "powershell"),
                    Files = project.SourceFiles,
                    Projects = [project]
                }
            };

            var projectGraphPath = Path.Combine(outputDir, $"{project.Name}.graph.json");
            HypergraphSerializer.SaveToJson(projectData, projectGraphPath);

            logger.LogDebug("  Project: {ProjectName} ({ProjectType})", project.Name, project.ProjectType);
            logger.LogDebug("    Nodes: {NodeCount}, Edges: {EdgeCount}", projectNodes.Count, projectEdges.Count);
        }
    }

    [TestMethod]
    public void ParseCSharpRepo_ExtractsProjectMetadata()
    {
        // Arrange
        if (!Directory.Exists(csharpRepoPath))
        {
            Assert.Inconclusive($"C# repo not found at {csharpRepoPath}");
            return;
        }

        logger.LogInformation("Extracting project metadata from: {RepoPath}", csharpRepoPath);

        // Act
        var extractor = new ProjectMetadataExtractor(csharpRepoPath);
        var repoMetadata = extractor.ExtractRepositoryMetadata();
        var projects = extractor.ExtractAllProjects();

        // Assert
        repoMetadata.Name.Should().NotBeNullOrEmpty();
        projects.Should().NotBeEmpty();

        // Output metadata
        var outputPath = Path.Combine(outputDir, "_project_metadata.json");
        var output = new
        {
            Repository = new
            {
                repoMetadata.Name,
                repoMetadata.RootPath,
                repoMetadata.Languages,
                repoMetadata.SolutionFiles,
                repoMetadata.GitRemoteUrl,
                repoMetadata.GitBranch,
                repoMetadata.GitCommitSha,
                repoMetadata.HasReadme,
                repoMetadata.HasLicense,
                repoMetadata.TotalSourceFiles
            },
            Projects = projects.Select(p => new
            {
                p.Name,
                p.ProjectPath,
                ProjectType = p.ProjectType.ToString(),
                p.Language,
                p.TargetFrameworks,
                p.OutputType,
                PackageReferences = p.PackageReferences.Select(pr => new
                {
                    pr.PackageId,
                    pr.Version
                }),
                p.ProjectReferences,
                p.Description,
                SourceFileCount = p.SourceFiles.Count
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Repository: {RepoName}", repoMetadata.Name);
        logger.LogInformation("  Path: {RepoPath}", repoMetadata.RootPath);
        logger.LogInformation("  Languages: {Languages}", string.Join(", ", repoMetadata.Languages));
        logger.LogInformation("  Solutions: {SolutionCount}", repoMetadata.SolutionFiles.Count);
        logger.LogInformation("  Projects: {ProjectCount}", projects.Count);

        foreach (var project in projects)
        {
            logger.LogDebug("  - {ProjectName} ({ProjectType})", project.Name, project.ProjectType);
            logger.LogDebug("    Target: {TargetFrameworks}", string.Join(", ", project.TargetFrameworks));
            logger.LogDebug("    Packages: {PackageCount}, Files: {FileCount}",
                project.PackageReferences.Count, project.SourceFiles.Count);
        }
    }
}
