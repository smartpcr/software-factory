// -----------------------------------------------------------------------
// <copyright file="ParseCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Ast.Core;
using Ast.Csharp;
using Ast.PowerShell;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to parse a repository and generate a hypergraph.
/// </summary>
public class ParseCommand : AsyncCommand<ParseCommand.Settings>
{
    /// <summary>
    /// Settings for the parse command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Path to the repository to parse")]
        [CommandArgument(0, "<REPO_PATH>")]
        public string RepoPath { get; set; } = string.Empty;

        [Description("Output file path (default: hypergraph.json)")]
        [CommandOption("-o|--output")]
        public string? OutputPath { get; set; }

        [Description("Parse C# files")]
        [CommandOption("--csharp")]
        [DefaultValue(true)]
        public bool ParseCSharp { get; set; } = true;

        [Description("Parse PowerShell files")]
        [CommandOption("--powershell")]
        [DefaultValue(true)]
        public bool ParsePowerShell { get; set; } = true;

        [Description("Verbose output")]
        [CommandOption("-v|--verbose")]
        public bool Verbose { get; set; }

        [Description("Pretty print JSON output")]
        [CommandOption("--pretty")]
        public bool PrettyPrint { get; set; }
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

        var outputPath = settings.OutputPath ?? Path.Combine(Directory.GetCurrentDirectory(), "hypergraph.json");

        AnsiConsole.MarkupLine($"[blue]Parsing repository:[/] {repoPath}");

        HypergraphData? result = null;
        var stopwatch = Stopwatch.StartNew();

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
                result = processor.Process(settings.Verbose);

                ctx.Status("Serializing output...");
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = settings.PrettyPrint,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(result, jsonOptions);
                await File.WriteAllTextAsync(outputPath, json);
            });

        stopwatch.Stop();

        if (result != null)
        {
            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());

            table.AddRow("Total Nodes", result.Nodes.Count.ToString("N0"));
            table.AddRow("C# Nodes", result.Metadata.NumCSharpNodes.ToString("N0"));
            table.AddRow("PowerShell Nodes", result.Metadata.NumPowerShellNodes.ToString("N0"));
            table.AddRow("Total Hyperedges", result.Hyperedges.Count.ToString("N0"));
            table.AddRow("Cross-Language Edges", result.Metadata.NumCrossLanguageEdges.ToString("N0"));
            table.AddRow("Files Processed", result.Metadata.NumFiles.ToString("N0"));
            table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Output written to:[/] {outputPath}");
        }

        return 0;
    }
}
