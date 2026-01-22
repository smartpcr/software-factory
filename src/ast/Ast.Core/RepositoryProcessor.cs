// -----------------------------------------------------------------------
// <copyright file="RepositoryProcessor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Processes repositories containing both C# and PowerShell code.
/// Extracts hyperedges from each language and identifies cross-language relationships.
/// </summary>
public class RepositoryProcessor
{
    private readonly string repoPath;
    private readonly AstConfiguration config;
    private readonly Dictionary<string, HypergraphNode> nodes = [];
    private readonly List<Hyperedge> hyperedges = [];
    private readonly Dictionary<string, List<string>> fileToNodes = [];
    private readonly Dictionary<string, int> fileNodeCounts = [];
    private readonly Dictionary<string, int> fileEdgeCounts = [];
    private int edgeCounter;

    // Track symbols for cross-language linking
    private readonly Dictionary<string, string> csharpTypes = [];
    private readonly Dictionary<string, string> csharpMethods = [];
    private readonly Dictionary<string, string> powershellFunctions = [];

    // Extractors - must be set before processing
    public Func<string, string, (Dictionary<string, HypergraphNode>, List<Hyperedge>)>? CSharpExtractor { get; set; }
    public Func<string, string, (Dictionary<string, HypergraphNode>, List<Hyperedge>)>? PowerShellExtractor { get; set; }

    private static readonly string[] CSharpExtensions = [".cs"];
    private static readonly string[] PowerShellExtensions = [".ps1", ".psm1", ".psd1"];
    private static readonly string[] ExcludePatterns = ["bin", "obj", "packages", ".git", "node_modules", "TestResults"];

    public RepositoryProcessor(string repoPath, AstConfiguration? config = null)
    {
        this.repoPath = Path.GetFullPath(repoPath);
        this.config = config ?? new AstConfiguration();
    }

