// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Ast.Benchmark.Benchmarks;
using Ast.Benchmark.Services;
using Ast.GraphDb;
using BenchmarkDotNet.Running;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Ast.Benchmark;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await Parser.Default.ParseArguments<BenchmarkOptions, InteractiveOptions>(args)
            .MapResult(
                (BenchmarkOptions opts) => RunBenchmarks(opts),
                (InteractiveOptions opts) => RunInteractive(opts),
                errs => Task.FromResult(1));
    }

    private static Task<int> RunBenchmarks(BenchmarkOptions options)
    {
        AnsiConsole.MarkupLine("[bold blue]AST Graph Benchmark Runner[/]");
        AnsiConsole.WriteLine();

        var config = BenchmarkDotNet.Configs.DefaultConfig.Instance;

        if (options.RunExtraction)
        {
            AnsiConsole.MarkupLine("[yellow]Running extraction benchmarks...[/]");
            BenchmarkRunner.Run<ExtractionBenchmarks>(config);
        }

        if (options.RunIngestion)
        {
            AnsiConsole.MarkupLine("[yellow]Running ingestion benchmarks...[/]");
            BenchmarkRunner.Run<IngestionBenchmarks>(config);
        }

        if (options.RunQuery)
        {
            AnsiConsole.MarkupLine("[yellow]Running query benchmarks...[/]");
            BenchmarkRunner.Run<QueryBenchmarks>(config);
        }

        if (!options.RunExtraction && !options.RunIngestion && !options.RunQuery)
        {
            // Run all benchmarks
            AnsiConsole.MarkupLine("[yellow]Running all benchmarks...[/]");
            BenchmarkRunner.Run<ExtractionBenchmarks>(config);
            BenchmarkRunner.Run<IngestionBenchmarks>(config);
            BenchmarkRunner.Run<QueryBenchmarks>(config);
        }

        return Task.FromResult(0);
    }

    private static async Task<int> RunInteractive(InteractiveOptions options)
    {
        AnsiConsole.MarkupLine("[bold blue]AST Graph Interactive Benchmark[/]");
        AnsiConsole.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.Configure<GraphStoreOptions>(o =>
        {
            o.Uri = options.Uri;
            o.Username = options.Username;
            o.Password = options.Password;
            o.BatchSize = options.BatchSize;
        });
        services.AddSingleton<Neo4jGraphStore>();
        services.AddSingleton<BenchmarkService>();

        await using var provider = services.BuildServiceProvider();
        var graphStore = provider.GetRequiredService<Neo4jGraphStore>();
        var benchmarkService = provider.GetRequiredService<BenchmarkService>();
        var logger = provider.GetRequiredService<ILogger<Program>>();

        // Check connection
        var connected = await AnsiConsole.Status()
            .StartAsync("Connecting to Neo4j...", async ctx =>
            {
                var available = await graphStore.IsAvailableAsync();
                if (!available)
                {
                    AnsiConsole.MarkupLine("[red]Failed to connect to Neo4j![/]");
                    AnsiConsole.MarkupLine($"[dim]URI: {options.Uri}[/]");
                    AnsiConsole.MarkupLine("[yellow]Make sure Neo4j is running. You can start it with:[/]");
                    AnsiConsole.MarkupLine("[dim]  cd src/ast && docker compose up -d[/]");
                    return false;
                }
                return true;
            });

        if (!connected)
        {
            return 1;
        }

        AnsiConsole.MarkupLine("[green]Connected to Neo4j[/]");

        // Clear database if requested
        if (options.Clear)
        {
            await AnsiConsole.Status()
                .StartAsync("Clearing database...", async ctx =>
                {
                    await graphStore.ClearAsync();
                });
            AnsiConsole.MarkupLine("[yellow]Database cleared[/]");
        }

        // Create indexes
        await graphStore.CreateIndexesAsync();

        // Process repository
        var repoPath = options.RepoPath ?? GetDefaultRepoPath();

        if (!Directory.Exists(repoPath))
        {
            AnsiConsole.MarkupLine($"[red]Repository not found at: {repoPath}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]Processing repository: {repoPath}[/]");
        AnsiConsole.WriteLine();

        // Run extraction and ingestion with progress tracking
        var result = await benchmarkService.RunBenchmarkAsync(
            repoPath,
            graphStore,
            options.Language,
            CancellationToken.None);

        // Display results
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Benchmark Results[/]"));

        var table = new Table();
        table.AddColumn("Phase");
        table.AddColumn("Metric");
        table.AddColumn("Value");

        // Extraction metrics
        table.AddRow("[cyan]Extraction[/]", "Files Processed", result.FilesProcessed.ToString("N0"));
        table.AddRow("", "Nodes Extracted", result.NodesExtracted.ToString("N0"));
        table.AddRow("", "Edges Extracted", result.EdgesExtracted.ToString("N0"));
        table.AddRow("", "Duration", $"{result.ExtractionDuration.TotalSeconds:F2}s");
        table.AddRow("", "Files/sec", $"{result.FilesPerSecond:F1}");
        table.AddRow("", "Nodes/sec", $"{result.ExtractionNodesPerSecond:F1}");

        // Ingestion metrics
        table.AddRow("[green]Ingestion[/]", "Nodes Ingested", result.NodesIngested.ToString("N0"));
        table.AddRow("", "Edges Ingested", result.EdgesIngested.ToString("N0"));
        table.AddRow("", "Duration", $"{result.IngestionDuration.TotalSeconds:F2}s");
        table.AddRow("", "Nodes/sec", $"{result.IngestionNodesPerSecond:F1}");
        table.AddRow("", "Edges/sec", $"{result.EdgesPerSecond:F1}");

        // Total
        table.AddRow("[bold yellow]Total[/]", "Total Duration", $"{result.TotalDuration.TotalSeconds:F2}s");

        AnsiConsole.Write(table);

        // Show database statistics
        if (options.ShowStats)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold]Database Statistics[/]"));

            var stats = await graphStore.GetStatisticsAsync();

            var statsTable = new Table();
            statsTable.AddColumn("Metric");
            statsTable.AddColumn("Value");

            statsTable.AddRow("Total Nodes", stats.TotalNodes.ToString("N0"));
            statsTable.AddRow("Total Edges", stats.TotalEdges.ToString("N0"));
            statsTable.AddRow("Unique Files", stats.UniqueFiles.ToString("N0"));

            AnsiConsole.Write(statsTable);

            if (stats.NodesByLanguage.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Nodes by Language:[/]");
                var langTable = new Table();
                langTable.AddColumn("Language");
                langTable.AddColumn("Count");
                foreach (var kv in stats.NodesByLanguage.OrderByDescending(x => x.Value))
                {
                    langTable.AddRow(kv.Key, kv.Value.ToString("N0"));
                }
                AnsiConsole.Write(langTable);
            }

            if (stats.NodesByType.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Nodes by Type (Top 10):[/]");
                var typeTable = new Table();
                typeTable.AddColumn("Type");
                typeTable.AddColumn("Count");
                foreach (var kv in stats.NodesByType.OrderByDescending(x => x.Value).Take(10))
                {
                    typeTable.AddRow(kv.Key, kv.Value.ToString("N0"));
                }
                AnsiConsole.Write(typeTable);
            }
        }

        // Run sample queries
        if (options.RunSampleQueries)
        {
            await RunSampleQueriesAsync(graphStore);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]Done![/]");

        return 0;
    }

    private static async Task RunSampleQueriesAsync(Neo4jGraphStore store)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Sample Queries[/]"));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Query 1: Get all nodes (limited)
        sw.Restart();
        var nodes = await store.GetNodesAsync(limit: 100);
        sw.Stop();
        AnsiConsole.MarkupLine($"[dim]Get all nodes (limit 100): {sw.ElapsedMilliseconds}ms, found {nodes.Count} nodes[/]");

        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes found in database[/]");
            return;
        }

        // Query 2: Get nodes by type
        sw.Restart();
        var classNodes = await store.GetNodesAsync(new NodeFilter { NodeTypes = new[] { "class" } }, limit: 50);
        sw.Stop();
        AnsiConsole.MarkupLine($"[dim]Get class nodes (limit 50): {sw.ElapsedMilliseconds}ms, found {classNodes.Count} nodes[/]");

        // Query 3: Get callers for a random method
        var methodNode = nodes.FirstOrDefault(n => n.NodeType.Equals("method", StringComparison.OrdinalIgnoreCase));
        if (methodNode != null)
        {
            sw.Restart();
            var callers = await store.GetCallersAsync(methodNode.Id, depth: 2);
            sw.Stop();
            AnsiConsole.MarkupLine($"[dim]Get callers (depth 2): {sw.ElapsedMilliseconds}ms, found {callers.Count} callers[/]");
        }

        // Query 4: Get statistics
        sw.Restart();
        var stats = await store.GetStatisticsAsync();
        sw.Stop();
        AnsiConsole.MarkupLine($"[dim]Get statistics: {sw.ElapsedMilliseconds}ms[/]");
    }

    private static string GetDefaultRepoPath()
    {
        return OperatingSystem.IsWindows()
            ? @"E:\work\hci\update"
            : "/mnt/e/work/hci/update";
    }
}

