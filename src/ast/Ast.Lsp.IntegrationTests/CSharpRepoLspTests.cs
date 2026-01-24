// -----------------------------------------------------------------------
// <copyright file="CSharpRepoLspTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.IntegrationTests;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ast.Csharp;
using Ast.Lsp.IntegrationTests.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LspModels = Ast.Lsp.IntegrationTests.Models;

/// <summary>
/// LSP integration tests for C# repository analysis.
/// Extracts repo-level metadata, DI registrations, API endpoints, and generates component diagrams.
/// </summary>
[TestClass]
[TestCategory("lsp")]
[TestCategory("integration")]
public partial class CSharpRepoLspTests
{
    // Windows path: E:\work\hci\update, WSL path: /mnt/e/work/hci/update
    private static readonly string csharpRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    private static readonly string outputDir = Path.Combine(
        AppContext.BaseDirectory, "output", "lsp", "csharp");

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static ILogger<CSharpRepoLspTests> logger = null!;
    private static LspServerManager? serverManager;
    private static ILspClient? lspClient;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext context)
    {
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<CSharpRepoLspTests>();

        // Create output directory
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Output directory: {outputDir}", outputDir);

        // Check if repo exists
        if (!Directory.Exists(csharpRepoPath))
        {
            logger.LogWarning("C# repo not found at {Path}, tests will be skipped", csharpRepoPath);
            return;
        }

        // Initialize LSP server manager
        try
        {
            var config = new LspConfiguration
            {
                Enabled = true,
                TimeoutMs = 60000, // 60 seconds for large repos
                CSharp = new CSharpLspConfiguration
                {
                    Enabled = true,
                    ServerType = CSharpLspServerType.CSharpLs
                }
            };

            var options = Options.Create(config);
            serverManager = new LspServerManager(options, TestLoggerFactory.Instance);

            logger.LogInformation("Starting C# LSP server for {Path}...", csharpRepoPath);
            lspClient = await serverManager.GetOrCreateClientAsync(LspLanguage.CSharp, csharpRepoPath);
            logger.LogInformation("C# LSP server started successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start C# LSP server. Tests will be skipped. " +
                "Ensure csharp-ls is installed: dotnet tool install -g csharp-ls");
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
    public async Task ExtractRepoLevelMetadataAsync()
    {
        // Arrange
        if (lspClient == null)
        {
            Assert.Inconclusive("LSP server not available. Install csharp-ls: dotnet tool install -g csharp-ls");
            return;
        }

        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"Source path not found: {srcPath}");
            return;
        }

        var metadata = new LspRepoMetadata
        {
            RepoName = Path.GetFileName(csharpRepoPath),
            RepoPath = csharpRepoPath
        };

        // Act - Find entry points
        logger.LogInformation("Searching for entry points...");
        await ExtractEntryPointsAsync(srcPath, metadata);

        // Act - Find DI registrations
        logger.LogInformation("Searching for DI registrations...");
        await ExtractDiRegistrationsAsync(srcPath, metadata);

        // Act - Find API controllers
        logger.LogInformation("Searching for API controllers...");
        await ExtractApiControllersAsync(srcPath, metadata);

        // Output results
        var outputPath = Path.Combine(outputDir, "_lsp_repo_metadata.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(metadata, jsonOptions));

        // Assert
        logger.LogInformation("Found {EntryPoints} entry points", metadata.EntryPoints.Count);
        logger.LogInformation("Found {DiRegistrations} DI registrations", metadata.DiRegistrations.Count);
        logger.LogInformation("Found {Controllers} API controllers with {Endpoints} endpoints",
            metadata.ApiControllers.Count,
            metadata.ApiControllers.Sum(c => c.Endpoints.Count));

        metadata.Should().NotBeNull();
        logger.LogInformation("Output written to: {Path}", outputPath);
    }

    [TestMethod]
    public async Task GenerateComponentDiagramAsync()
    {
        // Arrange
        if (lspClient == null)
        {
            Assert.Inconclusive("LSP server not available");
            return;
        }

        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"Source path not found: {srcPath}");
            return;
        }

        var diagram = new ComponentDiagram
        {
            RepoName = Path.GetFileName(csharpRepoPath)
        };

        // Act - Extract project references from .csproj files
        logger.LogInformation("Analyzing project structure...");
        await ExtractProjectStructureAsync(srcPath, diagram);

        // Act - Analyze namespace dependencies using LSP
        logger.LogInformation("Analyzing namespace dependencies via LSP...");
        await ExtractNamespaceDependenciesAsync(srcPath, diagram);

        // Generate Mermaid diagram
        diagram.GenerateMermaid();

        // Output results
        var outputPath = Path.Combine(outputDir, "_component_diagram.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(diagram, jsonOptions));

        // Also output just the Mermaid diagram
        var mermaidPath = Path.Combine(outputDir, "_component_diagram.mmd");
        await File.WriteAllTextAsync(mermaidPath, diagram.Mermaid);

        // Assert
        logger.LogInformation("Generated component diagram with {Nodes} nodes and {Edges} edges",
            diagram.Nodes.Count, diagram.Edges.Count);

        diagram.Nodes.Should().NotBeEmpty("Should have found at least one component");
        logger.LogInformation("Output written to: {Path}", outputPath);
        logger.LogInformation("Mermaid diagram written to: {Path}", mermaidPath);
    }

    private static async Task ExtractEntryPointsAsync(string srcPath, LspRepoMetadata metadata)
    {
        // Find Program.cs and Startup.cs files
        var entryFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                       !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => f.EndsWith("Program.cs") || f.EndsWith("Startup.cs"))
            .Take(50) // Limit for performance
            .ToList();

        logger.LogDebug("Found {Count} entry point files to analyze", entryFiles.Count);

        foreach (var file in entryFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                await lspClient!.OpenDocumentAsync(file, content);

                // Get document symbols
                var symbols = await lspClient.GetDocumentSymbolsAsync(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);

                foreach (var symbol in symbols)
                {
                    // Look for Main method
                    if (symbol.Name == "Main" && symbol.Kind == SymbolKind.Method)
                    {
                        var hover = await lspClient.GetHoverAsync(file, symbol.Range.Start.Line, symbol.Range.Start.Character);
                        metadata.EntryPoints.Add(new LspModels.EntryPointInfo
                        {
                            FilePath = relativePath,
                            EntryPointType = "Main",
                            Line = symbol.Range.Start.Line,
                            Signature = hover?.Contents?.Value,
                            ClassName = GetParentClassName(symbols, symbol)
                        });
                    }

                    // Look for WebApplication.CreateBuilder or Host.CreateDefaultBuilder
                    if (symbol.Name.Contains("CreateBuilder") || symbol.Name.Contains("CreateDefaultBuilder"))
                    {
                        metadata.EntryPoints.Add(new LspModels.EntryPointInfo
                        {
                            FilePath = relativePath,
                            EntryPointType = "WebApplicationBuilder",
                            Line = symbol.Range.Start.Line
                        });
                    }
                }

                // Check for top-level statements (no class containing Main)
                if (content.Contains("WebApplication.CreateBuilder") && !symbols.Any(s => s.Name == "Main"))
                {
                    metadata.EntryPoints.Add(new LspModels.EntryPointInfo
                    {
                        FilePath = relativePath,
                        EntryPointType = "TopLevelStatements",
                        Line = 1
                    });
                }

                await lspClient.CloseDocumentAsync(file);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Error analyzing {File}: {Error}", file, ex.Message);
            }
        }
    }

    private static async Task ExtractDiRegistrationsAsync(string srcPath, LspRepoMetadata metadata)
    {
        // Find files that likely contain DI registrations
        var diFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                       !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("Startup") ||
                       name.Contains("Program") ||
                       name.Contains("ServiceCollection") ||
                       name.Contains("DependencyInjection") ||
                       name.Contains("UnityConfig") ||
                       name.Contains("Container");
            })
            .Take(100)
            .ToList();

        logger.LogDebug("Found {Count} DI-related files to analyze", diFiles.Count);

        // Patterns for DI registrations
        var diPatterns = new[]
        {
            @"AddSingleton<([^,>]+),?\s*([^>]*)>",
            @"AddScoped<([^,>]+),?\s*([^>]*)>",
            @"AddTransient<([^,>]+),?\s*([^>]*)>",
            @"RegisterType<([^,>]+),?\s*([^>]*)>",
            @"RegisterInstance<([^>]+)>",
            @"\.Register<([^,>]+),?\s*([^>]*)>"
        };

        foreach (var file in diFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);
                var lines = content.Split('\n');

                for (var lineNum = 0; lineNum < lines.Length; lineNum++)
                {
                    var line = lines[lineNum];

                    foreach (var pattern in diPatterns)
                    {
                        var matches = Regex.Matches(line, pattern);
                        foreach (Match match in matches)
                        {
                            var lifetime = pattern switch
                            {
                                var p when p.Contains("Singleton") => "Singleton",
                                var p when p.Contains("Scoped") => "Scoped",
                                var p when p.Contains("Transient") => "Transient",
                                var p when p.Contains("Instance") => "Singleton",
                                _ => "Unknown"
                            };

                            var serviceType = match.Groups[1].Value.Trim();
                            var implementationType = match.Groups.Count > 2 && !string.IsNullOrWhiteSpace(match.Groups[2].Value)
                                ? match.Groups[2].Value.Trim()
                                : serviceType;

                            metadata.DiRegistrations.Add(new DiRegistration
                            {
                                FilePath = relativePath,
                                Line = lineNum + 1,
                                ServiceType = serviceType,
                                ImplementationType = implementationType,
                                Lifetime = lifetime,
                                RegistrationMethod = pattern.Contains("Register") ? "Unity" : "Microsoft.DI"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("Error analyzing DI in {File}: {Error}", file, ex.Message);
            }
        }
    }

    private static async Task ExtractApiControllersAsync(string srcPath, LspRepoMetadata metadata)
    {
        // Find controller files
        var controllerFiles = Directory.GetFiles(srcPath, "*Controller*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                       !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Take(100)
            .ToList();

        logger.LogDebug("Found {Count} controller files to analyze", controllerFiles.Count);

        foreach (var file in controllerFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                await lspClient!.OpenDocumentAsync(file, content);

                var symbols = await lspClient.GetDocumentSymbolsAsync(file);
                var relativePath = Path.GetRelativePath(csharpRepoPath, file);

                // Find class symbols that are controllers
                var classSymbols = symbols.Where(s => s.Kind == SymbolKind.Class).ToList();

                foreach (var classSymbol in classSymbols)
                {
                    if (!classSymbol.Name.Contains("Controller"))
                        continue;

                    var controller = new ApiControllerInfo
                    {
                        Name = classSymbol.Name,
                        FilePath = relativePath
                    };

                    // Extract routing from content
                    var routeMatch = MyRegex().Match(content);
                    if (routeMatch.Success)
                    {
                        controller.BaseRoute = routeMatch.Groups[1].Value;
                    }

                    // Check for authorization
                    controller.RequiresAuthorization = content.Contains("[Authorize]");

                    // Extract API version
                    var versionMatch = Regex.Match(content, @"\[ApiVersion\s*\(\s*""?([^""\)]+)""?\s*\)\]");
                    if (versionMatch.Success)
                    {
                        controller.ApiVersion = versionMatch.Groups[1].Value;
                    }

                    // Get hover info for base class
                    var hover = await lspClient.GetHoverAsync(file, classSymbol.Range.Start.Line, classSymbol.Range.Start.Character);
                    if (hover?.Contents?.Value != null)
                    {
                        var baseMatch = Regex.Match(hover.Contents.Value, @":\s*(\w+)");
                        if (baseMatch.Success)
                        {
                            controller.BaseClass = baseMatch.Groups[1].Value;
                        }
                    }

                    // Find methods (endpoints)
                    var methodSymbols = classSymbol.Children?
                        .Where(s => s.Kind == SymbolKind.Method)
                        .ToList() ?? [];

                    foreach (var methodSymbol in methodSymbols)
                    {
                        // Check if method has HTTP attribute
                        var methodLine = methodSymbol.Range.Start.Line;
                        var startLine = Math.Max(0, methodLine - 5);
                        var endLine = Math.Min(content.Split('\n').Length - 1, methodLine + 1);
                        var methodContext = string.Join("\n", content.Split('\n').Skip(startLine).Take(endLine - startLine + 1));

                        var httpMethodMatch = Regex.Match(methodContext, @"\[(Http(Get|Post|Put|Delete|Patch))(?:\s*\(\s*""([^""]*)""\s*\))?\]");
                        if (httpMethodMatch.Success || methodContext.Contains("[Get") || methodContext.Contains("[Put"))
                        {
                            var endpoint = new ApiEndpointInfo
                            {
                                MethodName = methodSymbol.Name,
                                HttpMethod = httpMethodMatch.Groups[2].Success ? httpMethodMatch.Groups[2].Value.ToUpper() : "GET",
                                Route = httpMethodMatch.Groups[3].Success ? httpMethodMatch.Groups[3].Value : "",
                                Line = methodLine
                            };

                            endpoint.FullRoute = string.IsNullOrEmpty(controller.BaseRoute)
                                ? endpoint.Route
                                : $"{controller.BaseRoute}/{endpoint.Route}".TrimEnd('/');

                            // Get return type from hover
                            var methodHover = await lspClient.GetHoverAsync(file, methodLine, methodSymbol.Range.Start.Character);
                            if (methodHover?.Contents?.Value != null)
                            {
                                var returnMatch = Regex.Match(methodHover.Contents.Value, @":\s*([^{]+)");
                                if (returnMatch.Success)
                                {
                                    endpoint.ReturnType = returnMatch.Groups[1].Value.Trim();
                                }
                            }

                            controller.Endpoints.Add(endpoint);
                        }
                    }

                    if (controller.Endpoints.Count > 0 || controller.Name.Contains("Controller"))
                    {
                        metadata.ApiControllers.Add(controller);
                    }
                }

                await lspClient.CloseDocumentAsync(file);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Error analyzing controller {File}: {Error}", file, ex.Message);
            }
        }
    }

    private static async Task ExtractProjectStructureAsync(string srcPath, ComponentDiagram diagram)
    {
        // Find all .csproj files
        var projectFiles = Directory.GetFiles(srcPath, "*.csproj", SearchOption.AllDirectories).ToList();

        logger.LogDebug("Found {Count} project files", projectFiles.Count);

        var projectNodes = new Dictionary<string, ComponentNode>();
        var projectReferences = new List<(string from, string to)>();

        foreach (var projectFile in projectFiles)
        {
            try
            {
                var projectName = Path.GetFileNameWithoutExtension(projectFile);
                var content = await File.ReadAllTextAsync(projectFile);

                // Add project node
                var node = new ComponentNode
                {
                    Id = projectName,
                    Label = projectName,
                    NodeType = ComponentNodeType.Project,
                    FilePath = Path.GetRelativePath(csharpRepoPath, projectFile)
                };
                projectNodes[projectName] = node;
                diagram.Nodes.Add(node);

                // Extract project references
                var refMatches = Regex.Matches(content, @"<ProjectReference\s+Include=""([^""]+)""");
                foreach (Match match in refMatches)
                {
                    var refPath = match.Groups[1].Value;
                    var refName = Path.GetFileNameWithoutExtension(refPath);
                    projectReferences.Add((projectName, refName));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("Error parsing project {File}: {Error}", projectFile, ex.Message);
            }
        }

        // Add edges for project references
        foreach (var (from, to) in projectReferences)
        {
            if (projectNodes.ContainsKey(from) && projectNodes.ContainsKey(to))
            {
                diagram.Edges.Add(new ComponentEdge
                {
                    FromId = from,
                    ToId = to,
                    EdgeType = ComponentEdgeType.References
                });
            }
        }
    }

    private static async Task ExtractNamespaceDependenciesAsync(string srcPath, ComponentDiagram diagram)
    {
        // Sample some key files for namespace analysis
        var sampleFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                       !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Take(200)
            .ToList();

        var namespaceUsages = new Dictionary<string, HashSet<string>>();

        foreach (var file in sampleFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);

                // Extract namespace
                var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
                if (!nsMatch.Success) continue;
                var currentNs = nsMatch.Groups[1].Value;

                // Initialize if needed
                if (!namespaceUsages.ContainsKey(currentNs))
                {
                    namespaceUsages[currentNs] = [];
                }

                // Extract using statements
                var usingMatches = Regex.Matches(content, @"using\s+([\w.]+)\s*;");
                foreach (Match usingMatch in usingMatches)
                {
                    var usedNs = usingMatch.Groups[1].Value;
                    // Only include project-internal namespaces
                    if (!usedNs.StartsWith("System") && !usedNs.StartsWith("Microsoft") &&
                        !usedNs.StartsWith("Newtonsoft") && usedNs != currentNs)
                    {
                        namespaceUsages[currentNs].Add(usedNs);
                    }
                }
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        // Add namespace nodes and edges (limit to top namespaces for readability)
        var topNamespaces = namespaceUsages
            .OrderByDescending(kv => kv.Value.Count)
            .Take(30)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var ns in topNamespaces.Keys)
        {
            // Determine project from namespace
            var projectId = ns.Split('.').FirstOrDefault() ?? ns;

            diagram.Nodes.Add(new ComponentNode
            {
                Id = ns,
                Label = ns.Split('.').LastOrDefault() ?? ns,
                NodeType = ComponentNodeType.Namespace,
                ProjectId = projectId
            });
        }

        foreach (var (fromNs, toNamespaces) in topNamespaces)
        {
            foreach (var toNs in toNamespaces)
            {
                if (topNamespaces.ContainsKey(toNs))
                {
                    diagram.Edges.Add(new ComponentEdge
                    {
                        FromId = fromNs,
                        ToId = toNs,
                        EdgeType = ComponentEdgeType.Uses
                    });
                }
            }
        }
    }

    private static string? GetParentClassName(IReadOnlyList<DocumentSymbol> symbols, DocumentSymbol method)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == SymbolKind.Class && symbol.Children != null)
            {
                if (symbol.Children.Any(c => c.Name == method.Name && c.Kind == method.Kind))
                {
                    return symbol.Name;
                }
            }
        }
        return null;
    }

    [GeneratedRegex(@"\[Route(?:Prefix)?\s*\(\s*""([^""]+)""\s*\)\]")]
    private static partial Regex MyRegex();
}