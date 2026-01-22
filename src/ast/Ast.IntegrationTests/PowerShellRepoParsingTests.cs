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

[TestClass]
public class PowerShellRepoParsingTests
{
    // Windows path: E:\work\hub\sln\Deploy, WSL path: /mnt/e/work/hub/sln/Deploy
    private static readonly string PowerShellRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\sln\Deploy"
        : "/mnt/e/work/hub/sln/Deploy";
    private static readonly string OutputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "powershell");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // Create output directory
        if (Directory.Exists(OutputDir))
        {
            Directory.Delete(OutputDir, recursive: true);
        }
        Directory.CreateDirectory(OutputDir);
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsAllPowerShellFiles()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(PowerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psd1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        // Act
        var results = new List<(string FilePath, HypergraphData Data)>();
        var errors = new List<(string FilePath, string Error)>();

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(PowerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var data = extractor.ExtractToHypergraph(sourceCode);
                results.Add((relativePath, data));
            }
            catch (Exception ex)
            {
                errors.Add((file, ex.Message));
            }
        }

        // Assert
        results.Should().NotBeEmpty($"Should have parsed at least some files from {psFiles.Count} PowerShell files");

        // Output individual file results
        foreach (var (filePath, data) in results)
        {
            var safeFileName = filePath.Replace("\\", "_").Replace("/", "_").Replace(":", "");
            var outputPath = Path.Combine(OutputDir, $"{safeFileName}.json");
            var json = JsonSerializer.Serialize(data, JsonOptions);
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

        var summaryPath = Path.Combine(OutputDir, "_summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, JsonOptions));

        Console.WriteLine($"Parsed {results.Count}/{psFiles.Count} PowerShell files");
        Console.WriteLine($"Total nodes: {summary.TotalNodes}");
        Console.WriteLine($"Total hyperedges: {summary.TotalHyperedges}");
        Console.WriteLine($"Output written to: {OutputDir}");
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsModuleImports()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(PowerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        // Act
        var allModuleImports = new List<(string File, Hyperedge Import)>();

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                if (!sourceCode.Contains("Import-Module") && !sourceCode.Contains("using module"))
                    continue;

                var relativePath = Path.GetRelativePath(PowerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var imports = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.ModuleImport)
                    .ToList();

                foreach (var import in imports)
                {
                    allModuleImports.Add((relativePath, import));
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output module imports
        var outputPath = Path.Combine(OutputDir, "_module_imports.json");
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
                File = i.File,
                ModuleName = i.Import.Metadata.GetValueOrDefault("module_name"),
                Version = i.Import.Metadata.GetValueOrDefault("version"),
                Prefix = i.Import.Metadata.GetValueOrDefault("prefix"),
                IsGlobal = i.Import.Metadata.GetValueOrDefault("is_global")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        Console.WriteLine($"Found {allModuleImports.Count} module imports");
        Console.WriteLine($"Unique modules: {output.UniqueModules.Count}");
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsRemoteExecution()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(PowerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        // Act
        var allRemoteExecutions = new List<(string File, Hyperedge Execution)>();

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                if (!sourceCode.Contains("Invoke-Command") &&
                    !sourceCode.Contains("Enter-PSSession") &&
                    !sourceCode.Contains("New-PSSession"))
                    continue;

                var relativePath = Path.GetRelativePath(PowerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var remoteExecs = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.RemoteExecution)
                    .ToList();

                foreach (var exec in remoteExecs)
                {
                    allRemoteExecutions.Add((relativePath, exec));
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output remote executions
        var outputPath = Path.Combine(OutputDir, "_remote_executions.json");
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
                File = r.File,
                Cmdlet = r.Execution.Metadata.GetValueOrDefault("cmdlet"),
                ComputerName = r.Execution.Metadata.GetValueOrDefault("computer_name"),
                HasCredential = r.Execution.Metadata.GetValueOrDefault("credential") != null,
                UseSSL = r.Execution.Metadata.GetValueOrDefault("use_ssl"),
                HasScriptBlock = r.Execution.Metadata.GetValueOrDefault("has_script_block"),
                HasScriptFile = r.Execution.Metadata.GetValueOrDefault("has_script_file")
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        Console.WriteLine($"Found {allRemoteExecutions.Count} remote execution calls");
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsFunctionDefinitions()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(PowerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        // Act
        var allFunctions = new List<(string File, Hyperedge Function)>();

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(PowerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var functions = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.FunctionDefinition)
                    .ToList();

                foreach (var func in functions)
                {
                    allFunctions.Add((relativePath, func));
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
        var outputPath = Path.Combine(OutputDir, "_functions.json");
        var output = new
        {
            TotalFunctions = allFunctions.Count,
            ByFile = allFunctions
                .GroupBy(f => f.File)
                .ToDictionary(g => g.Key, g => g.Count()),
            Functions = allFunctions.Select(f => new
            {
                File = f.File,
                IsFilter = f.Function.Metadata.GetValueOrDefault("is_filter"),
                IsWorkflow = f.Function.Metadata.GetValueOrDefault("is_workflow"),
                NodeCount = f.Function.NodeIds.Count
            })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        Console.WriteLine($"Found {allFunctions.Count} function definitions");
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsPipelines()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(PowerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        // Act
        var allPipelines = new List<(string File, Hyperedge Pipeline)>();

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(PowerShellRepoPath, file);
                var extractor = new PowerShellHyperedgeExtractor(relativePath);
                var (nodes, hyperedges) = extractor.Extract(sourceCode);

                var pipelines = hyperedges
                    .Where(e => e.EdgeType == HyperedgeType.Pipeline)
                    .ToList();

                foreach (var pipeline in pipelines)
                {
                    allPipelines.Add((relativePath, pipeline));
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output pipelines
        var outputPath = Path.Combine(OutputDir, "_pipelines.json");
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
                    File = p.File,
                    Length = p.Pipeline.Metadata.GetValueOrDefault("pipeline_length"),
                    Stages = p.Pipeline.NodeIds.Count
                })
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        Console.WriteLine($"Found {allPipelines.Count} pipelines");
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsCmdletCalls()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        var psFiles = Directory.GetFiles(PowerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(PowerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\.git\\"))
            .ToList();

        // Act
        var cmdletUsage = new Dictionary<string, int>();
        var totalCmdletCalls = 0;

        foreach (var file in psFiles)
        {
            try
            {
                var sourceCode = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(PowerShellRepoPath, file);
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
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Output cmdlet usage
        var outputPath = Path.Combine(OutputDir, "_cmdlet_usage.json");
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

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        Console.WriteLine($"Found {totalCmdletCalls} cmdlet calls ({cmdletUsage.Count} unique cmdlets)");
    }

    [TestMethod]
    public void ParsePowerShellRepo_GeneratesNamedRepoGraph()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        // Act - Use RepositoryProcessor for full repo processing
        var processor = new RepositoryProcessor(PowerShellRepoPath);
        processor.PowerShellExtractor = (path, source) =>
        {
            var extractor = new PowerShellHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        var data = processor.Process(verbose: true);

        // Assert - Verify metadata was extracted
        data.Metadata.Repository.Should().NotBeNull();
        data.Metadata.Repository!.Name.Should().NotBeNullOrEmpty();

        // Output repo-level graph with repo name
        var repoName = data.Metadata.Repository.Name;
        var repoGraphPath = Path.Combine(OutputDir, $"{repoName}.graph.json");
        HypergraphSerializer.SaveToJson(data, repoGraphPath);

        Console.WriteLine($"Repository: {repoName}");
        Console.WriteLine($"  Languages: {string.Join(", ", data.Metadata.Repository.Languages)}");
        Console.WriteLine($"  Projects/Modules: {data.Metadata.Projects.Count}");
        Console.WriteLine($"  Total nodes: {data.Nodes.Count}");
        Console.WriteLine($"  Total hyperedges: {data.Hyperedges.Count}");
        Console.WriteLine($"  Output: {repoGraphPath}");

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

            var projectGraphPath = Path.Combine(OutputDir, $"{project.Name}.graph.json");
            HypergraphSerializer.SaveToJson(projectData, projectGraphPath);

            Console.WriteLine($"  Module: {project.Name}");
            Console.WriteLine($"    Type: {project.ProjectType}");
            Console.WriteLine($"    Version: {project.ModuleVersion ?? "N/A"}");
            Console.WriteLine($"    Nodes: {projectNodes.Count}");
            Console.WriteLine($"    Edges: {projectEdges.Count}");
            Console.WriteLine($"    Output: {projectGraphPath}");
        }
    }

    [TestMethod]
    public void ParsePowerShellRepo_ExtractsModuleMetadata()
    {
        // Arrange
        if (!Directory.Exists(PowerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found at {PowerShellRepoPath}");
            return;
        }

        // Act
        var extractor = new ProjectMetadataExtractor(PowerShellRepoPath);
        var repoMetadata = extractor.ExtractRepositoryMetadata();
        var projects = extractor.ExtractAllProjects();

        // Assert
        repoMetadata.Name.Should().NotBeNullOrEmpty();

        // Output metadata
        var outputPath = Path.Combine(OutputDir, "_module_metadata.json");
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

        File.WriteAllText(outputPath, JsonSerializer.Serialize(output, JsonOptions));

        Console.WriteLine($"Repository: {repoMetadata.Name}");
        Console.WriteLine($"  Path: {repoMetadata.RootPath}");
        Console.WriteLine($"  Languages: {string.Join(", ", repoMetadata.Languages)}");
        Console.WriteLine($"  Modules: {projects.Count(p => p.ProjectType == ProjectType.PowerShellModule)}");
        Console.WriteLine($"  Scripts: {projects.Count(p => p.ProjectType == ProjectType.PowerShellScript)}");

        foreach (var project in projects.Where(p => p.ProjectType == ProjectType.PowerShellModule))
        {
            Console.WriteLine($"  Module: {project.Name}");
            Console.WriteLine($"    Version: {project.ModuleVersion ?? "N/A"}");
            Console.WriteLine($"    Author: {project.Author ?? "N/A"}");
            Console.WriteLine($"    Required modules: {project.RequiredModules.Count}");
            Console.WriteLine($"    Files: {project.SourceFiles.Count}");
        }
    }
}
