// -----------------------------------------------------------------------
// <copyright file="E2EIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.E2E.IntegrationTests;

using System.Diagnostics;
using Ast.Core;
using Ast.Csharp;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.PowerShell;
using Ast.Query;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// End-to-end integration tests for the complete pipeline:
/// Parse -> Enrich -> Embed -> Index -> Query.
/// </summary>
[TestClass]
public class E2EIntegrationTests
{
    private static readonly string csharpRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    private static readonly string powershellRepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hub\sln\Deploy"
        : "/mnt/e/work/hub/sln/Deploy";

    private static ILogger<E2EIntegrationTests> logger = null!;
    private static Neo4jGraphStore? graphStore;
    private static QdrantVectorStore? vectorStore;
    private static GraphQueryService? graphQueryService;
    private static ICodeQuery? queryBuilder;
    private static HypergraphData? indexedData;

    private const string TestCollection = "e2e_test_nodes";
    private const int EmbeddingDimensions = 768;

    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext context)
    {
        // Initialize logging
        TestLoggerFactory.Initialize(context);
        logger = TestLoggerFactory.CreateLogger<E2EIntegrationTests>();

        logger.LogInformation("=== E2E Integration Tests Starting ===");

        // Ensure all services are running
        await DockerFixture.EnsureServicesRunningAsync();

        // Initialize Neo4j graph store
        var graphOptions = Options.Create(new GraphStoreOptions
        {
            Uri = DockerFixture.Neo4jUri,
            Username = DockerFixture.Neo4jUsername,
            Password = DockerFixture.Neo4jPassword,
            BatchSize = 500
        });

        graphStore = new Neo4jGraphStore(graphOptions, TestLoggerFactory.CreateLogger<Neo4jGraphStore>());

        // Initialize Qdrant vector store
        var vectorOptions = Options.Create(new VectorStoreOptions
        {
            Host = DockerFixture.QdrantHost,
            GrpcPort = DockerFixture.QdrantGrpcPort,
            UseGrpc = true,
            UseTls = false,
            DefaultVectorSize = EmbeddingDimensions
        });

        vectorStore = new QdrantVectorStore(vectorOptions, TestLoggerFactory.CreateLogger<QdrantVectorStore>());

        // Verify connectivity
        await DockerFixture.AssertAllHealthyAsync();

        // Clear existing test data
        logger.LogInformation("Clearing existing test data...");
        await graphStore.ClearAsync();
        await graphStore.CreateIndexesAsync();

        if (await vectorStore.CollectionExistsAsync(TestCollection))
        {
            await vectorStore.DeleteCollectionAsync(TestCollection);
        }

        // Create query services
        queryBuilder = new CodeQueryBuilder(graphStore, TestLoggerFactory.CreateLogger<CodeQueryBuilder>());
        graphQueryService = new GraphQueryService(graphStore, TestLoggerFactory.CreateLogger<GraphQueryService>());

        logger.LogInformation("E2E test infrastructure initialized");
        logger.LogInformation("Neo4j: {Uri}", DockerFixture.Neo4jUri);
        logger.LogInformation("Qdrant: {Host}:{Port}", DockerFixture.QdrantHost, DockerFixture.QdrantGrpcPort);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        DockerFixture.StopHealthMonitor();

        logger.LogInformation("=== E2E Integration Tests Complete ===");
        logger.LogInformation("Services remain running for manual validation:");
        logger.LogInformation("  Neo4j Browser: http://localhost:7474");
        logger.LogInformation("  Qdrant Dashboard: http://localhost:6333/dashboard");

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

    #region Pipeline Step Tests

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Pipeline")]
    public async Task Step1_ParseRepository_ExtractsHypergraphAsync()
    {
        // Skip if repo doesn't exist
        var srcPath = Path.Combine(csharpRepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            Assert.Inconclusive($"C# repo not found at {srcPath}");
            return;
        }

        logger.LogInformation("Step 1: Parsing C# repository...");
        var sw = Stopwatch.StartNew();

        // Parse repository
        var processor = new RepositoryProcessor(csharpRepoPath)
        {
            CSharpExtractor = (path, source) =>
            {
                var extractor = new CSharpHyperedgeExtractor(path);
                return extractor.Extract(source);
            }
        };

        indexedData = processor.Process(verbose: false);
        sw.Stop();

        // Verify results
        indexedData.Should().NotBeNull();
        indexedData.Nodes.Should().NotBeEmpty();
        indexedData.Hyperedges.Should().NotBeEmpty();

        logger.LogInformation("Parsed {NodeCount} nodes and {EdgeCount} edges in {Duration}ms",
            indexedData.Nodes.Count, indexedData.Hyperedges.Count, sw.ElapsedMilliseconds);

        // Verify metadata
        indexedData.Metadata.NumCSharpNodes.Should().BeGreaterThan(0);
        indexedData.Metadata.NumFiles.Should().BeGreaterThan(0);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Pipeline")]
    public async Task Step2_IndexToNeo4j_CreatesGraphDataAsync()
    {
        // Ensure we have data to index
        if (indexedData == null || indexedData.Nodes.Count == 0)
        {
            await this.Step1_ParseRepository_ExtractsHypergraphAsync();
        }

        if (indexedData == null || indexedData.Nodes.Count == 0)
        {
            Assert.Inconclusive("No data to index");
            return;
        }

        logger.LogInformation("Step 2: Indexing to Neo4j...");
        var sw = Stopwatch.StartNew();

        // Index data
        var (nodesIndexed, edgesIndexed) = await graphQueryService!.IndexHypergraphAsync(indexedData);
        sw.Stop();

        // Verify results
        nodesIndexed.Should().Be(indexedData.Nodes.Count);
        edgesIndexed.Should().Be(indexedData.Hyperedges.Count);

        logger.LogInformation("Indexed {NodeCount} nodes and {EdgeCount} edges in {Duration}ms",
            nodesIndexed, edgesIndexed, sw.ElapsedMilliseconds);

        // Verify data is queryable
        var stats = await graphQueryService.GetStatisticsAsync();
        stats.TotalNodes.Should().BeGreaterThan(0);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Pipeline")]
    public async Task Step3_GenerateEmbeddings_CreatesVectorsAsync()
    {
        // This test requires an embedding endpoint (Ollama or similar)
        // Skip if not available
        var embeddingEndpoint = Environment.GetEnvironmentVariable("EMBEDDING_ENDPOINT") ?? "http://localhost:11434";

        // Check if embedding service is available
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await httpClient.GetAsync($"{embeddingEndpoint}/api/tags");
            if (!response.IsSuccessStatusCode)
            {
                Assert.Inconclusive($"Embedding service not available at {embeddingEndpoint}");
                return;
            }
        }
        catch
        {
            Assert.Inconclusive($"Embedding service not available at {embeddingEndpoint}");
            return;
        }

        // Ensure we have data
        if (indexedData == null || indexedData.Nodes.Count == 0)
        {
            await this.Step1_ParseRepository_ExtractsHypergraphAsync();
        }

        if (indexedData == null || indexedData.Nodes.Count == 0)
        {
            Assert.Inconclusive("No data to embed");
            return;
        }

        logger.LogInformation("Step 3: Generating embeddings...");
        var sw = Stopwatch.StartNew();

        // Create embedding generator
        var generatorOptions = new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.Http,
            Endpoint = embeddingEndpoint,
            ModelName = "nomic-embed-text",
            Dimensions = EmbeddingDimensions
        };

        var factory = new EmbeddingGeneratorFactory(NullLoggerFactory.Instance);
        using var generator = factory.Create(generatorOptions);
        using var embeddingService = new CodeEmbeddingService(generator, NullLogger<CodeEmbeddingService>.Instance);

        // Create a subset of the data for embedding (limit to 100 nodes for test speed)
        var subsetData = new HypergraphData
        {
            Nodes = indexedData.Nodes.Take(100).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Hyperedges = [],
            Metadata = indexedData.Metadata
        };

        // Generate embeddings using the correct API
        var embeddings = await embeddingService.GenerateNodeEmbeddingsAsync(subsetData);

        sw.Stop();

        // Verify results
        embeddings.Should().NotBeEmpty();
        embeddings.Should().HaveCount(subsetData.Nodes.Count);
        embeddings.All(e => e.Vector.Length == EmbeddingDimensions).Should().BeTrue();

        logger.LogInformation("Generated {Count} embeddings in {Duration}ms",
            embeddings.Count, sw.ElapsedMilliseconds);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Pipeline")]
    public async Task Step4_IndexToQdrant_CreatesVectorCollectionAsync()
    {
        logger.LogInformation("Step 4: Indexing to Qdrant...");

        // Create test collection
        if (!await vectorStore!.CollectionExistsAsync(TestCollection))
        {
            await vectorStore.CreateCollectionAsync(TestCollection, EmbeddingDimensions);
        }

        // Generate some test vectors (random for this test)
        var random = new Random(42);
        var testPoints = Enumerable.Range(0, 50).Select(i => new VectorPoint
        {
            Id = $"test-node-{i}",
            Vector = [.. Enumerable.Range(0, EmbeddingDimensions).Select(_ => (float)(random.NextDouble() * 2 - 1))],
            Payload = new Dictionary<string, object>
            {
                ["name"] = $"TestNode{i}",
                ["file_path"] = $"/test/file{i}.cs",
                ["element_type"] = i % 3 == 0 ? "Class" : "Method",
                ["language"] = "csharp",
                ["start_line"] = i * 10,
                ["end_line"] = i * 10 + 9
            }
        }).ToList();

        var sw = Stopwatch.StartNew();
        await vectorStore.UpsertAsync(TestCollection, testPoints);
        sw.Stop();

        logger.LogInformation("Indexed {Count} vectors in {Duration}ms",
            testPoints.Count, sw.ElapsedMilliseconds);

        // Verify data is queryable
        var count = await vectorStore.CountAsync(TestCollection);
        count.Should().Be(testPoints.Count);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Pipeline")]
    public async Task Step5_QueryGraph_ReturnsNodesAsync()
    {
        // Ensure we have indexed data
        var stats = await graphQueryService!.GetStatisticsAsync();
        if (stats.TotalNodes == 0)
        {
            await this.Step2_IndexToNeo4j_CreatesGraphDataAsync();
            stats = await graphQueryService.GetStatisticsAsync();
        }

        if (stats.TotalNodes == 0)
        {
            Assert.Inconclusive("No data in graph database");
            return;
        }

        logger.LogInformation("Step 5: Querying graph database...");

        // Query all nodes
        var nodes = await queryBuilder!.Nodes().Take(20).ToListAsync();
        nodes.Should().NotBeEmpty();
        logger.LogInformation("Found {Count} nodes", nodes.Count);

        // Query by type
        var classes = await queryBuilder.Nodes().Classes().Take(10).ToListAsync();
        logger.LogInformation("Found {Count} classes", classes.Count);

        // Query by language
        var csharpNodes = await queryBuilder.Nodes().OfLanguage("csharp").Take(10).ToListAsync();
        if (csharpNodes.Count > 0)
        {
            csharpNodes.Should().AllSatisfy(n => n.Language.Should().Be("csharp"));
        }
        logger.LogInformation("Found {Count} C# nodes", csharpNodes.Count);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Pipeline")]
    public async Task Step6_SemanticSearch_ReturnsRelevantResultsAsync()
    {
        // Ensure we have vector data
        if (!await vectorStore!.CollectionExistsAsync(TestCollection))
        {
            await this.Step4_IndexToQdrant_CreatesVectorCollectionAsync();
        }

        var count = await vectorStore.CountAsync(TestCollection);
        if (count == 0)
        {
            Assert.Inconclusive("No vectors in collection");
            return;
        }

        logger.LogInformation("Step 6: Performing semantic search...");

        // Create a random query vector
        var random = new Random(42);
        var queryVector = Enumerable.Range(0, EmbeddingDimensions)
            .Select(_ => (float)(random.NextDouble() * 2 - 1))
            .ToArray();

        var sw = Stopwatch.StartNew();
        var results = await vectorStore.SearchAsync(TestCollection, queryVector, 5);
        sw.Stop();

        // Verify results
        results.Should().NotBeEmpty();
        results.Should().HaveCountLessOrEqualTo(5);
        results.Should().BeInDescendingOrder(r => r.Score);

        logger.LogInformation("Semantic search returned {Count} results in {Duration}ms",
            results.Count, sw.ElapsedMilliseconds);

        foreach (var result in results.Take(3))
        {
            logger.LogDebug("  {Id}: score={Score:F4}", result.Id, result.Score);
        }
    }

    #endregion

    #region Graph Query Tests

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Query")]
    public async Task Query_Callers_FindsCallingMethodsAsync()
    {
        var stats = await graphQueryService!.GetStatisticsAsync();
        if (stats.TotalNodes == 0)
        {
            Assert.Inconclusive("No data in graph database");
            return;
        }

        // Find a method node
        var methods = await queryBuilder!.Nodes().Methods().Take(5).ToListAsync();
        if (methods.Count == 0)
        {
            Assert.Inconclusive("No method nodes found");
            return;
        }

        var method = methods.First();
        logger.LogInformation("Finding callers of method: {Name}", method.Name);

        var callers = await queryBuilder.Node(method.Id).Callers().WithDepth(2).ToListAsync();

        logger.LogInformation("Found {Count} callers", callers.Count);
        foreach (var caller in callers.Take(5))
        {
            logger.LogDebug("  {Name} at depth {Depth}", caller.Node.Name, caller.Depth);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Query")]
    public async Task Query_Callees_FindsCalledMethodsAsync()
    {
        var stats = await graphQueryService!.GetStatisticsAsync();
        if (stats.TotalNodes == 0)
        {
            Assert.Inconclusive("No data in graph database");
            return;
        }

        var methods = await queryBuilder!.Nodes().Methods().Take(5).ToListAsync();
        if (methods.Count == 0)
        {
            Assert.Inconclusive("No method nodes found");
            return;
        }

        var method = methods.First();
        logger.LogInformation("Finding callees of method: {Name}", method.Name);

        var callees = await queryBuilder.Node(method.Id).Callees().WithDepth(2).ToListAsync();

        logger.LogInformation("Found {Count} callees", callees.Count);
        foreach (var callee in callees.Take(5))
        {
            logger.LogDebug("  {Name} at depth {Depth}", callee.Node.Name, callee.Depth);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Query")]
    public async Task Query_Impact_AnalyzesAffectedCodeAsync()
    {
        var stats = await graphQueryService!.GetStatisticsAsync();
        if (stats.TotalNodes == 0)
        {
            Assert.Inconclusive("No data in graph database");
            return;
        }

        var methods = await queryBuilder!.Nodes().Methods().Take(5).ToListAsync();
        if (methods.Count == 0)
        {
            Assert.Inconclusive("No method nodes found");
            return;
        }

        var method = methods.First();
        logger.LogInformation("Analyzing impact of changes to: {Name}", method.Name);

        var impact = await queryBuilder.Node(method.Id).Impact().WithDepth(3).ExecuteAsync();

        impact.Should().NotBeNull();
        impact.SourceNode.Id.Should().Be(method.Id);

        logger.LogInformation("Impact analysis: Direct={Direct}, Indirect={Indirect}, Total={Total}, Files={Files}",
            impact.DirectlyAffected.Count,
            impact.IndirectlyAffected.Count,
            impact.TotalAffected,
            impact.AffectedFiles.Count);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Query")]
    public async Task Query_Statistics_ReturnsCompleteStatsAsync()
    {
        var stats = await graphQueryService!.GetStatisticsAsync();

        stats.Should().NotBeNull();

        logger.LogInformation("Graph Statistics:");
        logger.LogInformation("  Total Nodes: {Count}", stats.TotalNodes);
        logger.LogInformation("  Total Edges: {Count}", stats.TotalEdges);
        logger.LogInformation("  Unique Files: {Count}", stats.UniqueFiles);
        logger.LogInformation("  Cross-Language Edges: {Count}", stats.CrossLanguageEdges);

        if (stats.NodesByLanguage.Count > 0)
        {
            logger.LogInformation("  By Language:");
            foreach (var (lang, count) in stats.NodesByLanguage)
            {
                logger.LogInformation("    {Language}: {Count}", lang, count);
            }
        }

        if (stats.NodesByType.Count > 0)
        {
            logger.LogInformation("  By Type (top 5):");
            foreach (var (type, count) in stats.NodesByType.OrderByDescending(x => x.Value).Take(5))
            {
                logger.LogInformation("    {Type}: {Count}", type, count);
            }
        }
    }

    #endregion

    #region Vector Search Tests

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Vector")]
    public async Task Vector_FilteredSearch_AppliesFiltersAsync()
    {
        if (!await vectorStore!.CollectionExistsAsync(TestCollection))
        {
            await this.Step4_IndexToQdrant_CreatesVectorCollectionAsync();
        }

        var count = await vectorStore.CountAsync(TestCollection);
        if (count == 0)
        {
            Assert.Inconclusive("No vectors in collection");
            return;
        }

        logger.LogInformation("Performing filtered vector search...");

        var random = new Random(42);
        var queryVector = Enumerable.Range(0, EmbeddingDimensions)
            .Select(_ => (float)(random.NextDouble() * 2 - 1))
            .ToArray();

        // Search with language filter
        var filter = new VectorFilter
        {
            Must =
            [
                new FilterCondition { Field = "language", Value = "csharp" }
            ]
        };

        var results = await vectorStore.SearchAsync(TestCollection, queryVector, 10, filter);

        logger.LogInformation("Filtered search returned {Count} results", results.Count);

        // Verify filter was applied
        foreach (var result in results)
        {
            if (result.Payload.TryGetValue("language", out var lang))
            {
                lang.ToString().Should().Be("csharp");
            }
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("Vector")]
    public async Task Vector_CollectionManagement_WorksCorrectlyAsync()
    {
        var testCollectionName = "e2e_management_test";

        logger.LogInformation("Testing collection management...");

        // Create collection
        if (await vectorStore!.CollectionExistsAsync(testCollectionName))
        {
            await vectorStore.DeleteCollectionAsync(testCollectionName);
        }

        (await vectorStore.CollectionExistsAsync(testCollectionName)).Should().BeFalse();

        await vectorStore.CreateCollectionAsync(testCollectionName, 128);

        (await vectorStore.CollectionExistsAsync(testCollectionName)).Should().BeTrue();

        // Add some data
        var points = new List<VectorPoint>
        {
            new() {
                Id = "mgmt-test-1",
                Vector = [.. Enumerable.Range(0, 128).Select(i => (float)i / 128)],
                Payload = new Dictionary<string, object> { ["test"] = true }
            }
        };

        await vectorStore.UpsertAsync(testCollectionName, points);

        var count = await vectorStore.CountAsync(testCollectionName);
        count.Should().Be(1);

        // Cleanup
        await vectorStore.DeleteCollectionAsync(testCollectionName);
        (await vectorStore.CollectionExistsAsync(testCollectionName)).Should().BeFalse();

        logger.LogInformation("Collection management tests passed");
    }

    #endregion
}