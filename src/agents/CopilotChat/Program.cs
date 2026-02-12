// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat;

using CopilotChat.Helpers;
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
            session = await client.CreateSessionAsync(CreateSessionConfig(selectedModel));
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

                var prompt = ResolvePlaceholders(selected.Prompt, settings.Params);
                if (prompt == null)
                {
                    return;
                }

                AnsiConsole.MarkupLine($"[cyan]Question {selected.Number}:[/] {selected.Title}\n");

                var chatResponse = await OrchestrationRunner.RunOrchestratedAsync(
                    client, prompt, selectedModel, settings.RepoPath,
                    modelName: selectedModel, questionSlug: selected.Slug);

                PrintMetrics(chatResponse);
                OutputWriter.SaveQuestionOutput(chatResponse.Text, selectedModel, selected.Slug);
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
                    session = await client.CreateSessionAsync(CreateSessionConfig(selectedModel));
                    AnsiConsole.MarkupLine($"[green]✅ New session created! (ID: {session.SessionId})[/]\n");
                    continue;
                }

                if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.WriteLine("\n🔄 Starting new session...");
                    await session.DisposeAsync();
                    session = await client.CreateSessionAsync(CreateSessionConfig(selectedModel));
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
                            var resolved = ResolvePlaceholders(selected.Prompt, settings.Params);
                            if (resolved == null)
                            {
                                continue;
                            }

                            AnsiConsole.MarkupLine($"[grey]Running question {selected.Number}: {selected.Title}[/]\n");
                            activeQuestion = selected;

                            var orchResponse = await OrchestrationRunner.RunOrchestratedAsync(
                                client, resolved, selectedModel, settings.RepoPath,
                                modelName: selectedModel, questionSlug: activeQuestion.Slug);
                            PrintMetrics(orchResponse);
                            OutputWriter.SaveQuestionOutput(orchResponse.Text, selectedModel, activeQuestion.Slug);
                            continue;
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

                // Send message via multi-agent orchestration
                var chatResponse = await OrchestrationRunner.RunOrchestratedAsync(
                    client, input, selectedModel, settings.RepoPath,
                    modelName: selectedModel);

                PrintMetrics(chatResponse);

                // Save output for question runs
                if (activeQuestion != null)
                {
                    OutputWriter.SaveQuestionOutput(chatResponse.Text, selectedModel, activeQuestion.Slug);
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
    /// Finds all {placeholder} tokens in a prompt and resolves them from the params dictionary.
    /// For any placeholder not found in params, prompts the user interactively.
    /// Returns null if the user cancels any prompt.
    /// </summary>
    /// <param name="prompt">The prompt template.</param>
    /// <param name="parameters">Known parameter values.</param>
    /// <returns>The resolved prompt, or null if cancelled.</returns>
    private static string? ResolvePlaceholders(string prompt, Dictionary<string, string> parameters)
    {
        var result = prompt;
        var placeholders = System.Text.RegularExpressions.Regex.Matches(prompt, @"\{(\w+)\}");

        foreach (System.Text.RegularExpressions.Match match in placeholders)
        {
            var key = match.Groups[1].Value;

            // Skip if already replaced (duplicate placeholder)
            if (!result.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                result = result.Replace(match.Value, value, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            // Prompt interactively
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>($"[yellow]Enter value for '{key}' (or 'cancel'):[/] "));

            if (string.IsNullOrWhiteSpace(input) ||
                input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]\n");
                return null;
            }

            parameters[key] = input;
            result = result.Replace(match.Value, input, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Prints elapsed time and token usage metrics for a chat response.
    /// </summary>
    /// <param name="response">The chat response containing metrics.</param>
    private static void PrintMetrics(ChatResponse response)
    {
        var elapsed = $"{response.Elapsed.TotalSeconds:F1}s";
        var firstToken = response.TimeToFirstToken.HasValue
            ? $" (first token: {response.TimeToFirstToken.Value.TotalSeconds:F1}s)"
            : string.Empty;
        var tokens = $"Tokens: {response.InputTokens:N0} in → {response.OutputTokens:N0} out";

        var cache = string.Empty;
        if (response.CacheReadTokens > 0 || response.CacheWriteTokens > 0)
        {
            var parts = new List<string>();
            if (response.CacheReadTokens > 0)
            {
                parts.Add($"{response.CacheReadTokens:N0} read");
            }

            if (response.CacheWriteTokens > 0)
            {
                parts.Add($"{response.CacheWriteTokens:N0} write");
            }

            cache = $" (cache: {string.Join(", ", parts)})";
        }

        AnsiConsole.MarkupLine($"[grey]⏱ {elapsed}{firstToken} | {tokens}{cache}[/]");
    }

    /// <summary>
    /// Creates a <see cref="SessionConfig"/> with the anti-narration system prompt
    /// to ensure models output only the final analysis, not tool-call narration.
    /// </summary>
    private static SessionConfig CreateSessionConfig(string model)
    {
        return new SessionConfig
        {
            Model = model,
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = """
                    CRITICAL OUTPUT RULES:
                    - Do NOT narrate your actions. Never say "Let me read...", "Now I'll check...",
                      "Let me look at...", or similar tool-call narration.
                    - When you use tools to read files, do NOT describe what you are doing.
                    - Output ONLY your final, comprehensive analysis as a single well-structured document.
                    - If the prompt asks for exhaustive analysis, be thorough: include actual code snippets
                      with file paths and line numbers, trace every code path, and document every detail.
                    - Structure your output with a table of contents, numbered sections, and code blocks.
                    - Your response should read as a polished technical document, not a stream of consciousness.
                    """
            }
        };
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

                if (opts.Params != null)
                {
                    foreach (var param in opts.Params)
                    {
                        var eqIndex = param.IndexOf('=');
                        if (eqIndex > 0)
                        {
                            var key = param[..eqIndex].Trim();
                            var val = param[(eqIndex + 1)..].Trim();
                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                            {
                                settings.Params[key] = val;
                            }
                        }
                    }
                }

                if (opts.ListModels)
                {
                    settings.ListModels = true;
                }
            });

        return settings;
    }
}