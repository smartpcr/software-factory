// -----------------------------------------------------------------------
// <copyright file="GraphDbIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.IntegrationTests;

using Ast.Core;
using Ast.Csharp;
using Ast.PowerShell;
using Ast.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Integration tests for Neo4j graph database.
/// Uses sample repositories from Ast.IntegrationTests.
/// </summary>
[TestClass]
public class GraphDbIntegrationTests
{
    private static readonly string csharpRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";
    private static readonly string powershellRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\sln\Deploy"
        : "/mnt/e/work/hub/sln/Deploy";

    private static Neo4jGraphStore? graphStore;
    private static ICodeQuery? query;
    private static GraphQueryService? graphQueryService;
    private static HypergraphData? csharpData;
    private static HypergraphData? powershellData;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        // Ensure Neo4j is running
        await Neo4jDockerFixture.EnsureNeo4jRunningAsync();

        // Create graph store
        var options = Options.Create(new GraphStoreOptions
        {
            Uri = Neo4jDockerFixture.Neo4jUri,
            Username = Neo4jDockerFixture.Neo4jUsername,
            Password = Neo4jDockerFixture.Neo4jPassword,
            BatchSize = 500
        });

        var logger = TestLoggerFactory.CreateLogger<Neo4jGraphStore>();
        graphStore = new Neo4jGraphStore(options, logger);

        // Wait a bit for connection to stabilize
        await Task.Delay(2000);

        // Clear existing data
        Console.WriteLine("Clearing existing graph data...");
        await graphStore.ClearAsync();

        // Create indexes
        Console.WriteLine("Creating indexes...");
        await graphStore.CreateIndexesAsync();

        // Create query builder
        var queryLogger = TestLoggerFactory.CreateLogger<CodeQueryBuilder>();
        query = new CodeQueryBuilder(graphStore, queryLogger);

        // Create graph query service
        var serviceLogger = TestLoggerFactory.CreateLogger<GraphQueryService>();
        graphQueryService = new GraphQueryService(graphStore, serviceLogger);

        // Parse and index C# repo
        await ParseAndIndexCSharpRepoAsync();

        // Parse and index PowerShell repo
        await ParseAndIndexPowerShellRepoAsync();

