// -----------------------------------------------------------------------
// <copyright file="PipelineCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Ast.Core;
using Ast.Csharp;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.Lsp;
using Ast.PowerShell;
using Ast.VectorDb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to run the full pipeline: parse -> enrich -> embed -> index.
/// </summary>
public class PipelineCommand : AsyncCommand<PipelineCommand.Settings>
{
    /// <summary>
    /// Settings for the pipeline command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Path to the repository to process")]
        [CommandArgument(0, "<REPO_PATH>")]
        public string RepoPath { get; set; } = string.Empty;

        [Description("Output directory for intermediate files")]
        [CommandOption("-o|--output-dir")]
        public string? OutputDir { get; set; }

        [Description("Skip LSP enrichment")]
        [CommandOption("--skip-enrich")]
        public bool SkipEnrich { get; set; }

        [Description("Skip embedding generation")]
        [CommandOption("--skip-embed")]
        public bool SkipEmbed { get; set; }

        [Description("Skip database indexing")]
        [CommandOption("--skip-index")]
        public bool SkipIndex { get; set; }

        [Description("Parse C# files")]
        [CommandOption("--csharp")]
        [DefaultValue(true)]
        public bool ParseCSharp { get; set; } = true;

        [Description("Parse PowerShell files")]
        [CommandOption("--powershell")]
        [DefaultValue(true)]
        public bool ParsePowerShell { get; set; } = true;

        [Description("Neo4j connection URI")]
        [CommandOption("--neo4j")]
        public string? Neo4jUri { get; set; }

        [Description("Neo4j username")]
        [CommandOption("--neo4j-user")]
        [DefaultValue("neo4j")]
        public string Neo4jUser { get; set; } = "neo4j";

        [Description("Neo4j password")]
        [CommandOption("--neo4j-pass")]
        [DefaultValue("password123")]
        public string Neo4jPassword { get; set; } = "password123";

        [Description("Qdrant host:port")]
        [CommandOption("--qdrant")]
        public string? QdrantHost { get; set; }

        [Description("Qdrant API key")]
        [CommandOption("--qdrant-key")]
        public string? QdrantApiKey { get; set; }

        [Description("Embedding API endpoint")]
        [CommandOption("-e|--embedding-endpoint")]
        [DefaultValue("http://localhost:11434")]
        public string EmbeddingEndpoint { get; set; } = "http://localhost:11434";

        [Description("Embedding model name")]
        [CommandOption("-m|--model")]
        [DefaultValue("nomic-embed-text")]
        public string ModelName { get; set; } = "nomic-embed-text";

        [Description("Embedding dimensions")]
        [CommandOption("-d|--dimensions")]
        [DefaultValue(768)]
        public int Dimensions { get; set; } = 768;

        [Description("Clear existing data before indexing")]
        [CommandOption("--clear")]
        public bool ClearExisting { get; set; }

