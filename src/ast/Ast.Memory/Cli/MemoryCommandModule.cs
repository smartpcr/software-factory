// -----------------------------------------------------------------------
// <copyright file="MemoryCommandModule.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Cli;

using System.ComponentModel;
using System.Diagnostics;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Factory for creating a scoped <see cref="ServiceProvider"/> with all memory services
/// registered.  Each CLI command creates (and disposes) its own provider, using the
/// configured graph store backend from <see cref="MemoryOptions"/>.
/// </summary>
internal static class MemoryServiceFactory
{
    /// <summary>
    /// Creates a scoped <see cref="ServiceProvider"/> with all memory services.
    /// Registers the graph store backend selected by <see cref="MemoryOptions.Backend"/>
    /// (defaults to <see cref="MemoryBackend.Sqlite"/> with an auto-resolved path).
    /// </summary>
    public static ServiceProvider CreateProvider(string? storePath = null)
    {
        var options = new MemoryOptions();
        if (storePath != null)
        {
            options.SqlitePath = storePath;
        }
        else
        {
            options.SqlitePath = Path.Combine(
                Directory.GetCurrentDirectory(), ".ast-memory", "memory-graph.db");
        }

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddMemoryServices(options);
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Registrar that registers all memory CLI subcommands into a Spectre.Console.Cli branch.
/// Used by <c>Ast.Cli/Program.cs</c> to add the <c>memory</c> command group.
/// </summary>
public static class MemoryCommandModule
{
    /// <summary>
    /// Registers the memory subcommands (index, query, sync, stats, consolidate)
    /// into the specified branch configurator.
    /// </summary>
    public static void RegisterCommands(IConfigurator<CommandSettings> branch)
    {
        branch.SetDescription("Agent memory graph commands");

        branch.AddCommand<MemoryIndexCommand>("index")
            .WithDescription("Index a repository into the memory graph")
            .WithExample("memory", "index", "./my-repo")
            .WithExample("memory", "index", "./my-repo", "-l", "C#");

        branch.AddCommand<MemoryQueryCommand>("query")
            .WithDescription("Query the memory graph for context")
            .WithExample("memory", "query", "find methods calling SaveAsync")
            .WithExample("memory", "query", "HypergraphNode", "-m", "SemanticSearch");

        branch.AddCommand<MemorySyncCommand>("sync")
            .WithDescription("Sync memory graph with git changes")
            .WithExample("memory", "sync", "abc1234", "def5678")
            .WithExample("memory", "sync", "HEAD~1", "HEAD", "-r", "./my-repo");

        branch.AddCommand<MemoryStatsCommand>("stats")
            .WithDescription("Show memory graph statistics")
            .WithExample("memory", "stats");

        branch.AddCommand<MemoryConsolidateCommand>("consolidate")
            .WithDescription("Consolidate similar expertise entries")
            .WithExample("memory", "consolidate")
            .WithExample("memory", "consolidate", "-t", "0.9", "-n", "20");
    }
}

/// <summary>
/// CLI command: <c>memory index &lt;repo-path&gt;</c> — runs the extraction pipeline.
/// </summary>
public class MemoryIndexCommand : AsyncCommand<MemoryIndexCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [Description("Path to the repository to index")]
        [CommandArgument(0, "<REPO_PATH>")]
        public string RepoPath { get; set; } = string.Empty;

        [Description("Languages to index (comma-separated, e.g. C#,PowerShell)")]
        [CommandOption("-l|--languages")]
        [DefaultValue("C#,PowerShell")]
        public string Languages { get; set; } = "C#,PowerShell";

        [Description("Scope subtree (relative path within repo)")]
        [CommandOption("-s|--scope")]
        public string? ScopeSubtree { get; set; }

        [Description("Path to the memory graph store file")]
        [CommandOption("--store")]
        public string? StorePath { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = Path.GetFullPath(settings.RepoPath);
        if (!Directory.Exists(repoPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Repository path does not exist: {Markup.Escape(repoPath)}");
            return 1;
        }

        using var provider = MemoryServiceFactory.CreateProvider(settings.StorePath);
        var pipeline = provider.GetRequiredService<IExtractionPipeline>();

        var languages = settings.Languages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var options = new ExtractionOptions(languages, settings.ScopeSubtree);
        var stopwatch = Stopwatch.StartNew();

        AnsiConsole.MarkupLine($"[blue]Indexing repository:[/] {Markup.Escape(repoPath)}");
        AnsiConsole.MarkupLine($"[dim]Languages: {Markup.Escape(string.Join(", ", languages))}[/]");

        ExtractionResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Extracting code structure...", async _ =>
            {
                result = await pipeline.ExtractAsync(repoPath, options, cancellationToken);
            });

        stopwatch.Stop();

        if (result != null)
        {
            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());
            table.AddRow("Nodes Created", result.NodesCreated.ToString("N0"));
            table.AddRow("Edges Created", result.EdgesCreated.ToString("N0"));
            table.AddRow("Embeddings Generated", result.EmbeddingsGenerated.ToString("N0"));
            table.AddRow("Files Skipped", result.FilesSkipped.ToString("N0"));
            table.AddRow("Errors", result.Errors.Count.ToString("N0"));
            table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");
            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);

            if (result.Errors.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Errors:[/]");
                foreach (var error in result.Errors.Take(10))
                {
                    AnsiConsole.MarkupLine($"  [dim]•[/] {Markup.Escape(error)}");
                }

                if (result.Errors.Count > 10)
                {
                    AnsiConsole.MarkupLine($"  [dim]... and {result.Errors.Count - 10} more[/]");
                }
            }

            // If extraction produced only errors and no nodes, report failure
            if (result.NodesCreated == 0 && result.Errors.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]Indexing failed: no nodes were created.[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine("[green]Indexing complete.[/]");
        return 0;
    }
}

/// <summary>
/// CLI command: <c>memory query &lt;text&gt;</c> — queries the memory graph.
/// </summary>
public class MemoryQueryCommand : AsyncCommand<MemoryQueryCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [Description("Query text (natural language or code pattern)")]
        [CommandArgument(0, "<TEXT>")]
        public string Text { get; set; } = string.Empty;

