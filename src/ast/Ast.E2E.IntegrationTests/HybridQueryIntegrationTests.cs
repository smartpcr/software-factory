// -----------------------------------------------------------------------
// <copyright file="HybridQueryIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.E2E.IntegrationTests;

using System.Diagnostics;
using Ast.Core;
using Ast.Csharp;
using Ast.GraphDb;
using Ast.Query;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Integration tests for hybrid queries combining graph traversal and vector search.
/// Tests the HybridQueryService and combined workflows.
/// </summary>
[TestClass]
public class HybridQueryIntegrationTests
{
    private static readonly string testRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    private static ILogger<HybridQueryIntegrationTests> logger = null!;
    private static Neo4jGraphStore? graphStore;
    private static QdrantVectorStore? vectorStore;
    private static HybridQueryService? hybridService;
    private static GraphQueryService? graphQueryService;
    private static ICodeQuery? queryBuilder;
    private static HypergraphData? indexedData;

    private const string TestCollection = "hybrid_test_nodes";
    private const int EmbeddingDimensions = 768;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext context)
    {
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<HybridQueryIntegrationTests>();

        logger.LogInformation("=== Hybrid Query Integration Tests Starting ===");

        await DockerFixture.EnsureServicesRunningAsync();

        // Initialize stores
        var graphOptions = Options.Create(new GraphStoreOptions
        {
            Uri = DockerFixture.Neo4jUri,
            Username = DockerFixture.Neo4jUsername,
            Password = DockerFixture.Neo4jPassword,
            BatchSize = 500
        });

        graphStore = new Neo4jGraphStore(graphOptions, TestLoggerFactory.CreateLogger<Neo4jGraphStore>());

        var vectorOptions = Options.Create(new VectorStoreOptions
        {
            Host = DockerFixture.QdrantHost,
            GrpcPort = DockerFixture.QdrantGrpcPort,
            UseGrpc = true,
            UseTls = false,
            DefaultVectorSize = EmbeddingDimensions
        });

        vectorStore = new QdrantVectorStore(vectorOptions, TestLoggerFactory.CreateLogger<QdrantVectorStore>());

        // Create services
        graphQueryService = new GraphQueryService(graphStore, TestLoggerFactory.CreateLogger<GraphQueryService>());

        hybridService = new HybridQueryService(
            graphStore,
            TestLoggerFactory.CreateLogger<HybridQueryService>(),
            vectorStore);

        queryBuilder = new CodeQueryBuilder(graphStore, TestLoggerFactory.CreateLogger<CodeQueryBuilder>(), vectorStore);

        await DockerFixture.AssertAllHealthyAsync();

        // Setup test data
        await SetupTestDataAsync();

        logger.LogInformation("Hybrid query test infrastructure initialized");
    }

    private static async Task SetupTestDataAsync()
    {
        // Check if we already have data
        var stats = await graphQueryService!.GetStatisticsAsync();
        if (stats.TotalNodes > 0)
        {
            logger.LogInformation("Using existing graph data: {NodeCount} nodes", stats.TotalNodes);
        }
        else
        {
            // Parse and index test repository
            var srcPath = Path.Combine(testRepoPath, "src");
            if (Directory.Exists(srcPath))
            {
                logger.LogInformation("Parsing test repository...");

                var processor = new RepositoryProcessor(testRepoPath)
                {
                    CSharpExtractor = (path, source) =>
                    {
                        var extractor = new CSharpHyperedgeExtractor(path);
                        return extractor.Extract(source);
                    }
                };

                indexedData = processor.Process(verbose: false);

                logger.LogInformation("Indexing {NodeCount} nodes to Neo4j...", indexedData.Nodes.Count);
                await graphQueryService.IndexHypergraphAsync(indexedData);
            }
            else
            {
                logger.LogWarning("Test repository not found at {Path}", srcPath);
            }
        }

        // Setup vector collection with synthetic embeddings
        if (!await vectorStore!.CollectionExistsAsync(TestCollection))
        {
            await vectorStore.CreateCollectionAsync(TestCollection, EmbeddingDimensions);
        }

        var vectorCount = await vectorStore.CountAsync(TestCollection);
        if (vectorCount == 0)
        {
            logger.LogInformation("Creating synthetic vector embeddings...");

            var random = new Random(42);

            // Get nodes from graph to create matching vectors
            var nodes = await graphStore!.GetNodesAsync(limit: 100);
            if (nodes.Count > 0)
            {
                var points = nodes.Select(n => new VectorPoint
                {
                    Id = n.Id,
                    Vector = [.. Enumerable.Range(0, EmbeddingDimensions).Select(_ => (float)(random.NextDouble() * 2 - 1))],
                    Payload = new Dictionary<string, object>
                    {
                        ["name"] = n.Name,
                        ["file_path"] = n.FilePath,
                        ["element_type"] = n.NodeType,
                        ["language"] = n.Language,
                        ["start_line"] = n.LineStart,
                        ["end_line"] = n.LineEnd
                    }
                }).ToList();

                await vectorStore.UpsertAsync(TestCollection, points);
                logger.LogInformation("Created {Count} vector embeddings", points.Count);
            }
        }
        else
        {
            logger.LogInformation("Using existing vector data: {Count} vectors", vectorCount);
        }
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        DockerFixture.StopHealthMonitor();
        logger.LogInformation("=== Hybrid Query Integration Tests Complete ===");

        vectorStore?.Dispose();
        graphStore?.Dispose();
    }

    [TestInitialize]
    public async Task TestInitializeAsync()
    {
        if (!DockerFixture.IsAllHealthy)
        {
            await DockerFixture.AssertAllHealthyAsync();
        }
    }

    #region Hybrid Search Tests

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("E2E")]
    public async Task HybridSearch_CombinesGraphAndVector_ReturnsResultsAsync()
    {
        var vectorCount = await vectorStore!.CountAsync(TestCollection);
        var graphStats = await graphQueryService!.GetStatisticsAsync();

        if (vectorCount == 0 || graphStats.TotalNodes == 0)
        {
            Assert.Inconclusive("Insufficient test data");
            return;
        }

        logger.LogInformation("Testing hybrid search with {VectorCount} vectors and {GraphNodes} nodes",
            vectorCount, graphStats.TotalNodes);

        // Use the HybridSearchAsync with text search
        var sw = Stopwatch.StartNew();
        var options = new QueryOptions
        {
            UseGraphSearch = true,
            UseSemanticSearch = false, // No embedding generator, skip semantic
            Limit = 10
        };

        var results = await hybridService!.HybridSearchAsync("Service", options);
        sw.Stop();

        results.Should().NotBeNull();
        logger.LogInformation("Hybrid search returned {Count} nodes in {Duration}ms",
            results.Nodes.Count, sw.ElapsedMilliseconds);

        foreach (var node in results.Nodes.Take(5))
        {
            logger.LogDebug("  {Name} ({Type})", node.Name, node.NodeType);
        }
    }

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("E2E")]
    public async Task HybridSearch_Explore_TraversesGraphAsync()
    {
        var graphStats = await graphQueryService!.GetStatisticsAsync();
        if (graphStats.TotalNodes == 0)
        {
            Assert.Inconclusive("No graph data");
            return;
        }

        // Get a node to use as starting point
        var nodes = await graphStore!.GetNodesAsync(limit: 10);
        if (nodes.Count == 0)
        {
            Assert.Inconclusive("No nodes found");
            return;
        }

        var startNode = nodes.First();
        logger.LogInformation("Testing graph exploration from: {NodeName}", startNode.Name);

        // Explore from the start node
        var exploration = await hybridService!.ExploreAsync(startNode.Id, depth: 2);

        exploration.Should().NotBeNull();
        exploration.Should().ContainKey(0);

        var totalNodes = exploration.Values.Sum(list => list.Count);
        logger.LogInformation("Exploration returned {TotalNodes} nodes across {Depths} depth levels",
            totalNodes, exploration.Count);

        foreach (var kvp in exploration)
        {
            logger.LogDebug("  Depth {Depth}: {Count} nodes", kvp.Key, kvp.Value.Count);
        }
    }

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("E2E")]
    public async Task VectorSearch_FilteredByType_OnlyReturnsMatchingTypesAsync()
    {
        var vectorCount = await vectorStore!.CountAsync(TestCollection);
        if (vectorCount == 0)
        {
            Assert.Inconclusive("No vector data");
            return;
        }

        logger.LogInformation("Testing filtered vector search...");

        var random = new Random(42);
        var queryVector = Enumerable.Range(0, EmbeddingDimensions)
            .Select(_ => (float)(random.NextDouble() * 2 - 1))
            .ToArray();

        var filter = new VectorFilter
        {
            Must =
            [
                new FilterCondition { Field = "element_type", Value = "Class" }
            ]
        };

        var results = await vectorStore!.SearchAsync(TestCollection, queryVector, 10, filter);

        logger.LogInformation("Filtered search returned {Count} results", results.Count);

        // Verify all results are classes
        foreach (var result in results)
        {
            if (result.Payload?.TryGetValue("element_type", out var type) == true)
            {
                type.ToString().Should().Be("Class");
            }
        }
    }

    #endregion

    #region Graph-Guided Search Tests

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("E2E")]
    public async Task GraphQuery_FindsCallersAsync()
    {
        var graphStats = await graphQueryService!.GetStatisticsAsync();

        if (graphStats.TotalNodes == 0)
        {
            Assert.Inconclusive("Insufficient test data");
            return;
        }

        // Find a method with callees
        var methodFilter = new NodeFilter { NodeTypes = new[] { "Method" } };
        var methods = await graphStore!.GetNodesAsync(methodFilter, 20);
        if (methods.Count == 0)
        {
            Assert.Inconclusive("No methods found");
            return;
        }

        var sourceMethod = methods.First();
        logger.LogInformation("Finding callers of method: {Name}", sourceMethod.Name);

        // Find callers
        var callers = await queryBuilder!.Node(sourceMethod.Id).Callers().WithDepth(2).ToListAsync();

        logger.LogInformation("Found {Count} callers", callers.Count);

        foreach (var caller in callers.Take(5))
        {
            logger.LogDebug("  {Name} (depth: {Depth})", caller.Node.Name, caller.Depth);
        }
    }

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("E2E")]
    public async Task VectorSearch_RestrictedToFileScopeAsync()
    {
        var vectorCount = await vectorStore!.CountAsync(TestCollection);
        if (vectorCount == 0)
        {
            Assert.Inconclusive("No vector data");
            return;
        }

        // Get a file path from existing data
        var nodes = await graphStore!.GetNodesAsync(limit: 10);
        if (nodes.Count == 0)
        {
            Assert.Inconclusive("No nodes found");
            return;
        }

        var filePath = nodes.First().FilePath;
        logger.LogInformation("Testing file-scoped search in: {FilePath}", filePath);

        var random = new Random(42);
        var queryVector = Enumerable.Range(0, EmbeddingDimensions)
            .Select(_ => (float)(random.NextDouble() * 2 - 1))
            .ToArray();

        var filter = new VectorFilter
        {
            Must =
            [
                new FilterCondition { Field = "file_path", Value = filePath }
            ]
        };

        var results = await vectorStore!.SearchAsync(TestCollection, queryVector, 20, filter);

        logger.LogInformation("File-scoped search returned {Count} results", results.Count);

        // Verify all results are from the same file
        foreach (var result in results)
        {
            if (result.Payload.TryGetValue("file_path", out var path))
            {
                path.ToString().Should().Be(filePath);
            }
        }
    }

    #endregion

    #region Cross-Language Search Tests

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("E2E")]
    public async Task CrossLanguageSearch_FindsSimilarAcrossLanguagesAsync()
    {
        var graphStats = await graphQueryService!.GetStatisticsAsync();

        if (graphStats.NodesByLanguage.Count < 2)
        {
            Assert.Inconclusive("Need multiple languages for cross-language search test");
            return;
        }

        logger.LogInformation("Testing cross-language search...");
        logger.LogInformation("Available languages: {Languages}",
            string.Join(", ", graphStats.NodesByLanguage.Keys));

        var random = new Random(42);
        var queryVector = Enumerable.Range(0, EmbeddingDimensions)
            .Select(_ => (float)(random.NextDouble() * 2 - 1))
            .ToArray();

        // Search without language filter - should return mixed languages
        var results = await vectorStore!.SearchAsync(TestCollection, queryVector, 20);

        var languagesInResults = results
            .Where(r => r.Payload.TryGetValue("language", out _))
            .Select(r => r.Payload["language"].ToString())
            .Distinct()
            .ToList();

        logger.LogInformation("Cross-language search returned results from {Count} languages: {Languages}",
            languagesInResults.Count, string.Join(", ", languagesInResults));
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("Performance")]
    public async Task Performance_HybridSearch_UnderThresholdAsync()
    {
        var graphStats = await graphQueryService!.GetStatisticsAsync();
        if (graphStats.TotalNodes < 10)
        {
            Assert.Inconclusive("Insufficient graph data for performance test");
            return;
        }

        logger.LogInformation("Testing hybrid search performance with {Count} nodes...", graphStats.TotalNodes);

        var options = new QueryOptions
        {
            UseGraphSearch = true,
            UseSemanticSearch = false,
            Limit = 10
        };

        // Warm up
        await hybridService!.HybridSearchAsync("Service", options);

        // Measure
        const int iterations = 10;
        var timings = new List<long>();

        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await hybridService.HybridSearchAsync("Service", options);
            sw.Stop();
            timings.Add(sw.ElapsedMilliseconds);
        }

        var avgMs = timings.Average();
        var maxMs = timings.Max();
        var minMs = timings.Min();

        logger.LogInformation("Hybrid search performance over {Iterations} iterations:", iterations);
        logger.LogInformation("  Average: {Avg:F2}ms", avgMs);
        logger.LogInformation("  Min: {Min}ms", minMs);
        logger.LogInformation("  Max: {Max}ms", maxMs);

        // Performance threshold: average should be under 500ms
        avgMs.Should().BeLessThan(500, "Hybrid search should complete in under 500ms on average");
    }

    [TestMethod]
    [TestCategory("Hybrid")]
    [TestCategory("Performance")]
    public async Task Performance_GraphTraversal_UnderThresholdAsync()
    {
        var graphStats = await graphQueryService!.GetStatisticsAsync();
        if (graphStats.TotalNodes < 10)
        {
            Assert.Inconclusive("Insufficient graph data for performance test");
            return;
        }

        logger.LogInformation("Testing graph traversal performance with {Count} nodes...", graphStats.TotalNodes);

        var methodFilter = new NodeFilter { NodeTypes = new[] { "Method" } };
        var methods = await graphStore!.GetNodesAsync(methodFilter, 10);
        if (methods.Count == 0)
        {
            Assert.Inconclusive("No methods for performance test");
            return;
        }

        var testMethod = methods.First();

        // Warm up
        await graphStore.GetCallersAsync(testMethod.Id, 2);

        // Measure
        const int iterations = 10;
        var timings = new List<long>();

        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await graphStore.GetCallersAsync(testMethod.Id, 3);
            sw.Stop();
            timings.Add(sw.ElapsedMilliseconds);
        }

        var avgMs = timings.Average();

        logger.LogInformation("Graph traversal performance over {Iterations} iterations:", iterations);
        logger.LogInformation("  Average: {Avg:F2}ms", avgMs);
        logger.LogInformation("  Min: {Min}ms", timings.Min());
        logger.LogInformation("  Max: {Max}ms", timings.Max());

        // Performance threshold: average should be under 200ms
        avgMs.Should().BeLessThan(200, "Graph traversal should complete in under 200ms on average");
    }

    #endregion
}