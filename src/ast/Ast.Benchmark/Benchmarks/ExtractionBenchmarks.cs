// -----------------------------------------------------------------------
// <copyright file="ExtractionBenchmarks.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Benchmark.Benchmarks;

using Ast.Core;
using Ast.Csharp;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

/// <summary>
/// Benchmarks for AST extraction performance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[RankColumn]
public class ExtractionBenchmarks
{
    private static readonly string RepoPath = OperatingSystem.IsWindows()
        ? @"E:\work\hci\update"
        : "/mnt/e/work/hci/update";

    private List<string> smallFiles = new();
    private List<string> mediumFiles = new();
    private List<string> largeFiles = new();
    private Dictionary<string, string> fileContents = new();

    [GlobalSetup]
    public void Setup()
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
            .ToList();

        // Categorize files by size
        foreach (var file in allFiles)
        {
            var info = new FileInfo(file);
            var content = File.ReadAllText(file);
            this.fileContents[file] = content;

            if (info.Length < 5_000) // < 5KB
            {
                this.smallFiles.Add(file);
            }
            else if (info.Length < 50_000) // < 50KB
            {
                this.mediumFiles.Add(file);
            }
            else // >= 50KB
            {
                this.largeFiles.Add(file);
            }
        }

        // Take limited samples for benchmarking
        this.smallFiles = this.smallFiles.Take(50).ToList();
        this.mediumFiles = this.mediumFiles.Take(30).ToList();
        this.largeFiles = this.largeFiles.Take(10).ToList();
    }

    [Benchmark(Description = "Extract small files (< 5KB)")]
    public HypergraphData ExtractSmallFiles()
    {
        return this.ExtractFiles(this.smallFiles);
    }

    [Benchmark(Description = "Extract medium files (5-50KB)")]
    public HypergraphData ExtractMediumFiles()
    {
        return this.ExtractFiles(this.mediumFiles);
    }

    [Benchmark(Description = "Extract large files (> 50KB)")]
    public HypergraphData ExtractLargeFiles()
    {
        return this.ExtractFiles(this.largeFiles);
    }

    [Benchmark(Description = "Extract single file")]
    public (Dictionary<string, HypergraphNode>, List<Hyperedge>) ExtractSingleFile()
    {
        var file = this.mediumFiles.FirstOrDefault() ?? this.smallFiles.First();
        var extractor = new CSharpHyperedgeExtractor(file);
        return extractor.Extract(this.fileContents[file]);
    }

    private HypergraphData ExtractFiles(List<string> files)
    {
        var allNodes = new Dictionary<string, HypergraphNode>();
        var allEdges = new List<Hyperedge>();

        foreach (var file in files)
        {
            var extractor = new CSharpHyperedgeExtractor(file);
            var (nodes, edges) = extractor.Extract(this.fileContents[file]);

            foreach (var node in nodes)
            {
                allNodes.TryAdd(node.Key, node.Value);
            }
            allEdges.AddRange(edges);
        }

        return new HypergraphData
        {
            Nodes = allNodes,
            Hyperedges = allEdges,
            Metadata = new HypergraphMetadata { NumFiles = files.Count }
        };
    }
}
