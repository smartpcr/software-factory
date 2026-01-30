// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="AzureStack Compute">
// </copyright>
// -----------------------------------------------------------------------

namespace Chat;

using Chat.Helpers;
using GitHub.Copilot.SDK;
using Spectre.Console;

/// <summary>
/// Chat console application using GitHub Copilot SDK.
/// </summary>
public class Program
{
    /// <summary>
    /// Main entry point for the Chat application.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>A task representing the async operation.</returns>
    public static async Task Main(string[] args)
    {
        AnsiConsole.Write(
            new FigletText("Copilot Chat")
                .LeftJustified()
                .Color(Color.Cyan1));

        // ============================================================
        // Step 1: Check if Copilot CLI is installed and user is authenticated
        // ============================================================
        AnsiConsole.WriteLine("🔍 Checking prerequisites...\n");

        var copilotStatus = await CliChecker.CheckCopilotStatusAsync();

        if (!CliChecker.IsReady(copilotStatus))
        {
            AnsiConsole.MarkupLine("[yellow]Press any key to exit...[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.MarkupLine("[grey]Type your message and press Enter. Type 'exit' to quit.[/]");
        AnsiConsole.WriteLine();

        await using var client = new CopilotClient();

        try
        {
            await client.StartAsync();
            AnsiConsole.MarkupLine("[green]Connected to GitHub Copilot![/]");
            AnsiConsole.WriteLine();

            await using var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = "claude-opus-4-5"
            });

            while (true)
            {
                var prompt = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]You:[/] ")
                        .PromptStyle("white"));

                if (string.Equals(prompt, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine("[yellow]Goodbye![/]");
                    break;
                }

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    continue;
                }

                var responseComplete = new TaskCompletionSource();
                var responseBuilder = new System.Text.StringBuilder();

                session.On(evt =>
                {
                    if (evt is AssistantMessageEvent msg)
                    {
                        responseBuilder.Append(msg.Data.Content);
                        AnsiConsole.Markup($"[green]{msg.Data.Content}[/]");
                    }
                    else if (evt is SessionIdleEvent)
                    {
                        responseComplete.TrySetResult();
                    }
                });

                AnsiConsole.Markup("[green]Copilot:[/] ");

                await session.SendAsync(new MessageOptions { Prompt = prompt });
                await responseComplete.Task;

                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            AnsiConsole.MarkupLine("[yellow]Make sure GitHub Copilot CLI is installed and authenticated.[/]");
            AnsiConsole.MarkupLine("[grey]Run: gh auth login[/]");
        }
    }
}