        [Description("Retrieval mode (Hybrid, StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall)")]
        [CommandOption("-m|--mode")]
        [DefaultValue("Hybrid")]
        public string Mode { get; set; } = "Hybrid";

        [Description("Token budget for context assembly")]
        [CommandOption("-b|--budget")]
        [DefaultValue(8000)]
        public int TokenBudget { get; set; } = 8000;

        [Description("Output format (markdown, json)")]
        [CommandOption("--format")]
        [DefaultValue("markdown")]
        public string Format { get; set; } = "markdown";

        [Description("Path to the memory graph store file")]
        [CommandOption("--store")]
        public string? StorePath { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RetrievalMode>(settings.Mode, ignoreCase: true, out var mode))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Unknown retrieval mode: {Markup.Escape(settings.Mode)}");
            AnsiConsole.MarkupLine("[dim]Valid modes: Hybrid, StructuralDrill, CallTrace, SemanticSearch, ExpertiseRecall[/]");
            return 1;
        }

        using var provider = MemoryServiceFactory.CreateProvider(settings.StorePath);
        var retrieval = provider.GetRequiredService<IMemoryRetrievalEngine>();

        var request = new RetrievalRequest(
            QueryText: settings.Text,
            Mode: mode,
            TokenBudget: settings.TokenBudget);

        AnsiConsole.MarkupLine($"[blue]Query:[/] {Markup.Escape(settings.Text)}");
        AnsiConsole.MarkupLine($"[dim]Mode: {mode}, Budget: {settings.TokenBudget} tokens[/]");

        RetrievalResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Retrieving from memory graph...", async _ =>
            {
                result = await retrieval.RetrieveAsync(request, cancellationToken);
            });

        if (result != null)
        {
            if (settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    nodes = result.Nodes.Select(n => new
                    {
                        nodeId = n.Node.Inner.Id,
                        name = n.Node.Inner.Name,
                        compositeScore = n.CompositeScore,
                    }),
                    tokensUsed = result.TokensUsed,
                    modesExecuted = result.ModesExecuted.Select(m => m.ToString()),
                    contextMarkdown = result.ContextMarkdown,
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                AnsiConsole.WriteLine(json);
            }
            else
            {
                AnsiConsole.MarkupLine($"\n[dim]Tokens used: {result.TokensUsed}, Nodes: {result.Nodes.Count}[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Markup(Markup.Escape(result.ContextMarkdown)));
                AnsiConsole.WriteLine();
            }
        }

        return 0;
    }
}

