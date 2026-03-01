// -----------------------------------------------------------------------
// <copyright file="OrchestrationRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat.Helpers;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentOrchestrator;
using GitHub.Copilot.SDK;
using Spectre.Console;

/// <summary>
/// Runs prompts using multi-agent orchestration. Decomposes a prompt into
/// parallel subagents via a planner LLM call, executes them using DAG-based
/// scheduling, and reports detailed metrics per agent.
/// </summary>
public static class OrchestrationRunner
{
    /// <summary>
    /// Decomposes a prompt into subagents, executes them via the DAG orchestrator,
    /// and returns the aggregated result. Writes an orchestration plan file to the
    /// output directory if <paramref name="modelName"/> and <paramref name="questionSlug"/>
    /// are provided.
    /// </summary>
    /// <param name="client">The Copilot client.</param>
    /// <param name="prompt">The user's prompt to decompose.</param>
    /// <param name="model">The model to use for subagents.</param>
    /// <param name="repoPath">Optional repository path for context.</param>
    /// <param name="modelName">Model name for output directory (e.g., "claude-opus-4.6").</param>
    /// <param name="questionSlug">Question slug for output directory (e.g., "03-feature-details").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated response text.</returns>
    public static async Task<ChatResponse> RunOrchestratedAsync(
        CopilotClient client,
        string prompt,
        string model,
        string? repoPath = null,
        string? modelName = null,
        string? questionSlug = null,
        CancellationToken cancellationToken = default)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var fullRepoPath = !string.IsNullOrWhiteSpace(repoPath) ? Path.GetFullPath(repoPath) : null;

        // ── Phase 1: File Discovery (programmatic, no LLM) ──────────
        List<string> discoveredFiles = [];
        var featureKeyword = ExtractFeatureKeyword(prompt);
        if (fullRepoPath != null && featureKeyword != null)
        {
            AnsiConsole.MarkupLine($"[yellow]🔍 Phase 1: Discovering files for feature \"{Markup.Escape(featureKeyword)}\"...[/]");
            discoveredFiles = DiscoverRepoFiles(fullRepoPath, featureKeyword);
        }

