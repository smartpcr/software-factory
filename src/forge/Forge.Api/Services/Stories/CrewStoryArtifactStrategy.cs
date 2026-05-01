// -----------------------------------------------------------------------
// <copyright file="CrewStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Real-LLM <see cref="IStoryArtifactStrategy"/> — invokes the same
/// <see cref="IAgentRunner"/> rail that <c>CrewQueueWorker.RunAgentAsync</c>
/// uses for live work-item execution. Per-phase agents (Architect / Engineer
/// / Review / QA) are resolved via the matching <c>crew-{role}</c> row and
/// its <c>Lead</c> agent; their crew row supplies model + backend +
/// invoke-mode; their <c>soul.md</c> is loaded as the system prompt.
///
/// <para>The agent runs against a per-invocation Forge-managed workspace
/// (<c>{stateDir}/storyworkspaces/{spawnIdSlug}/{phaseTag}/</c>) — never
/// %TEMP%, never the repo root. The agent uses its filesystem tools to
/// write the expected files at relative paths inside that directory; the
/// strategy reads them back and returns them to the orchestrator, which
/// commits them via <see cref="IStoryGitOps"/>.</para>
/// </summary>
public sealed class CrewStoryArtifactStrategy : IStoryArtifactStrategy
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IAgentRunner runner;
    private readonly TaskChannel taskChannel;
    private readonly IConfiguration config;
    private readonly ILogger<CrewStoryArtifactStrategy>? logger;

    public CrewStoryArtifactStrategy(
        IServiceScopeFactory scopeFactory,
        IAgentRunner runner,
        TaskChannel taskChannel,
        IConfiguration config,
        ILogger<CrewStoryArtifactStrategy>? logger = null)
    {
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.taskChannel = taskChannel;
        this.config = config;
        this.logger = logger;
    }

    // ── Per-phase entry points ───────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        var docDir = $"docs/stories/{spawn.StoryId}";
        var budget = BudgetForPhase(spawn.StoryPoints, "plan");

        // Plan files are independent — fire 4 architect invocations in
        // parallel (one per file). Wall-clock = max(per-file budget) instead
        // of sum. Each agent writes into its own per-file workspace so the
        // SDKs don't fight over a shared cwd.
        var specs = new (string FileName, string Brief, string RepoPath)[]
        {
            ("tech-spec.md",
             "Problem statement, in/out-of-scope items, non-goals, hard constraints, identified risks. Anchor every section in the story description's concrete details.",
             $"{docDir}/tech-spec.md"),
            ("architecture.md",
             "Components and their responsibilities, data model (entities + key fields), interfaces between components, end-to-end sequence flows for the primary scenarios.",
             $"{docDir}/architecture.md"),
            ("implementation-plan.md",
             "Phased work breakdown an engineer can execute. Order phases so each builds on the previous; call out hard sequencing dependencies and parallel-safe items.",
             $"{docDir}/implementation-plan.md"),
            ("e2e-scenarios.md",
             "Gherkin-style feature scenarios (Feature / Scenario / Given-When-Then) covering happy paths and the edge cases QA will run.",
             $"{docDir}/e2e-scenarios.md"),
        };

        var tasks = specs.Select(spec => this.ProducePlanFileAsync(spawn, story, spec, budget, ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < specs.Length; i++)
        {
            dict[specs[i].RepoPath] = results[i];
        }
        return dict;
    }

    private async Task<string> ProducePlanFileAsync(
        StorySpawnRow spawn, WorkItemRow story,
        (string FileName, string Brief, string RepoPath) spec,
        TimeSpan budget, CancellationToken ct)
    {
        // Per-file workspace so 4 parallel SDKs don't share a cwd.
        var workspace = this.PrepareWorkspace(spawn.Id, $"plan-{Path.GetFileNameWithoutExtension(spec.FileName)}");
        var diskPath = Path.Combine(workspace, spec.FileName);

        // Resume case — a previous tick's architect already wrote this file
        // (typical when commit/push failed and the orchestrator re-ticks).
        // Skip re-invoking the LLM so we don't burn tokens regenerating
        // identical content on every retry.
        if (File.Exists(diskPath))
        {
            var existing = await File.ReadAllTextAsync(diskPath, ct);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                this.logger?.LogInformation(
                    "Plan file '{File}' cached in workspace ({Bytes} bytes); skipping architect invocation.",
                    spec.FileName, existing.Length);
                return existing;
            }
        }

        var prompt = $$"""
            You are the Architect agent for Forge story `{{spawn.StoryId}}`.

            ## Story title
            {{story.Title}}

            ## Story description (operator-authored prompt)
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## Story points
            {{spawn.StoryPoints}}

            ## Your task
            Produce ONE planning artifact: `{{spec.FileName}}`. It must be a
            real, production-quality markdown document — not a placeholder,
            not an outline. Anchor every section on the story description's
            concrete details.

            ### Brief for this file
            {{spec.Brief}}

            ## How to deliver
            Use your filesystem tools to write a SINGLE file named
            `{{spec.FileName}}` at the root of your working directory. Do NOT
            create subdirectories. Do NOT write any other files. Reply
            `DONE` when finished and stop.
            """;

        await this.InvokeAsync("Architect", spawn.Id, $"plan:{spec.FileName}", prompt, workspace, budget, ct);

        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Architect did not produce '{spec.FileName}'. Workspace: {workspace}");
        }
        return await File.ReadAllTextAsync(diskPath, ct);
    }

    public async Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, "decompose");
        var prompt = $$"""
            You are the Architect agent decomposing story `{{spawn.StoryId}}`
            into parallel-executable workstreams.

            ## Story title
            {{story.Title}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## Your task
            Split the work into 2–6 concrete workstreams that can be assigned to
            independent engineers. Each workstream must be small enough to land
            as one PR. Set `deps` only when a workstream literally cannot start
            until another's PR has merged.

            ## How to deliver
            Use your filesystem tools to write a SINGLE file named
            `workstreams.json` inside your working directory. Its content must
            be valid JSON conforming exactly to this schema:

            [
              {
                "title": "short imperative title",
                "description": "1-3 sentences of scope",
                "acceptanceCriteria": ["bullet", "..."],
                "deps": ["title of upstream workstream", "..."],
                "deliverables": ["file or artifact", "..."],
                "timeline": "e.g. day 1-2",
                "estimatedEffort": "e.g. S | M | L or hours"
              }
            ]

            Do NOT write any other files. Do NOT create subdirectories. Reply
            `DONE` after writing the file and stop.
            """;

        await this.InvokeAsync("Architect", spawn.Id, "decompose", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "decompose"), ct);

        var diskPath = Path.Combine(workspace, "workstreams.json");
        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Architect did not write workstreams.json. Workspace: {workspace}");
        }
        var json = await File.ReadAllTextAsync(diskPath, ct);
        try
        {
            var rows = JsonSerializer.Deserialize<List<DecomposeJsonRow>>(json, JsonOpts);
            if (rows is null || rows.Count == 0)
            {
                throw new InvalidOperationException("Architect produced an empty decomposition.");
            }
            return rows.Select(r => new DecompositionWorkstream(
                Title: NonEmpty(r.Title, "untitled"),
                Description: r.Description ?? string.Empty,
                AcceptanceCriteria: (IReadOnlyList<string>)(r.AcceptanceCriteria ?? new List<string>()),
                Deps: (IReadOnlyList<string>)(r.Deps ?? new List<string>()),
                Deliverables: (IReadOnlyList<string>)(r.Deliverables ?? new List<string>()),
                Timeline: r.Timeline ?? string.Empty,
                EstimatedEffort: r.EstimatedEffort ?? string.Empty)).ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Architect workstreams.json failed to parse: {ex.Message}. Workspace: {workspace}", ex);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"impl-{Slug(workstream.Id)}");
        var repoPrefix = $"src/.story-artifacts/{spawn.StoryId}/{workstream.Id}";
        var prompt = $$"""
            You are the Engineer agent implementing workstream `{{workstream.Id}}`
            for story `{{spawn.StoryId}}`.

            ## Story
            {{spawn.StoryTitle}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription)}}

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}
            Branch: `{{workstream.Branch}}`

            ## Your task
            Produce the source-code changes that implement this workstream. Use
            real implementation — types, methods, tests — not placeholders.

            ## How to deliver
            Use your filesystem tools to write files inside your working
            directory. Use relative paths only; do not use absolute paths and
            do not write outside the working directory. You may create
            subdirectories within the working directory if your implementation
            spans multiple files. Reply `DONE` when finished.
            """;

        await this.InvokeAsync("Engineer", spawn.Id, $"impl:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "impl"), ct);

        var files = ReadAllFiles(workspace);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer produced no files for workstream {workstream.Id}. Workspace: {workspace}");
        }
        // Re-key relative paths under the repo prefix the orchestrator commits.
        return files.ToDictionary(
            kv => $"{repoPrefix}/{kv.Key}",
            kv => kv.Value,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"review-{Slug(workstream.Id)}-r{round}");
        var prompt = $$"""
            You are the Reviewer agent reviewing PR `{{prId}}` for workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Story
            {{spawn.StoryTitle}}

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}

            ## Round
            {{round}} (0 = first pass; if there are no real issues, return an
            empty array `[]` to approve.)

            ## Your task
            Produce review comments. Each comment must point at a real
            file/line and describe a concrete issue worth fixing — not nits.

            ## How to deliver
            Write a single file `comments.json` inside your working directory
            containing valid JSON of this shape:

            [
              {
                "filePath": "src/.../somefile",
                "line": 1,
                "body": "issue + suggested fix",
                "severity": "major | minor | nit"
              }
            ]

            Empty array `[]` means approve. Reply `DONE` when finished.
            """;

        await this.InvokeAsync("Review", spawn.Id, $"review:{workstream.Id}:r{round}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review"), ct);

        var diskPath = Path.Combine(workspace, "comments.json");
        if (!File.Exists(diskPath))
        {
            throw new InvalidOperationException(
                $"Reviewer did not write comments.json. Workspace: {workspace}");
        }
        var json = await File.ReadAllTextAsync(diskPath, ct);
        try
        {
            var rows = JsonSerializer.Deserialize<List<ReviewCommentJsonRow>>(json, JsonOpts) ?? new();
            return rows.Select(r => new ReviewComment(
                FilePath: r.FilePath ?? string.Empty,
                Line: r.Line ?? 1,
                Body: r.Body ?? string.Empty,
                Severity: r.Severity ?? "minor")).ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Reviewer comments.json failed to parse: {ex.Message}. Workspace: {workspace}", ex);
        }
    }

    public async Task<ReviewFix> ProduceReviewFixAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"fix-{Slug(workstream.Id)}-{Guid.NewGuid():N}");
        var safePath = comment.FilePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var prompt = $$"""
            You are the Engineer agent fixing review feedback on workstream
            `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Reviewer comment
            File: `{{comment.FilePath}}` line {{comment.Line}}
            Body: {{comment.Body}}

            ## How to deliver
            1. Write the FIXED file at the SAME relative path as the comment's
               file. Inside your working directory, create whatever
               subdirectories are needed and write the full updated content to
               `{{comment.FilePath}}`.
            2. Write a SECOND file at the working-directory root named
               `reply.md` containing a 1-3 sentence reply for the reviewer.

            Reply `DONE` when both files are written.
            """;

        await this.InvokeAsync("Engineer", spawn.Id, $"review-fix:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "review-fix"), ct);

        var fixedFile = Path.Combine(workspace, safePath);
        if (!File.Exists(fixedFile))
        {
            throw new InvalidOperationException(
                $"Engineer did not write fixed file '{comment.FilePath}'. Workspace: {workspace}");
        }
        var replyFile = Path.Combine(workspace, "reply.md");
        if (!File.Exists(replyFile))
        {
            throw new InvalidOperationException(
                $"Engineer did not write reply.md. Workspace: {workspace}");
        }
        var content = await File.ReadAllTextAsync(fixedFile, ct);
        var reply = (await File.ReadAllTextAsync(replyFile, ct)).Trim();
        return new ReviewFix(comment.FilePath, content, reply);
    }

    public async Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, "qa");
        var wsList = string.Join("\n", workstreams.Select(w => $"- `{w.Id}`: {w.Name} — {w.Summary}"));
        var repoPrefix = $"tests/Features/{spawn.StoryId}";
        var prompt = $$"""
            You are the QA agent for story `{{spawn.StoryId}}`.

            ## Story
            {{story.Title}}

            ## Story description
            {{NonEmpty(spawn.StoryDescription, story.DescriptionMd)}}

            ## Workstreams
            {{wsList}}

            ## Your task
            Produce one Gherkin `.feature` file per workstream that describes
            the user-visible behavior the workstream must deliver. Real
            scenarios — Given/When/Then — that step definitions can be written
            against.

            ## How to deliver
            Write one `.feature` file per workstream into your working
            directory. Name each file using the workstream's last segment
            (after the final '-') with `.feature` extension; e.g. workstream
            `ws-myStory-auth` becomes `auth.feature`. Reply `DONE` when done.
            """;

        await this.InvokeAsync("QA", spawn.Id, "qa-features", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "qa-features"), ct);

        var files = ReadAllFiles(workspace);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"QA produced no feature files. Workspace: {workspace}");
        }
        return files.ToDictionary(
            kv => $"{repoPrefix}/{kv.Key}",
            kv => kv.Value,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        var workspace = this.PrepareWorkspace(spawn.Id, $"qa-steps-{Slug(workstream.Id)}");
        var repoPrefix = $"tests/Steps/{spawn.StoryId}";
        var prompt = $$"""
            You are the Engineer agent writing step definitions for the QA
            feature file of workstream `{{workstream.Id}}` (story `{{spawn.StoryId}}`).

            ## Workstream
            **{{workstream.Name}}** — {{workstream.Summary}}

            ## How to deliver
            Use your filesystem tools to write step-definition source files
            into your working directory. Name the primary file
            `{{workstream.Id}}Steps.cs`. You may add helper files. Reply
            `DONE` when finished.
            """;

        await this.InvokeAsync("Engineer", spawn.Id, $"qa-steps:{workstream.Id}", prompt, workspace,
            BudgetForPhase(spawn.StoryPoints, "qa-steps"), ct);

        var files = ReadAllFiles(workspace);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Engineer produced no step definitions for workstream {workstream.Id}. Workspace: {workspace}");
        }
        return files.ToDictionary(
            kv => $"{repoPrefix}/{kv.Key}",
            kv => kv.Value,
            StringComparer.Ordinal);
    }

    // ── Workspace + agent invocation ─────────────────────────────────────────

    /// <summary>
    /// Resolve a per-spawn-per-phase workspace under the Forge state directory
    /// (sibling of the SQLite DB). Idempotent — does NOT wipe an existing
    /// workspace so the calling strategy can resume from cached agent output
    /// when a downstream step (commit/push/PR-create) failed and the
    /// orchestrator re-ticks. Each individual producer method is responsible
    /// for deciding whether existing files in the workspace are reusable.
    /// </summary>
    private string PrepareWorkspace(string spawnId, string phaseTag)
    {
        var stateDir = ResolveStateDir(this.config);
        var slug = SlugForFs(spawnId);
        var path = Path.Combine(stateDir, "storyworkspaces", slug, phaseTag);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveStateDir(IConfiguration config)
    {
        var dbPath = config["Forge:Db:Path"] ?? @"E:\forge\forge.db";
        return Path.GetDirectoryName(dbPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge");
    }

    /// <summary>Map team name → crew row id pattern (<c>crew-arch</c> etc.).</summary>
    private static string CrewIdPrefix(string team) => team.ToLowerInvariant() switch
    {
        "architect" => "crew-arch",
        "engineer" => "crew-eng",
        "review" => "crew-rev",
        "qa" => "crew-qa",
        _ => throw new InvalidOperationException($"Unknown team '{team}' for story strategy."),
    };

    /// <summary>
    /// Per-phase wall-clock budget, computed from the originating story's
    /// points so a 1-pt change isn't given the same window as a 13-pt epic.
    /// Formula: <c>base = max(2 minutes, points * 30s)</c>; per-phase
    /// multiplier on top.
    /// </summary>
    internal static TimeSpan BudgetForPhase(int points, string phaseTag)
    {
        var baseSeconds = Math.Max(120.0, points * 30.0);
        // phaseTag examples: "plan", "plan:tech-spec.md", "decompose",
        //                    "impl:ws-...", "review:ws-...:r0", "review-fix:ws-...",
        //                    "qa-features", "qa-steps:ws-..."
        var prefix = phaseTag.Split(':', 2)[0];
        var multiplier = prefix switch
        {
            "plan" => 1.0,           // per-file budget when run in parallel
            "decompose" => 0.7,
            "impl" => 1.5,           // engineering writes more code than planning
            "review" => 0.5,         // reviewer skim
            "review-fix" => 0.5,
            "qa-features" => 0.8,
            "qa-steps" => 0.7,
            _ => 1.0,
        };
        return TimeSpan.FromSeconds(baseSeconds * multiplier);
    }

    private async Task InvokeAsync(string team, string spawnId, string phaseTag, string prompt, string workspace, CancellationToken ct)
        => await this.InvokeAsync(team, spawnId, phaseTag, prompt, workspace, budget: null, ct);

    private async Task InvokeAsync(string team, string spawnId, string phaseTag, string prompt, string workspace, TimeSpan? budget, CancellationToken ct)
    {
        var prefix = CrewIdPrefix(team);

        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == prefix, ct)
            ?? await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id.StartsWith(prefix), ct)
            ?? throw new InvalidOperationException(
                $"No crew found for team '{team}' (looked for id '{prefix}' or prefix). Configure crews in DB.");

        var agentId = crew.DefaultGeneratorAgentId ?? crew.Lead
            ?? throw new InvalidOperationException(
                $"Crew '{crew.Id}' has no Lead/DefaultGeneratorAgentId set.");

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);

        var model = !string.IsNullOrEmpty(agent?.GeneratorModel) ? agent!.GeneratorModel
            : !string.IsNullOrEmpty(crew.Model) ? crew.Model
            : throw new InvalidOperationException($"Crew '{crew.Id}' has no Model set.");

        var backendStr = !string.IsNullOrEmpty(agent?.Backend) ? agent!.Backend : crew.Backend;
        var backend = string.Equals(backendStr, "claude", StringComparison.OrdinalIgnoreCase)
            ? AgentBackend.Claude : AgentBackend.Copilot;

        var invokeStr = !string.IsNullOrEmpty(agent?.InvokeMode) ? agent!.InvokeMode : crew.InvokeMode;
        var invokeMode = string.Equals(invokeStr, "cli", StringComparison.OrdinalIgnoreCase)
            ? AgentInvokeMode.Cli : AgentInvokeMode.Sdk;

        var soul = string.Empty;
        if (!string.IsNullOrEmpty(agent?.SoulPath) && File.Exists(agent!.SoulPath))
        {
            try { soul = await File.ReadAllTextAsync(agent!.SoulPath, ct); }
            catch { /* best effort */ }
        }

        var context = new AgentContext(
            CrewId: crew.Id,
            Model: model,
            SoulMd: soul,
            // Per-invocation Forge-managed workspace. NOT %TEMP%, NOT the
            // repo root. The agent reads/writes here only.
            WorktreePath: workspace,
            AllowedTools: Array.Empty<string>(),
            MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
            Backend: backend,
            InvokeMode: invokeMode,
            // Hard wall-clock cap so a runaway agent fails fast instead of
            // sitting in the orchestrator queue indefinitely. The runner
            // cancels the SDK session at this cap; we then surface as a
            // failure that the next scheduler tick can retry.
            WallClockBudget: budget);

        await this.taskChannel.EmitAsync(spawnId, "story.agent-prompt", new
        {
            phase = phaseTag,
            team,
            agentId,
            crewId = crew.Id,
            model,
            backend = backendStr,
            invokeMode = invokeStr,
            workspace,
            promptPreview = Trim(prompt, 600),
        }, agentId: agentId, crewId: crew.Id, ct: ct);

        var invocation = await this.runner.StartAsync(spawnId, agentId, prompt, context, ct);

        string? failure = null;
        await foreach (var evt in invocation.Events.WithCancellation(ct))
        {
            await this.taskChannel.EmitAsync(spawnId, $"story.agent-{evt.Kind.ToString().ToLowerInvariant()}", new
            {
                phase = phaseTag,
                team,
                agentId,
                invocationId = invocation.InvocationId,
                text = evt.Text,
                toolName = evt.ToolName,
                toolPayload = evt.ToolPayload,
                error = evt.Error,
                inputTokens = evt.InputTokens,
                outputTokens = evt.OutputTokens,
                costUsd = evt.CostUsd,
            }, agentId: agentId, crewId: crew.Id, ct: ct);

            if (evt.Kind == AgentEventKind.Failed)
            {
                failure = evt.Error ?? "agent reported failure";
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Agent {agentId} (team {team}) failed during phase '{phaseTag}': {failure}");
        }
    }

    // ── Filesystem helpers ───────────────────────────────────────────────────

    /// <summary>Read every file under <paramref name="root"/> recursively into a relative-path → content map.</summary>
    private static IReadOnlyDictionary<string, string> ReadAllFiles(string root)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return dict;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            dict[rel] = File.ReadAllText(path);
        }
        return dict;
    }

    private static readonly Regex SlugRegex = new(@"[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private static string Slug(string s) => SlugRegex.Replace(s, "-").Trim('-');

    /// <summary>Filesystem-safe slug: keep ascii alnum / dash / underscore / dot.</summary>
    private static string SlugForFs(string s) => SlugRegex.Replace(s, "_").Trim('_');

    private static string NonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        }
        return string.Empty;
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    // ── JSON DTOs ────────────────────────────────────────────────────────────

    private sealed class DecomposeJsonRow
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public List<string>? AcceptanceCriteria { get; set; }
        public List<string>? Deps { get; set; }
        public List<string>? Deliverables { get; set; }
        public string? Timeline { get; set; }
        public string? EstimatedEffort { get; set; }
    }

    private sealed class ReviewCommentJsonRow
    {
        public string? FilePath { get; set; }
        public int? Line { get; set; }
        public string? Body { get; set; }
        public string? Severity { get; set; }
    }
}