        [Description("Keep intermediate files")]
        [CommandOption("--keep-files")]
        public bool KeepFiles { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = Path.GetFullPath(settings.RepoPath);
        if (!Directory.Exists(repoPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Repository path does not exist: {repoPath}");
            return 1;
        }

        var outputDir = settings.OutputDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".ast-output");
        Directory.CreateDirectory(outputDir);

        var repoName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar));
        var graphPath = Path.Combine(outputDir, $"{repoName}.hypergraph.json");
        var enrichedPath = Path.Combine(outputDir, $"{repoName}.enriched.json");
        var embeddingsPath = Path.Combine(outputDir, $"{repoName}.embeddings.json");

        var overallStopwatch = Stopwatch.StartNew();

        AnsiConsole.Write(new Rule("[blue]AST Code Analysis Pipeline[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"[dim]Repository:[/] {repoPath}");
        AnsiConsole.MarkupLine($"[dim]Output:[/] {outputDir}");
        AnsiConsole.WriteLine();

        HypergraphData? graphData = null;
        IReadOnlyList<CodeEmbedding>? embeddings = null;

        // Step 1: Parse
        AnsiConsole.Write(new Rule("[yellow]Step 1: Parse Repository[/]").LeftJustified());

        var parseStopwatch = Stopwatch.StartNew();
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Parsing repository...", async ctx =>
            {
                var processor = new RepositoryProcessor(repoPath);

                if (settings.ParseCSharp)
                {
                    ctx.Status("Loading C# extractor...");
                    processor.CSharpExtractor = (path, source) =>
                    {
                        var extractor = new CSharpHyperedgeExtractor(path);
                        return extractor.Extract(source);
                    };
                }

                if (settings.ParsePowerShell)
                {
                    ctx.Status("Loading PowerShell extractor...");
                    processor.PowerShellExtractor = (path, source) =>
                    {
                        var extractor = new PowerShellHyperedgeExtractor(path);
                        return extractor.Extract(source);
                    };
                }

                ctx.Status("Processing files...");
                graphData = processor.Process(false);

                var json = JsonSerializer.Serialize(graphData, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(graphPath, json);
            });

        parseStopwatch.Stop();
        AnsiConsole.MarkupLine($"  [green]\u2713[/] Parsed {graphData!.Nodes.Count} nodes, {graphData.Hyperedges.Count} edges ({parseStopwatch.Elapsed.TotalSeconds:F2}s)");

        // Step 2: Enrich with LSP
        if (!settings.SkipEnrich)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Step 2: LSP Enrichment[/]").LeftJustified());

            var enrichStopwatch = Stopwatch.StartNew();
            var enrichedCount = 0;

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Enriching {graphData.Nodes.Count} nodes", maxValue: graphData.Nodes.Count);

                    var lspConfig = new LspConfiguration
                    {
                        Enabled = true,
                        CSharp = new CSharpLspConfiguration { Enabled = settings.ParseCSharp },
                        PowerShell = new PowerShellLspConfiguration { Enabled = settings.ParsePowerShell }
                    };

                    var loggerFactory = new NullLoggerFactory();
                    var serverManager = new LspServerManager(Options.Create(lspConfig), loggerFactory);
                    var enrichmentService = new LspEnrichmentService(
                        serverManager,
                        Options.Create(lspConfig),
                        NullLogger<LspEnrichmentService>.Instance);

                    try
                    {
                        var progress = new Progress<EnrichmentProgress>(p =>
                        {
                            task.Value = p.Processed;
                        });

                        graphData = await enrichmentService.EnrichHypergraphAsync(graphData, repoPath, progress);
                        enrichedCount = graphData.Nodes.Values.Count(n => n.IsLspEnriched);
                        task.Value = graphData.Nodes.Count;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"  [yellow]\u26a0[/] LSP enrichment failed: {ex.Message}");
                    }
                    finally
                    {
                        await enrichmentService.DisposeAsync();
                    }
                });

            enrichStopwatch.Stop();

            var enrichedJson = JsonSerializer.Serialize(graphData, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(enrichedPath, enrichedJson);

            var rate = graphData.Nodes.Count > 0 ? (double)enrichedCount / graphData.Nodes.Count * 100 : 0;
            AnsiConsole.MarkupLine($"  [green]\u2713[/] Enriched {enrichedCount}/{graphData.Nodes.Count} nodes ({rate:F1}%) ({enrichStopwatch.Elapsed.TotalSeconds:F2}s)");
        }

        // Step 3: Generate Embeddings
        if (!settings.SkipEmbed)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Step 3: Generate Embeddings[/]").LeftJustified());

            var embedStopwatch = Stopwatch.StartNew();

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Embedding {graphData.Nodes.Count} nodes", maxValue: graphData.Nodes.Count);

                    var generatorOptions = new EmbeddingGeneratorOptions
                    {
                        Provider = EmbeddingProvider.Http,
                        Endpoint = settings.EmbeddingEndpoint,
                        ModelName = settings.ModelName,
                        Dimensions = settings.Dimensions
                    };

                    var factory = new EmbeddingGeneratorFactory(NullLoggerFactory.Instance);
                    using var generator = factory.Create(generatorOptions);
                    using var embeddingService = new CodeEmbeddingService(generator, NullLogger<CodeEmbeddingService>.Instance);

                    var progress = new Progress<EmbeddingProgress>(p =>
                    {
                        task.Value = p.ProcessedItems;
                    });

                    embeddings = await embeddingService.GenerateNodeEmbeddingsAsync(graphData, progress);
                    task.Value = graphData.Nodes.Count;
                });

            embedStopwatch.Stop();

            var embeddingsOutput = new
            {
                Nodes = embeddings,
                Edges = Array.Empty<object>(),
                Metadata = new
                {
                    SourceFile = graphPath,
                    Model = settings.ModelName,
                    Dimensions = settings.Dimensions,
                    GeneratedAt = DateTimeOffset.UtcNow
                }
            };
            var embeddingsJson = JsonSerializer.Serialize(embeddingsOutput, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(embeddingsPath, embeddingsJson);

            AnsiConsole.MarkupLine($"  [green]\u2713[/] Generated {embeddings!.Count} embeddings ({embedStopwatch.Elapsed.TotalSeconds:F2}s)");
        }

        // Step 4: Index to databases
        if (!settings.SkipIndex && (!string.IsNullOrEmpty(settings.Neo4jUri) || !string.IsNullOrEmpty(settings.QdrantHost)))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Step 4: Index to Databases[/]").LeftJustified());

            var indexStopwatch = Stopwatch.StartNew();

            // Neo4j indexing
            if (!string.IsNullOrEmpty(settings.Neo4jUri))
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Indexing to Neo4j...", async ctx =>
                    {
                        var graphOptions = Options.Create(new GraphStoreOptions
                        {
                            Uri = settings.Neo4jUri,
                            Username = settings.Neo4jUser,
                            Password = settings.Neo4jPassword
                        });

                        await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

                        if (settings.ClearExisting)
                        {
                            ctx.Status("Clearing existing data...");
                            await graphStore.ClearAsync();
                        }

                        ctx.Status("Indexing nodes...");
                        await graphStore.CreateNodesAsync(graphData.Nodes.Values.ToList());

                        ctx.Status("Indexing hyperedges...");
                        await graphStore.CreateEdgesAsync(graphData.Hyperedges);
                    });

                AnsiConsole.MarkupLine($"  [green]\u2713[/] Indexed to Neo4j: {settings.Neo4jUri}");
            }

            // Qdrant indexing
            if (!string.IsNullOrEmpty(settings.QdrantHost) && embeddings != null)
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Indexing to Qdrant...", async ctx =>
                    {
                        var hostParts = settings.QdrantHost.Split(':');
                        var host = hostParts[0];
                        var port = hostParts.Length > 1 ? int.Parse(hostParts[1]) : 6334;

                        var vectorOptions = Options.Create(new VectorStoreOptions
                        {
                            Host = host,
                            GrpcPort = port,
                            UseGrpc = true,
                            UseTls = !string.IsNullOrEmpty(settings.QdrantApiKey),
                            ApiKey = settings.QdrantApiKey,
                            DefaultVectorSize = settings.Dimensions
                        });

                        using var vectorStore = new QdrantVectorStore(vectorOptions, NullLogger<QdrantVectorStore>.Instance);

                        if (settings.ClearExisting)
                        {
                            ctx.Status("Clearing existing collections...");
                            await vectorStore.DeleteCollectionAsync("code_nodes");
                        }

                        ctx.Status("Ensuring collection exists...");
                        if (!await vectorStore.CollectionExistsAsync("code_nodes"))
                        {
                            await vectorStore.CreateCollectionAsync("code_nodes", settings.Dimensions);
                        }

                        ctx.Status("Indexing embeddings...");
                        var points = embeddings.Select(e => new VectorPoint
                        {
                            Id = e.NodeId,
                            Vector = e.Vector,
                            Payload = new Dictionary<string, object>
                            {
                                ["file_path"] = e.FilePath,
                                ["element_type"] = e.ElementType,
                                ["name"] = e.Name,
                                ["language"] = e.Language,
                                ["start_line"] = e.StartLine,
                                ["end_line"] = e.EndLine
                            }
                        }).ToList();

                        await vectorStore.UpsertAsync("code_nodes", points);
                    });

                AnsiConsole.MarkupLine($"  [green]\u2713[/] Indexed to Qdrant: {settings.QdrantHost}");
            }

            indexStopwatch.Stop();
            AnsiConsole.MarkupLine($"  [dim]Indexing took {indexStopwatch.Elapsed.TotalSeconds:F2}s[/]");
        }

        // Cleanup
        if (!settings.KeepFiles)
        {
            try
            {
                if (File.Exists(graphPath)) File.Delete(graphPath);
                if (File.Exists(enrichedPath)) File.Delete(enrichedPath);
                if (File.Exists(embeddingsPath)) File.Delete(embeddingsPath);
                if (Directory.Exists(outputDir) && !Directory.EnumerateFileSystemEntries(outputDir).Any())
                {
                    Directory.Delete(outputDir);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        overallStopwatch.Stop();

        // Summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Pipeline Complete[/]").RuleStyle("green"));

        var summaryTable = new Table();
        summaryTable.AddColumn("Metric");
        summaryTable.AddColumn(new TableColumn("Value").RightAligned());

        summaryTable.AddRow("Total Nodes", graphData.Nodes.Count.ToString("N0"));
        summaryTable.AddRow("Total Hyperedges", graphData.Hyperedges.Count.ToString("N0"));
        summaryTable.AddRow("C# Nodes", graphData.Metadata.NumCSharpNodes.ToString("N0"));
        summaryTable.AddRow("PowerShell Nodes", graphData.Metadata.NumPowerShellNodes.ToString("N0"));
        if (!settings.SkipEnrich)
        {
            summaryTable.AddRow("LSP Enriched", graphData.Nodes.Values.Count(n => n.IsLspEnriched).ToString("N0"));
        }
        if (embeddings != null)
        {
            summaryTable.AddRow("Embeddings", embeddings.Count.ToString("N0"));
        }
        summaryTable.AddRow("Total Duration", $"{overallStopwatch.Elapsed.TotalSeconds:F2}s");

        AnsiConsole.Write(summaryTable);

        if (settings.KeepFiles)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Output files in:[/] {outputDir}");
        }

        return 0;
    }
}
