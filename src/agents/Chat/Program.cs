// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Chat;

using Chat.Helpers;
using CommandLine;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
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
        // ============================================================
        // Load configuration: appsettings.json → env vars → CLI args
        // ============================================================
        var settings = LoadSettings(args);

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
        // List models and exit if requested
        // ============================================================
        if (settings.ListModels)
        {
            await ModelSelector.PrintModelsAsync();
            return;
        }

        // ============================================================
        // Step 2: Model Selection (use config or interactive)
        // ============================================================
        string? selectedModel;
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            selectedModel = settings.Model;
            AnsiConsole.MarkupLine($"[green]✅ Using configured model: {selectedModel}[/]");
        }
        else
        {
            selectedModel = await ModelSelector.SelectModelAsync();
            if (selectedModel == null)
            {
                AnsiConsole.MarkupLine("[yellow]Press any key to exit...[/]");
                Console.ReadKey(true);
                return;
            }
        }

        AnsiConsole.WriteLine();

        // Show repo context if configured
        if (!string.IsNullOrWhiteSpace(settings.RepoPath))
        {
            var fullPath = Path.GetFullPath(settings.RepoPath);
            if (Directory.Exists(fullPath))
            {
                AnsiConsole.MarkupLine($"[green]📂 Repository context: {Markup.Escape(fullPath)}[/]\n");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Repository path not found: {Markup.Escape(fullPath)}[/]\n");
            }
        }

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
                Streaming = true
            });
            AnsiConsole.MarkupLine($"[green]✅ Session created! (ID: {session.SessionId})[/]\n");

            // Load questions from Questions folder
            var questions = QuestionLoader.LoadQuestions();

            // ============================================================
            // Non-interactive mode: run a specific question by number
            // ============================================================
            if (settings.Question.HasValue)
            {
                var selected = QuestionLoader.GetByNumber(questions, settings.Question.Value);
                if (selected == null)
                {
                    AnsiConsole.MarkupLine($"[red]❌ Question {settings.Question.Value} not found.[/]");
                    if (questions.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Available: {string.Join(", ", questions.Select(q => q.Number))}[/]");
                    }
                    return;
                }

                var prompt = ResolveFeaturePlaceholder(selected.Prompt, settings.Feature);
                if (prompt == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(settings.RepoPath))
                {
                    prompt = $"[Repository: {Path.GetFullPath(settings.RepoPath)}]\n\n{prompt}";
                }

                AnsiConsole.MarkupLine($"[cyan]Question {selected.Number}:[/] {selected.Title}\n");
                var response = await ChatHelper.SendMessageAndStreamResponse(session, prompt);
                OutputWriter.SaveQuestionOutput(response, selectedModel, selected.Slug);
                return;
            }

            // Show demo prompts and repo questions
            DemoPrompts.ShowDemoPrompts();
            QuestionLoader.ShowQuestions(questions);

            // ============================================================
            // Step 4: Interactive Chat Loop
            // ============================================================
            AnsiConsole.MarkupLine("[yellow]💬 Interactive Chat Mode[/]");
            AnsiConsole.WriteLine("   Type your message and press Enter to send.");
            AnsiConsole.WriteLine("   Type 'exit' or 'quit' to end the session.");
            AnsiConsole.WriteLine("   Type 'demo <number>' to run a demo prompt (e.g., 'demo 1')");
            AnsiConsole.WriteLine("   Type 'question <number>' to run a repo question (e.g., 'question 1')");
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

                // Handle question commands
                QuestionLoader.Question? activeQuestion = null;
                if (input.StartsWith("question ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = input.Split(' ', 2);
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var qNum))
                    {
                        var selected = QuestionLoader.GetByNumber(questions, qNum);
                        if (selected != null)
                        {
                            var resolved = ResolveFeaturePlaceholder(selected.Prompt, settings.Feature);
                            if (resolved == null)
                            {
                                continue;
                            }

                            AnsiConsole.MarkupLine($"[grey]Running question {selected.Number}: {selected.Title}[/]\n");
                            activeQuestion = selected;
                            input = resolved;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[red]Question {qNum} not found.[/]");
                            if (questions.Count > 0)
                            {
                                AnsiConsole.MarkupLine($"[yellow]Available: {string.Join(", ", questions.Select(q => q.Number))}[/]\n");
                            }
                            continue;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]Invalid question number. Use 'question 1', 'question 2', etc.[/]\n");
                        continue;
                    }
                }

                // Prepend repo context if configured
                var message = input;
                if (!string.IsNullOrWhiteSpace(settings.RepoPath))
                {
                    message = $"[Repository: {Path.GetFullPath(settings.RepoPath)}]\n\n{input}";
                }

                // Send message and stream response
                var responseText = await ChatHelper.SendMessageAndStreamResponse(session, message);

                // Save output for question runs
                if (activeQuestion != null)
                {
                    OutputWriter.SaveQuestionOutput(responseText, selectedModel, activeQuestion.Slug);
                }
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

    /// <summary>
    /// Replaces {feature} placeholders in a prompt. If the prompt contains {feature}
    /// and no feature name is available, prompts the user interactively.
    /// Returns null if the user cancels.
    /// </summary>
    /// <param name="prompt">The prompt template.</param>
    /// <param name="feature">The feature name from settings, or null.</param>
    /// <returns>The resolved prompt, or null if cancelled.</returns>
    private static string? ResolveFeaturePlaceholder(string prompt, string? feature)
    {
        if (!prompt.Contains("{feature}", StringComparison.OrdinalIgnoreCase))
        {
            return prompt;
        }

        if (string.IsNullOrWhiteSpace(feature))
        {
            feature = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter feature name (or 'cancel'):[/] "));

            if (string.IsNullOrWhiteSpace(feature) ||
                feature.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]\n");
                return null;
            }
        }

        return prompt.Replace("{feature}", feature, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads settings from appsettings.json, environment variables, and command-line arguments.
    /// Priority (highest wins): CLI args → environment variables → appsettings.json.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Merged <see cref="ChatSettings"/>.</returns>
    private static ChatSettings LoadSettings(string[] args)
    {
        // Build configuration from appsettings.json + environment variables
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CHAT_")
            .Build();

        var settings = new ChatSettings();
        configuration.GetSection("Chat").Bind(settings);

        // Parse command-line arguments and overlay onto settings
        Parser.Default.ParseArguments<CommandLineOptions>(args)
            .WithParsed(opts =>
            {
                if (!string.IsNullOrWhiteSpace(opts.Model))
                {
                    settings.Model = opts.Model;
                }

                if (opts.Question.HasValue)
                {
                    settings.Question = opts.Question;
                }

                if (!string.IsNullOrWhiteSpace(opts.RepoPath))
                {
                    settings.RepoPath = opts.RepoPath;
                }

                if (!string.IsNullOrWhiteSpace(opts.Feature))
                {
                    settings.Feature = opts.Feature;
                }

                if (opts.ListModels)
                {
                    settings.ListModels = true;
                }
            });

        return settings;
    }
}