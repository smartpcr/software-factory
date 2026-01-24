// -----------------------------------------------------------------------
// <copyright file="PowerShellRepoLspTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.IntegrationTests;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ast.Core;
using Ast.Lsp.IntegrationTests.Models;
using Ast.PowerShell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// LSP integration tests for PowerShell repository analysis.
/// Extracts module dependencies, class hierarchies, and generates interaction diagrams.
/// </summary>
[TestClass]
[TestCategory("lsp")]
[TestCategory("integration")]
public partial class PowerShellRepoLspTests
{
    // Windows path: E:\work\hub\sln\Deploy, WSL path: /mnt/e/work/hub/sln/Deploy
    private static readonly string powerShellRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\sln\Deploy"
        : "/mnt/e/work/hub/sln/Deploy";

    private static readonly string outputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "lsp", "powershell");

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static ILogger<PowerShellRepoLspTests> logger = null!;
    private static LspServerManager? serverManager;
    private static ILspClient? lspClient;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext context)
    {
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<PowerShellRepoLspTests>();

        // Create output directory
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Output directory: {outputDir}", outputDir);

        // Check if repo exists
        if (!Directory.Exists(powerShellRepoPath))
        {
            logger.LogWarning("PowerShell repo not found at {Path}, tests will be skipped", powerShellRepoPath);
            return;
        }

        // Initialize LSP server manager
        try
        {
            var config = new LspConfiguration
            {
                Enabled = true,
                TimeoutMs = 60000,
                PowerShell = new PowerShellLspConfiguration
                {
                    Enabled = true,
                    EditorServicesPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "PowerShellEditorServices",
                        "PowerShellEditorServices")
                }
            };

            var options = Options.Create(config);
            serverManager = new LspServerManager(options, TestLoggerFactory.Instance);

            logger.LogInformation("Starting PowerShell LSP server for {Path}...", powerShellRepoPath);
            lspClient = await serverManager.GetOrCreateClientAsync(LspLanguage.PowerShell, powerShellRepoPath);
            logger.LogInformation("PowerShell LSP server started successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start PowerShell LSP server. Tests will be skipped. " +
                "Ensure PowerShell Editor Services is available.");
            lspClient = null;
        }
    }

    [ClassCleanup]
    public static async Task ClassCleanupAsync()
    {
        if (serverManager != null)
        {
            logger.LogInformation("Shutting down LSP servers...");
            await serverManager.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task GenerateModuleInteractionDiagramAsync()
    {
        // Arrange
        if (lspClient == null)
        {
            Assert.Inconclusive("PowerShell LSP server not available");
            return;
        }

        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found: {powerShellRepoPath}");
            return;
        }

        var diagram = new InteractionDiagram
        {
            RepoName = Path.GetFileName(powerShellRepoPath)
        };

        // Act - Find all modules
        logger.LogInformation("Discovering PowerShell modules...");
        await this.DiscoverModulesAsync(diagram);

        // Act - Extract interactions
        logger.LogInformation("Analyzing module interactions via LSP...");
        await ExtractInteractionsAsync(diagram);

        // Generate Mermaid diagrams
        diagram.GenerateMermaid();

        // Output results
        var outputPath = Path.Combine(outputDir, "_interaction_diagram.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(diagram, jsonOptions));

        var sequencePath = Path.Combine(outputDir, "_sequence_diagram.mmd");
        await File.WriteAllTextAsync(sequencePath, diagram.MermaidSequence);

        var classPath = Path.Combine(outputDir, "_class_diagram.mmd");
        await File.WriteAllTextAsync(classPath, diagram.MermaidClass);

        // Assert
        logger.LogInformation("Found {Modules} modules with {Interactions} interactions",
            diagram.Modules.Count, diagram.Interactions.Count);

        diagram.Modules.Should().NotBeEmpty("Should have found at least one module");
        logger.LogInformation("Output written to: {Path}", outputPath);
    }

    [TestMethod]
    public async Task ExtractModuleHierarchyAsync()
    {
        // Arrange
        if (lspClient == null)
        {
            Assert.Inconclusive("PowerShell LSP server not available");
            return;
        }

        if (!Directory.Exists(powerShellRepoPath))
        {
            Assert.Inconclusive($"PowerShell repo not found: {powerShellRepoPath}");
            return;
        }

        var diagram = new ComponentDiagram
        {
            RepoName = Path.GetFileName(powerShellRepoPath)
        };

        // Act - Build module dependency graph
        logger.LogInformation("Building module dependency graph...");
        await BuildModuleDependencyGraphAsync(diagram);

        // Generate Mermaid
        diagram.GenerateMermaid();

        // Output
        var outputPath = Path.Combine(outputDir, "_module_hierarchy.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(diagram, jsonOptions));

        var mermaidPath = Path.Combine(outputDir, "_module_hierarchy.mmd");
        await File.WriteAllTextAsync(mermaidPath, diagram.Mermaid);

        // Assert
        logger.LogInformation("Generated module hierarchy with {Nodes} nodes and {Edges} edges",
            diagram.Nodes.Count, diagram.Edges.Count);

        logger.LogInformation("Output written to: {Path}", outputPath);
    }

    private async Task DiscoverModulesAsync(InteractionDiagram diagram)
    {
        // Find all .psm1 and .psd1 files
        var moduleFiles = Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psd1", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .ToList();

        logger.LogDebug("Found {Count} module files", moduleFiles.Count);

        // Group by module (psm1 and psd1 pairs)
        var moduleGroups = moduleFiles
            .GroupBy(f => Path.GetFileNameWithoutExtension(f))
            .Take(100) // Limit for performance
            .ToList();

        foreach (var group in moduleGroups)
        {
            var moduleName = group.Key;
            var psm1File = group.FirstOrDefault(f => f.EndsWith(".psm1"));
            var psd1File = group.FirstOrDefault(f => f.EndsWith(".psd1"));

            var moduleInfo = new ModuleInfo
            {
                Name = moduleName,
                FilePath = psm1File != null
                    ? Path.GetRelativePath(powerShellRepoPath, psm1File)
                    : Path.GetRelativePath(powerShellRepoPath, psd1File!)
            };

            // Parse manifest for metadata
            if (psd1File != null)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(psd1File);
                    ParseModuleManifest(content, moduleInfo);
                }
                catch
                {
                    // Skip if can't parse
                }
            }

            // Parse module for classes and functions
            if (psm1File != null)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(psm1File);
                    await ParseModuleContentAsync(psm1File, content, moduleInfo);
                }
                catch
                {
                    // Skip if can't parse
                }
            }

            diagram.Modules.Add(moduleInfo);
        }
    }

    private static void ParseModuleManifest(string content, ModuleInfo moduleInfo)
    {
        // Extract version
        var versionMatch = MyRegex().Match(content);
        if (versionMatch.Success)
        {
            moduleInfo.Version = versionMatch.Groups[1].Value;
        }

        // Extract required modules
        var requiredModulesMatch = Regex.Match(content, @"RequiredModules\s*=\s*@\(([^)]+)\)", RegexOptions.Singleline);
        if (requiredModulesMatch.Success)
        {
            var modulesContent = requiredModulesMatch.Groups[1].Value;
            var moduleNames = Regex.Matches(modulesContent, @"'([^']+)'");
            foreach (Match m in moduleNames)
            {
                moduleInfo.RequiredModules.Add(m.Groups[1].Value);
            }
        }

        // Extract exported functions
        var functionsMatch = Regex.Match(content, @"FunctionsToExport\s*=\s*@\(([^)]+)\)", RegexOptions.Singleline);
        if (functionsMatch.Success)
        {
            var functionsContent = functionsMatch.Groups[1].Value;
            var functionNames = Regex.Matches(functionsContent, @"'([^']+)'");
            foreach (Match m in functionNames)
            {
                moduleInfo.ExportedFunctions.Add(m.Groups[1].Value);
            }
        }
    }

    private static async Task ParseModuleContentAsync(string filePath, string content, ModuleInfo moduleInfo)
    {
        // Extract 'using module' statements
        var usingModules = Regex.Matches(content, @"using\s+module\s+([^\r\n]+)");
        foreach (Match m in usingModules)
        {
            var modulePath = m.Groups[1].Value.Trim();
            // Clean up the path to get module name
            var moduleName = Path.GetFileNameWithoutExtension(modulePath.Trim('\'', '"'));
            moduleInfo.UsingModules.Add(moduleName);
        }

        // Try to use LSP to get document symbols
        try
        {
            await lspClient!.OpenDocumentAsync(filePath, content);
            var symbols = await lspClient.GetDocumentSymbolsAsync(filePath);

            foreach (var symbol in symbols)
            {
                if (symbol.Kind == SymbolKind.Class)
                {
                    var classInfo = new Models.ClassInfo
                    {
                        Name = symbol.Name
                    };

                    // Check for base class in the content
                    var classMatch = Regex.Match(content, $@"class\s+{Regex.Escape(symbol.Name)}\s*:\s*(\w+)");
                    if (classMatch.Success)
                    {
                        classInfo.BaseClass = classMatch.Groups[1].Value;
                    }

                    // Extract methods from children
                    if (symbol.Children != null)
                    {
                        foreach (var child in symbol.Children)
                        {
                            if (child.Kind == SymbolKind.Method)
                            {
                                classInfo.Methods.Add(child.Name);
                            }
                            else if (child.Kind == SymbolKind.Property)
                            {
                                classInfo.Properties.Add(child.Name);
                            }
                        }
                    }

                    moduleInfo.Classes.Add(classInfo);
                }
                else if (symbol.Kind == SymbolKind.Function)
                {
                    if (!moduleInfo.ExportedFunctions.Contains(symbol.Name))
                    {
                        moduleInfo.ExportedFunctions.Add(symbol.Name);
                    }
                }
            }

            await lspClient.CloseDocumentAsync(filePath);
        }
        catch (Exception ex)
        {
            logger.LogDebug("LSP analysis failed for {File}: {Error}", filePath, ex.Message);

            // Fallback: regex-based extraction
            var classMatches = Regex.Matches(content, @"class\s+(\w+)(?:\s*:\s*(\w+))?");
            foreach (Match m in classMatches)
            {
                moduleInfo.Classes.Add(new Models.ClassInfo
                {
                    Name = m.Groups[1].Value,
                    BaseClass = m.Groups[2].Success ? m.Groups[2].Value : null
                });
            }

            var functionMatches = Regex.Matches(content, @"function\s+([\w-]+)");
            foreach (Match m in functionMatches)
            {
                var funcName = m.Groups[1].Value;
                if (!moduleInfo.ExportedFunctions.Contains(funcName))
                {
                    moduleInfo.ExportedFunctions.Add(funcName);
                }
            }
        }
    }

    private static async Task ExtractInteractionsAsync(InteractionDiagram diagram)
    {
        var moduleByName = diagram.Modules.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var module in diagram.Modules)
        {
            // Add interactions from 'using module'
            foreach (var usedModule in module.UsingModules)
            {
                var targetName = Path.GetFileNameWithoutExtension(usedModule);
                diagram.Interactions.Add(new ModuleInteraction
                {
                    FromModule = module.Name,
                    ToModule = targetName,
                    InteractionType = ModuleInteractionType.UsingModule,
                    Symbol = $"using module {usedModule}"
                });
            }

            // Add interactions from RequiredModules
            foreach (var required in module.RequiredModules)
            {
                diagram.Interactions.Add(new ModuleInteraction
                {
                    FromModule = module.Name,
                    ToModule = required,
                    InteractionType = ModuleInteractionType.Imports,
                    Symbol = "RequiredModule"
                });
            }

            // Add inheritance relationships
            foreach (var classInfo in module.Classes)
            {
                if (!string.IsNullOrEmpty(classInfo.BaseClass))
                {
                    // Find which module defines the base class
                    var baseModule = diagram.Modules.FirstOrDefault(m =>
                        m.Classes.Any(c => c.Name == classInfo.BaseClass));

                    if (baseModule != null && baseModule.Name != module.Name)
                    {
                        diagram.Interactions.Add(new ModuleInteraction
                        {
                            FromModule = module.Name,
                            ToModule = baseModule.Name,
                            InteractionType = ModuleInteractionType.Inherits,
                            Symbol = $"class {classInfo.Name} : {classInfo.BaseClass}"
                        });
                    }
                }
            }
        }

        // Also scan for Import-Module calls in .ps1 files
        var scriptFiles = Directory.GetFiles(powerShellRepoPath, "*.ps1", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .Take(200)
            .ToList();

        foreach (var file in scriptFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var fileName = Path.GetFileNameWithoutExtension(file);

                // Find Import-Module calls
                var importMatches = Regex.Matches(content, @"Import-Module\s+(?:-Name\s+)?['""]?([^\s'""]+)['""]?");
                foreach (Match m in importMatches)
                {
                    var targetModule = Path.GetFileNameWithoutExtension(m.Groups[1].Value);
                    var sourceModule = diagram.Modules.FirstOrDefault(mod =>
                        mod.FilePath.Contains(fileName, StringComparison.OrdinalIgnoreCase))?.Name ?? fileName;

                    // Avoid duplicate interactions
                    if (!diagram.Interactions.Any(i =>
                        i.FromModule == sourceModule &&
                        i.ToModule == targetModule &&
                        i.InteractionType == ModuleInteractionType.Imports))
                    {
                        diagram.Interactions.Add(new ModuleInteraction
                        {
                            FromModule = sourceModule,
                            ToModule = targetModule,
                            InteractionType = ModuleInteractionType.Imports,
                            FilePath = Path.GetRelativePath(powerShellRepoPath, file),
                            Symbol = "Import-Module"
                        });
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }
    }

    private static async Task BuildModuleDependencyGraphAsync(ComponentDiagram diagram)
    {
        var moduleFiles = Directory.GetFiles(powerShellRepoPath, "*.psm1", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .Take(100)
            .ToList();

        var moduleDependencies = new Dictionary<string, HashSet<string>>();

        foreach (var file in moduleFiles)
        {
            try
            {
                var moduleName = Path.GetFileNameWithoutExtension(file);
                var content = await File.ReadAllTextAsync(file);

                if (!moduleDependencies.TryGetValue(moduleName, out var value))
                {
                    value = [];
                    moduleDependencies[moduleName] = value;
                }

                // Add node
                diagram.Nodes.Add(new ComponentNode
                {
                    Id = moduleName,
                    Label = moduleName,
                    NodeType = ComponentNodeType.Module,
                    FilePath = Path.GetRelativePath(powerShellRepoPath, file)
                });

                // Extract dependencies
                var usingModules = Regex.Matches(content, @"using\s+module\s+['""]?([^'""<>\r\n]+)['""]?");
                foreach (Match m in usingModules)
                {
                    var dep = Path.GetFileNameWithoutExtension(m.Groups[1].Value.Trim());
                    value.Add(dep);
                }

                var importModules = Regex.Matches(content, @"Import-Module\s+(?:-Name\s+)?['""]?([^\s'""<>]+)['""]?");
                foreach (Match m in importModules)
                {
                    var dep = Path.GetFileNameWithoutExtension(m.Groups[1].Value.Trim());
                    value.Add(dep);
                }
            }
            catch
            {
                // Skip
            }
        }

        // Add edges
        var nodeIds = diagram.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (module, deps) in moduleDependencies)
        {
            foreach (var dep in deps)
            {
                // Only add edges to known modules
                if (nodeIds.Contains(dep))
                {
                    diagram.Edges.Add(new ComponentEdge
                    {
                        FromId = module,
                        ToId = dep,
                        EdgeType = ComponentEdgeType.Imports
                    });
                }
            }
        }
    }

    [GeneratedRegex(@"ModuleVersion\s*=\s*'([^']+)'")]
    private static partial Regex MyRegex();
}