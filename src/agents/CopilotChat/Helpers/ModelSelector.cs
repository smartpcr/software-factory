// -----------------------------------------------------------------------
// <copyright file="ModelSelector.cs" company="AzureStack Compute">
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat.Helpers;

using GitHub.Copilot.SDK;
using Spectre.Console;

/// <summary>
/// Handles model selection for the Copilot session.
/// Fetches available models from the Copilot SDK.
/// Based on: https://github.com/jamesmontemagno/podcast-metadata-generator
/// </summary>
public static class ModelSelector
{
    /// <summary>
    /// Fetches the list of available models from the Copilot SDK.
    /// Returns null if models cannot be fetched.
    /// </summary>
    /// <returns>A list of available models or null if unavailable.</returns>
    public static async Task<IList<ModelInfo>?> GetModelsFromSdkAsync()
    {
        try
        {
            await using var client = new CopilotClient();
            await client.StartAsync();

            var models = await client.ListModelsAsync();

            await client.StopAsync();

            return models;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prints all available models to the console and returns.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    public static async Task PrintModelsAsync()
    {
        AnsiConsole.MarkupLine("[grey]Fetching available models from Copilot SDK...[/]\n");

        var models = await GetModelsFromSdkAsync();

        if (models == null || models.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]❌ Could not fetch models from Copilot SDK.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]#[/]")
            .AddColumn("[bold]ID[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Multiplier[/]");

        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var multiplier = model.Billing?.Multiplier != null
                ? $"{model.Billing.Multiplier}x"
                : "-";
            table.AddRow(
                (i + 1).ToString(),
                Markup.Escape(model.Id),
                Markup.Escape(model.Name),
                multiplier);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]{models.Count} model(s) available.[/]");
    }

    /// <summary>
    /// Prompts the user to select a model from available options.
    /// </summary>
    /// <returns>The selected model ID, or null if no models available.</returns>
    public static async Task<string?> SelectModelAsync()
    {
        AnsiConsole.MarkupLine("[grey]   Fetching available models from Copilot SDK...[/]");

        var models = await GetModelsFromSdkAsync();

        if (models == null || models.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]❌ Could not fetch models from Copilot SDK.[/]");
            AnsiConsole.MarkupLine("[red]   Make sure 'copilot' is installed and working.[/]");
            return null;
        }

        AnsiConsole.MarkupLine("[yellow]🤖 Select a model:[/]");

        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var multiplierText = model.Billing?.Multiplier != null
                ? $" (multiplier: {model.Billing.Multiplier}x)"
                : string.Empty;
            AnsiConsole.WriteLine($"   {i + 1}. {model.Name}{multiplierText}");
        }

        var choice = AnsiConsole.Prompt(
            new TextPrompt<string>($"\nEnter choice (1-{models.Count}) [[default: 1]]: ")
                .AllowEmpty());

        if (string.IsNullOrEmpty(choice) || !int.TryParse(choice, out var index) || index < 1 || index > models.Count)
        {
            index = 1;
        }

        var selected = models[index - 1];
        var selectedMultiplierText = selected.Billing?.Multiplier != null
            ? $" (multiplier: {selected.Billing.Multiplier}x)"
            : string.Empty;
        AnsiConsole.MarkupLine($"[green]✅ Selected: {selected.Name}{selectedMultiplierText}[/]");

        return selected.Id;
    }
}