/// <summary>
/// CLI command: <c>memory sync &lt;from&gt; &lt;to&gt;</c> — applies a git diff to the memory graph.
/// </summary>
public class MemorySyncCommand : AsyncCommand<MemorySyncCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [Description("Source commit SHA")]
        [CommandArgument(0, "<FROM>")]
        public string FromCommit { get; set; } = string.Empty;

        [Description("Target commit SHA")]
        [CommandArgument(1, "<TO>")]
        public string ToCommit { get; set; } = string.Empty;

        [Description("Repository root path")]
        [CommandOption("-r|--repo")]
        [DefaultValue(".")]
        public string RepoPath { get; set; } = ".";

        [Description("Path to the memory graph store file")]
        [CommandOption("--store")]
        public string? StorePath { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var repoPath = Path.GetFullPath(settings.RepoPath);
        if (!Directory.Exists(repoPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Repository path does not exist: {Markup.Escape(repoPath)}");
            return 1;
        }

        using var provider = MemoryServiceFactory.CreateProvider(settings.StorePath);
        var sync = provider.GetRequiredService<IIncrementalSync>();

        AnsiConsole.MarkupLine($"[blue]Syncing memory graph:[/] {Markup.Escape(settings.FromCommit)} → {Markup.Escape(settings.ToCommit)}");

        SyncResult? result = null;

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Applying diff to memory graph...", async _ =>
                {
                    result = await sync.ApplyDiffAsync(repoPath, settings.FromCommit, settings.ToCommit, cancellationToken);
                });
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (result != null)
        {
            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());
            table.AddRow("Files Changed", result.FilesChanged.ToString("N0"));
            table.AddRow("Nodes Added", result.NodesAdded.ToString("N0"));
            table.AddRow("Nodes Retired", result.NodesRetired.ToString("N0"));
            table.AddRow("Edges Added", result.EdgesAdded.ToString("N0"));
            table.AddRow("Edges Retired", result.EdgesRetired.ToString("N0"));
            table.AddRow("Expertise Decayed", result.ExpertiseDecayed.ToString("N0"));
            table.AddRow("Duration", $"{result.Duration.TotalSeconds:F2}s");
            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine("[green]Sync complete.[/]");
        return 0;
    }
}