        // ── Phase 2: Build agents ─────────────────────────────────────
        List<SubAgentConfig> agents;
        if (IsStructuredQuestion(prompt) && discoveredFiles.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]📋 Building section agents from structured question...[/]");
            agents = BuildSectionAgents(prompt, model, discoveredFiles, fullRepoPath!);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]🧠 Planning — decomposing prompt into subagents...[/]");
            agents = await PlanAgentsAsync(client, prompt, model, cancellationToken);

            if (agents.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]❌ Planner returned no agents. Falling back to single-agent mode.[/]");
                agents =
                [
                    new SubAgentConfig
                    {
                        Id = "single-agent",
                        AgentType = AgentType.GeneralPurpose,
                        Model = model,
                        Prompt = prompt
                    }
                ];
            }

            // Inject discovered files + working directory into planner agents
            if (fullRepoPath != null)
            {
                agents = agents.Select(a => new SubAgentConfig
                {
                    Id = a.Id,
                    Description = a.Description,
                    AgentType = a.AgentType,
                    Model = a.Model,
                    SystemPrompt = a.SystemPrompt,
                    Prompt = a.Prompt,
                    FileAttachments = discoveredFiles.Count > 0 ? discoveredFiles : a.FileAttachments,
                    Timeout = a.Timeout,
                    MaxRetries = a.MaxRetries,
                    WorkingDirectory = a.WorkingDirectory ?? fullRepoPath,
                    Tools = a.Tools,
                    DependsOn = a.DependsOn
                }).ToList();
            }
        }

        // ── Display the orchestration plan ───────────────────────────
        DisplayPlan(agents);

        // ── Write the plan file ──────────────────────────────────────
        var planFilePath = WritePlanFile(agents, model, prompt, modelName, questionSlug);

        // ── Phase 3: Execute via DAG orchestrator ────────────────────
        AnsiConsole.MarkupLine("\n[yellow]⚡ Phase 3: Executing subagents...[/]\n");

        await using var orchestrator = client.CreateOrchestrator(new OrchestratorConfig
        {
            MaxConcurrency = 7,
            DefaultTimeout = TimeSpan.FromMinutes(30),
            SynthesisModel = model,
            ContinueOnFailure = true
        });

        // Start background progress polling
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressTask = PollProgressAsync(orchestrator, progressCts.Token);

        var result = await orchestrator.RunAsync(
            agents,
            AggregationStrategy.Concatenate,
            cancellationToken: cancellationToken);

        // Stop progress polling
        await progressCts.CancelAsync();
        try { await progressTask; } catch (OperationCanceledException) { }

        // ── Step 4: Display execution report ─────────────────────────
        DisplayExecutionReport(result);

        // ── Step 4b: Update plan file with execution results ─────────
        UpdatePlanFileWithResults(planFilePath, result, totalStopwatch.Elapsed);

        // ── Post-processing: build document + distill ─────────────────
        string fullDocument;
        var distillInputTokens = 0;
        var distillOutputTokens = 0;

        var synthesisResult = result.AgentResults.FirstOrDefault(r => r.AgentId == "synthesis");
        var distillResult = result.AgentResults.FirstOrDefault(r => r.AgentId == "distill");

        if (synthesisResult is { Success: true, Content: not null })
        {
            // Structured path: synthesis + distill ran as DAG agents (tokens already counted)
            fullDocument = synthesisResult.Content;

            if (distillResult is { Success: true, Content: not null })
            {
                var kgMatch = Regex.Match(
                    distillResult.Content,
                    @"##\s+Knowledge Graph\s*\n([\s\S]*?)(?=\n##\s+Context Document)",
                    RegexOptions.IgnoreCase);
                var ctxMatch = Regex.Match(
                    distillResult.Content,
                    @"##\s+Context Document\s*\n([\s\S]*?)$",
                    RegexOptions.IgnoreCase);

                var knowledgeGraph = kgMatch.Success ? kgMatch.Groups[1].Value.Trim() : "";
                var contextDoc = ctxMatch.Success ? ctxMatch.Groups[1].Value.Trim() : "";

                if (!string.IsNullOrEmpty(knowledgeGraph))
                {
                    fullDocument += $"\n## Knowledge Graph\n\n{knowledgeGraph}\n";
                }

                SaveContextDoc(contextDoc, modelName, questionSlug);
            }
        }
        else
        {
            // Freeform path: build document from agent results + distill separately
            fullDocument = BuildFullDocument(result.AgentResults);

            AnsiConsole.MarkupLine("\n[yellow]📝 Phase 4: Distilling context document...[/]");
            var (knowledgeGraph, contextDoc, dIn, dOut) =
                await DistillContextAsync(client, fullDocument, model, cancellationToken);

            if (!string.IsNullOrWhiteSpace(knowledgeGraph))
            {
                fullDocument += $"\n## Knowledge Graph\n\n{knowledgeGraph}\n";
            }

            SaveContextDoc(contextDoc, modelName, questionSlug);
            distillInputTokens = dIn;
            distillOutputTokens = dOut;
        }

        totalStopwatch.Stop();

        var totalInputTokens = result.AgentResults.Sum(r => r.InputTokens) + distillInputTokens;
        var totalOutputTokens = result.AgentResults.Sum(r => r.OutputTokens) + distillOutputTokens;
        var totalCacheRead = result.AgentResults.Sum(r => r.CacheReadTokens);
        var totalCacheWrite = result.AgentResults.Sum(r => r.CacheWriteTokens);

        return new ChatResponse(
            fullDocument,
            totalStopwatch.Elapsed,
            null,
            totalInputTokens,
            totalOutputTokens,
            totalCacheRead,
            totalCacheWrite);
    }

    /// <summary>
    /// Uses a fast planner LLM to decompose the prompt into subagent tasks.
    /// Returns a list of <see cref="SubAgentConfig"/> with dependency info.
    /// </summary>
    private static async Task<List<SubAgentConfig>> PlanAgentsAsync(
        CopilotClient client,
        string prompt,
        string model,
        CancellationToken cancellationToken)
    {
        var plannerPrompt = $$"""
            You are an orchestration planner. Given a user prompt, decompose it into
            independent subtasks that can be executed by parallel subagents.

            Rules:
            - Each subtask should be self-contained with a clear, specific prompt
            - Identify which subtasks can run in parallel (no dependencies)
            - Identify which subtasks depend on others (use dependsOn field)
            - Use agentType "Explore" for information gathering, "GeneralPurpose" for deep analysis
            - Do NOT include a synthesis or summary agent — post-processing handles that automatically
            - Return ONLY valid JSON, no markdown fences, no explanation

            Return JSON in this exact format:
            {
              "agents": [
                {
                  "id": "short-kebab-id",
                  "description": "One-line description",
                  "agentType": "Explore",
                  "prompt": "The specific task prompt for this agent",
                  "dependsOn": []
                }
              ]
            }

            User prompt to decompose:
            ---
            {{prompt}}
            ---
            """;

        CopilotSession? plannerSession = null;
        try
        {
            plannerSession = await client.CreateSessionAsync(new SessionConfig
            {
                Model = AgentDefaults.FastModel
            }, cancellationToken);

            var response = await plannerSession.SendAndWaitAsync(
                new MessageOptions { Prompt = plannerPrompt },
                TimeSpan.FromMinutes(2),
                cancellationToken);

            var json = response?.Data?.Content;
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            // Strip markdown code fences if present
            json = StripCodeFences(json);

            return ParsePlannerResponse(json, model);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Planner failed: {Markup.Escape(ex.Message)}. Using fallback.[/]");
            return [];
        }
        finally
        {
            if (plannerSession != null)
            {
                await plannerSession.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Parses the planner's JSON response into SubAgentConfig list.
    /// </summary>
    private static List<SubAgentConfig> ParsePlannerResponse(string json, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var agentsArray = doc.RootElement.GetProperty("agents");
            var agents = new List<SubAgentConfig>();

            foreach (var agentEl in agentsArray.EnumerateArray())
            {
                var id = agentEl.GetProperty("id").GetString() ?? "unknown";
                var description = agentEl.TryGetProperty("description", out var descEl)
                    ? descEl.GetString()
                    : null;
                var agentTypeStr = agentEl.TryGetProperty("agentType", out var typeEl)
                    ? typeEl.GetString()
                    : "Explore";
                var prompt = agentEl.GetProperty("prompt").GetString() ?? "";
                var dependsOn = new List<string>();

                if (agentEl.TryGetProperty("dependsOn", out var depsEl))
                {
                    foreach (var dep in depsEl.EnumerateArray())
                    {
                        var depStr = dep.GetString();
                        if (!string.IsNullOrEmpty(depStr))
                        {
                            dependsOn.Add(depStr);
                        }
                    }
                }

                var agentType = agentTypeStr switch
                {
                    "Explore" => AgentType.Explore,
                    "Task" => AgentType.Task,
                    "CodeReview" => AgentType.CodeReview,
                    "GeneralPurpose" => AgentType.GeneralPurpose,
                    _ => AgentType.Explore
                };

                agents.Add(new SubAgentConfig
                {
                    Id = id,
                    Description = description,
                    AgentType = agentType,
                    Model = agentType == AgentType.GeneralPurpose ? model : null,
                    Prompt = prompt,
                    DependsOn = dependsOn.Count > 0 ? dependsOn : null
                });
            }

            return agents;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Failed to parse planner JSON: {Markup.Escape(ex.Message)}[/]");
            return [];
        }
    }

    /// <summary>
    /// Displays the orchestration plan as a table with wave groupings.
    /// </summary>
    private static void DisplayPlan(List<SubAgentConfig> agents)
    {
        List<List<SubAgentConfig>> waves;
        try
        {
            waves = AgentOrchestrator.TopologicalSort(agents);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]❌ {Markup.Escape(ex.Message)}[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]📋 Orchestration Plan[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Wave[/]").Centered())
            .AddColumn(new TableColumn("[bold]Agent ID[/]"))
            .AddColumn(new TableColumn("[bold]Type[/]"))
            .AddColumn(new TableColumn("[bold]Model[/]"))
            .AddColumn(new TableColumn("[bold]Depends On[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        for (var w = 0; w < waves.Count; w++)
        {
            var isParallel = waves[w].Count > 1;
            var waveLabel = isParallel ? $"[cyan]W{w + 1} ║║[/]" : $"[green]W{w + 1}[/]";

            foreach (var agent in waves[w])
            {
                var agentModel = agent.Model ?? AgentDefaults.GetDefaultModel(agent.AgentType);
                var deps = agent.DependsOn is { Count: > 0 }
                    ? string.Join(", ", agent.DependsOn)
                    : "[grey]-[/]";
                var desc = agent.Description ?? "[grey]-[/]";

                table.AddRow(
                    waveLabel,
                    $"[bold]{Markup.Escape(agent.Id)}[/]",
                    agent.AgentType.ToString(),
                    Markup.Escape(agentModel),
                    deps,
                    Markup.Escape(desc));
            }

            if (w < waves.Count - 1)
            {
                table.AddEmptyRow();
            }
        }

        AnsiConsole.Write(table);

        var totalAgents = agents.Count;
        var parallelAgents = waves.Where(w => w.Count > 1).Sum(w => w.Count);
        AnsiConsole.MarkupLine($"\n[grey]  {totalAgents} agents in {waves.Count} waves ({parallelAgents} parallel, {totalAgents - parallelAgents} sequential)[/]");
    }

    /// <summary>
    /// Displays a detailed execution report with per-agent timing and token metrics.
    /// </summary>
    private static void DisplayExecutionReport(OrchestratedResult result)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]📊 Execution Report[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Status[/]").Centered())
            .AddColumn(new TableColumn("[bold]Agent[/]"))
            .AddColumn(new TableColumn("[bold]Model[/]"))
            .AddColumn(new TableColumn("[bold]Started[/]"))
            .AddColumn(new TableColumn("[bold]Completed[/]"))
            .AddColumn(new TableColumn("[bold]Duration[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]In Tokens[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Out Tokens[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Cache R/W[/]").RightAligned());

        foreach (var r in result.AgentResults)
        {
            var status = r.Success ? "[green]✓[/]" : "[red]✗[/]";
            var started = r.StartedAtUtc != default
                ? r.StartedAtUtc.ToString("HH:mm:ss.fff")
                : "-";
            var completed = r.CompletedAtUtc != default
                ? r.CompletedAtUtc.ToString("HH:mm:ss.fff")
                : "-";
            var duration = $"{r.Duration.TotalSeconds:F1}s";
            var cacheStr = r.CacheReadTokens > 0 || r.CacheWriteTokens > 0
                ? $"{r.CacheReadTokens:N0}/{r.CacheWriteTokens:N0}"
                : "[grey]-[/]";

            table.AddRow(
                status,
                Markup.Escape(r.AgentId),
                Markup.Escape(r.ModelUsed ?? "-"),
                started,
                completed,
                duration,
                $"{r.InputTokens:N0}",
                $"{r.OutputTokens:N0}",
                cacheStr);
        }

        AnsiConsole.Write(table);

        // Summary row
        var totalIn = result.AgentResults.Sum(r => r.InputTokens);
        var totalOut = result.AgentResults.Sum(r => r.OutputTokens);
        var totalCacheR = result.AgentResults.Sum(r => r.CacheReadTokens);
        var totalCacheW = result.AgentResults.Sum(r => r.CacheWriteTokens);

        AnsiConsole.MarkupLine($"\n[grey]  Total: {result.SuccessCount} succeeded, {result.FailureCount} failed | " +
                               $"Wall clock: {result.TotalDuration.TotalSeconds:F1}s | " +
                               $"Tokens: {totalIn:N0} in → {totalOut:N0} out " +
                               $"(cache: {totalCacheR:N0}R/{totalCacheW:N0}W)[/]");
    }

    /// <summary>
    /// Polls the orchestrator's live progress every 60 seconds and displays a status table.
    /// Runs as a background task; cancelled when orchestration completes.
    /// </summary>
    private static async Task PollProgressAsync(
        AgentOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                DisplayProgress(orchestrator);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when orchestration completes
        }
    }

    /// <summary>
    /// Displays current progress of all tracked subagents as a Spectre.Console table.
    /// </summary>
    private static void DisplayProgress(AgentOrchestrator orchestrator)
    {
        var progress = orchestrator.LiveProgress;
        if (progress.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]📡 Progress Update[/]").LeftJustified());

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Status[/]").Centered())
            .AddColumn(new TableColumn("[bold]Agent[/]"))
            .AddColumn(new TableColumn("[bold]Model[/]"))
            .AddColumn(new TableColumn("[bold]Elapsed[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]In Tokens[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Out Tokens[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Cache R/W[/]").RightAligned());

        foreach (var kvp in progress.OrderBy(p => p.Value.StartedAtUtc))
        {
            var p = kvp.Value;
            var statusIcon = p.Status switch
            {
                "running" => "[yellow]⏳[/]",
                "completed" => "[green]✓[/]",
                "failed" => "[red]✗[/]",
                "timeout" => "[red]⏰[/]",
                _ => "[grey]·[/]"
            };

            var elapsed = p.StartedAtUtc != default
                ? (now - p.StartedAtUtc).TotalSeconds
                : 0;
            var elapsedStr = p.Status == "running"
                ? $"{elapsed:F0}s"
                : $"[grey]{elapsed:F0}s[/]";

            var cacheStr = p.CacheReadTokens > 0 || p.CacheWriteTokens > 0
                ? $"{p.CacheReadTokens:N0}/{p.CacheWriteTokens:N0}"
                : "[grey]-[/]";

            table.AddRow(
                statusIcon,
                Markup.Escape(p.AgentId),
                Markup.Escape(p.ModelUsed ?? "-"),
                elapsedStr,
                $"{p.InputTokens:N0}",
                $"{p.OutputTokens:N0}",
                cacheStr);
        }

        AnsiConsole.Write(table);

        var running = progress.Values.Count(p => p.Status == "running");
        var completed = progress.Values.Count(p => p.Status == "completed");
        var failed = progress.Values.Count(p => p.Status is "failed" or "timeout");
        AnsiConsole.MarkupLine($"[grey]  {running} running, {completed} completed, {failed} failed[/]");
    }

    /// <summary>
    /// Strips markdown code fences (```json ... ```) from the response.
    /// </summary>
    private static string StripCodeFences(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline > 0)
            {
                text = text[(firstNewline + 1)..];
            }
        }

        if (text.EndsWith("```"))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    /// <summary>
    /// Writes the orchestration plan to a markdown file in the output directory.
    /// Includes wave groupings, subagent prompts, dependencies, and a status column
    /// that gets updated after execution.
    /// </summary>
    /// <returns>The path to the plan file, or null if not written.</returns>
    private static string? WritePlanFile(
        List<SubAgentConfig> agents,
        string model,
        string originalPrompt,
        string? modelName,
        string? questionSlug)
    {
        var safeModel = modelName ?? model;
        // Sanitize model name for path
        var invalidChars = Path.GetInvalidFileNameChars();
        safeModel = new string(safeModel.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

        var outputDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            "PromptOutput",
            safeModel,
            questionSlug ?? "orchestrated");
        Directory.CreateDirectory(outputDir);

        var planPath = Path.Combine(outputDir, "orchestration-plan.md");

        List<List<SubAgentConfig>> waves;
        try
        {
            waves = AgentOrchestrator.TopologicalSort(agents);
        }
        catch
        {
            waves = [agents];
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Orchestration Plan");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Model:** {model}");
        sb.AppendLine($"**Total Agents:** {agents.Count}");
        sb.AppendLine($"**Waves:** {waves.Count}");
        sb.AppendLine();

        // Original prompt
        sb.AppendLine("## Original Prompt");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(originalPrompt.Length > 2000 ? originalPrompt[..2000] + "\n... (truncated)" : originalPrompt);
        sb.AppendLine("```");
        sb.AppendLine();

        // DAG visualization
        sb.AppendLine("## Execution DAG");
        sb.AppendLine();
        for (var w = 0; w < waves.Count; w++)
        {
            var isParallel = waves[w].Count > 1;
            var mode = isParallel ? "parallel" : "sequential";
            sb.AppendLine($"### Wave {w + 1} ({mode})");
            sb.AppendLine();
            foreach (var agent in waves[w])
            {
                var deps = agent.DependsOn is { Count: > 0 }
                    ? string.Join(", ", agent.DependsOn)
                    : "none";
                sb.AppendLine($"- **{agent.Id}** ({agent.AgentType}) — depends on: {deps}");
            }

            sb.AppendLine();
        }

        // Per-agent details with prompts
        sb.AppendLine("## Subagent Details");
        sb.AppendLine();
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            var agentModel = agent.Model ?? AgentDefaults.GetDefaultModel(agent.AgentType);
            var deps = agent.DependsOn is { Count: > 0 }
                ? string.Join(", ", agent.DependsOn)
                : "none";

            sb.AppendLine($"### {i + 1}. {agent.Id}");
            sb.AppendLine();
            sb.AppendLine($"| Property | Value |");
            sb.AppendLine($"|----------|-------|");
            sb.AppendLine($"| **Type** | {agent.AgentType} |");
            sb.AppendLine($"| **Model** | {agentModel} |");
            sb.AppendLine($"| **Depends On** | {deps} |");
            sb.AppendLine($"| **Description** | {agent.Description ?? "-"} |");
            sb.AppendLine($"| **Status** | ⏳ Pending |");
            sb.AppendLine();
            sb.AppendLine("**Prompt:**");
            sb.AppendLine();
            sb.AppendLine("```");
            var promptText = agent.Prompt;
            if (promptText.Length > 3000)
            {
                promptText = promptText[..3000] + "\n... (truncated)";
            }

            sb.AppendLine(promptText);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Execution results placeholder
        sb.AppendLine("## Execution Results");
        sb.AppendLine();
        sb.AppendLine("_Execution not yet started._");
        sb.AppendLine();

        File.WriteAllText(planPath, sb.ToString());
        AnsiConsole.MarkupLine($"[green]📋 Plan written to:[/] {Markup.Escape(planPath)}");

        return planPath;
    }

    /// <summary>
    /// Updates the plan file with execution results: per-agent status, timestamps,
    /// duration, token counts, and a summary table.
    /// </summary>
    private static void UpdatePlanFileWithResults(
        string? planFilePath,
        OrchestratedResult result,
        TimeSpan totalDuration)
    {
        if (planFilePath == null || !File.Exists(planFilePath))
        {
            return;
        }

        var content = File.ReadAllText(planFilePath);

        // Update each agent's status in the Details section
        foreach (var r in result.AgentResults)
        {
            var status = r.Success ? "✅ Completed" : "❌ Failed";
            var oldStatus = $"| **Status** | ⏳ Pending |";

            // Find the status line within this agent's section
            var agentHeaderPattern = $"### ";
            var sections = content.Split(agentHeaderPattern);
            var updatedSections = new StringBuilder();
            updatedSections.Append(sections[0]);

            for (var i = 1; i < sections.Length; i++)
            {
                updatedSections.Append(agentHeaderPattern);
                var section = sections[i];

                // Check if this section is for the current agent
                if (section.Contains($". {r.AgentId}\n") || section.StartsWith($"{r.AgentId}\n"))
                {
                    // Only replace first pending status in this section
                    var idx = section.IndexOf(oldStatus, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var started = r.StartedAtUtc != default
                            ? r.StartedAtUtc.ToString("HH:mm:ss.fff")
                            : "-";
                        var completed = r.CompletedAtUtc != default
                            ? r.CompletedAtUtc.ToString("HH:mm:ss.fff")
                            : "-";

                        var newStatus = $"| **Status** | {status} |";
                        var timing = $"\n| **Started** | {started} UTC |";
                        timing += $"\n| **Completed** | {completed} UTC |";
                        timing += $"\n| **Duration** | {r.Duration.TotalSeconds:F1}s |";
                        timing += $"\n| **Input Tokens** | {r.InputTokens:N0} |";
                        timing += $"\n| **Output Tokens** | {r.OutputTokens:N0} |";
                        if (r.CacheReadTokens > 0 || r.CacheWriteTokens > 0)
                        {
                            timing += $"\n| **Cache Tokens** | {r.CacheReadTokens:N0} read / {r.CacheWriteTokens:N0} write |";
                        }

                        if (!r.Success && !string.IsNullOrEmpty(r.Error))
                        {
                            timing += $"\n| **Error** | {r.Error} |";
                        }

                        section = section.Remove(idx, oldStatus.Length)
                            .Insert(idx, newStatus + timing);
                    }
                }

                updatedSections.Append(section);
            }

            content = updatedSections.ToString();
        }

        // Replace the "Execution Results" placeholder with the summary table
        var placeholder = "_Execution not yet started._";
        if (content.Contains(placeholder))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**Completed:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"**Total Wall Clock:** {totalDuration.TotalSeconds:F1}s");
            sb.AppendLine($"**Succeeded:** {result.SuccessCount} / {result.AgentResults.Count}");
            sb.AppendLine();

            sb.AppendLine("| Status | Agent | Model | Duration | In Tokens | Out Tokens |");
            sb.AppendLine("|--------|-------|-------|----------|-----------|------------|");

            foreach (var r in result.AgentResults)
            {
                var statusIcon = r.Success ? "✅" : "❌";
                sb.AppendLine($"| {statusIcon} | {r.AgentId} | {r.ModelUsed ?? "-"} | {r.Duration.TotalSeconds:F1}s | {r.InputTokens:N0} | {r.OutputTokens:N0} |");
            }

            sb.AppendLine();
            var totalIn = result.AgentResults.Sum(r => r.InputTokens);
            var totalOut = result.AgentResults.Sum(r => r.OutputTokens);
            sb.AppendLine($"**Total Tokens:** {totalIn:N0} in → {totalOut:N0} out");

            content = content.Replace(placeholder, sb.ToString());
        }

        File.WriteAllText(planFilePath, content);
        AnsiConsole.MarkupLine($"[green]📋 Plan updated with results:[/] {Markup.Escape(planFilePath)}");
    }

    /// <summary>
    /// Builds a full markdown document from agent results with a table of contents.
    /// Concatenates all successful agent outputs and prepends a TOC extracted from ## headers.
    /// </summary>
    private static string BuildFullDocument(IReadOnlyList<AgentResult> results)
    {
        var sections = new StringBuilder();
        foreach (var r in results.Where(r => r.Success && !string.IsNullOrEmpty(r.Content)))
        {
            sections.AppendLine(r.Content!.Trim());
            sections.AppendLine();
        }

        var allContent = sections.ToString();

        // Extract ## headers for TOC
        var headers = Regex.Matches(allContent, @"^##\s+(.+)$", RegexOptions.Multiline);
        if (headers.Count == 0)
        {
            return allContent;
        }

        var toc = new StringBuilder();
        toc.AppendLine("## Table of Contents");
        toc.AppendLine();
        for (var i = 0; i < headers.Count; i++)
        {
            toc.AppendLine($"{i + 1}. {headers[i].Groups[1].Value}");
        }

        toc.AppendLine();

        return toc + allContent;
    }

    /// <summary>
    /// Runs a distillation agent that produces a knowledge graph (Mermaid) and a concise
    /// context document from the full analysis output. The context document is designed
    /// to be consumed as system prompt context by downstream PR or implementation agents.
    /// </summary>
    private static async Task<(string KnowledgeGraph, string ContextDoc, int InputTokens, int OutputTokens)>
        DistillContextAsync(
            CopilotClient client,
            string fullDocument,
            string model,
            CancellationToken cancellationToken)
    {
        // Truncate if extremely large to stay within context window
        const int maxChars = 200_000;
        var docForPrompt = fullDocument.Length > maxChars
            ? fullDocument[..maxChars] + "\n\n... (truncated)"
            : fullDocument;

        var distillPrompt = $$"""
            You are given a comprehensive feature analysis document. Produce exactly two sections
            with the headers shown below. Output ONLY these two sections, no preamble.

            ## Knowledge Graph

            Generate a Mermaid diagram capturing every component discovered in the analysis:
            - Nodes: classes, interfaces, factories, maps/registries, serializers, converters,
              config classes, endpoints, test classes, external dependencies
            - Edges: implements, creates, resolves-from, populates, reads-from, serializes-with,
              persists-to, calls, tests, configures, publishes, subscribes, retries, falls-back-to,
              locks, observes
            - Subgraphs: group nodes by layer (Registration, Serialization, Storage, Runtime,
              Resilience, Concurrency, Messaging, Observability, Testing)

            Follow the diagram with a prose summary of the end-to-end feature lifecycle.

            ## Context Document

            Produce a concise, self-contained reference document (under 4000 words) that a developer
            or AI agent could use as system-prompt context to understand and safely modify this feature.
            Include:
            - Architecture overview and end-to-end lifecycle
            - Key components and relationships (DI registrations, factories, configuration)
            - Non-obvious wiring (factory maps, custom serializer resolution, multi-layer caching)
            - Critical invariants and constraints that must be preserved
            - Where to add new functionality and what registrations must be updated
            - Concurrency and thread-safety requirements
            - Error handling and resilience patterns
            - Test coverage and known gaps

            Use specific class names, method names, and file paths. Write it as a reference document,
            not a narrative summary.

            ---
            ANALYSIS DOCUMENT:
            {{docForPrompt}}
            """;

        CopilotSession? session = null;
        IDisposable? subscription = null;
        var inputTokens = 0;
        var outputTokens = 0;
        var responseBuilder = new StringBuilder();
        var done = new TaskCompletionSource();

        try
        {
            session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = model
            }, cancellationToken);

            subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        responseBuilder.Append(delta.Data.DeltaContent);
                        break;

                    case AssistantMessageEvent msg:
                        if (responseBuilder.Length == 0 && !string.IsNullOrEmpty(msg.Data.Content))
                        {
                            responseBuilder.Append(msg.Data.Content);
                        }

                        break;

                    case AssistantUsageEvent usage:
                        inputTokens += (int)(usage.Data.InputTokens ?? 0);
                        outputTokens += (int)(usage.Data.OutputTokens ?? 0);
                        break;

                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;

                    case SessionErrorEvent:
                        done.TrySetResult();
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = distillPrompt }, cancellationToken);

            var completed = await System.Threading.Tasks.Task.WhenAny(
                done.Task,
                System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(10), cancellationToken));

            if (completed != done.Task)
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Distillation timed out.[/]");
                return ("", "", inputTokens, outputTokens);
            }

            var content = responseBuilder.ToString();

            // Parse the two sections
            var kgMatch = Regex.Match(
                content,
                @"##\s+Knowledge Graph\s*\n([\s\S]*?)(?=\n##\s+Context Document)",
                RegexOptions.IgnoreCase);
            var ctxMatch = Regex.Match(
                content,
                @"##\s+Context Document\s*\n([\s\S]*?)$",
                RegexOptions.IgnoreCase);

            var knowledgeGraph = kgMatch.Success ? kgMatch.Groups[1].Value.Trim() : "";
            var contextDoc = ctxMatch.Success ? ctxMatch.Groups[1].Value.Trim() : "";

            // If parsing failed, treat entire output as knowledge graph
            if (string.IsNullOrEmpty(knowledgeGraph) && string.IsNullOrEmpty(contextDoc)
                && !string.IsNullOrEmpty(content))
            {
                knowledgeGraph = content;
            }

            AnsiConsole.MarkupLine($"[grey]  Distillation: {inputTokens:N0} in → {outputTokens:N0} out[/]");
            return (knowledgeGraph, contextDoc, inputTokens, outputTokens);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Distillation failed: {Markup.Escape(ex.Message)}[/]");
            return ("", "", inputTokens, outputTokens);
        }
        finally
        {
            subscription?.Dispose();
            if (session != null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    /// <summary>
    /// Saves the distilled context document to the output directory as context.md.
    /// This file is designed to be loaded as system prompt context for downstream agents.
    /// </summary>
    private static void SaveContextDoc(
        string contextDoc,
        string? modelName,
        string? questionSlug)
    {
        if (string.IsNullOrWhiteSpace(contextDoc) ||
            string.IsNullOrWhiteSpace(modelName) ||
            string.IsNullOrWhiteSpace(questionSlug))
        {
            return;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeModel = new string(modelName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        var outputDir = Path.Combine(
            Directory.GetCurrentDirectory(),
            "PromptOutput",
            safeModel,
            questionSlug);
        Directory.CreateDirectory(outputDir);

        var contextPath = Path.Combine(outputDir, "context.md");
        File.WriteAllText(contextPath, contextDoc);
        AnsiConsole.MarkupLine($"[green]📋 Context document saved:[/] {Markup.Escape(contextPath)}");
    }

    /// <summary>
    /// Extracts a feature keyword from bold text in the prompt (e.g., **hybridcache**).
    /// Returns null if no bold text is found.
    /// </summary>
    private static string? ExtractFeatureKeyword(string prompt)
    {
        var match = Regex.Match(prompt, @"\*\*(\w[\w\s-]*?)\*\*");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Programmatically discovers files in the repository that match the feature keyword.
    /// Searches by file path first (cheaper), then by content. Caps at 200 files.
    /// </summary>
    private static List<string> DiscoverRepoFiles(string repoPath, string featureKeyword)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".json", ".xml", ".config", ".csproj", ".yaml", ".yml"
        };
        var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obj", "bin", ".git", "node_modules", ".vs"
        };

        var pathMatches = new List<string>();
        var contentMatches = new List<string>();

        foreach (var file in Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!extensions.Contains(ext))
            {
                continue;
            }

            // Skip excluded directories
            var relativePath = Path.GetRelativePath(repoPath, file);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => excludeDirs.Contains(p)))
            {
                continue;
            }

            // Check path match first (cheaper)
            if (relativePath.Contains(featureKeyword, StringComparison.OrdinalIgnoreCase))
            {
                pathMatches.Add(file);
                continue;
            }

            // Check content match (read file)
            try
            {
                var content = File.ReadAllText(file);
                if (content.Contains(featureKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    contentMatches.Add(file);
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        // Prioritize path matches, then content matches, cap at 200
        var result = pathMatches.Concat(contentMatches).Take(200).ToList();
        AnsiConsole.MarkupLine(
            $"[green]📂 Found {result.Count} relevant files " +
            $"({pathMatches.Count} by path, {Math.Min(contentMatches.Count, 200 - pathMatches.Count)} by content)[/]");
        return result;
    }

    /// <summary>
    /// Returns true if the prompt is a structured question with 3 or more ## headers.
    /// </summary>
    private static bool IsStructuredQuestion(string prompt)
    {
        var headerCount = Regex.Matches(prompt, @"^##\s+", RegexOptions.Multiline).Count;
        return headerCount >= 3;
    }

    /// <summary>
    /// Builds one subagent per ## section in the prompt. Each agent receives the preamble
    /// (text before first ##) prepended to its section content, plus all discovered file
    /// contents injected directly into the prompt as code blocks. This bypasses
    /// FileAttachments (which may not prevent tool use) and guarantees the model
    /// sees actual code in its context.
    /// </summary>
    private static List<SubAgentConfig> BuildSectionAgents(
        string prompt,
        string model,
        List<string> discoveredFiles,
        string repoPath)
    {
        var sections = OutputWriter.SplitIntoSections(prompt);
        if (sections.Count == 0)
        {
            return [];
        }

        // Extract preamble (content before first ## header, titled "overview")
        var preamble = "";
        var agentSections = new List<OutputWriter.Section>();
        foreach (var section in sections)
        {
            if (string.Equals(section.Title, "overview", StringComparison.OrdinalIgnoreCase))
            {
                preamble = section.Content.Trim();
            }
            else if (section.Title.Contains("Knowledge Graph", StringComparison.OrdinalIgnoreCase))
            {
                // Skip — handled by post-processing distillation
            }
            else
            {
                agentSections.Add(section);
            }
        }

        // Read file contents and format as code blocks for injection into prompts.
        // This guarantees the model sees actual code (not file paths) and produces text output.
        var codeContext = ReadAndFormatFiles(discoveredFiles, repoPath);

        var agents = new List<SubAgentConfig>();
        foreach (var section in agentSections)
        {
            var sectionPrompt = string.IsNullOrEmpty(preamble)
                ? section.Content
                : $"{preamble}\n\n{section.Content}";

            agents.Add(new SubAgentConfig
            {
                Id = OutputWriter.ToSlug(section.Title),
                Description = section.Title,
                AgentType = AgentType.GeneralPurpose,
                Model = model,
                SystemPrompt = """
                    You are a senior software engineer performing deep code analysis.
                    All relevant source files are provided in the prompt below.
                    Do NOT use any tools — everything you need is already in your context.
                    Reference code by filename:lineNumber range (e.g., MyService.cs:42-58) instead of copying code blocks.
                    Structure your output with clear ## headers and tables where appropriate.
                    """,
                Prompt = $"""
                    IMPORTANT: All relevant source files are included below as code blocks.
                    Analyze ONLY the provided code to answer the question.
                    Do NOT use tools to read, search, or explore files.
                    When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

                    {sectionPrompt}

                    ---
                    SOURCE FILES ({discoveredFiles.Count} files):
                    {codeContext}
                    """,
                WorkingDirectory = repoPath,
                DependsOn = null
            });
        }

        // ── W2: Synthesis agent — combines all section results ──────
        var sectionIds = agents.Select(a => a.Id).ToList();
        var synthesisPrompt = new StringBuilder();
        synthesisPrompt.AppendLine(
            "You are given the results of detailed code analysis sections. " +
            "Combine them into a single comprehensive document.");
        synthesisPrompt.AppendLine();
        synthesisPrompt.AppendLine("Requirements:");
        synthesisPrompt.AppendLine("1. Start with a Table of Contents listing all sections");
        synthesisPrompt.AppendLine("2. Merge and deduplicate content across sections — do NOT repeat the same information verbatim from multiple sections");
        synthesisPrompt.AppendLine("3. Preserve ## headers and key details, but summarize redundant or overlapping content");
        synthesisPrompt.AppendLine("4. Ensure cross-references between sections are consistent");
        synthesisPrompt.AppendLine("5. Do not add new analysis — only organize and present what was provided");
        synthesisPrompt.AppendLine("6. Target output length: aim for a concise document under 20,000 words. Prioritize clarity over completeness");
        synthesisPrompt.AppendLine();
        synthesisPrompt.AppendLine("Section results:");
        foreach (var id in sectionIds)
        {
            synthesisPrompt.AppendLine();
            synthesisPrompt.AppendLine($"--- {id} ---");
            synthesisPrompt.AppendLine($"{{result:{id}}}");
        }

        agents.Add(new SubAgentConfig
        {
            Id = "synthesis",
            Description = "Combine all section analyses into comprehensive document with TOC",
            AgentType = AgentType.GeneralPurpose,
            Model = model,
            SystemPrompt = "You are a synthesis agent. Combine the provided analysis sections into a single cohesive document. Do NOT use tools — all input is provided in the prompt. Be concise: merge overlapping content rather than repeating it.",
            Prompt = synthesisPrompt.ToString(),
            Timeout = TimeSpan.FromMinutes(45),
            WorkingDirectory = repoPath,
            DependsOn = sectionIds
        });

        // ── W3: Distill agent — knowledge graph + context document ──
        agents.Add(new SubAgentConfig
        {
            Id = "distill",
            Description = "Produce knowledge graph (Mermaid) and context document",
            AgentType = AgentType.GeneralPurpose,
            Model = model,
            SystemPrompt = "You are a distillation agent. Produce a knowledge graph and context document from the provided analysis. Do NOT use tools — all input is provided in the prompt.",
            Prompt = """
                You are given a comprehensive feature analysis document. Produce exactly two sections
                with the headers shown below. Output ONLY these two sections, no preamble.

                ## Knowledge Graph

                Generate a Mermaid diagram capturing every component discovered in the analysis:
                - Nodes: classes, interfaces, factories, maps/registries, serializers, converters,
                  config classes, endpoints, test classes, external dependencies
                - Edges: implements, creates, resolves-from, populates, reads-from, serializes-with,
                  persists-to, calls, tests, configures, publishes, subscribes, retries, falls-back-to,
                  locks, observes
                - Subgraphs: group nodes by layer (Registration, Serialization, Storage, Runtime,
                  Resilience, Concurrency, Messaging, Observability, Testing)

                Follow the diagram with a prose summary of the end-to-end feature lifecycle.

                ## Context Document

                Produce a concise, self-contained reference document (under 4000 words) that a developer
                or AI agent could use as system-prompt context to understand and safely modify this feature.
                Include:
                - Architecture overview and end-to-end lifecycle
                - Key components and relationships (DI registrations, factories, configuration)
                - Non-obvious wiring (factory maps, custom serializer resolution, multi-layer caching)
                - Critical invariants and constraints that must be preserved
                - Where to add new functionality and what registrations must be updated
                - Concurrency and thread-safety requirements
                - Error handling and resilience patterns
                - Test coverage and known gaps

                Use specific class names, method names, and file paths. Write it as a reference document,
                not a narrative summary.

                ---
                ANALYSIS DOCUMENT:
                {result:synthesis}
                """,
            WorkingDirectory = repoPath,
            DependsOn = ["synthesis"]
        });

        AnsiConsole.MarkupLine(
            $"[green]📋 Built {sectionIds.Count} section agents + synthesis + distill (3 waves)[/]");
        return agents;
    }

    /// <summary>
    /// Reads file contents and formats them as markdown code blocks for injection
    /// into agent prompts. Caps total output at <paramref name="maxTotalChars"/> to
    /// avoid context overflow. Files that exceed the remaining budget are truncated.
    /// </summary>
    private static string ReadAndFormatFiles(
        List<string> files,
        string repoPath,
        int maxTotalChars = 150_000)
    {
        var sb = new StringBuilder();
        var totalChars = 0;

        foreach (var file in files)
        {
            if (totalChars >= maxTotalChars)
            {
                sb.AppendLine($"\n... ({files.Count - files.IndexOf(file)} files omitted — context limit reached)");
                break;
            }

            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(repoPath, file);
            var ext = Path.GetExtension(file).TrimStart('.');
            var lang = ext switch
            {
                "cs" => "csharp",
                "json" => "json",
                "xml" or "csproj" or "config" => "xml",
                "yaml" or "yml" => "yaml",
                _ => ext
            };

            // Truncate individual file if needed
            var remaining = maxTotalChars - totalChars;
            if (content.Length > remaining)
            {
                content = content[..remaining] + "\n// ... (file truncated)";
            }

            sb.AppendLine();
            sb.AppendLine($"### {relativePath}");
            sb.AppendLine($"```{lang}");
            sb.AppendLine(content);
            sb.AppendLine("```");

            totalChars += content.Length;
        }

        AnsiConsole.MarkupLine(
            $"[green]📄 Injected {totalChars:N0} chars of source code into prompts[/]");
        return sb.ToString();
    }
}
