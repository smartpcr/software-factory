// -----------------------------------------------------------------------
// <copyright file="BenchmarkService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Benchmark.Services;

using System.Diagnostics;
using Ast.Core;
using Ast.Csharp;
using Ast.GraphDb;
using Ast.PowerShell;
using Microsoft.Extensions.Logging;
using Spectre.Console;

/// <summary>
/// Service for running extraction and ingestion benchmarks with progress tracking.
/// </summary>
public class BenchmarkService(ILogger<BenchmarkService> logger)
{
    private readonly ILogger<BenchmarkService> logger = logger;

    /// <summary>
    /// Runs the full extraction and ingestion benchmark with progress tracking.
    /// </summary>
    public async Task<BenchmarkResult> RunBenchmarkAsync(
        string repoPath,
        Neo4jGraphStore graphStore,
        string language,
        CancellationToken cancellationToken)
    {
        var result = new BenchmarkResult { RepoPath = repoPath };
        var totalStopwatch = Stopwatch.StartNew();

        // Phase 1: File Discovery
        var (csFiles, psFiles) = await DiscoverFilesAsync(repoPath, language);
        var allFiles = csFiles.Concat(psFiles).ToList();
        result.FilesDiscovered = allFiles.Count;

        if (allFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No source files found in repository[/]");
            return result;
        }

        AnsiConsole.MarkupLine($"[cyan]Discovered {csFiles.Count} C# files, {psFiles.Count} PowerShell files[/]");

        // Phase 2: Extraction with Progress
        var extractionStopwatch = Stopwatch.StartNew();
        var hypergraphData = await this.ExtractWithProgressAsync(
            csFiles, psFiles, repoPath, cancellationToken);
        extractionStopwatch.Stop();

        result.FilesProcessed = hypergraphData.Metadata.NumFiles;
        result.NodesExtracted = hypergraphData.Nodes.Count;
        result.EdgesExtracted = hypergraphData.Hyperedges.Count;
        result.ExtractionDuration = extractionStopwatch.Elapsed;

        AnsiConsole.MarkupLine($"[green]Extraction complete: {result.NodesExtracted:N0} nodes, {result.EdgesExtracted:N0} edges[/]");

        // Phase 3: Ingestion with Progress
        var ingestionStopwatch = Stopwatch.StartNew();
        var (nodesIngested, edgesIngested) = await IngestWithProgressAsync(
            hypergraphData, graphStore, cancellationToken);
        ingestionStopwatch.Stop();

        result.NodesIngested = nodesIngested;
        result.EdgesIngested = edgesIngested;
        result.IngestionDuration = ingestionStopwatch.Elapsed;

        totalStopwatch.Stop();
        result.TotalDuration = totalStopwatch.Elapsed;

        return result;
    }

    private static async Task<(List<string> csFiles, List<string> psFiles)> DiscoverFilesAsync(
        string repoPath, string language)
    {
        var csFiles = new List<string>();
        var psFiles = new List<string>();

        await AnsiConsole.Status()
            .StartAsync("Discovering source files...", ctx =>
            {
                if (language == "csharp" || language == "all")
                {
                    var srcPath = Path.Combine(repoPath, "src");
                    var searchPath = Directory.Exists(srcPath) ? srcPath : repoPath;
                    csFiles = [.. Directory.GetFiles(searchPath, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains(".Designer."))];
                }

                if (language == "powershell" || language == "all")
                {
                    psFiles =
                    [
                        .. Directory.GetFiles(repoPath, "*.ps1", SearchOption.AllDirectories)
,
                        .. Directory.GetFiles(repoPath, "*.psm1", SearchOption.AllDirectories),
                    ];
                }

                return Task.CompletedTask;
            });

        return (csFiles, psFiles);
    }

