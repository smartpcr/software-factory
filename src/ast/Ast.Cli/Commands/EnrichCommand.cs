// -----------------------------------------------------------------------
// <copyright file="EnrichCommand.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Cli.Commands;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Ast.Core;
using Ast.Lsp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

/// <summary>
/// Command to enrich a hypergraph with LSP semantic information.
/// </summary>
public class EnrichCommand : AsyncCommand<EnrichCommand.Settings>
{
    /// <summary>
    /// Settings for the enrich command.
    /// </summary>
    public class Settings : CommandSettings
    {
        [Description("Path to the hypergraph JSON file")]
        [CommandArgument(0, "<GRAPH_PATH>")]
        public string GraphPath { get; set; } = string.Empty;

        [Description("Output file path (default: enriched.json)")]
        [CommandOption("-o|--output")]
        public string? OutputPath { get; set; }

        [Description("Workspace root path for LSP")]
        [CommandOption("-w|--workspace")]
        public string? WorkspacePath { get; set; }

        [Description("Enable C# LSP enrichment")]
        [CommandOption("--csharp")]
        [DefaultValue(true)]
        public bool EnableCSharp { get; set; } = true;

        [Description("Enable PowerShell LSP enrichment")]
        [CommandOption("--powershell")]
        [DefaultValue(true)]
        public bool EnablePowerShell { get; set; } = true;

        [Description("C# LSP server type (csharp-ls or omnisharp)")]
        [CommandOption("--csharp-server")]
        [DefaultValue("csharp-ls")]
        public string CSharpServer { get; set; } = "csharp-ls";

        [Description("Path to C# LSP server executable")]
        [CommandOption("--csharp-path")]
        public string? CSharpServerPath { get; set; }

        [Description("Path to PowerShell executable")]
        [CommandOption("--pwsh-path")]
        public string? PowerShellPath { get; set; }

        [Description("Path to PowerShellEditorServices")]
        [CommandOption("--pses-path")]
        public string? PsesPath { get; set; }

        [Description("Collect call hierarchy information")]
        [CommandOption("--call-hierarchy")]
        [DefaultValue(true)]
        public bool CollectCallHierarchy { get; set; } = true;

        [Description("Collect diagnostics")]
        [CommandOption("--diagnostics")]
        [DefaultValue(true)]
        public bool CollectDiagnostics { get; set; } = true;

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

        var outputPath = settings.OutputPath ?? Path.ChangeExtension(settings.GraphPath, ".enriched.json");

        AnsiConsole.MarkupLine($"[blue]Enriching hypergraph:[/] {settings.GraphPath}");

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

        // Determine workspace path
        var workspacePath = settings.WorkspacePath ?? data.Metadata.RepoPath;
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Workspace path is required for LSP enrichment. Use --workspace option.");
            return 1;
        }

        // Configure LSP
        var lspConfig = new LspConfiguration
        {
            Enabled = true,
            CollectCallHierarchy = settings.CollectCallHierarchy,
            CollectDiagnostics = settings.CollectDiagnostics,
            CSharp = new CSharpLspConfiguration
            {
                Enabled = settings.EnableCSharp,
                ServerType = settings.CSharpServer.ToLowerInvariant() == "omnisharp"
                    ? CSharpLspServerType.OmniSharp
                    : CSharpLspServerType.CSharpLs,
                ServerPath = settings.CSharpServerPath
            },
            PowerShell = new PowerShellLspConfiguration
            {
                Enabled = settings.EnablePowerShell,
                PowerShellPath = settings.PowerShellPath,
                EditorServicesPath = settings.PsesPath
            }
        };

        var stopwatch = Stopwatch.StartNew();
        HypergraphData? enrichedData = null;
        var enrichedCount = 0;

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
                var task = ctx.AddTask($"Enriching {data.Nodes.Count} nodes", maxValue: data.Nodes.Count);

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
                        task.Description = $"Enriching: {p.CurrentItem ?? "..."}";
                    });

                    enrichedData = await enrichmentService.EnrichHypergraphAsync(
                        data,
                        workspacePath,
                        progress);

                    enrichedCount = enrichedData.Nodes.Values.Count(n => n.IsLspEnriched);
                    task.Value = data.Nodes.Count;
                }
                finally
                {
                    await enrichmentService.DisposeAsync();
                }
            });

        stopwatch.Stop();

        if (enrichedData != null)
        {
            // Save enriched data
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = settings.PrettyPrint,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var outputJson = JsonSerializer.Serialize(enrichedData, jsonOptions);
            await File.WriteAllTextAsync(outputPath, outputJson);

            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn(new TableColumn("Value").RightAligned());

            table.AddRow("Total Nodes", enrichedData.Nodes.Count.ToString("N0"));
            table.AddRow("Nodes Enriched", enrichedCount.ToString("N0"));
            table.AddRow("Enrichment Rate", $"{(double)enrichedCount / enrichedData.Nodes.Count * 100:F1}%");
            table.AddRow("Duration", $"{stopwatch.Elapsed.TotalSeconds:F2}s");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Output written to:[/] {outputPath}");
        }

        return 0;
    }
}
