// -----------------------------------------------------------------------
// <copyright file="IndexCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.VectorDb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to index embeddings into vector and graph databases.
/// </summary>
public class IndexCommand : AsyncCommand<IndexCommand.Settings>
{
    /// <summary>
    /// Settings for the index command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Path to the hypergraph JSON file (or embeddings JSON)")]
        [CommandArgument(0, "<FILE_PATH>")]
        public string FilePath { get; set; } = string.Empty;

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

        [Description("Clear existing data before indexing")]
        [CommandOption("--clear")]
        public bool ClearExisting { get; set; }

        [Description("Nodes collection name in Qdrant")]
        [CommandOption("--nodes-collection")]
        [DefaultValue("code_nodes")]
        public string NodesCollection { get; set; } = "code_nodes";

        [Description("Edges collection name in Qdrant")]
        [CommandOption("--edges-collection")]
        [DefaultValue("code_edges")]
        public string EdgesCollection { get; set; } = "code_edges";
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File does not exist: {settings.FilePath}");
            return 1;
        }

        if (string.IsNullOrEmpty(settings.Neo4jUri) && string.IsNullOrEmpty(settings.QdrantHost))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] At least one of --neo4j or --qdrant must be specified");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Indexing data from:[/] {settings.FilePath}");

        // Load the data file
        string json;
        HypergraphData? graphData = null;
        EmbeddingsFile? embeddingsData = null;

        try
        {
            json = await File.ReadAllTextAsync(settings.FilePath);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Try to detect file type
            if (json.Contains("\"nodes\"") && json.Contains("\"hyperedges\""))
            {
                graphData = JsonSerializer.Deserialize<HypergraphData>(json, jsonOptions);
            }
            else if (json.Contains("\"nodes\"") && json.Contains("\"vector\""))
            {
                embeddingsData = JsonSerializer.Deserialize<EmbeddingsFile>(json, jsonOptions);
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Unrecognized file format");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error reading file:[/] {ex.Message}");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var indexedNodes = 0;
        var indexedEdges = 0;

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
                // Index to Neo4j if configured
                if (!string.IsNullOrEmpty(settings.Neo4jUri) && graphData != null)
                {
                    var neo4jTask = ctx.AddTask("Indexing to Neo4j...", maxValue: graphData.Nodes.Count + graphData.Hyperedges.Count);

                    var graphOptions = Options.Create(new GraphStoreOptions
                    {
                        Uri = settings.Neo4jUri,
                        Username = settings.Neo4jUser,
                        Password = settings.Neo4jPassword
                    });

                    await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

                    if (settings.ClearExisting)
                    {
                        neo4jTask.Description = "Clearing existing data...";
                        await graphStore.ClearAsync();
                    }

                    neo4jTask.Description = "Indexing nodes...";
                    await graphStore.CreateNodesAsync(graphData.Nodes.Values.ToList());
                    indexedNodes = graphData.Nodes.Count;
                    neo4jTask.Value = indexedNodes;

                    neo4jTask.Description = "Indexing hyperedges...";
                    await graphStore.CreateEdgesAsync(graphData.Hyperedges);
                    indexedEdges = graphData.Hyperedges.Count;
                    neo4jTask.Value = indexedNodes + indexedEdges;

                    neo4jTask.Description = "Neo4j indexing complete";
                }

                // Index to Qdrant if configured
                if (!string.IsNullOrEmpty(settings.QdrantHost) && embeddingsData != null)
                {
                    var qdrantTask = ctx.AddTask("Indexing to Qdrant...", maxValue: embeddingsData.Nodes.Count + (embeddingsData.Edges?.Count ?? 0));

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
                        NodesCollection = settings.NodesCollection,
                        EdgesCollection = settings.EdgesCollection,
                        DefaultVectorSize = embeddingsData.Nodes.FirstOrDefault()?.Vector?.Length ?? 768
                    });

                    using var vectorStore = new QdrantVectorStore(vectorOptions, NullLogger<QdrantVectorStore>.Instance);

                    if (settings.ClearExisting)
                    {
                        qdrantTask.Description = "Clearing existing collections...";
                        await vectorStore.DeleteCollectionAsync(settings.NodesCollection);
                        await vectorStore.DeleteCollectionAsync(settings.EdgesCollection);
                    }

                    qdrantTask.Description = "Ensuring collections exist...";
                    if (!await vectorStore.CollectionExistsAsync(settings.NodesCollection))
                    {
                        await vectorStore.CreateCollectionAsync(settings.NodesCollection, vectorOptions.Value.DefaultVectorSize);
                    }
                    if (embeddingsData.Edges?.Count > 0)
                    {
                        if (!await vectorStore.CollectionExistsAsync(settings.EdgesCollection))
                        {
                            await vectorStore.CreateCollectionAsync(settings.EdgesCollection, vectorOptions.Value.DefaultVectorSize);
                        }
                    }

                    qdrantTask.Description = "Indexing node embeddings...";
                    var nodePoints = embeddingsData.Nodes.Select(n => new VectorPoint
                    {
                        Id = n.NodeId,
                        Vector = n.Vector,
                        Payload = new Dictionary<string, object>
                        {
                            ["file_path"] = n.FilePath,
                            ["element_type"] = n.ElementType,
                            ["name"] = n.Name,
                            ["language"] = n.Language,
                            ["start_line"] = n.StartLine,
                            ["end_line"] = n.EndLine
                        }
                    }).ToList();

                    await vectorStore.UpsertAsync(settings.NodesCollection, nodePoints);
                    indexedNodes = nodePoints.Count;
                    qdrantTask.Value = indexedNodes;

                    if (embeddingsData.Edges?.Count > 0)
                    {
                        qdrantTask.Description = "Indexing edge embeddings...";
                        var edgePoints = embeddingsData.Edges.Select(e => new VectorPoint
                        {
                            Id = e.Id,
                            Vector = e.Vector,
                            Payload = e.Metadata ?? new Dictionary<string, object>()
                        }).ToList();

                        await vectorStore.UpsertAsync(settings.EdgesCollection, edgePoints);
                        indexedEdges = edgePoints.Count;
                        qdrantTask.Value = indexedNodes + indexedEdges;
                    }

                    qdrantTask.Description = "Qdrant indexing complete";
                }
            });

        stopwatch.Stop();

        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Value").RightAligned());

        if (!string.IsNullOrEmpty(settings.Neo4jUri))
        {
            table.AddRow("Neo4j URI", settings.Neo4jUri);
        }
        if (!string.IsNullOrEmpty(settings.QdrantHost))
        {
            table.AddRow("Qdrant Host", settings.QdrantHost);
        }
        table.AddRow("Nodes Indexed", indexedNodes.ToString("N0"));
        table.AddRow("Edges Indexed", indexedEdges.ToString("N0"));
        table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Indexing complete![/]");

        return 0;
    }

    /// <summary>
    /// Model for embeddings file deserialization.
    /// </summary>
    private class EmbeddingsFile
    {
        public List<CodeEmbedding> Nodes { get; set; } = [];
        public List<EmbeddingResult>? Edges { get; set; }
    }
}
