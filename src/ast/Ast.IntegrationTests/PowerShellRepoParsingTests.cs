// -----------------------------------------------------------------------
// <copyright file="PowerShellRepoParsingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.IntegrationTests;

using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Core;
using Ast.PowerShell;
using Microsoft.Extensions.Logging;

[TestClass]
public class PowerShellRepoParsingTests
{
    // Windows path: E:\work\hub\sln\Deploy, WSL path: /mnt/e/work/hub/sln/Deploy
    private static readonly string powerShellRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\sln\Deploy"
        : "/mnt/e/work/hub/sln/Deploy";
    private static readonly string outputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "powershell");

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static ILogger<PowerShellRepoParsingTests> logger = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // Initialize logging with TestContext for colored console output
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<PowerShellRepoParsingTests>();

        // Create output directory
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Output directory: {OutputDir}", outputDir);
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsAllPowerShellFiles()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psd1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        logger.LogInformation("Found {FileCount} PowerShell files to parse", psFiles.Count);

        // Act
        var results = new List<(string FilePath, HypergraphData Data)>();
        var errors = new List<(string FilePath, string Error)>();
        var filesProcessed = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(powerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var data = extractor.ExtractToHypergraph(sourceCode);
                results.Add((relativePath, data));

                filesProcessed++;
                if (filesProcessed % 20 == 0 || filesProcessed == psFiles.Count)
                {
                    logger.LogInformation("Extraction progress: {Current}/{Total} files ({Percent:P0})",
                        filesProcessed, psFiles.Count, (double)filesProcessed / psFiles.Count);
                }
            }
            catch (Exception ex)
            {
                errors.Add((file, ex.Message));
                logger.LogWarning("Failed to parse {FilePath}: {Error}", file, ex.Message);
            }
        }

        // Assert
        results.Should().NotBeEmpty($"Should have parsed at least some files from {psFiles.Count} PowerShell files");

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
            TotalFiles = psFiles.Count,
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

        logger.LogInformation("Parsed {Parsed}/{Total} PowerShell files", results.Count, psFiles.Count);
        logger.LogInformation("Total nodes: {TotalNodes}", summary.TotalNodes);
        logger.LogInformation("Total hyperedges: {TotalHyperedges}", summary.TotalHyperedges);
        logger.LogInformation("Output written to: {OutputDir}", outputDir);

        if (errors.Count > 0)
        {
            logger.LogWarning("Encountered {ErrorCount} parse errors", errors.Count);
        }
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsModuleImports()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} PowerShell files for module imports", psFiles.Count);

        // Act
        var allModuleImports = new List<(string File, Hyperedge Import)>();
        var filesProcessed = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                if (!sourceCode.Contains("Import-Module") && !sourceCode.Contains("using module"))
                    continue;

                filesProcessed++;
                var relativePath = Path.GetRelativePath(powerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var imports = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.ModuleImport)
                    .ToList();

                foreach (var import in imports)
                {
                    allModuleImports.Add((relativePath, import));
                }

                if (filesProcessed % 20 == 0)
                {
                    logger.LogDebug("Extraction progress: {Current} files with imports processed", filesProcessed);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output module imports
        var outputPath = Path.Combine(outputDir, "_module_imports.json");
        var output = new
        {
            TotalImports = allModuleImports.Count,
            UniqueModules = allModuleImports
                .Select(i => i.Import.Metadata.GetValueOrDefault("module_name")?.ToString())
                .Where(m => m != null)
                .Distinct()
                .OrderBy(m => m)
                .ToList(),
            Imports = allModuleImports.Select(i => new
            {
                i.File,
                ModuleName = i.Import.Metadata.GetValueOrDefault("module_name"),
                Version = i.Import.Metadata.GetValueOrDefault("version"),
                Prefix = i.Import.Metadata.GetValueOrDefault("prefix"),
                IsGlobal = i.Import.Metadata.GetValueOrDefault("is_global")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} module imports", allModuleImports.Count);
        logger.LogInformation("Unique modules: {UniqueCount}", output.UniqueModules.Count);
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsRemoteExecution()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} PowerShell files for remote execution", psFiles.Count);

        // Act
        var allRemoteExecutions = new List<(string File, Hyperedge Execution)>();
        var filesProcessed = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                if (!sourceCode.Contains("Invoke-Command") &&
                    !sourceCode.Contains("Enter-PSSession") &&
                    !sourceCode.Contains("New-PSSession"))
                    continue;

                filesProcessed++;
                var relativePath = Path.GetRelativePath(powerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var remoteExecs = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.RemoteExecution)
                    .ToList();

                foreach (var exec in remoteExecs)
                {
                    allRemoteExecutions.Add((relativePath, exec));
                }

                if (filesProcessed % 10 == 0)
                {
                    logger.LogDebug("Extraction progress: {Current} files with remote execution processed", filesProcessed);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output remote executions
        var outputPath = Path.Combine(outputDir, "_remote_executions.json");
        var output = new
        {
            TotalRemoteExecutions = allRemoteExecutions.Count,
            ByCmdlet = allRemoteExecutions
                .GroupBy(r => r.Execution.Metadata.GetValueOrDefault("cmdlet")?.ToString() ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            WithCredentials = allRemoteExecutions.Count(r =>
                r.Execution.Metadata.GetValueOrDefault("credential") != null),
            WithSSL = allRemoteExecutions.Count(r =>
                r.Execution.Metadata.GetValueOrDefault("use_ssl") is true),
            Executions = allRemoteExecutions.Select(r => new
            {
                r.File,
                Cmdlet = r.Execution.Metadata.GetValueOrDefault("cmdlet"),
                ComputerName = r.Execution.Metadata.GetValueOrDefault("computer_name"),
                HasCredential = r.Execution.Metadata.GetValueOrDefault("credential") != null,
                UseSSL = r.Execution.Metadata.GetValueOrDefault("use_ssl"),
                HasScriptBlock = r.Execution.Metadata.GetValueOrDefault("has_script_block"),
                HasScriptFile = r.Execution.Metadata.GetValueOrDefault("has_script_file")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} remote execution calls", allRemoteExecutions.Count);
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsFunctionDefinitions()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} PowerShell files for function definitions", psFiles.Count);

        // Act
        var allFunctions = new List<(string File, Hyperedge Function)>();
        var filesProcessed = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(powerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var functions = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.FunctionDefinition)
                    .ToList();

                foreach (var func in functions)
                {
                    allFunctions.Add((relativePath, func));
                }

                filesProcessed++;
                if (filesProcessed % 20 == 0 || filesProcessed == psFiles.Count)
                {
                    logger.LogDebug("Extraction progress: {Current}/{Total} files", filesProcessed, psFiles.Count);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Extract function names from nodes
        var functionNames = allFunctions
            .SelectMany(f => f.Function.NodeIds)
            .Distinct()
            .ToList();

        // Output functions
        var outputPath = Path.Combine(outputDir, "_functions.json");
        var output = new
        {
            TotalFunctions = allFunctions.Count,
            ByFile = allFunctions
                .GroupBy(f => f.File)
                .ToDictionary(g => g.Key, g => g.Count()),
            Functions = allFunctions.Select(f => new
            {
                f.File,
                IsFilter = f.Function.Metadata.GetValueOrDefault("is_filter"),
                IsWorkflow = f.Function.Metadata.GetValueOrDefault("is_workflow"),
                NodeCount = f.Function.NodeIds.Count
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} function definitions", allFunctions.Count);
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsPipelines()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} PowerShell files for pipelines", psFiles.Count);

        // Act
        var allPipelines = new List<(string File, Hyperedge Pipeline)>();
        var filesProcessed = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(powerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var pipelines = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.Pipeline)
                    .ToList();

                foreach (var pipeline in pipelines)
                {
                    allPipelines.Add((relativePath, pipeline));
                }

                filesProcessed++;
                if (filesProcessed % 20 == 0 || filesProcessed == psFiles.Count)
                {
                    logger.LogDebug("Extraction progress: {Current}/{Total} files", filesProcessed, psFiles.Count);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output pipelines
        var outputPath = Path.Combine(outputDir, "_pipelines.json");
        var output = new
        {
            TotalPipelines = allPipelines.Count,
            ByLength = allPipelines
                .GroupBy(p => p.Pipeline.Metadata.GetValueOrDefault("pipeline_length")?.ToString() ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count()),
            LongestPipelines = allPipelines
                .OrderByDescending(p =>
                {
                    var len = p.Pipeline.Metadata.GetValueOrDefault("pipeline_length");
                    return len is int i ? i : 0;
                })
                .Take(20)
                .Select(p => new
                {
                    p.File,
                    Length = p.Pipeline.Metadata.GetValueOrDefault("pipeline_length"),
                    Stages = p.Pipeline.NodeIds.Count
                })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {Count} pipelines", allPipelines.Count);
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsCmdletCalls()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        logger.LogInformation("Scanning {FileCount} PowerShell files for cmdlet calls", psFiles.Count);

        // Act
        var cmdletUsage = new Dictionary<string, int>();
        var totalCmdletCalls = 0;
        var filesProcessed = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(powerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var cmdletCalls = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.CmdletCall)
                    .ToList();

                totalCmdletCalls += cmdletCalls.Count;

                // Extract cmdlet names from the first node (cmdlet role)
                foreach (var call in cmdletCalls)
                {
                    var cmdletNodeId = call.Roles.FirstOrDefault(r => r.Value == "cmdlet").Key;
                    if (cmdletNodeId != null && nodes.TryGetValue(cmdletNodeId, out var cmdletNode))
                    {
                        var cmdletName = cmdletNode.Name;
                        if (!cmdletUsage.ContainsKey(cmdletName))
                            cmdletUsage[cmdletName] = 0;
                        cmdletUsage[cmdletName]++;
                    }
                }

                filesProcessed++;
                if (filesProcessed % 20 == 0 || filesProcessed == psFiles.Count)
                {
                    logger.LogDebug("Extraction progress: {Current}/{Total} files, {CmdletCount} cmdlet calls found",
                        filesProcessed, psFiles.Count, totalCmdletCalls);
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output cmdlet usage
        var outputPath = Path.Combine(outputDir, "_cmdlet_usage.json");
        var output = new
        {
            TotalCmdletCalls = totalCmdletCalls,
            UniqueCmdlets = cmdletUsage.Count,
            TopCmdlets = cmdletUsage
                .OrderByDescending(kv => kv.Value)
                .Take(50)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            AllCmdlets = cmdletUsage
                .OrderBy(kv => kv.Key)
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Found {TotalCalls} cmdlet calls ({UniqueCmdlets} unique cmdlets)",
            totalCmdletCalls, cmdletUsage.Count);
    }

    [TestMethod]
    public void ParsePowerShellRepo_GeneratesNamedRepoGraph()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        logger.LogInformation("Processing repository: {RepoPath}", powerShellRepoPath);

        // Count files for progress tracking
        var totalFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .Count();

        // Act - Use RepositoryProcessor for full repo processing
        var processor = new RepositoryProcessor(powerShellRepoPath);
        var filesProcessed = 0;

        processor.PowerShellExtractor = (path, source) =>
        {
            filesProcessed++;
            if (filesProcessed % 20 == 0 || filesProcessed == totalFiles)
            {
                logger.LogDebug("Extraction progress: {Current}/{Total} files ({Percent:P0})",
                    filesProcessed, totalFiles, (double)filesProcessed / totalFiles);
            }

            var extractor = new PowerShellHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        var data = processor.Process(verbose: true);

        // Assert - Verify metadata was extracted
        data.Metadata.Repository.Should().NotBeNull();
        data.Metadata.Repository!.Name.Should().NotBeNullOrEmpty();

        // Output repo-level graph with repo name
        var repoName = data.Metadata.Repository.Name;
        var repoGraphPath = Path.Combine(outputDir, $"{repoName}.graph.json");
        HypergraphSerializer.SaveToJson(data, repoGraphPath);

        logger.LogInformation("Repository: {RepoName}", repoName);
        logger.LogInformation("  Languages: {Languages}", string.Join(", ", data.Metadata.Repository.Languages));
        logger.LogInformation("  Projects/Modules: {ProjectCount}", data.Metadata.Projects.Count);
        logger.LogInformation("  Total nodes: {NodeCount}", data.Nodes.Count);
        logger.LogInformation("  Total hyperedges: {EdgeCount}", data.Hyperedges.Count);
        logger.LogInformation("  Output: {OutputPath}", repoGraphPath);

        // Output individual module/script graphs
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
                    NumPowerShellNodes = projectNodes.Values.Count(n => n.Language == "powershell"),
                    Files = project.SourceFiles,
                    Projects = [project]
                }
            };

            var projectGraphPath = Path.Combine(outputDir, $"{project.Name}.graph.json");
            HypergraphSerializer.SaveToJson(projectData, projectGraphPath);

            logger.LogDebug("  Module: {ModuleName} ({ModuleType})", project.Name, project.ProjectType);
            logger.LogDebug("    Version: {Version}, Nodes: {NodeCount}, Edges: {EdgeCount}",
                project.ModuleVersion ?? "N/A", projectNodes.Count, projectEdges.Count);
        }
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsModuleMetadata()
    {
        // Arrange
        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {powerShellRepoPath}");
            return;
        }

        logger.LogInformation("Extracting module metadata from: {RepoPath}", powerShellRepoPath);

        // Act
        var extractor = new ProjectMetadataExtractor(powerShellRepoPath);
        var repoMetadata = extractor.ExtractRepositoryMetadata();
        var projects = extractor.ExtractAllProjects();

        // Assert
        repoMetadata.Name.Should().NotBeNullOrEmpty();

        // Output metadata
        var outputPath = Path.Combine(outputDir, "_module_metadata.json");
        var output = new
        {
            Repository = new
            {
                repoMetadata.Name,
                repoMetadata.RootPath,
                repoMetadata.Languages,
                repoMetadata.GitRemoteUrl,
                repoMetadata.GitBranch,
                repoMetadata.HasReadme,
                repoMetadata.HasLicense,
                repoMetadata.TotalSourceFiles
            },
            Modules = projects.Where(p => p.ProjectType == ProjectType.PowerShellModule).Select(p => new
            {
                p.Name,
                p.ProjectPath,
                p.ModuleVersion,
                p.Author,
                p.Description,
                RequiredModules = p.RequiredModules.Select(m => new
                {
                    m.ModuleName,
                    m.ModuleVersion,
                    m.RequiredVersion
                }),
                SourceFileCount = p.SourceFiles.Count
            }),
            Scripts = projects.Where(p => p.ProjectType == ProjectType.PowerShellScript).Select(p => new
            {
                p.Name,
                p.ProjectPath
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, jsonOptions));

        logger.LogInformation("Repository: {RepoName}", repoMetadata.Name);
        logger.LogInformation("  Path: {RepoPath}", repoMetadata.RootPath);
        logger.LogInformation("  Languages: {Languages}", string.Join(", ", repoMetadata.Languages));
        logger.LogInformation("  Modules: {ModuleCount}", projects.Count(p => p.ProjectType == ProjectType.PowerShellModule));
        logger.LogInformation("  Scripts: {ScriptCount}", projects.Count(p => p.ProjectType == ProjectType.PowerShellScript));

        foreach (var project in projects.Where(p => p.ProjectType == ProjectType.PowerShellModule))
        {
            logger.LogDebug("  Module: {ModuleName}", project.Name);
            logger.LogDebug("    Version: {Version}, Author: {Author}",
                project.ModuleVersion ?? "N/A", project.Author ?? "N/A");
            logger.LogDebug("    Required modules: {RequiredCount}, Files: {FileCount}",
                project.RequiredModules.Count, project.SourceFiles.Count);
        }
    }
}