    private async Task<HypergraphData> ExtractWithProgressAsync(
        List<string> csFiles,
        List<string> psFiles,
        string repoPath,
        CancellationToken cancellationToken)
    {
        var allNodes = new Dictionary<string, HypergraphNode>();
        var allEdges = new List<Hyperedge>();
        var filesProcessed = 0;
        var totalFiles = csFiles.Count + psFiles.Count;
        var errors = 0;

        await AnsiConsole.Progress()
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            ])
            .StartAsync(async ctx =>
            {
                var extractionTask = ctx.AddTask("[green]Extracting AST[/]", maxValue: totalFiles);

                // Process C# files
                foreach (var file in csFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var source = await File.ReadAllTextAsync(file, cancellationToken);
                        var extractor = new CSharpHyperedgeExtractor(file);
                        var (nodes, edges) = extractor.Extract(source);

                        foreach (var node in nodes)
                        {
                            allNodes.TryAdd(node.Key, node.Value);
                        }
                        allEdges.AddRange(edges);

                        filesProcessed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        this.logger.LogDebug(ex, "Error extracting {File}", file);
                    }

                    extractionTask.Increment(1);

                    // Update description with current file
                    if (filesProcessed % 50 == 0)
                    {
                        var relativePath = Path.GetRelativePath(repoPath, file);
                        if (relativePath.Length > 40)
                        {
                            relativePath = "..." + relativePath[^37..];
                        }
                        extractionTask.Description = $"[green]Extracting: {relativePath}[/]";
                    }
                }

                // Process PowerShell files
                foreach (var file in psFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var source = await File.ReadAllTextAsync(file, cancellationToken);
                        var extractor = new PowerShellHyperedgeExtractor(file);
                        var (nodes, edges) = extractor.Extract(source);

                        foreach (var node in nodes)
                        {
                            allNodes.TryAdd(node.Key, node.Value);
                        }
                        allEdges.AddRange(edges);

                        filesProcessed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        this.logger.LogDebug(ex, "Error extracting {File}", file);
                    }

                    extractionTask.Increment(1);
                }

                extractionTask.Description = "[green]Extraction complete[/]";
            });

        if (errors > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Extraction completed with {errors} errors[/]");
        }

        return new HypergraphData
        {
            Nodes = allNodes,
            Hyperedges = allEdges,
            Metadata = new HypergraphMetadata
            {
                RepoPath = repoPath,
                NumFiles = filesProcessed,
                NumCSharpNodes = allNodes.Values.Count(n => n.Language == "csharp"),
                NumPowerShellNodes = allNodes.Values.Count(n => n.Language == "powershell"),
                ExtractedAt = DateTime.UtcNow
            }
        };
    }

    private static async Task<(int nodesIngested, int edgesIngested)> IngestWithProgressAsync(
        HypergraphData data,
        Neo4jGraphStore graphStore,
        CancellationToken cancellationToken)
    {
        var nodesIngested = 0;
        var edgesIngested = 0;
        var totalNodes = data.Nodes.Count;
        var totalEdges = data.Hyperedges.Count;

        await AnsiConsole.Progress()
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            ])
            .StartAsync(async ctx =>
            {
                // Ingest nodes
                var nodeTask = ctx.AddTask("[cyan]Ingesting nodes[/]", maxValue: totalNodes);

                var nodeList = data.Nodes.Values.ToList();
                var batchSize = 500;
                var batches = nodeList.Chunk(batchSize).ToList();

                for (var i = 0; i < batches.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batch = batches[i];
                    var created = await graphStore.CreateNodesAsync(batch, cancellationToken);
                    nodesIngested += created;
                    nodeTask.Increment(batch.Length);

                    // Update description with progress
                    nodeTask.Description = $"[cyan]Ingesting nodes ({nodesIngested:N0}/{totalNodes:N0})[/]";
                }

                nodeTask.Description = "[cyan]Nodes ingested[/]";

                // Ingest edges
                var edgeTask = ctx.AddTask("[magenta]Ingesting edges[/]", maxValue: totalEdges);

                var edgeList = data.Hyperedges;
                var edgeBatchSize = 100;
                var edgeBatches = edgeList.Chunk(edgeBatchSize).ToList();

                for (var i = 0; i < edgeBatches.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batch = edgeBatches[i];
                    var created = await graphStore.CreateEdgesAsync(batch, cancellationToken);
                    edgesIngested += created;
                    edgeTask.Increment(batch.Length);

                    // Update description with progress
                    edgeTask.Description = $"[magenta]Ingesting edges ({edgesIngested:N0}/{totalEdges:N0})[/]";
                }

                edgeTask.Description = "[magenta]Edges ingested[/]";
            });

        return (nodesIngested, edgesIngested);
    }
}

/// <summary>
/// Result of a benchmark run.
/// </summary>
public class BenchmarkResult
{
    public string RepoPath { get; set; } = string.Empty;

    // Discovery
    public int FilesDiscovered { get; set; }

    // Extraction
    public int FilesProcessed { get; set; }
    public int NodesExtracted { get; set; }
    public int EdgesExtracted { get; set; }
    public TimeSpan ExtractionDuration { get; set; }

    // Ingestion
    public int NodesIngested { get; set; }
    public int EdgesIngested { get; set; }
    public TimeSpan IngestionDuration { get; set; }

    // Total
    public TimeSpan TotalDuration { get; set; }

    // Computed metrics
    public double FilesPerSecond =>
        this.ExtractionDuration.TotalSeconds > 0 ? this.FilesProcessed / this.ExtractionDuration.TotalSeconds : 0;

    public double ExtractionNodesPerSecond =>
        this.ExtractionDuration.TotalSeconds > 0 ? this.NodesExtracted / this.ExtractionDuration.TotalSeconds : 0;

    public double IngestionNodesPerSecond =>
        this.IngestionDuration.TotalSeconds > 0 ? this.NodesIngested / this.IngestionDuration.TotalSeconds : 0;

    public double EdgesPerSecond =>
        this.IngestionDuration.TotalSeconds > 0 ? this.EdgesIngested / this.IngestionDuration.TotalSeconds : 0;
}