    public HypergraphData Process(bool verbose = false)
    {
        var files = new List<(string Path, string Language)>();

        // Find all relevant files
        foreach (var file in Directory.EnumerateFiles(this.repoPath, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(this.repoPath, file);

            // Skip excluded directories
            if (ExcludePatterns.Any(p => relativePath.Contains(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            var ext = Path.GetExtension(file).ToLowerInvariant();

            if (CSharpExtensions.Contains(ext))
                files.Add((file, "csharp"));
            else if (PowerShellExtensions.Contains(ext))
                files.Add((file, "powershell"));
        }

        if (verbose)
            Console.WriteLine($"Found {files.Count} files to process");

        // Process C# files first (to build type/method index for cross-language detection)
        if (this.CSharpExtractor != null)
        {
            foreach (var (path, language) in files.Where(f => f.Language == "csharp"))
            {
                try
                {
                    this.ProcessCSharpFile(path, verbose);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error processing {path}: {ex.Message}");
                }
            }
        }

        // Then process PowerShell files (can reference C# types)
        if (this.PowerShellExtractor != null)
        {
            foreach (var (path, language) in files.Where(f => f.Language == "powershell"))
            {
                try
                {
                    this.ProcessPowerShellFile(path, verbose);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error processing {path}: {ex.Message}");
                }
            }
        }

        // Extract project and repository metadata
        var metadataExtractor = new ProjectMetadataExtractor(this.repoPath, this.config);
        var repoMetadata = metadataExtractor.ExtractRepositoryMetadata();
        var projects = metadataExtractor.ExtractAllProjects();

        // Update project metadata with node/edge counts
        foreach (var project in projects)
        {
            var projectNodeCount = 0;
            var projectEdgeCount = 0;
            foreach (var sourceFile in project.SourceFiles)
            {
                if (this.fileNodeCounts.TryGetValue(sourceFile, out var nodeCount))
                    projectNodeCount += nodeCount;
                if (this.fileEdgeCounts.TryGetValue(sourceFile, out var edgeCount))
                    projectEdgeCount += edgeCount;
            }
            project.NodeCount = projectNodeCount;
            project.HyperedgeCount = projectEdgeCount;
        }

        // Calculate statistics
        var csNodes = this.nodes.Values.Count(n => n.Language == "csharp");
        var psNodes = this.nodes.Values.Count(n => n.Language == "powershell");
        var crossEdges = this.hyperedges.Count(e => e.IsCrossLanguage);

        return new HypergraphData
        {
            Nodes = this.nodes,
            Hyperedges = this.hyperedges,
            Metadata = new HypergraphMetadata
            {
                RepoPath = this.repoPath,
                NumFiles = this.fileToNodes.Count,
                NumCSharpNodes = csNodes,
                NumPowerShellNodes = psNodes,
                NumCrossLanguageEdges = crossEdges,
                Files = this.fileToNodes.Keys.ToList(),
                Repository = repoMetadata,
                Projects = projects
            }
        };
    }

    private void ProcessCSharpFile(string filePath, bool verbose)
    {
        if (this.CSharpExtractor == null) return;

        var sourceCode = File.ReadAllText(filePath);
        var relativePath = Path.GetRelativePath(this.repoPath, filePath);

        var (nodes, hyperedges) = this.CSharpExtractor(relativePath, sourceCode);

        // Track counts for project metadata
        this.fileNodeCounts[relativePath] = nodes.Count;
        this.fileEdgeCounts[relativePath] = hyperedges.Count;

        // Merge nodes
        foreach (var (oldId, node) in nodes)
        {
            var newId = $"cs_{oldId}";
            var newNode = new HypergraphNode
            {
                Id = newId,
                Name = node.Name,
                NodeType = node.NodeType,
                Language = "csharp",
                FilePath = relativePath,
                Scope = node.Scope,
                LineStart = node.LineStart,
                LineEnd = node.LineEnd,
                Modifiers = node.Modifiers,
                Metadata = node.Metadata
            };
            this.nodes[newId] = newNode;

            if (!this.fileToNodes.ContainsKey(relativePath))
                this.fileToNodes[relativePath] = [];
            this.fileToNodes[relativePath].Add(newId);

            // Track types and methods for cross-language linking
            if (node.NodeType == "class" || node.NodeType == "interface" || node.NodeType == "struct")
                this.csharpTypes[node.Name] = newId;
            else if (node.NodeType == "method")
                this.csharpMethods[node.Name] = newId;
        }

        // Convert hyperedges
        foreach (var edge in hyperedges)
        {
            var newEdge = new Hyperedge
            {
                Id = $"uhe_{++this.edgeCounter:D4}",
                EdgeType = edge.EdgeType,
                NodeIds = edge.NodeIds.Select(id => $"cs_{id}").ToList(),
                Roles = edge.Roles.ToDictionary(r => $"cs_{r.Key}", r => r.Value),
                Languages = ["csharp"],
                Metadata = edge.Metadata
            };
            this.hyperedges.Add(newEdge);
        }

        if (verbose)
            Console.WriteLine($"  {relativePath}: {nodes.Count} nodes, {hyperedges.Count} edges");
    }

    private void ProcessPowerShellFile(string filePath, bool verbose)
    {
        if (this.PowerShellExtractor == null) return;

        var sourceCode = File.ReadAllText(filePath);
        var relativePath = Path.GetRelativePath(this.repoPath, filePath);

        var (nodes, hyperedges) = this.PowerShellExtractor(relativePath, sourceCode);

        // Track counts for project metadata
        this.fileNodeCounts[relativePath] = nodes.Count;
        this.fileEdgeCounts[relativePath] = hyperedges.Count;

        // Merge nodes
        foreach (var (oldId, node) in nodes)
        {
            var newId = $"ps_{oldId}";
            var newNode = new HypergraphNode
            {
                Id = newId,
                Name = node.Name,
                NodeType = node.NodeType,
                Language = "powershell",
                FilePath = relativePath,
                Scope = node.Scope,
                LineStart = node.LineStart,
                LineEnd = node.LineEnd,
                Metadata = node.Metadata
            };
            this.nodes[newId] = newNode;

            if (!this.fileToNodes.ContainsKey(relativePath))
                this.fileToNodes[relativePath] = [];
            this.fileToNodes[relativePath].Add(newId);

            // Track functions for cross-language linking
            if (node.NodeType == "function")
                this.powershellFunctions[node.Name] = newId;
        }

        // Convert hyperedges
        foreach (var edge in hyperedges)
        {
            var newEdge = new Hyperedge
            {
                Id = $"uhe_{++this.edgeCounter:D4}",
                EdgeType = edge.EdgeType,
                NodeIds = edge.NodeIds.Select(id => $"ps_{id}").ToList(),
                Roles = edge.Roles.ToDictionary(r => $"ps_{r.Key}", r => r.Value),
                Languages = ["powershell"],
                Metadata = edge.Metadata
            };
            this.hyperedges.Add(newEdge);
        }

        // Look for cross-language references
        this.ExtractCrossLanguageReferences(sourceCode, relativePath);

        if (verbose)
            Console.WriteLine($"  {relativePath}: {nodes.Count} nodes, {hyperedges.Count} edges");
    }

    private void ExtractCrossLanguageReferences(string psSource, string filePath)
    {
        // Find .NET type references: [TypeName]
        var typePattern = new Regex(@"\[(?<type>[\w.]+)\]", RegexOptions.Compiled);
        foreach (Match match in typePattern.Matches(psSource))
        {
            var typeName = match.Groups["type"].Value;
            var simpleTypeName = typeName.Split('.').Last();

            if (this.csharpTypes.TryGetValue(simpleTypeName, out var csNodeId))
            {
                this.CreateCrossLanguageEdge(csNodeId, typeName, filePath, HyperedgeType.CrossLanguageTypeReference);
            }
        }

        // Find .NET static method calls: [Type]::Method()
        var methodPattern = new Regex(@"\[[\w.]+\]::(?<method>\w+)", RegexOptions.Compiled);
        foreach (Match match in methodPattern.Matches(psSource))
        {
            var methodName = match.Groups["method"].Value;

            if (this.csharpMethods.TryGetValue(methodName, out var csNodeId))
            {
                this.CreateCrossLanguageEdge(csNodeId, methodName, filePath, HyperedgeType.CrossLanguageMethodCall);
            }
        }
    }

    private void CreateCrossLanguageEdge(string csNodeId, string referenceName, string psFilePath, HyperedgeType edgeType)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes($"cross:{referenceName}:{psFilePath}"));
        var psRefId = $"cross_{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";

        // Create PS reference node if not exists
        if (!this.nodes.ContainsKey(psRefId))
        {
            this.nodes[psRefId] = new HypergraphNode
            {
                Id = psRefId,
                Name = referenceName,
                NodeType = edgeType == HyperedgeType.CrossLanguageTypeReference ? "type_reference" : "method_call",
                Language = "powershell",
                FilePath = psFilePath,
                Scope = psFilePath,
                Metadata = new Dictionary<string, object?> { ["referenced_name"] = referenceName }
            };
        }

        // Create cross-language hyperedge
        var crossEdge = new Hyperedge
        {
            Id = $"uhe_{++this.edgeCounter:D4}",
            EdgeType = edgeType,
            NodeIds = [csNodeId, psRefId],
            Roles = new Dictionary<string, string>
            {
                [csNodeId] = "dotnet_definition",
                [psRefId] = "ps_reference"
            },
            Languages = ["csharp", "powershell"],
            Metadata = new Dictionary<string, object?> { ["cross_language"] = true }
        };
        this.hyperedges.Add(crossEdge);
    }
}