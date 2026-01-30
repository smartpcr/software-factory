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

        // ============================================================
        // Step 2: Model Selection
        // ============================================================
        var selectedModel = await ModelSelector.SelectModelAsync();
        if (selectedModel == null)
        {
            AnsiConsole.MarkupLine("[yellow]Press any key to exit...[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.WriteLine();

        // ============================================================
        // Step 3: Start Copilot Client
        // ============================================================
        AnsiConsole.WriteLine("🚀 Starting Copilot client...");

        CopilotClient? client = null;
        CopilotSession? session = null;

        try
        {
            client = new CopilotClient();
            await client.StartAsync();
            AnsiConsole.MarkupLine("[green]✅ Copilot client started successfully![/]\n");

            // Ping to verify connection
            var pingResponse = await client.PingAsync("hello");
            AnsiConsole.WriteLine("📡 Server connection verified (ping response received)\n");

            // Create session with selected model
            AnsiConsole.WriteLine($"📝 Creating session with model: {selectedModel}...");
            session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = selectedModel,
                Streaming = true // Enable streaming for real-time output
            });
            AnsiConsole.MarkupLine($"[green]✅ Session created! (ID: {session.SessionId})[/]\n");

            // Show demo prompts
            DemoPrompts.ShowDemoPrompts();

            // ============================================================
            // Step 4: Interactive Chat Loop
            // ============================================================
            AnsiConsole.MarkupLine("[yellow]💬 Interactive Chat Mode[/]");
            AnsiConsole.WriteLine("   Type your message and press Enter to send.");
            AnsiConsole.WriteLine("   Type 'exit' or 'quit' to end the session.");
            AnsiConsole.WriteLine("   Type 'demo <number>' to run a demo prompt (e.g., 'demo 1')");
            AnsiConsole.WriteLine("   Type 'model' to change the model.");
            AnsiConsole.WriteLine("   Type 'clear' to start a new session.\n");

            while (true)
            {
                var input = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]You:[/] ")
                        .AllowEmpty());

                if (string.IsNullOrEmpty(input))
                    continue;

                // Handle special commands
                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.WriteLine("\n👋 Goodbye!");
                    break;
                }

                if (input.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    var newModel = await ModelSelector.SelectModelAsync();
                    if (newModel == null)
                    {
                        AnsiConsole.MarkupLine("[red]Could not fetch models. Keeping current model.[/]\n");
                        continue;
                    }
                    selectedModel = newModel;
                    AnsiConsole.WriteLine($"\n🔄 Recreating session with model: {selectedModel}...");
                    await session.DisposeAsync();
                    session = await client.CreateSessionAsync(new SessionConfig
                    {
                        Model = selectedModel,
                        Streaming = true
                    });
                    AnsiConsole.MarkupLine($"[green]✅ New session created! (ID: {session.SessionId})[/]\n");
                    continue;
                }

                if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.WriteLine("\n🔄 Starting new session...");
                    await session.DisposeAsync();
                    session = await client.CreateSessionAsync(new SessionConfig
                    {
                        Model = selectedModel,
                        Streaming = true
                    });
                    AnsiConsole.MarkupLine($"[green]✅ New session created! (ID: {session.SessionId})[/]\n");
                    continue;
                }

                // Handle demo commands
                if (input.StartsWith("demo ", StringComparison.OrdinalIgnoreCase))
                {
                    var demoPrompt = DemoPrompts.GetDemoPrompt(input);
                    if (demoPrompt != null)
                    {
                        AnsiConsole.MarkupLine($"[grey]Running: {Markup.Escape(demoPrompt)}[/]\n");
                        input = demoPrompt;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]Invalid demo number. Use 'demo 1' through 'demo 6'.[/]\n");
                        continue;
                    }
                }

                // Send message and stream response
                await ChatHelper.SendMessageAndStreamResponse(session, input);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]❌ Error: {Markup.Escape(ex.Message)}[/]");
            if (ex.InnerException != null)
            {
                AnsiConsole.MarkupLine($"[red]   Inner: {Markup.Escape(ex.InnerException.Message)}[/]");
            }
        }
        finally
        {
            if (session != null)
            {
                await session.DisposeAsync();
            }
            if (client != null)
            {
                await client.DisposeAsync();
            }
            AnsiConsole.WriteLine("\n🛑 Client stopped.");
        }
    }
}
