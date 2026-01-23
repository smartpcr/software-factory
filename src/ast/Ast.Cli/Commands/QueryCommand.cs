// -----------------------------------------------------------------------
// <copyright file="QueryCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.VectorDb;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to query the indexed code knowledge base.
/// </summary>
public class QueryCommand : AsyncCommand<QueryCommand.Settings>
{
    /// <summary>
    /// Settings for the query command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Query string (natural language or code pattern)")]
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; set; } = string.Empty;

        [Description("Number of results to return")]
        [CommandOption("-n|--top")]
        [DefaultValue(10)]
        public int TopK { get; set; } = 10;

        [Description("Query type (semantic, callers, callees, path, impact)")]
        [CommandOption("-t|--type")]
        [DefaultValue("semantic")]
        public string QueryType { get; set; } = "semantic";

        [Description("Filter by language (csharp, powershell)")]
        [CommandOption("-l|--language")]
        public string? Language { get; set; }

        [Description("Filter by node type (class, method, function, etc.)")]
        [CommandOption("--node-type")]
        public string? NodeType { get; set; }

        [Description("Filter by file path pattern")]
        [CommandOption("-f|--file")]
        public string? FilePattern { get; set; }

        [Description("Neo4j connection URI (for graph queries)")]
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

        [Description("Qdrant host:port (for semantic queries)")]
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

        [Description("Nodes collection name")]
        [CommandOption("--nodes-collection")]
        [DefaultValue("code_nodes")]
        public string NodesCollection { get; set; } = "code_nodes";

        [Description("Output format (table, json)")]
        [CommandOption("--format")]
        [DefaultValue("table")]
        public string Format { get; set; } = "table";
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[blue]Query:[/] {settings.Query}");
        AnsiConsole.MarkupLine($"[dim]Type: {settings.QueryType}, Top: {settings.TopK}[/]");

        try
        {
            switch (settings.QueryType.ToLowerInvariant())
            {
                case "semantic":
                    return await ExecuteSemanticQueryAsync(settings);
                case "callers":
                    return await ExecuteCallersQueryAsync(settings);
                case "callees":
                    return await ExecuteCalleesQueryAsync(settings);
                case "path":
                    return await ExecutePathQueryAsync(settings);
                case "impact":
                    return await ExecuteImpactQueryAsync(settings);
                default:
                    AnsiConsole.MarkupLine($"[red]Error:[/] Unknown query type: {settings.QueryType}");
                    AnsiConsole.MarkupLine("[dim]Valid types: semantic, callers, callees, path, impact[/]");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task<int> ExecuteSemanticQueryAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.QdrantHost))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --qdrant is required for semantic queries");
            return 1;
        }

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
            NodesCollection = settings.NodesCollection
        });

        using var vectorStore = new QdrantVectorStore(vectorOptions, NullLogger<QdrantVectorStore>.Instance);

        // Generate embedding for query
        var generatorOptions = new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.Http,
            Endpoint = settings.EmbeddingEndpoint,
            ModelName = settings.ModelName
        };
        var factory = new EmbeddingGeneratorFactory(NullLoggerFactory.Instance);
        using var generator = factory.Create(generatorOptions);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Generating query embedding...", _ => { });

        var queryVector = await generator.GenerateEmbeddingAsync(settings.Query);

        // Build filter
        VectorFilter? filter = null;
        var conditions = new List<FilterCondition>();
        if (!string.IsNullOrEmpty(settings.Language))
        {
            conditions.Add(new FilterCondition { Field = "language", Value = settings.Language });
        }
        if (!string.IsNullOrEmpty(settings.NodeType))
        {
            conditions.Add(new FilterCondition { Field = "element_type", Value = settings.NodeType });
        }
        if (conditions.Count > 0)
        {
            filter = new VectorFilter { Must = conditions };
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Searching...", _ => { });

        var results = await vectorStore.SearchAsync(
            settings.NodesCollection,
            queryVector,
            settings.TopK,
            filter);

        DisplayResults(results.Select(r => new QueryResult
        {
            Id = r.Id,
            Name = r.Payload.TryGetValue("name", out var name) ? name?.ToString() ?? "" : "",
            FilePath = r.Payload.TryGetValue("file_path", out var fp) ? fp?.ToString() ?? "" : "",
            ElementType = r.Payload.TryGetValue("element_type", out var et) ? et?.ToString() ?? "" : "",
            Language = r.Payload.TryGetValue("language", out var lang) ? lang?.ToString() ?? "" : "",
            Score = r.Score
        }).ToList(), settings.Format);

        return 0;
    }

    private async Task<int> ExecuteCallersQueryAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Neo4jUri))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --neo4j is required for caller queries");
            return 1;
        }

        var graphOptions = Options.Create(new GraphStoreOptions
        {
            Uri = settings.Neo4jUri,
            Username = settings.Neo4jUser,
            Password = settings.Neo4jPassword
        });

        await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Finding callers...", _ => { });

        // First find the node by name
        var nodes = await graphStore.GetNodesAsync(new NodeFilter { NamePattern = settings.Query }, settings.TopK);
        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No node found matching:[/] {settings.Query}");
            return 0;
        }

        var targetNode = nodes.First();
        AnsiConsole.MarkupLine($"[dim]Finding callers of:[/] {targetNode.Name} ({targetNode.NodeType})");

        var callers = await graphStore.GetCallersAsync(targetNode.Id, settings.TopK);

        DisplayResults(callers.Select(c => new QueryResult
        {
            Id = c.Node.Id,
            Name = c.Node.Name,
            FilePath = c.Node.FilePath,
            ElementType = c.Node.NodeType,
            Language = c.Node.Language,
            Line = c.Node.LineStart
        }).ToList(), settings.Format);

        return 0;
    }

    private async Task<int> ExecuteCalleesQueryAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Neo4jUri))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --neo4j is required for callee queries");
            return 1;
        }

        var graphOptions = Options.Create(new GraphStoreOptions
        {
            Uri = settings.Neo4jUri,
            Username = settings.Neo4jUser,
            Password = settings.Neo4jPassword
        });

        await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Finding callees...", _ => { });

        // First find the node by name
        var nodes = await graphStore.GetNodesAsync(new NodeFilter { NamePattern = settings.Query }, settings.TopK);
        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No node found matching:[/] {settings.Query}");
            return 0;
        }

        var targetNode = nodes.First();
        AnsiConsole.MarkupLine($"[dim]Finding callees of:[/] {targetNode.Name} ({targetNode.NodeType})");

        var callees = await graphStore.GetCalleesAsync(targetNode.Id, settings.TopK);

        DisplayResults(callees.Select(c => new QueryResult
        {
            Id = c.Node.Id,
            Name = c.Node.Name,
            FilePath = c.Node.FilePath,
            ElementType = c.Node.NodeType,
            Language = c.Node.Language,
            Line = c.Node.LineStart
        }).ToList(), settings.Format);

        return 0;
    }

    private async Task<int> ExecutePathQueryAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Neo4jUri))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --neo4j is required for path queries");
            return 1;
        }

        // Parse query for source and target (format: "source -> target")
        var parts = settings.Query.Split("->", StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Path query must be in format: 'source -> target'");
            return 1;
        }

        var graphOptions = Options.Create(new GraphStoreOptions
        {
            Uri = settings.Neo4jUri,
            Username = settings.Neo4jUser,
            Password = settings.Neo4jPassword
        });

        await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Finding paths...", _ => { });

        // Find source and target nodes
        var sourceNodes = await graphStore.GetNodesAsync(new NodeFilter { NamePattern = parts[0] }, 1);
        var targetNodes = await graphStore.GetNodesAsync(new NodeFilter { NamePattern = parts[1] }, 1);

        if (sourceNodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Source node not found:[/] {parts[0]}");
            return 0;
        }

        if (targetNodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Target node not found:[/] {parts[1]}");
            return 0;
        }

        var paths = await graphStore.FindPathsAsync(sourceNodes.First().Id, targetNodes.First().Id, settings.TopK);

        if (paths.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No paths found[/]");
            return 0;
        }

        foreach (var (path, index) in paths.Select((p, i) => (p, i)))
        {
            AnsiConsole.MarkupLine($"\n[blue]Path {index + 1}:[/] ({path.Nodes.Count} nodes)");
            var tree = new Tree(path.Nodes.First().Name);

            TreeNode current = tree.AddNode($"[green]{path.Nodes.First().Name}[/] [dim]({path.Nodes.First().NodeType})[/]");
            foreach (var node in path.Nodes.Skip(1))
            {
                current = current.AddNode($"[green]{node.Name}[/] [dim]({node.NodeType})[/]");
            }

            AnsiConsole.Write(tree);
        }

        return 0;
    }

    private async Task<int> ExecuteImpactQueryAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Neo4jUri))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --neo4j is required for impact queries");
            return 1;
        }

        var graphOptions = Options.Create(new GraphStoreOptions
        {
            Uri = settings.Neo4jUri,
            Username = settings.Neo4jUser,
            Password = settings.Neo4jPassword
        });

        await using var graphStore = new Neo4jGraphStore(graphOptions, NullLogger<Neo4jGraphStore>.Instance);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Analyzing impact...", _ => { });

        // First find the node by name
        var nodes = await graphStore.GetNodesAsync(new NodeFilter { NamePattern = settings.Query }, 1);
        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No node found matching:[/] {settings.Query}");
            return 0;
        }

        var targetNode = nodes.First();
        AnsiConsole.MarkupLine($"\n[blue]Impact Analysis for:[/] {targetNode.Name} ({targetNode.NodeType})");

        var impact = await graphStore.AnalyzeImpactAsync(targetNode.Id, settings.TopK);

        AnsiConsole.MarkupLine($"[dim]Found {impact.TotalAffected} potentially impacted nodes[/]\n");

        // Show directly affected
        if (impact.DirectlyAffected.Count > 0)
        {
            var directTable = new Table();
            directTable.Title = new TableTitle("[yellow]Directly Affected[/]");
            directTable.AddColumn("#");
            directTable.AddColumn("Name");
            directTable.AddColumn("Type");
            directTable.AddColumn("File");

            var i = 1;
            foreach (var affected in impact.DirectlyAffected.Take(settings.TopK))
            {
                directTable.AddRow(
                    i++.ToString(),
                    affected.Node.Name,
                    affected.Node.NodeType,
                    affected.Node.FilePath
                );
            }

            AnsiConsole.Write(directTable);
        }

        // Show indirectly affected
        if (impact.IndirectlyAffected.Count > 0)
        {
            AnsiConsole.WriteLine();
            var indirectTable = new Table();
            indirectTable.Title = new TableTitle("[dim]Indirectly Affected[/]");
            indirectTable.AddColumn("#");
            indirectTable.AddColumn("Name");
            indirectTable.AddColumn("Type");
            indirectTable.AddColumn("Depth");
            indirectTable.AddColumn("File");

            var i = 1;
            foreach (var affected in impact.IndirectlyAffected.Take(settings.TopK))
            {
                indirectTable.AddRow(
                    i++.ToString(),
                    affected.Node.Name,
                    affected.Node.NodeType,
                    affected.Depth.ToString(),
                    affected.Node.FilePath
                );
            }

            AnsiConsole.Write(indirectTable);
        }

        // Show affected files
        if (impact.AffectedFiles.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[blue]Affected Files ({impact.AffectedFiles.Count}):[/]");
            foreach (var file in impact.AffectedFiles.Take(20))
            {
                AnsiConsole.MarkupLine($"  [dim]\u2022[/] {file}");
            }
            if (impact.AffectedFiles.Count > 20)
            {
                AnsiConsole.MarkupLine($"  [dim]... and {impact.AffectedFiles.Count - 20} more[/]");
            }
        }

        if (impact.CrossLanguageImpact)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]\u26a0 Cross-language impact detected[/]");
        }

        return 0;
    }

    private static void DisplayResults(List<QueryResult> results, string format)
    {
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No results found[/]");
            return;
        }

        if (format.ToLowerInvariant() == "json")
        {
            var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            AnsiConsole.WriteLine(json);
            return;
        }

        var table = new Table();
        table.AddColumn("#");
        table.AddColumn("Name");
        table.AddColumn("Type");
        table.AddColumn("Language");
        table.AddColumn("File");
        table.AddColumn(new TableColumn("Score").RightAligned());

        var index = 1;
        foreach (var result in results)
        {
            table.AddRow(
                index++.ToString(),
                result.Name,
                result.ElementType,
                result.Language,
                result.FilePath + (result.Line > 0 ? $":{result.Line}" : ""),
                result.Score.HasValue ? $"{result.Score:F3}" : "-"
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]Found {results.Count} results[/]");
    }

    private class QueryResult
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ElementType { get; set; } = "";
        public string Language { get; set; } = "";
        public int Line { get; set; }
        public float? Score { get; set; }
    }
}
