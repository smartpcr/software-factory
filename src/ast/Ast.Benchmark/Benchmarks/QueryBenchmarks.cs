// -----------------------------------------------------------------------
// <copyright file="QueryBenchmarks.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Benchmark.Benchmarks;

using Ast.Core;
using Ast.Csharp;
using Ast.GraphDb;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Benchmarks for Neo4j query performance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 5)]
[MemoryDiagnoser]
[RankColumn]
public class QueryBenchmarks
{
    private static readonly string RepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    private Neo4jGraphStore? graphStore;
    private List<string> sampleNodeIds = new();
    private List<string> methodNodeIds = new();
    private List<string> classNodeIds = new();

    [GlobalSetup]
    public void Setup()
    {
        // Create graph store
        var options = Options.Create(new GraphStoreOptions
        {
            Uri = "bolt://localhost:7687",
            Username = "neo4j",
            Password = "password123",
            BatchSize = 500
        });
        this.graphStore = new Neo4jGraphStore(options, NullLogger<Neo4jGraphStore>.Instance);

        // Verify connection
        var available = this.graphStore.IsAvailableAsync().GetAwaiter().GetResult();
        if (!available)
        {
            throw new InvalidOperationException("Neo4j is not available. Make sure it's running.");
        }

        // Check if there's data in the database
        var stats = this.graphStore.GetStatisticsAsync().GetAwaiter().GetResult();
        if (stats.TotalNodes < 100)
        {
            // Need to populate the database first
            this.PopulateDatabase();
            stats = this.graphStore.GetStatisticsAsync().GetAwaiter().GetResult();
        }

        // Get sample node IDs for queries
        var nodes = this.graphStore.GetNodesAsync(limit: 1000).GetAwaiter().GetResult();
        this.sampleNodeIds = nodes.Select(n => n.Id).ToList();
        this.methodNodeIds = nodes.Where(n => n.NodeType.Equals("method", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id).Take(100).ToList();
        this.classNodeIds = nodes.Where(n => n.NodeType.Equals("class", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id).Take(50).ToList();

        if (this.sampleNodeIds.Count == 0)
        {
            throw new InvalidOperationException("No nodes found in database. Run interactive mode first.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.graphStore?.Dispose();
    }

    [Benchmark(Description = "Get node by ID")]
    public async Task<HypergraphNode?> GetNodeById()
    {
        var nodeId = this.sampleNodeIds[Random.Shared.Next(this.sampleNodeIds.Count)];
        return await this.graphStore!.GetNodeAsync(nodeId);
    }

    [Benchmark(Description = "Get nodes (limit 100)")]
    public async Task<IReadOnlyList<HypergraphNode>> GetNodes()
    {
        return await this.graphStore!.GetNodesAsync(limit: 100);
    }

    [Benchmark(Description = "Get nodes by type (class)")]
    public async Task<IReadOnlyList<HypergraphNode>> GetNodesByType()
    {
        return await this.graphStore!.GetNodesAsync(
            new NodeFilter { NodeTypes = new[] { "class" } },
            limit: 50);
    }

    [Benchmark(Description = "Get nodes by language (csharp)")]
    public async Task<IReadOnlyList<HypergraphNode>> GetNodesByLanguage()
    {
        return await this.graphStore!.GetNodesAsync(
            new NodeFilter { Language = "csharp" },
            limit: 50);
    }

    [Benchmark(Description = "Get nodes by name pattern")]
    public async Task<IReadOnlyList<HypergraphNode>> GetNodesByNamePattern()
    {
        return await this.graphStore!.GetNodesAsync(
            new NodeFilter { NamePattern = "*Service*" },
            limit: 50);
    }

    [Benchmark(Description = "Get callers (depth 1)")]
    public async Task<IReadOnlyList<NodeWithDepth>> GetCallersDepth1()
    {
        if (this.methodNodeIds.Count == 0) return new List<NodeWithDepth>();
        var nodeId = this.methodNodeIds[Random.Shared.Next(this.methodNodeIds.Count)];
        return await this.graphStore!.GetCallersAsync(nodeId, depth: 1);
    }

    [Benchmark(Description = "Get callers (depth 2)")]
    public async Task<IReadOnlyList<NodeWithDepth>> GetCallersDepth2()
    {
        if (this.methodNodeIds.Count == 0) return new List<NodeWithDepth>();
        var nodeId = this.methodNodeIds[Random.Shared.Next(this.methodNodeIds.Count)];
        return await this.graphStore!.GetCallersAsync(nodeId, depth: 2);
    }

    [Benchmark(Description = "Get callers (depth 3)")]
    public async Task<IReadOnlyList<NodeWithDepth>> GetCallersDepth3()
    {
        if (this.methodNodeIds.Count == 0) return new List<NodeWithDepth>();
        var nodeId = this.methodNodeIds[Random.Shared.Next(this.methodNodeIds.Count)];
        return await this.graphStore!.GetCallersAsync(nodeId, depth: 3);
    }

    [Benchmark(Description = "Get callees (depth 2)")]
    public async Task<IReadOnlyList<NodeWithDepth>> GetCallees()
    {
        if (this.methodNodeIds.Count == 0) return new List<NodeWithDepth>();
        var nodeId = this.methodNodeIds[Random.Shared.Next(this.methodNodeIds.Count)];
        return await this.graphStore!.GetCalleesAsync(nodeId, depth: 2);
    }

    [Benchmark(Description = "Get edges for node")]
    public async Task<IReadOnlyList<Hyperedge>> GetEdgesForNode()
    {
        var nodeId = this.sampleNodeIds[Random.Shared.Next(this.sampleNodeIds.Count)];
        return await this.graphStore!.GetEdgesForNodeAsync(nodeId);
    }

    [Benchmark(Description = "Find paths between nodes")]
    public async Task<IReadOnlyList<GraphPath>> FindPaths()
    {
        if (this.classNodeIds.Count < 2) return new List<GraphPath>();
        var startId = this.classNodeIds[0];
        var endId = this.classNodeIds[Math.Min(10, this.classNodeIds.Count - 1)];
        return await this.graphStore!.FindPathsAsync(startId, endId, maxDepth: 5);
    }

    [Benchmark(Description = "Analyze impact")]
    public async Task<ImpactAnalysis> AnalyzeImpact()
    {
        if (this.methodNodeIds.Count == 0)
            return new ImpactAnalysis { SourceNode = null! };
        var nodeId = this.methodNodeIds[Random.Shared.Next(this.methodNodeIds.Count)];
        return await this.graphStore!.AnalyzeImpactAsync(nodeId, maxDepth: 2);
    }

    [Benchmark(Description = "Get statistics")]
    public async Task<GraphStatistics> GetStatistics()
    {
        return await this.graphStore!.GetStatisticsAsync();
    }

    private void PopulateDatabase()
    {
        var srcPath = Path.Combine(RepoPath, "src");
        if (!Directory.Exists(srcPath))
        {
            srcPath = RepoPath;
        }

        if (!Directory.Exists(srcPath))
        {
            throw new InvalidOperationException($"Repository not found at {RepoPath}");
        }

        var allFiles = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains(".Designer."))
            .Take(200)
            .ToList();

        var allNodes = new Dictionary<string, HypergraphNode>();
        var allEdges = new List<Hyperedge>();

        foreach (var file in allFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var extractor = new CSharpHyperedgeExtractor(file);
                var (nodes, edges) = extractor.Extract(content);

                foreach (var node in nodes)
                {
                    allNodes.TryAdd(node.Key, node.Value);
                }
                allEdges.AddRange(edges);
            }
            catch
            {
                // Skip files that fail to parse
            }
        }

        // Ingest data
        this.graphStore!.CreateIndexesAsync().GetAwaiter().GetResult();
        this.graphStore.CreateNodesAsync(allNodes.Values).GetAwaiter().GetResult();
        this.graphStore.CreateEdgesAsync(allEdges).GetAwaiter().GetResult();
    }
}