[Verb("benchmark", HelpText = "Run BenchmarkDotNet benchmarks")]
public class BenchmarkOptions
{
    [Option('e', "extraction", HelpText = "Run extraction benchmarks")]
    public bool RunExtraction { get; set; }

    [Option('i', "ingestion", HelpText = "Run ingestion benchmarks")]
    public bool RunIngestion { get; set; }

    [Option('q', "query", HelpText = "Run query benchmarks")]
    public bool RunQuery { get; set; }
}

[Verb("interactive", isDefault: true, HelpText = "Run interactive benchmark")]
public class InteractiveOptions
{
    [Option('u', "uri", Default = "bolt://localhost:7687", HelpText = "Neo4j URI")]
    public string Uri { get; set; } = "bolt://localhost:7687";

    [Option("user", Default = "neo4j", HelpText = "Neo4j username")]
    public string Username { get; set; } = "neo4j";

    [Option('p', "password", Default = "password123", HelpText = "Neo4j password")]
    public string Password { get; set; } = "password123";

    [Option('r', "repo", HelpText = "Repository path to process")]
    public string? RepoPath { get; set; }

    [Option('l', "language", Default = "csharp", HelpText = "Language to extract (csharp, powershell, or all)")]
    public string Language { get; set; } = "csharp";

    [Option('b', "batch-size", Default = 500, HelpText = "Batch size for ingestion")]
    public int BatchSize { get; set; } = 500;

    [Option('c', "clear", Default = false, HelpText = "Clear database before benchmark")]
    public bool Clear { get; set; }

    [Option('s', "stats", Default = true, HelpText = "Show database statistics after benchmark")]
    public bool ShowStats { get; set; } = true;

    [Option("sample-queries", Default = true, HelpText = "Run sample queries after benchmark")]
    public bool RunSampleQueries { get; set; } = true;
}
