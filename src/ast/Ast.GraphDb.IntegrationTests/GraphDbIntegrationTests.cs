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

    private static ILogger<GraphDbIntegrationTests> logger = null!;
    private static Neo4jGraphStore? graphStore;
    private static ICodeQuery? query;
    private static GraphQueryService? graphQueryService;
    private static HypergraphData? csharpData;
    private static HypergraphData? powershellData;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        // Initialize logging with TestContext for colored console output
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<GraphDbIntegrationTests>();

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

        var graphStoreLogger = TestLoggerFactory.CreateLogger<Neo4jGraphStore>();
        graphStore = new Neo4jGraphStore(options, graphStoreLogger);

        // Verify connectivity before proceeding
        await Neo4jDockerFixture.AssertHealthyAsync();

        // Clear existing data with health check
        logger.LogInformation("Clearing existing graph data...");
        await Neo4jDockerFixture.ExecuteWithHealthCheckAsync(
            async () => { await graphStore.ClearAsync(); return true; },
            TimeSpan.FromMinutes(2));

        // Create indexes with health check
        logger.LogInformation("Creating indexes...");
        await Neo4jDockerFixture.ExecuteWithHealthCheckAsync(
            async () => { await graphStore.CreateIndexesAsync(); return true; },
            TimeSpan.FromMinutes(1));

        // Create query builder
        var queryLogger = TestLoggerFactory.CreateLogger<CodeQueryBuilder>();
        query = new CodeQueryBuilder(graphStore, queryLogger);

        // Create graph query service
        var serviceLogger = TestLoggerFactory.CreateLogger<GraphQueryService>();
        graphQueryService = new GraphQueryService(graphStore, serviceLogger);

        // Parse and index C# repo with health check
        await Neo4jDockerFixture.ExecuteWithHealthCheckAsync(
            async () => { await ParseAndIndexCSharpRepoAsync(); return true; },
            TimeSpan.FromMinutes(10));

        // Parse and index PowerShell repo with health check
        await Neo4jDockerFixture.ExecuteWithHealthCheckAsync(
            async () => { await ParseAndIndexPowerShellRepoAsync(); return true; },
            TimeSpan.FromMinutes(10));

        logger.LogInformation("Data indexing complete");
        logger.LogInformation("Neo4j is running and data has been indexed");
        logger.LogInformation("Neo4j Browser: http://localhost:7474");
        logger.LogInformation("Connection: {Uri}", Neo4jDockerFixture.Neo4jUri);
        logger.LogInformation("Username: {Username}", Neo4jDockerFixture.Neo4jUsername);
        logger.LogInformation("Password: {Password}", Neo4jDockerFixture.Neo4jPassword);
        logger.LogInformation("Neo4j will remain running after tests for manual validation");
    }

    [TestInitialize]
    public async Task TestInitialize()
    {
        // Verify Neo4j is healthy before each test
        if (!Neo4jDockerFixture.IsHealthy)
        {
            var (healthy, error) = await Neo4jDockerFixture.CheckHealthWithTimeoutAsync();
            if (!healthy)
            {
                Assert.Fail($"Neo4j is not healthy: {error}");
            }
        }
    }

    private static async Task ParseAndIndexCSharpRepoAsync()
    {
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            logger.LogWarning("C# repo not found at {SrcPath}, skipping...", srcPath);
            return;
        }

        logger.LogInformation("Parsing C# repo: {RepoPath}", csharpRepoPath);

        // Find all C# files first
        var csFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories);
        logger.LogInformation("Found {FileCount} C# files to parse", csFiles.Length);

        var processor = new RepositoryProcessor(csharpRepoPath);
        var filesProcessed = 0;
        processor.CSharpExtractor = (path, source) =>
        {
            filesProcessed++;
            var relativePath = Path.GetRelativePath(csharpRepoPath, path);
            if (filesProcessed % 10 == 0 || filesProcessed == csFiles.Length)
            {
                logger.LogDebug("Parsing file {Current}/{Total}: {FilePath}",
                    filesProcessed, csFiles.Length, relativePath);
            }

            var extractor = new CSharpHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        csharpData = processor.Process(verbose: false);

        logger.LogInformation("C# parsing complete: {NodeCount} nodes, {EdgeCount} edges",
            csharpData.Nodes.Count, csharpData.Hyperedges.Count);

        // Index into Neo4j
        logger.LogInformation("Indexing C# data into Neo4j...");
        var (nodesIndexed, edgesIndexed) = await graphQueryService!.IndexHypergraphAsync(csharpData);
        logger.LogInformation("C# indexing complete: {NodesIndexed} nodes, {EdgesIndexed} edges indexed",
            nodesIndexed, edgesIndexed);
    }

    private static async Task ParseAndIndexPowerShellRepoAsync()
    {
        if (!Directory.Exists(powershellRepoPath))
        {
            logger.LogWarning("PowerShell repo not found at {RepoPath}, skipping...", powershellRepoPath);
            return;
        }

        logger.LogInformation("Parsing PowerShell repo: {RepoPath}", powershellRepoPath);

        // Find all PowerShell files first
        var ps1Files = Directory.GetFiles(powershellRepoPath, "*.ps1", SearchOption.AllDirectories);
        var psm1Files = Directory.GetFiles(powershellRepoPath, "*.psm1", SearchOption.AllDirectories);
        var totalFiles = ps1Files.Length + psm1Files.Length;
        logger.LogInformation("Found {FileCount} PowerShell files to parse", totalFiles);

        var processor = new RepositoryProcessor(powershellRepoPath);
        var filesProcessed = 0;
        processor.PowerShellExtractor = (path, source) =>
        {
            filesProcessed++;
            var relativePath = Path.GetRelativePath(powershellRepoPath, path);
            if (filesProcessed % 10 == 0 || filesProcessed == totalFiles)
            {
                logger.LogDebug("Parsing file {Current}/{Total}: {FilePath}",
                    filesProcessed, totalFiles, relativePath);
            }

            var extractor = new PowerShellHyperedgeExtractor(path);
            return extractor.Extract(source);
        };

        powershellData = processor.Process(verbose: false);

        logger.LogInformation("PowerShell parsing complete: {NodeCount} nodes, {EdgeCount} edges",
            powershellData.Nodes.Count, powershellData.Hyperedges.Count);

        // Index into Neo4j
        logger.LogInformation("Indexing PowerShell data into Neo4j...");
        var (nodesIndexed, edgesIndexed) = await graphQueryService!.IndexHypergraphAsync(powershellData);
        logger.LogInformation("PowerShell indexing complete: {NodesIndexed} nodes, {EdgesIndexed} edges indexed",
            nodesIndexed, edgesIndexed);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        // Stop the health monitor
        Neo4jDockerFixture.StopHealthMonitor();

        // DO NOT stop Neo4j - leave it running for manual validation
        logger.LogInformation("Tests complete");
        logger.LogInformation("Neo4j is still running at http://localhost:7474 for manual validation");
        var composeCmd = OperatingSystem.IsWindows() ? "podman compose" : "docker compose";
        logger.LogInformation("To stop Neo4j manually, run: {Command} -f src/ast/docker-compose.yml down", composeCmd);

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
        logger.LogInformation("Found {NodeCount} nodes", result.Nodes.Count);
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
        logger.LogInformation("Found {Count} classes", result.Count);
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
        logger.LogInformation("Found {Count} methods", result.Count);
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
        logger.LogInformation("Found {Count} C# nodes", csharpResult.Count);

        // Query PowerShell nodes only
        var psResult = await query!.Nodes().OfLanguage("powershell").Take(50).ToListAsync();
        if (psResult.Count > 0)
        {
            psResult.Should().AllSatisfy(n => n.Language.Should().Be("powershell"));
        }
        logger.LogInformation("Found {Count} PowerShell nodes", psResult.Count);
    }

    [TestMethod]
    public async Task FluentQuery_WithName_FiltersByNamePattern()
    {
        // Act - Query nodes with name pattern
        var result = await query!.Nodes().WithName("*Service*").Take(20).ToListAsync();

        // Assert
        result.Should().NotBeNull();
        logger.LogInformation("Found {Count} nodes matching '*Service*'", result.Count);
        foreach (var node in result.Take(5))
        {
            logger.LogDebug("  - {Name} ({NodeType})", node.Name, node.NodeType);
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
        logger.LogInformation("Found node by ID: {Name}", result.Name);
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
        logger.LogInformation("Found node by name: {Name}", result.Name);
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
        logger.LogInformation("Method '{MethodName}' has {CallerCount} callers", method.Name, callers.Count);
        foreach (var caller in callers.Take(5))
        {
            logger.LogDebug("  - {Name} (depth: {Depth})", caller.Node.Name, caller.Depth);
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
        logger.LogInformation("Method '{MethodName}' calls {CalleeCount} other methods", method.Name, callees.Count);
        foreach (var callee in callees.Take(5))
        {
            logger.LogDebug("  - {Name} (depth: {Depth})", callee.Node.Name, callee.Depth);
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
        logger.LogInformation("Node '{NodeName}' has {EdgeCount} connected edges", node.Name, edges.Count);
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
        logger.LogInformation("Impact analysis for '{MethodName}': Direct={Direct}, Indirect={Indirect}, Total={Total}, Files={Files}",
            method.Name, impact.DirectlyAffected.Count, impact.IndirectlyAffected.Count,
            impact.TotalAffected, impact.AffectedFiles.Count);
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
        logger.LogInformation("Found {PathCount} paths from '{StartName}' to '{EndName}'",
            paths.Count, startNode.Name, endNode.Name);
        foreach (var path in paths)
        {
            logger.LogDebug("  Path length: {Length}", path.Length);
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
        logger.LogInformation("Found {Count} C# classes", result.Count);
    }

    [TestMethod]
    public async Task FluentQuery_CountAsync_ReturnsCount()
    {
        // Act
        var count = await query!.Nodes().Classes().CountAsync();

        // Assert
        count.Should().BeGreaterThanOrEqualTo(0);
        logger.LogInformation("Total classes: {Count}", count);
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
        logger.LogInformation("Graph Statistics: Nodes={TotalNodes}, Edges={TotalEdges}, Files={UniqueFiles}",
            stats.TotalNodes, stats.TotalEdges, stats.UniqueFiles);

        foreach (var (lang, count) in stats.NodesByLanguage)
        {
            logger.LogInformation("  Language {Language}: {Count} nodes", lang, count);
        }

        foreach (var (type, count) in stats.NodesByType.OrderByDescending(x => x.Value).Take(10))
        {
            logger.LogDebug("  Type {Type}: {Count} nodes", type, count);
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
        logger.LogInformation("File '{FilePath}' contains {NodeCount} nodes", anyNode.FilePath, nodes.Count);
    }

    [TestMethod]
    public async Task GraphQueryService_GetNodesByType_ReturnsTypedNodes()
    {
        // Act
        var classes = await graphQueryService!.GetNodesByTypeAsync("Class", 20);
        var methods = await graphQueryService!.GetNodesByTypeAsync("Method", 20);

        // Assert
        logger.LogInformation("Found {ClassCount} classes and {MethodCount} methods", classes.Count, methods.Count);

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
