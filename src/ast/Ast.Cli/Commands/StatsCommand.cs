// -----------------------------------------------------------------------
// <copyright file="StatsCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using System.Text.Json;
using Ast.Core;
using Ast.GraphDb;
using Ast.VectorDb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to show statistics about a hypergraph or database.
/// </summary>
public class StatsCommand : AsyncCommand<StatsCommand.Settings>
{
    /// <summary>
    /// Settings for the stats command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Path to a hypergraph or embeddings JSON file")]
        [CommandArgument(0, "[FILE_PATH]")]
        public string? FilePath { get; set; }

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

        [Description("Collection name for Qdrant stats")]
        [CommandOption("--collection")]
        [DefaultValue("code_nodes")]
        public string Collection { get; set; } = "code_nodes";
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var hasSource = false;

        // File stats
        if (!string.IsNullOrEmpty(settings.FilePath))
        {
            hasSource = true;
            await ShowFileStatsAsync(settings.FilePath);
        }

        // Neo4j stats
        if (!string.IsNullOrEmpty(settings.Neo4jUri))
        {
            hasSource = true;
            if (!string.IsNullOrEmpty(settings.FilePath))
            {
                AnsiConsole.WriteLine();
            }
            await ShowNeo4jStatsAsync(settings);
        }

        // Qdrant stats
        if (!string.IsNullOrEmpty(settings.QdrantHost))
        {
            hasSource = true;
            if (!string.IsNullOrEmpty(settings.FilePath) || !string.IsNullOrEmpty(settings.Neo4jUri))
            {
                AnsiConsole.WriteLine();
            }
            await ShowQdrantStatsAsync(settings);
        }