/// <summary>
/// CLI command: <c>memory stats</c> — displays memory graph statistics.
/// </summary>
public class MemoryStatsCommand : AsyncCommand<MemoryStatsCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [Description("Path to the memory graph store file")]
        [CommandOption("--store")]
        public string? StorePath { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var provider = MemoryServiceFactory.CreateProvider(settings.StorePath);
        var store = provider.GetRequiredService<IMemoryGraphStore>();

        AnsiConsole.MarkupLine("[blue]Memory Graph Statistics[/]");

        MemoryGraphStatistics? stats = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching statistics...", async _ =>
            {
                stats = await store.GetMemoryStatisticsAsync(cancellationToken);
            });

        if (stats != null)
        {
            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());
            table.AddRow("Total Nodes", stats.TotalNodes.ToString("N0"));
            table.AddRow("Total Edges", stats.TotalEdges.ToString("N0"));
            table.AddRow("Total Expertise", stats.TotalExpertise.ToString("N0"));
            table.AddRow("Archived Expertise", stats.ArchivedExpertise.ToString("N0"));
            AnsiConsole.Write(table);

            if (stats.NodesByLayer.Count > 0)
            {
                AnsiConsole.WriteLine();
                var layerTable = new Table();
                layerTable.Title = new TableTitle("Nodes by Layer");
                layerTable.AddColumn("Layer");
                layerTable.AddColumn(new TableColumn("Count").RightAligned());
                foreach (var (layer, count) in stats.NodesByLayer.OrderByDescending(x => x.Value))
                {
                    layerTable.AddRow(Markup.Escape(layer), count.ToString("N0"));
                }

                AnsiConsole.Write(layerTable);
            }

            if (stats.EdgesByLayer.Count > 0)
            {
                AnsiConsole.WriteLine();
                var edgeTable = new Table();
                edgeTable.Title = new TableTitle("Edges by Layer");
                edgeTable.AddColumn("Layer");
                edgeTable.AddColumn(new TableColumn("Count").RightAligned());
                foreach (var (layer, count) in stats.EdgesByLayer.OrderByDescending(x => x.Value))
                {
                    edgeTable.AddRow(Markup.Escape(layer), count.ToString("N0"));
                }

                AnsiConsole.Write(edgeTable);
            }

            if (stats.ExpertiseByKind.Count > 0)
            {
                AnsiConsole.WriteLine();
                var expertiseTable = new Table();
                expertiseTable.Title = new TableTitle("Expertise by Kind");
                expertiseTable.AddColumn("Kind");
                expertiseTable.AddColumn(new TableColumn("Count").RightAligned());
                foreach (var (kind, count) in stats.ExpertiseByKind.OrderByDescending(x => x.Value))
                {
                    expertiseTable.AddRow(Markup.Escape(kind), count.ToString("N0"));
                }

                AnsiConsole.Write(expertiseTable);
            }
        }

        return 0;
    }
}

/// <summary>
/// CLI command: <c>memory consolidate</c> — consolidates expertise entries.
/// </summary>
public class MemoryConsolidateCommand : AsyncCommand<MemoryConsolidateCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [Description("Similarity threshold for merging (0.0-1.0)")]
        [CommandOption("-t|--threshold")]
        [DefaultValue(0.85)]
        public double SimilarityThreshold { get; set; } = 0.85;

        [Description("Maximum merges per run")]
        [CommandOption("-n|--max-merges")]
        [DefaultValue(50)]
        public int MaxMergesPerRun { get; set; } = 50;

        [Description("Path to the memory graph store file")]
        [CommandOption("--store")]
        public string? StorePath { get; set; }
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var provider = MemoryServiceFactory.CreateProvider(settings.StorePath);
        var evolution = provider.GetRequiredService<IEvolutionEngine>();

        var options = new ConsolidationOptions(settings.SimilarityThreshold, settings.MaxMergesPerRun);

        AnsiConsole.MarkupLine($"[blue]Consolidating expertise[/] [dim](threshold: {settings.SimilarityThreshold}, max merges: {settings.MaxMergesPerRun})[/]");

        ConsolidationResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Consolidating expertise entries...", async _ =>
            {
                result = await evolution.ConsolidateAsync(options, cancellationToken);
            });

        if (result != null)
        {
            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());
            table.AddRow("Candidates Paired", result.CandidatesPaired.ToString("N0"));
            table.AddRow("Merged Count", result.MergedCount.ToString("N0"));
            AnsiConsole.Write(table);

            if (result.MergedEntryIds.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Merged entry IDs:[/]");
                foreach (var id in result.MergedEntryIds)
                {
                    AnsiConsole.MarkupLine($"  [dim]•[/] {Markup.Escape(id)}");
                }
            }
        }

        AnsiConsole.MarkupLine("[green]Consolidation complete.[/]");
        return 0;
    }
}