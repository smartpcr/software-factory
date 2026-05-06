// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Ast.Cli.Commands;
using Ast.Memory.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("ast-cli");
    config.SetApplicationVersion("1.0.0");

    config.AddCommand<ParseCommand>("parse")
        .WithDescription("Parse a repository and generate a hypergraph")
        .WithExample("parse", "./my-repo", "-o", "graph.json");

    config.AddCommand<EnrichCommand>("enrich")
        .WithDescription("Enrich a hypergraph with LSP semantic information")
        .WithExample("enrich", "graph.json", "-o", "enriched.json", "--workspace", "./my-repo");

    config.AddCommand<EmbedCommand>("embed")
        .WithDescription("Generate embeddings from a hypergraph")
        .WithExample("embed", "graph.json", "-o", "embeddings.json", "--endpoint", "http://localhost:11434");

    config.AddCommand<IndexCommand>("index")
        .WithDescription("Index embeddings into vector and graph databases")
        .WithExample("index", "embeddings.json", "--neo4j", "bolt://localhost:7687", "--qdrant", "localhost:6334");

    config.AddCommand<QueryCommand>("query")
        .WithDescription("Query the indexed code knowledge base")
        .WithExample("query", "find methods calling SaveAsync", "--top", "10");

    config.AddCommand<PipelineCommand>("pipeline")
        .WithDescription("Run the full pipeline: parse -> enrich -> embed -> index")
        .WithExample("pipeline", "./my-repo", "--neo4j", "bolt://localhost:7687");

    config.AddCommand<StatsCommand>("stats")
        .WithDescription("Show statistics about a hypergraph or database")
        .WithExample("stats", "graph.json")
        .WithExample("stats", "--neo4j", "bolt://localhost:7687");

    config.AddBranch("memory", MemoryCommandModule.RegisterCommands);
});

return app.Run(args);