        if (!hasSource)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Specify a file path, --neo4j, or --qdrant");
            return 1;
        }

        return 0;
    }

    private static async Task ShowFileStatsAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File does not exist: {filePath}");
            return;
        }

        AnsiConsole.Write(new Rule($"[blue]File Statistics: {Path.GetFileName(filePath)}[/]").LeftJustified());

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var fileInfo = new FileInfo(filePath);
            var sizeKb = fileInfo.Length / 1024.0;

            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());

            table.AddRow("File Size", sizeKb > 1024 ? $"{sizeKb / 1024:F2} MB" : $"{sizeKb:F2} KB");

            // Try to parse as hypergraph
            if (json.Contains("\"nodes\"") && json.Contains("\"hyperedges\""))
            {
                var data = JsonSerializer.Deserialize<HypergraphData>(json, jsonOptions);
                if (data != null)
                {
                    table.AddRow("Type", "Hypergraph");
                    table.AddRow("Total Nodes", data.Nodes.Count.ToString("N0"));
                    table.AddRow("Total Hyperedges", data.Hyperedges.Count.ToString("N0"));

                    if (data.Metadata != null)
                    {
                        table.AddRow("C# Nodes", data.Metadata.NumCSharpNodes.ToString("N0"));
                        table.AddRow("PowerShell Nodes", data.Metadata.NumPowerShellNodes.ToString("N0"));
                        table.AddRow("Cross-Language Edges", data.Metadata.NumCrossLanguageEdges.ToString("N0"));
                        table.AddRow("Files", data.Metadata.NumFiles.ToString("N0"));

                        if (!string.IsNullOrEmpty(data.Metadata.RepoPath))
                        {
                            table.AddRow("Repository", data.Metadata.RepoPath);
                        }
                    }

                    // Count LSP enriched nodes
                    var enrichedCount = data.Nodes.Values.Count(n => n.IsLspEnriched);
                    if (enrichedCount > 0)
                    {
                        var rate = (double)enrichedCount / data.Nodes.Count * 100;
                        table.AddRow("LSP Enriched", $"{enrichedCount:N0} ({rate:F1}%)");
                    }

                    // Node type breakdown
                    var nodeTypes = data.Nodes.Values
                        .GroupBy(n => n.NodeType)
                        .OrderByDescending(g => g.Count())
                        .Take(10)
                        .ToList();

                    AnsiConsole.Write(table);
                    AnsiConsole.WriteLine();

                    var nodeTypeTable = new Table();
                    nodeTypeTable.Title = new TableTitle("Node Types (Top 10)");
                    nodeTypeTable.AddColumn("Type");
                    nodeTypeTable.AddColumn(new TableColumn("Count").RightAligned());
                    nodeTypeTable.AddColumn(new TableColumn("%").RightAligned());

                    foreach (var group in nodeTypes)
                    {
                        var pct = (double)group.Count() / data.Nodes.Count * 100;
                        nodeTypeTable.AddRow(group.Key, group.Count().ToString("N0"), $"{pct:F1}%");
                    }

                    AnsiConsole.Write(nodeTypeTable);

                    // Edge type breakdown
                    var edgeTypes = data.Hyperedges
                        .GroupBy(e => e.EdgeType)
                        .OrderByDescending(g => g.Count())
                        .Take(10)
                        .ToList();

                    AnsiConsole.WriteLine();
                    var edgeTypeTable = new Table();
                    edgeTypeTable.Title = new TableTitle("Edge Types (Top 10)");
                    edgeTypeTable.AddColumn("Type");
                    edgeTypeTable.AddColumn(new TableColumn("Count").RightAligned());
                    edgeTypeTable.AddColumn(new TableColumn("%").RightAligned());

                    foreach (var group in edgeTypes)
                    {
                        var pct = (double)group.Count() / data.Hyperedges.Count * 100;
                        edgeTypeTable.AddRow(group.Key.ToString(), group.Count().ToString("N0"), $"{pct:F1}%");
                    }

                    AnsiConsole.Write(edgeTypeTable);
                    return;
                }
            }

            // Try to parse as embeddings file
            if (json.Contains("\"vector\""))
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                table.AddRow("Type", "Embeddings");

                if (root.TryGetProperty("nodes", out var nodes))
                {
                    var nodeCount = nodes.GetArrayLength();
                    table.AddRow("Node Embeddings", nodeCount.ToString("N0"));

                    // Get dimensions from first embedding
                    if (nodeCount > 0)
                    {
                        var first = nodes[0];
                        if (first.TryGetProperty("vector", out var vector))
                        {
                            table.AddRow("Dimensions", vector.GetArrayLength().ToString("N0"));
                        }
                    }
                }

                if (root.TryGetProperty("edges", out var edges))
                {
                    table.AddRow("Edge Embeddings", edges.GetArrayLength().ToString("N0"));
                }

                if (root.TryGetProperty("metadata", out var metadata))
                {
                    if (metadata.TryGetProperty("model", out var model))
                    {
                        table.AddRow("Model", model.GetString() ?? "");
                    }
                    if (metadata.TryGetProperty("generatedAt", out var generatedAt))
                    {
                        table.AddRow("Generated", generatedAt.GetString() ?? "");
                    }
                }

                AnsiConsole.Write(table);
                return;
            }

            table.AddRow("Type", "Unknown");
            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing file:[/] {ex.Message}");
        }
    }

    private static async Task ShowNeo4jStatsAsync(Settings settings)
    {
        AnsiConsole.Write(new Rule($"[blue]Neo4j Statistics: {settings.Neo4jUri}[/]").LeftJustified());

        try
        {
            var graphOptions = Options.Create(new GraphStoreOptions
            {
                Uri = settings.Neo4jUri!,
                Username = settings.Neo4jUser,
                Password = settings.Neo4jPassword
            });

            await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

            var stats = await graphStore.GetStatisticsAsync();

            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());

            table.AddRow("Total Nodes", stats.TotalNodes.ToString("N0"));
            table.AddRow("Total Hyperedges", stats.TotalEdges.ToString("N0"));
            table.AddRow("Cross-Language Edges", stats.CrossLanguageEdges.ToString("N0"));
            table.AddRow("Unique Files", stats.UniqueFiles.ToString("N0"));

            AnsiConsole.Write(table);

            if (stats.NodesByType.Count > 0)
            {
                AnsiConsole.WriteLine();
                var nodeTypeTable = new Table();
                nodeTypeTable.Title = new TableTitle("Node Types");
                nodeTypeTable.AddColumn("Type");
                nodeTypeTable.AddColumn(new TableColumn("Count").RightAligned());

                foreach (var (type, count) in stats.NodesByType.OrderByDescending(x => x.Value).Take(15))
                {
                    nodeTypeTable.AddRow(type, count.ToString("N0"));
                }

                AnsiConsole.Write(nodeTypeTable);
            }

            if (stats.EdgesByType.Count > 0)
            {
                AnsiConsole.WriteLine();
                var edgeTypeTable = new Table();
                edgeTypeTable.Title = new TableTitle("Edge Types");
                edgeTypeTable.AddColumn("Type");
                edgeTypeTable.AddColumn(new TableColumn("Count").RightAligned());

                foreach (var (type, count) in stats.EdgesByType.OrderByDescending(x => x.Value).Take(15))
                {
                    edgeTypeTable.AddRow(type, count.ToString("N0"));
                }

                AnsiConsole.Write(edgeTypeTable);
            }

            if (stats.NodesByLanguage.Count > 0)
            {
                AnsiConsole.WriteLine();
                var langTable = new Table();
                langTable.Title = new TableTitle("Nodes by Language");
                langTable.AddColumn("Language");
                langTable.AddColumn(new TableColumn("Count").RightAligned());

                foreach (var (lang, count) in stats.NodesByLanguage.OrderByDescending(x => x.Value))
                {
                    langTable.AddRow(lang, count.ToString("N0"));
                }

                AnsiConsole.Write(langTable);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error connecting to Neo4j:[/] {ex.Message}");
        }
    }

    private static async Task ShowQdrantStatsAsync(Settings settings)
    {
        AnsiConsole.Write(new Rule($"[blue]Qdrant Statistics: {settings.QdrantHost}[/]").LeftJustified());

        try
        {
            var hostParts = settings.QdrantHost!.Split(':');
            var host = hostParts[0];
            var port = hostParts.Length > 1 ? int.Parse(hostParts[1]) : 6334;

            var vectorOptions = Options.Create(new VectorStoreOptions
            {
                Host = host,
                GrpcPort = port,
                UseGrpc = true,
                UseTls = !string.IsNullOrEmpty(settings.QdrantApiKey),
                ApiKey = settings.QdrantApiKey
            });

            using var vectorStore = new QdrantVectorStore(vectorOptions, NullLogger<QdrantVectorStore>.Instance);

            var table = new Table();
            table.AddColumn("Collection");
            table.AddColumn(new TableColumn("Points").RightAligned());
            table.AddColumn("Status");

            // Check common collection names
            var collectionsToCheck = new[] { settings.Collection, "code_nodes", "code_edges" }.Distinct();
            var foundAny = false;

            foreach (var collectionName in collectionsToCheck)
            {
                if (await vectorStore.CollectionExistsAsync(collectionName))
                {
                    foundAny = true;
                    var count = await vectorStore.CountAsync(collectionName);
                    table.AddRow(
                        collectionName,
                        count.ToString("N0"),
                        "[green]Ready[/]"
                    );
                }
            }

            if (foundAny)
            {
                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]No collections found[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error connecting to Qdrant:[/] {ex.Message}");
        }
    }
}
