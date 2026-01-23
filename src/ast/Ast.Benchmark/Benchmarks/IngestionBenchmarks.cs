// -----------------------------------------------------------------------
// <copyright file="IngestionBenchmarks.cs" company="Microsoft Corp.">
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
/// Benchmarks for Neo4j ingestion performance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser]
[RankColumn]
public class IngestionBenchmarks
{
    private static readonly string RepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    private HypergraphData smallData = new();
    private HypergraphData mediumData = new();
    private HypergraphData largeData = new();
    private Neo4jGraphStore? graphStore;

    [Params(100, 500, 1000)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Create graph store
        var options = Options.Create(new GraphStoreOptions
        {
            Uri = "bolt://localhost:7687",
            Username = "neo4j",
            Password = "password123",
            BatchSize = this.BatchSize
        });
        this.graphStore = new Neo4jGraphStore(options, NullLogger<Neo4jGraphStore>.Instance);

        // Pre-extract data for different sizes
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
            .ToList();

        // Extract different sized datasets
        this.smallData = this.ExtractFiles(allFiles.Take(20).ToList());
        this.mediumData = this.ExtractFiles(allFiles.Take(100).ToList());
        this.largeData = this.ExtractFiles(allFiles.Take(500).ToList());

        // Verify connection
        var available = this.graphStore.IsAvailableAsync().GetAwaiter().GetResult();
        if (!available)
        {
            throw new InvalidOperationException("Neo4j is not available. Make sure it's running.");
        }

        // Create indexes once
        this.graphStore.CreateIndexesAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.graphStore?.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Clear database before each iteration
        this.graphStore?.ClearAsync().GetAwaiter().GetResult();
    }

    [Benchmark(Description = "Ingest small dataset (~20 files)")]
    public async Task<int> IngestSmallDataset()
    {
        return await this.IngestDataAsync(this.smallData);
    }

    [Benchmark(Description = "Ingest medium dataset (~100 files)")]
    public async Task<int> IngestMediumDataset()
    {
        return await this.IngestDataAsync(this.mediumData);
    }

    [Benchmark(Description = "Ingest large dataset (~500 files)")]
    public async Task<int> IngestLargeDataset()
    {
        return await this.IngestDataAsync(this.largeData);
    }

    [Benchmark(Description = "Ingest nodes only (medium)")]
    public async Task<int> IngestNodesOnly()
    {
        return await this.graphStore!.CreateNodesAsync(this.mediumData.Nodes.Values);
    }

    [Benchmark(Description = "Ingest edges only (medium)")]
    public async Task<int> IngestEdgesOnly()
    {
        // First ingest nodes (required for edges)
        await this.graphStore!.CreateNodesAsync(this.mediumData.Nodes.Values);
        return await this.graphStore.CreateEdgesAsync(this.mediumData.Hyperedges);
    }

    private async Task<int> IngestDataAsync(HypergraphData data)
    {
        var nodesCreated = await this.graphStore!.CreateNodesAsync(data.Nodes.Values);
        var edgesCreated = await this.graphStore.CreateEdgesAsync(data.Hyperedges);
        return nodesCreated + edgesCreated;
    }

    private HypergraphData ExtractFiles(List<string> files)
    {
        var allNodes = new Dictionary<string, HypergraphNode>();
        var allEdges = new List<Hyperedge>();

        foreach (var file in files)
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

        return new HypergraphData
        {
            Nodes = allNodes,
            Hyperedges = allEdges,
            Metadata = new HypergraphMetadata { NumFiles = files.Count }
        };
    }
}