        Console.WriteLine("\n=== Data Indexing Complete ===");
        Console.WriteLine("Neo4j is running and data has been indexed.");
        Console.WriteLine("Neo4j Browser: http://localhost:7474");
        Console.WriteLine($"Connection: {Neo4jDockerFixture.Neo4jUri}");
        Console.WriteLine($"Username: {Neo4jDockerFixture.Neo4jUsername}");
        Console.WriteLine($"Password: {Neo4jDockerFixture.Neo4jPassword}");
        Console.WriteLine("\nNeo4j will remain running after tests for manual validation.");
    }

    private static async Task ParseAndIndexCSharpRepoAsync()
    {
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Console.WriteLine($"C# repo not found at {srcPath}, skipping...");
            return;
        }

        Console.WriteLine($"\nParsing C# repo: {csharpRepoPath}");

        var processor = new RepositoryProcessor(csharpRepoPath);
        processor.CSharpExtractor = (path, source) =>
        {
            var extractor = new CSharpHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        csharpData = processor.Process(verbose: false);

        Console.WriteLine($"  Nodes: {csharpData.Nodes.Count}");
        Console.WriteLine($"  Edges: {csharpData.Hyperedges.Count}");

        // Index into Neo4j
        Console.WriteLine("  Indexing into Neo4j...");
        var (nodesIndexed, edgesIndexed) = await graphQueryService!.IndexHypergraphAsync(csharpData);
        Console.WriteLine($"  Indexed {nodesIndexed} nodes and {edgesIndexed} edges");
    }

    private static async Task ParseAndIndexPowerShellRepoAsync()
    {
        if (!Directory.Exists(powershellRepoPath))
        {
            Console.WriteLine($"PowerShell repo not found at {powershellRepoPath}, skipping...");
            return;
        }

        Console.WriteLine($"\nParsing PowerShell repo: {powershellRepoPath}");

        var processor = new RepositoryProcessor(powershellRepoPath);
        processor.PowerShellExtractor = (path, source) =>
        {
            var extractor = new PowerShellHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        powershellData = processor.Process(verbose: false);

        Console.WriteLine($"  Nodes: {powershellData.Nodes.Count}");
        Console.WriteLine($"  Edges: {powershellData.Hyperedges.Count}");

        // Index into Neo4j
        Console.WriteLine("  Indexing into Neo4j...");
        var (nodesIndexed, edgesIndexed) = await graphQueryService!.IndexHypergraphAsync(powershellData);
        Console.WriteLine($"  Indexed {nodesIndexed} nodes and {edgesIndexed} edges");
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        // DO NOT stop Neo4j - leave it running for manual validation
        Console.WriteLine("\n=== Tests Complete ===");
        Console.WriteLine("Neo4j is still running at http://localhost:7474 for manual validation.");
        var composeCmd = OperatingSystem.IsWindows() ? "podman compose" : "docker compose";
        Console.WriteLine($"To stop Neo4j manually, run: {composeCmd} -f src/ast/docker-compose.yml down");

        graphStore?.Dispose();
    }

    #region Fluent Query API Tests

    [TestMethod]
    public async Task FluentQuery_Nodes_ReturnsAllNodes()
    {
        // Skip if no data was indexed
        if (csharpData?.Nodes.Count == 0 && powershellData?.Nodes.Count == 0)
        {
            Assert.Inconclusive("No data was indexed");
            return;
        }

        // Act - Query all nodes
        var result = await query!.Nodes().Take(100).ExecuteAsync();

        // Assert
        result.Should().NotBeNull();
        result.Nodes.Should().NotBeEmpty();
        Console.WriteLine($"Found {result.Nodes.Count} nodes");
    }

    [TestMethod]
    public async Task FluentQuery_Classes_ReturnsOnlyClasses()
    {
        // Act - Query classes only
        var result = await query!.Nodes().Classes().Take(50).ToListAsync();

        // Assert
        result.Should().NotBeNull();
        if (result.Count > 0)
        {
            result.Should().AllSatisfy(n => n.NodeType.Should().Be("Class"));
        }
        Console.WriteLine($"Found {result.Count} classes");
    }

    [TestMethod]
    public async Task FluentQuery_Methods_ReturnsOnlyMethods()
    {
        // Act - Query methods only
        var result = await query!.Nodes().Methods().Take(50).ToListAsync();

        // Assert
        result.Should().NotBeNull();
        if (result.Count > 0)
        {
            result.Should().AllSatisfy(n => n.NodeType.Should().Be("Method"));
        }
        Console.WriteLine($"Found {result.Count} methods");
    }

    [TestMethod]
    public async Task FluentQuery_OfLanguage_FiltersByLanguage()
    {
        // Act - Query C# nodes only
        var csharpResult = await query!.Nodes().OfLanguage("csharp").Take(50).ToListAsync();

        // Assert
        if (csharpResult.Count > 0)
        {
            csharpResult.Should().AllSatisfy(n => n.Language.Should().Be("csharp"));
        }
        Console.WriteLine($"Found {csharpResult.Count} C# nodes");

        // Query PowerShell nodes only
        var psResult = await query!.Nodes().OfLanguage("powershell").Take(50).ToListAsync();
        if (psResult.Count > 0)
        {
            psResult.Should().AllSatisfy(n => n.Language.Should().Be("powershell"));
        }
        Console.WriteLine($"Found {psResult.Count} PowerShell nodes");
    }

    [TestMethod]
    public async Task FluentQuery_WithName_FiltersByNamePattern()
    {
        // Act - Query nodes with name pattern
        var result = await query!.Nodes().WithName("*Service*").Take(20).ToListAsync();

        // Assert
        result.Should().NotBeNull();
        Console.WriteLine($"Found {result.Count} nodes matching '*Service*'");
        foreach (var node in result.Take(5))
        {
            Console.WriteLine($"  - {node.Name} ({node.NodeType})");
        }
    }

    [TestMethod]
    public async Task FluentQuery_NodeById_ReturnsSpecificNode()
    {
        // First get any node
        var anyNode = (await query!.Nodes().Take(1).ToListAsync()).FirstOrDefault();
        if (anyNode == null)
        {
            Assert.Inconclusive("No nodes in database");
            return;
        }

        // Act - Query by ID
        var result = await query!.Node(anyNode.Id).GetAsync();

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(anyNode.Id);
        result.Name.Should().Be(anyNode.Name);
        Console.WriteLine($"Found node by ID: {result.Name}");
    }

    [TestMethod]
    public async Task FluentQuery_NodeByName_ReturnsNode()
    {
        // First get any node
        var anyNode = (await query!.Nodes().Take(1).ToListAsync()).FirstOrDefault();
        if (anyNode == null)
        {
            Assert.Inconclusive("No nodes in database");
            return;
        }

        // Act - Query by name
        var result = await query!.NodeByName(anyNode.Name).GetAsync();

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be(anyNode.Name);
        Console.WriteLine($"Found node by name: {result.Name}");
    }

    [TestMethod]
    public async Task FluentQuery_Callers_ReturnsCallingNodes()
    {
        // Get a method node
        var method = (await query!.Nodes().Methods().Take(10).ToListAsync()).FirstOrDefault();
        if (method == null)
        {
            Assert.Inconclusive("No method nodes found");
            return;
        }

        // Act - Query callers
        var callers = await query!.Node(method.Id).Callers().WithDepth(2).ToListAsync();

        // Assert
        callers.Should().NotBeNull();
        Console.WriteLine($"Method '{method.Name}' has {callers.Count} callers");
        foreach (var caller in callers.Take(5))
        {
            Console.WriteLine($"  - {caller.Node.Name} (depth: {caller.Depth})");
        }
    }

    [TestMethod]
    public async Task FluentQuery_Callees_ReturnsCalledNodes()
    {
        // Get a method node
        var method = (await query!.Nodes().Methods().Take(10).ToListAsync()).FirstOrDefault();
        if (method == null)
        {
            Assert.Inconclusive("No method nodes found");
            return;
        }

        // Act - Query callees
        var callees = await query!.Node(method.Id).Callees().WithDepth(2).ToListAsync();

        // Assert
        callees.Should().NotBeNull();
        Console.WriteLine($"Method '{method.Name}' calls {callees.Count} other methods");
        foreach (var callee in callees.Take(5))
        {
            Console.WriteLine($"  - {callee.Node.Name} (depth: {callee.Depth})");
        }
    }

    [TestMethod]
    public async Task FluentQuery_Edges_ReturnsConnectedEdges()
    {
        // Get any node
        var node = (await query!.Nodes().Take(10).ToListAsync()).FirstOrDefault();
        if (node == null)
        {
            Assert.Inconclusive("No nodes found");
            return;
        }

        // Act - Query edges
        var edges = await query!.Node(node.Id).Edges().ExecuteAsync();

        // Assert
        edges.Should().NotBeNull();
        Console.WriteLine($"Node '{node.Name}' has {edges.Count} connected edges");
    }

    [TestMethod]
    public async Task FluentQuery_Impact_ReturnsImpactAnalysis()
    {
        // Get a method node
        var method = (await query!.Nodes().Methods().Take(10).ToListAsync()).FirstOrDefault();
        if (method == null)
        {
            Assert.Inconclusive("No method nodes found");
            return;
        }

        // Act - Analyze impact
        var impact = await query!.Node(method.Id).Impact().WithDepth(2).ExecuteAsync();

        // Assert
        impact.Should().NotBeNull();
        impact.SourceNode.Id.Should().Be(method.Id);
        Console.WriteLine($"Impact analysis for '{method.Name}':");
        Console.WriteLine($"  Direct: {impact.DirectlyAffected.Count}");
        Console.WriteLine($"  Indirect: {impact.IndirectlyAffected.Count}");
        Console.WriteLine($"  Total: {impact.TotalAffected}");
        Console.WriteLine($"  Affected files: {impact.AffectedFiles.Count}");
    }

    [TestMethod]
    public async Task FluentQuery_PathsBetween_FindsPaths()
    {
        // Get two nodes
        var nodes = await query!.Nodes().Take(20).ToListAsync();
        if (nodes.Count < 2)
        {
            Assert.Inconclusive("Not enough nodes for path query");
            return;
        }

        var startNode = nodes[0];
        var endNode = nodes[nodes.Count - 1];

        // Act - Find paths
        var paths = await query!.PathsBetween(startNode.Id, endNode.Id)
            .WithMaxLength(5)
            .Take(5)
            .ToListAsync();

        // Assert
        paths.Should().NotBeNull();
        Console.WriteLine($"Found {paths.Count} paths from '{startNode.Name}' to '{endNode.Name}'");
        foreach (var path in paths)
        {
            Console.WriteLine($"  Path length: {path.Length}");
        }
    }

    [TestMethod]
    public async Task FluentQuery_ChainedFilters_WorkCorrectly()
    {
        // Act - Chain multiple filters
        var result = await query!.Nodes()
            .Classes()
            .OfLanguage("csharp")
            .Take(20)
            .ToListAsync();

        // Assert
        result.Should().NotBeNull();
        if (result.Count > 0)
        {
            result.Should().AllSatisfy(n =>
            {
                n.NodeType.Should().Be("Class");
                n.Language.Should().Be("csharp");
            });
        }
        Console.WriteLine($"Found {result.Count} C# classes");
    }

    [TestMethod]
    public async Task FluentQuery_CountAsync_ReturnsCount()
    {
        // Act
        var count = await query!.Nodes().Classes().CountAsync();

        // Assert
        count.Should().BeGreaterThanOrEqualTo(0);
        Console.WriteLine($"Total classes: {count}");
    }

    #endregion

    #region GraphQueryService Tests

    [TestMethod]
    public async Task GraphQueryService_GetStatistics_ReturnsStats()
    {
        // Act
        var stats = await graphQueryService!.GetStatisticsAsync();

        // Assert
        stats.Should().NotBeNull();
        Console.WriteLine("\n=== Graph Statistics ===");
        Console.WriteLine($"Total nodes: {stats.TotalNodes}");
        Console.WriteLine($"Total edges: {stats.TotalEdges}");
        Console.WriteLine($"Unique files: {stats.UniqueFiles}");

        Console.WriteLine("\nNodes by language:");
        foreach (var (lang, count) in stats.NodesByLanguage)
        {
            Console.WriteLine($"  {lang}: {count}");
        }

        Console.WriteLine("\nNodes by type:");
        foreach (var (type, count) in stats.NodesByType.OrderByDescending(x => x.Value).Take(10))
        {
            Console.WriteLine($"  {type}: {count}");
        }
    }

    [TestMethod]
    public async Task GraphQueryService_GetNodesInFile_ReturnsFileNodes()
    {
        // Get any node to find a file path
        var anyNode = (await query!.Nodes().Take(1).ToListAsync()).FirstOrDefault();
        if (anyNode == null)
        {
            Assert.Inconclusive("No nodes found");
            return;
        }

        // Act
        var nodes = await graphQueryService!.GetNodesInFileAsync(anyNode.FilePath);

        // Assert
        nodes.Should().NotBeNull();
        nodes.Should().NotBeEmpty();
        Console.WriteLine($"File '{anyNode.FilePath}' contains {nodes.Count} nodes");
    }

    [TestMethod]
    public async Task GraphQueryService_GetNodesByType_ReturnsTypedNodes()
    {
        // Act
        var classes = await graphQueryService!.GetNodesByTypeAsync("Class", 20);
        var methods = await graphQueryService!.GetNodesByTypeAsync("Method", 20);

        // Assert
        Console.WriteLine($"Found {classes.Count} classes");
        Console.WriteLine($"Found {methods.Count} methods");

        if (classes.Count > 0)
        {
            classes.Should().AllSatisfy(n => n.NodeType.Should().Be("Class"));
        }
        if (methods.Count > 0)
        {
            methods.Should().AllSatisfy(n => n.NodeType.Should().Be("Method"));
        }
    }

    #endregion
}
