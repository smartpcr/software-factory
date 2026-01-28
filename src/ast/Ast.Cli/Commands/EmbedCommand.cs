// -----------------------------------------------------------------------
// <copyright file="EmbedCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Ast.Core;
using Ast.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to generate embeddings from a hypergraph.
/// </summary>
public class EmbedCommand : AsyncCommand<EmbedCommand.Settings>
{
    /// <summary>
    /// Settings for the embed command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Path to the hypergraph JSON file")]
        [CommandArgument(0, "<GRAPH_PATH>")]
        public string GraphPath { get; set; } = string.Empty;

        [Description("Output file path (default: embeddings.json)")]
        [CommandOption("-o|--output")]
        public string? OutputPath { get; set; }

        [Description("Embedding provider (http, llama, openai)")]
        [CommandOption("-p|--provider")]
        [DefaultValue("http")]
        public string Provider { get; set; } = "http";

        [Description("Embedding API endpoint")]
        [CommandOption("-e|--endpoint")]
        [DefaultValue("http://localhost:11434")]
        public string Endpoint { get; set; } = "http://localhost:11434";

        [Description("Model name")]
        [CommandOption("-m|--model")]
        [DefaultValue("nomic-embed-text")]
        public string ModelName { get; set; } = "nomic-embed-text";

        [Description("Embedding dimensions")]
        [CommandOption("-d|--dimensions")]
        [DefaultValue(768)]
        public int Dimensions { get; set; } = 768;

        [Description("Path to local GGUF model file (for llama provider)")]
        [CommandOption("--model-path")]
        public string? ModelPath { get; set; }

        [Description("API key (for openai provider)")]
        [CommandOption("--api-key")]
        public string? ApiKey { get; set; }

        [Description("Batch size for embedding generation")]
        [CommandOption("-b|--batch-size")]
        [DefaultValue(32)]
        public int BatchSize { get; set; } = 32;

        [Description("Generate embeddings for edges too")]
        [CommandOption("--include-edges")]
        public bool IncludeEdges { get; set; }

        [Description("Pretty print JSON output")]
        [CommandOption("--pretty")]
        public bool PrettyPrint { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.GraphPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Graph file does not exist: {settings.GraphPath}");
            return 1;
        }

        var outputPath = settings.OutputPath ?? Path.ChangeExtension(settings.GraphPath, ".embeddings.json");

        AnsiConsole.MarkupLine($"[blue]Generating embeddings for:[/] {settings.GraphPath}");
        AnsiConsole.MarkupLine($"[dim]Provider: {settings.Provider}, Model: {settings.ModelName}[/]");

        // Load the hypergraph
        HypergraphData? data;
        try
        {
            var json = await File.ReadAllTextAsync(settings.GraphPath);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            data = JsonSerializer.Deserialize<HypergraphData>(json, jsonOptions);

            if (data == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to deserialize hypergraph");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error reading graph file:[/] {ex.Message}");
            return 1;
        }

        // Configure embedding generator
        var generatorOptions = new EmbeddingGeneratorOptions
        {
            Endpoint = settings.Endpoint,
            ModelName = settings.ModelName,
            Dimensions = settings.Dimensions,
            BatchSize = settings.BatchSize,
            ModelPath = settings.ModelPath,
            ApiKey = settings.ApiKey
        };

        generatorOptions.Provider = settings.Provider.ToLowerInvariant() switch
        {
            "llama" => EmbeddingProvider.LLama,
            "openai" => EmbeddingProvider.OpenAI,
            _ => EmbeddingProvider.Http
        };

        if (generatorOptions.Provider == EmbeddingProvider.LLama && string.IsNullOrEmpty(settings.ModelPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --model-path is required for llama provider");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<CodeEmbedding>? nodeEmbeddings = null;
        IReadOnlyList<EmbeddingResult>? edgeEmbeddings = null;

        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var factory = new EmbeddingGeneratorFactory(NullLoggerFactory.Instance);
                using var generator = factory.Create(generatorOptions);
                using var embeddingService = new CodeEmbeddingService(
                    generator,
                    NullLogger<CodeEmbeddingService>.Instance);

                // Check if embedding service is available
                var nodeTask = ctx.AddTask($"Embedding {data.Nodes.Count} nodes", maxValue: data.Nodes.Count);

                var nodeProgress = new Progress<EmbeddingProgress>(p =>
                {
                    nodeTask.Value = p.ProcessedItems;
                    nodeTask.Description = $"Embedding: {p.CurrentItem}";
                });

                nodeEmbeddings = await embeddingService.GenerateNodeEmbeddingsAsync(data, nodeProgress);
                nodeTask.Value = data.Nodes.Count;

                if (settings.IncludeEdges && data.Hyperedges.Count > 0)
                {
                    var edgeTask = ctx.AddTask($"Embedding {data.Hyperedges.Count} edges", maxValue: data.Hyperedges.Count);

                    var edgeProgress = new Progress<EmbeddingProgress>(p =>
                    {
                        edgeTask.Value = p.ProcessedItems;
                        edgeTask.Description = $"Embedding: {p.CurrentItem}";
                    });

                    edgeEmbeddings = await embeddingService.GenerateEdgeEmbeddingsAsync(data, edgeProgress);
                    edgeTask.Value = data.Hyperedges.Count;
                }
            });

        stopwatch.Stop();

        if (nodeEmbeddings != null)
        {
            // Create output object
            var output = new
            {
                Nodes = nodeEmbeddings,
                Edges = edgeEmbeddings ?? [],
                Metadata = new
                {
                    SourceFile = settings.GraphPath,
                    Provider = settings.Provider,
                    Model = settings.ModelName,
                    Dimensions = settings.Dimensions,
                    GeneratedAt = DateTimeOffset.UtcNow
                }
            };

            // Save embeddings
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = settings.PrettyPrint,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var outputJson = JsonSerializer.Serialize(output, jsonOptions);
            await File.WriteAllTextAsync(outputPath, outputJson);

            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());

            table.AddRow("Node Embeddings", nodeEmbeddings.Count.ToString("N0"));
            if (edgeEmbeddings != null)
            {
                table.AddRow("Edge Embeddings", edgeEmbeddings.Count.ToString("N0"));
            }
            table.AddRow("Dimensions", settings.Dimensions.ToString("N0"));
            table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Output written to:[/] {outputPath}");
        }

        return 0;
    }
}
