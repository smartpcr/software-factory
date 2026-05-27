// -----------------------------------------------------------------------
// <copyright file="DeterministicStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using System.Text;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Services.Stories.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// TEST-ONLY <see cref="IStoryArtifactStrategy"/> — produces deterministic
/// markdown / feature / step content from templates so the Phase 28
/// orchestrator lifecycle is drivable in xUnit without an LLM.
///
/// <para>
/// This class lives in the test assembly on purpose. Production must never
/// be able to bind it: the spec says every plan / decompose / impl artifact
/// is real LLM output, and a templated stub leaking into prod paths leaves
/// behind on-disk files (under <c>docs/stories/&lt;story&gt;/</c>) that look
/// like real plans but ignore the actual story description. We removed an
/// instance of exactly that pollution by moving this class out of the
/// production assembly.
/// </para>
/// <para>
/// Output is a function of (spawn, story, workstream) so re-runs are
/// byte-identical.
/// </para>
/// </summary>
public sealed class DeterministicStoryArtifactStrategy : IStoryArtifactStrategy
{
    private readonly IStoryGitOps? git;
    private readonly IServiceScopeFactory? scopeFactory;

    /// <summary>
    /// docs/forge/e2e.md Stage 7.3 — per-spawn idempotency marker for
    /// the per-spawn <c>E2eBase.cs</c> emission. The FIRST
    /// <see cref="ProduceStageE2eAsync"/> call for a given spawn includes
    /// the base class in its output dict; every subsequent qa-e2e
    /// workstream for the same spawn finds the spawn id already in the
    /// set and skips the entry. Same shape as
    /// <c>CrewStoryArtifactStrategy.e2eBaseEmittedForSpawn</c> so both
    /// producers honour identical idempotency semantics.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> e2eBaseEmittedForSpawn =
        new(StringComparer.Ordinal);

    /// <summary>Test hook — true when the per-spawn E2eBase has already
    /// been emitted by this strategy instance. Used by Stage 7.3's
    /// idempotency integration test.</summary>
    internal bool HasEmittedE2eBaseFor(string spawnId) =>
        this.e2eBaseEmittedForSpawn.ContainsKey(spawnId);

    /// <summary>Test hook — forget the per-spawn E2eBase marker so a
    /// subsequent qa-e2e call re-emits. Mirrors the cache-purge hook on
    /// <see cref="CrewStoryArtifactStrategy"/>; primarily used to reset
    /// state between integration test cases.</summary>
    internal void ResetE2eBaseEmittedFor(string spawnId) =>
        this.e2eBaseEmittedForSpawn.TryRemove(spawnId, out _);

    /// <summary>
    /// Default zero-arg ctor — keeps direct construction working for
    /// pure-unit tests that don't drive the new
    /// <see cref="ProduceStageE2eAsync"/> code path. The qa-e2e
    /// emission falls back to <see cref="E2eSetupType.Inline"/> when
    /// <see cref="IStoryGitOps"/> isn't available (no setup block to
    /// read from).
    /// </summary>
    public DeterministicStoryArtifactStrategy() : this(git: null) { }

    /// <summary>
    /// DI ctor — Stage 4.2 adds <paramref name="git"/> so the qa-e2e
    /// producer can read the merged <c>e2e-scenarios.md</c> from the
    /// spawn's base branch (defaults to <c>main</c>) and detect the
    /// setup type for the parent phase. Argument is optional so
    /// pure-unit tests that hand-construct the strategy don't need to
    /// wire it; the fallback is <see cref="E2eSetupType.Inline"/>.
    /// The repo is synthesised from <see cref="StorySpawnRow.RepoId"/>
    /// — the deterministic strategy only needs the repo id for the
    /// in-memory git ops' branch-tree key, not the full <c>RepoRow</c>
    /// metadata.
    /// </summary>
    public DeterministicStoryArtifactStrategy(IStoryGitOps? git)
    {
        this.git = git;
    }

    /// <summary>
    /// DI ctor used by orchestrator integration tests. The production
    /// strategy persists a pass deliverable before returning implementation
    /// files; the deterministic strategy mirrors that contract so Phase 75's
    /// post-loop pass gate exercises the same invariant in tests.
    /// </summary>
    public DeterministicStoryArtifactStrategy(IStoryGitOps? git, IServiceScopeFactory scopeFactory)
    {
        this.git = git;
        this.scopeFactory = scopeFactory;
    }

    public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
    {
        var dir = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}";
        var techSpec = new StringBuilder()
            .AppendLine($"# Tech spec — {story.Title}")
            .AppendLine()
            .AppendLine($"Story: `{story.Id}`")
            .AppendLine()
            .AppendLine("## Goals")
            .AppendLine($"- Deliver {story.Title.ToLowerInvariant()} end-to-end across the four crews.")
            .AppendLine()
            .AppendLine("## Constraints")
            .AppendLine("- Backwards-compatible with the existing public API.")
            .AppendLine("- No new infrastructure dependencies.")
            .AppendLine()
            .AppendLine("## Risks")
            .AppendLine("- Schema migration on the live DB during cut-over.")
            .ToString();

        var architecture = new StringBuilder()
            .AppendLine($"# Architecture — {story.Title}")
            .AppendLine()
            .AppendLine("## Components")
            .AppendLine("- API surface (Forge.Api endpoints)")
            .AppendLine("- Persistence (EF Core entities + idempotent migration)")
            .AppendLine("- UI surface (React components)")
            .AppendLine()
            .AppendLine("## Data flow")
            .AppendLine("Operator → UI → REST → service → SQLite + provider remote.")
            .AppendLine()
            .AppendLine("## API contracts")
            .AppendLine("See `tech-spec.md` for endpoint shapes.")
            .ToString();

        var implPlan = new StringBuilder()
            .AppendLine($"# Implementation plan — {story.Title}")
            .AppendLine()
            .AppendLine("## Ordered work")
            .AppendLine("1. Data model + migration")
            .AppendLine("2. REST endpoints with validation")
            .AppendLine("3. UI component")
            .AppendLine("4. Provider write paths")
            .AppendLine("5. Tests + docs")
            .ToString();

        var e2e = new StringBuilder()
            .AppendLine($"# E2E scenarios — {story.Title}")
            .AppendLine()
            .AppendLine("## AC-1 happy path")
            .AppendLine($"Given an operator selects `{story.Id}`,")
            .AppendLine("When they trigger the primary action,")
            .AppendLine("Then the system completes the workflow end-to-end.")
            .AppendLine()
            .AppendLine("## AC-2 error handling")
            .AppendLine("Given an upstream call fails,")
            .AppendLine("When the agent retries,")
            .AppendLine("Then the failure is surfaced and recovery succeeds.")
            .ToString();

        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{dir}/tech-spec.md"] = techSpec,
            [$"{dir}/architecture.md"] = architecture,
            [$"{dir}/implementation-plan.md"] = implPlan,
            [$"{dir}/e2e-scenarios.md"] = e2e,
        };
        return Task.FromResult<IReadOnlyDictionary<string, string>>(dict);
    }

    public Task ResumeSingleArtifactAsync(
        StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
    {
        // Deterministic strategy has no gen↔eval loop — record an observable
        // signal so integration tests can assert "the recovery service called
        // through" without running real agents. Each call appends to a static
        // dictionary keyed by spawnId+key; tests inspect Resumed.Snapshot()
        // and clear it before the next setup.
        var bucket = $"{spawn.Id}:{key}";
        Resumed.AddOrUpdate(bucket, 1, (_, n) => n + 1);
        // Phase 50 follow-up — record whether Restart vs ordinary Resume
        // invoked the strategy, so RestartBehaviorTests can assert that
        // POST /spawn/artifacts/{key}/restart actually plumbs the flag
        // through RunResumeWithCleanupAsync rather than degrading silently
        // to a plain Resume.
        if (restartFromScratch)
        {
            ResumedWithRestartFromScratch[bucket] = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 53 — sequential plan-doc entry point. The orchestrator calls
    /// this once per doc on the doc's own branch + worktree; the
    /// deterministic strategy returns the static content matching the
    /// full-run output for the requested key.
    /// </summary>
    public async Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
        StorySpawnRow spawn,
        WorkItemRow story,
        string docKey,
        string worktreePath,
        string planBranch,
        RepoRow? repo,
        bool forceResume = false,
        CancellationToken ct = default)
    {
        if (!PlanDocStateHelpers.PlanDocFileName.ContainsKey(docKey))
        {
            throw new ArgumentException($"Unknown plan-doc key '{docKey}'.", nameof(docKey));
        }
        var fileName = PlanDocStateHelpers.PlanDocFileName[docKey];
        var repoPath = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}/{fileName}";

        // Reuse the same deterministic content as the full plan-run path so
        // tests asserting file content stay byte-identical between the
        // legacy single-PR and Phase 53 sequential paths.
        var full = await this.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);
        if (!full.TryGetValue(repoPath, out var content))
        {
            content = $"# {fileName} — {story.Title}\n\n(deterministic stub)\n";
        }
        return (repoPath, content);
    }

    /// <summary>
    /// Test hook — counter of <see cref="ResumeSingleArtifactAsync"/> invocations
    /// keyed by <c>{spawnId}:{key}</c>. Reset between tests via <see cref="Reset"/>.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Resumed = new();

    /// <summary>
    /// Test hook — records buckets that invoked <see cref="ResumeSingleArtifactAsync"/>
    /// with <c>restartFromScratch=true</c>. Used by RestartBehaviorTests to
    /// verify the Restart endpoint propagates the flag end-to-end. Cleared by
    /// <see cref="Reset"/>.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ResumedWithRestartFromScratch = new();

    public static void Reset()
    {
        Resumed.Clear();
        ResumedWithRestartFromScratch.Clear();
    }

    public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
    {
        // Two-workstream decomposition is the spec's typical example. The
        // first workstream covers the API + data model; the second covers
        // the UI integration. Deterministic, no mutual deps.
        var ws = new List<DecompositionWorkstream>
        {
            new(
                Title: "api",
                Description: $"API surface + data model for {story.Title}",
                AcceptanceCriteria: new[] { "Endpoints live", "Migration idempotent" },
                Deps: Array.Empty<string>(),
                Deliverables: new[] { "REST endpoints", "Migration", "Unit tests" },
                Timeline: "2d",
                EstimatedEffort: "5pts"),
            new(
                Title: "ui",
                Description: $"UI integration for {story.Title}",
                AcceptanceCriteria: new[] { "Modal renders", "API wiring lives" },
                Deps: new[] { "api" },
                Deliverables: new[] { "Modal component", "API client", "Wiring" },
                Timeline: "1d",
                EstimatedEffort: "3pts"),
        };
        return Task.FromResult<IReadOnlyList<DecompositionWorkstream>>(ws);
    }

    public async Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        if (this.scopeFactory is not null)
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var hasPass = await db.Deliverables.AsNoTracking()
                .AnyAsync(d => d.WorkItemId == workstream.Id
                    && d.Verdict == "pass"
                    && d.Score >= ScoringConstants.PassThreshold
                    && d.FailureKind == null, ct);
            if (!hasPass)
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = workstream.Id,
                    CrewId = "crew-eng",
                    GeneratorAgentId = "gen",
                    EvaluatorAgentId = "eval",
                    Iter = 1,
                    Score = ScoringConstants.PassThreshold,
                    Verdict = "pass",
                    DimsJson = "{}",
                    Notes = "Deterministic test pass.",
                    GeneratorSummary = "## Change Summary\nDeterministic test implementation.",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        }

        // Each workstream lays a single deterministic file at a path that
        // depends on the workstream slug. Keeps the test fixture small but
        // gives the artifact-existence check + commit/PR flow real content.
        var slug = workstream.Id.Split('-').Last();
        var path = $"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/{slug}.md";
        var body = new StringBuilder()
            .AppendLine($"# {workstream.Name}")
            .AppendLine()
            .AppendLine($"Workstream: `{workstream.Id}`")
            .AppendLine($"Branch: `{workstream.Branch}`")
            .AppendLine()
            .AppendLine($"Implements the `{slug}` slice of story `{spawn.StoryId}` per the merged plan.")
            .ToString();
        return new Dictionary<string, string>(StringComparer.Ordinal) { [path] = body };
    }

    public Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        // Deterministic strategy has no workspace — return same content as ProduceWorkstreamChangesAsync.
        return this.ProduceWorkstreamChangesAsync(spawn, workstream, ct);
    }

    public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
    {
        // Round 0: post one comment. Round 1+: approve.
        if (round == 0)
        {
            var slug = workstream.Id.Split('-').Last();
            var path = $"src/.story-artifacts/{StoryPaths.Safe(spawn.StoryId)}/{slug}.md";
            return Task.FromResult<IReadOnlyList<ReviewComment>>(new[]
            {
                new ReviewComment(path, 1, "Title looks too generic — please clarify the slice.", "info"),
            });
        }
        return Task.FromResult<IReadOnlyList<ReviewComment>>(Array.Empty<ReviewComment>());
    }

    public Task<ReviewFix> ProduceReviewFixAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
    {
        // Append a clarification line to the workstream artifact and reply.
        var newContent = new StringBuilder()
            .AppendLine($"# {workstream.Name}")
            .AppendLine()
            .AppendLine($"Workstream: `{workstream.Id}`")
            .AppendLine($"Branch: `{workstream.Branch}`")
            .AppendLine()
            .AppendLine($"Implements the `{workstream.Id.Split('-').Last()}` slice of story `{spawn.StoryId}` per the merged plan.")
            .AppendLine()
            .AppendLine("> Clarification: this slice is the concrete delivery of the contract documented in the plan PR; see `architecture.md` for the component diagram.")
            .ToString();
        return Task.FromResult(new ReviewFix(comment.FilePath, newContent, "Done — clarified the slice in the file."));
    }

    /// <summary>
    /// docs/forge/e2e.md Stage 4.2 — deterministic per-stage qa-e2e
    /// emission. Always returns one Gherkin <c>.feature</c> file + one
    /// Reqnroll <c>*Steps.cs</c> file under the
    /// <c>tests/Features/{storyId}/{phaseSlug}/</c> +
    /// <c>tests/Steps/{storyId}/{phaseSlug}/</c> subtrees. For
    /// <c>compose</c> / <c>lab-*</c> setups (read from the merged
    /// <c>e2e-scenarios.md</c> via <see cref="E2eScenariosSetupReader"/>)
    /// also emits an ADO pipeline YAML + a GitHub workflow YAML so the
    /// CI-config-generation path is exercisable without an LLM. Falls
    /// back to <see cref="E2eSetupType.Inline"/> when the doc is absent
    /// or this strategy was constructed without git ops (zero-arg ctor).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        var (phaseSlug, stageSlug) = ResolveQaE2eSlugs(workstream);
        var storySafe = StoryPaths.Safe(spawn.StoryId);
        var setupType = await this.ReadSetupTypeAsync(spawn, phaseSlug, ct);

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        // Always-emitted: one .feature + one Steps.cs per stage.
        var featurePath = $"tests/Features/{storySafe}/{phaseSlug}/{stageSlug}.feature";
        dict[featurePath] = BuildFeatureFile(spawn, workstream, phaseSlug, stageSlug, setupType);

        var stepsClassName = Pascal(stageSlug) + "Steps";
        var stepsPath = $"tests/Steps/{storySafe}/{phaseSlug}/{stepsClassName}.cs";
        dict[stepsPath] = BuildStepsFile(spawn, workstream, phaseSlug, stageSlug, stepsClassName, setupType);

        // docs/forge/e2e.md Stage 7.1 + 7.2 — both compose AND lab
        // setups now run through their respective production
        // renderers. Inline setups skip the pipeline-YAML branch
        // entirely (no infra to spin up). The Stage 7 renderers are
        // shared with the LLM-driven CrewStoryArtifactStrategy so
        // both producers emit byte-identical pipeline files for the
        // same (setupType, phaseSlug, stageSlug) input.
        if (setupType == E2eSetupType.Compose)
        {
            foreach (var (path, body) in E2eComposeRenderer.RenderAll(phaseSlug, stageSlug))
            {
                dict[path] = body;
            }
        }
        else if (setupType is E2eSetupType.LabWsfc or E2eSetupType.LabBareMetal)
        {
            // Stage 7.2: the parsed setup block's `Pre-test bootstrap`
            // bullet may name `tests/e2e/{phaseSlug}/bootstrap.ps1`.
            // The deterministic strategy's setup parsing doesn't surface
            // that field yet; pass false here so the renderer omits the
            // bootstrap step. The LLM strategy (Stage 4.3) can detect
            // it from the resolved PhaseSetup body and pass true.
            foreach (var (path, body) in E2eLabRenderer.RenderAll(setupType, phaseSlug, stageSlug, includeBootstrap: false))
            {
                dict[path] = body;
            }
        }

        // docs/forge/e2e.md Stage 7.3 — always emit the per-phase
        // appsettings.e2e.json; emit the per-spawn E2eBase.cs only on
        // the FIRST qa-e2e workstream for the spawn. TryAdd's atomic
        // semantics over the ConcurrentDictionary make the
        // "first-wins" decision race-safe even when two qa-e2e
        // workstreams for the same spawn fire concurrently.
        var includeBase = this.e2eBaseEmittedForSpawn.TryAdd(spawn.Id, 0);
        foreach (var (path, body) in E2eBaseRenderer.RenderAll(spawn.StoryId, phaseSlug, setupType, includeBaseClass: includeBase))
        {
            dict[path] = body;
        }

        return dict;
    }

    private async Task<E2eSetupType> ReadSetupTypeAsync(StorySpawnRow spawn, string phaseSlug, CancellationToken ct)
    {
        if (this.git is null) return E2eSetupType.Inline;
        if (string.IsNullOrEmpty(spawn.RepoId)) return E2eSetupType.Inline;

        // Synthesise a minimal RepoRow — the deterministic git ops
        // (`InMemoryStoryGitOps`) keys by repo id only, so we don't need
        // the full row from the control DB. Stage 4.3's
        // `CrewStoryArtifactStrategy` reads the full row because the
        // real git CLI needs <c>CloneRoot</c>.
        var repo = new RepoRow { Id = spawn.RepoId, DefaultBranch = "main", Kind = "local" };

        var baseBranch = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        var e2eScenariosPath = $"docs/stories/{StoryPaths.Safe(spawn.StoryId)}/e2e-scenarios.md";

        string? md;
        try
        {
            md = await this.git.ReadFileAsync(repo, baseBranch, e2eScenariosPath, ct);
        }
        catch
        {
            md = null;
        }

        return E2eScenariosSetupReader.ReadSetupTypeForPhase(md, phaseSlug);
    }

    /// <summary>
    /// Split a qa-e2e workstream's PlanAnchor
    /// (<c>phase-{phaseSlug}/stage-{stageSlug}/e2e</c>) into the
    /// component slugs the file paths need. Falls back to safe defaults
    /// when the anchor doesn't follow the convention (defensive — the
    /// reconciler always produces well-formed anchors for qa-e2e rows).
    /// </summary>
    private static (string PhaseSlug, string StageSlug) ResolveQaE2eSlugs(WorkstreamRow workstream)
    {
        var anchor = workstream.PlanAnchor ?? string.Empty;
        if (anchor.EndsWith(CrewStoryArtifactStrategy.QaE2eAnchorSuffix, StringComparison.Ordinal))
        {
            anchor = anchor[..^CrewStoryArtifactStrategy.QaE2eAnchorSuffix.Length];
        }
        var slash = anchor.IndexOf('/');
        var phasePart = slash > 0 ? anchor[..slash] : "phase-unknown";
        var stagePart = slash > 0 ? anchor[(slash + 1)..] : "stage-unknown";
        var phaseSlug = phasePart.StartsWith("phase-", StringComparison.Ordinal)
            ? phasePart["phase-".Length..]
            : phasePart;
        var stageSlug = stagePart.StartsWith("stage-", StringComparison.Ordinal)
            ? stagePart["stage-".Length..]
            : stagePart;
        if (string.IsNullOrEmpty(phaseSlug)) phaseSlug = "unknown";
        if (string.IsNullOrEmpty(stageSlug)) stageSlug = "unknown";
        // Trailing @{hash} from the reconciler's scope-edit suffix —
        // strip so the file path stays stable across plan revs.
        var at = stageSlug.IndexOf('@');
        if (at > 0) stageSlug = stageSlug[..at];
        return (phaseSlug, stageSlug);
    }

    private static string BuildFeatureFile(
        StorySpawnRow spawn, WorkstreamRow workstream,
        string phaseSlug, string stageSlug, E2eSetupType setupType)
    {
        var sb = new StringBuilder()
            .AppendLine($"@story-{spawn.StoryId} @phase-{phaseSlug} @stage-{stageSlug} @setup-{E2eScenariosSetupReader.ToWire(setupType)}")
            .AppendLine($"Feature: {workstream.Name}")
            .AppendLine($"  E2E for Stage `{stageSlug}` of Phase `{phaseSlug}` in story `{spawn.StoryId}`.")
            .AppendLine()
            .AppendLine("  Scenario: AC-1 happy path")
            .AppendLine($"    Given the `{stageSlug}` stage is shipped on main")
            .AppendLine("    When the e2e suite runs against the deployed bits")
            .AppendLine("    Then every acceptance scenario passes");
        return sb.ToString();
    }

    private static string BuildStepsFile(
        StorySpawnRow spawn, WorkstreamRow workstream,
        string phaseSlug, string stageSlug, string className, E2eSetupType setupType)
    {
        var ns = "Forge.Tests.Stories."
            + StoryPaths.Safe(spawn.StoryId).Replace('-', '_')
            + "." + phaseSlug.Replace('-', '_');
        var sb = new StringBuilder()
            .AppendLine("// Auto-generated step definitions (deterministic strategy, Stage 4.2).")
            .AppendLine($"// Story: {spawn.StoryId} / Phase: {phaseSlug} / Stage: {stageSlug}")
            .AppendLine($"// Setup type: {E2eScenariosSetupReader.ToWire(setupType)}")
            .AppendLine()
            .AppendLine($"namespace {ns};")
            .AppendLine()
            .AppendLine("using Reqnroll;")
            .AppendLine()
            .AppendLine("[Binding]")
            .AppendLine($"public class {className}")
            .AppendLine("{")
            .AppendLine($"    [Given(@\"the `{stageSlug}` stage is shipped on main\")]")
            .AppendLine("    public void GivenStageShipped() { /* deterministic stub */ }")
            .AppendLine()
            .AppendLine("    [When(@\"the e2e suite runs against the deployed bits\")]")
            .AppendLine("    public void WhenE2eRuns() { /* deterministic stub */ }")
            .AppendLine()
            .AppendLine("    [Then(@\"every acceptance scenario passes\")]")
            .AppendLine("    public void ThenScenariosPass() { /* deterministic stub */ }")
            .AppendLine("}");
        return sb.ToString();
    }

    [Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]
    public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ws in workstreams)
        {
            var slug = ws.Id.Split('-').Last();
            var path = $"tests/Features/{StoryPaths.Safe(spawn.StoryId)}/{slug}.feature";
            var body = new StringBuilder()
                .AppendLine($"@story-{spawn.StoryId} @ws-{slug}")
                .AppendLine($"Feature: {ws.Name} — {story.Title}")
                .AppendLine()
                .AppendLine("  Scenario: AC-1 happy path")
                .AppendLine($"    Given the {slug} slice is shipped")
                .AppendLine("    When the operator triggers the primary action")
                .AppendLine("    Then the workflow completes end-to-end")
                .ToString();
            dict[path] = body;
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(dict);
    }

    [Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]
    public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
    {
        var slug = workstream.Id.Split('-').Last();
        var path = $"tests/Steps/{StoryPaths.Safe(spawn.StoryId)}/{slug}Steps.cs";
        var body = new StringBuilder()
            .AppendLine("// Auto-generated step definitions for Phase 28 spawn (deterministic strategy).")
            .AppendLine($"// Workstream: {workstream.Id}")
            .AppendLine()
            .AppendLine($"namespace Forge.Tests.Stories.{StoryPaths.Safe(spawn.StoryId).Replace('-', '_')};")
            .AppendLine()
            .AppendLine("using Reqnroll;")
            .AppendLine()
            .AppendLine("[Binding]")
            .AppendLine($"public class {Pascal(slug)}Steps")
            .AppendLine("{")
            .AppendLine($"    [Given(@\"the {slug} slice is shipped\")]")
            .AppendLine("    public void GivenSliceShipped() { /* deterministic stub */ }")
            .AppendLine()
            .AppendLine("    [When(@\"the operator triggers the primary action\")]")
            .AppendLine("    public void WhenOperatorTriggers() { /* deterministic stub */ }")
            .AppendLine()
            .AppendLine("    [Then(@\"the workflow completes end-to-end\")]")
            .AppendLine("    public void ThenWorkflowCompletes() { /* deterministic stub */ }")
            .AppendLine("}")
            .ToString();
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal) { [path] = body });
    }

    /// <summary>
    /// Phase 79 — deterministic stub for the conflict resolver. Tests
    /// that exercise the merge-conflict-resolve path provide a custom
    /// fake; the default implementation strips conflict markers
    /// keeping "ours", producing a deterministic but trivial output.
    /// Real strategy in <c>CrewStoryArtifactStrategy</c> dispatches the
    /// Engineer agent.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
        StorySpawnRow spawn,
        WorkstreamRow workstream,
        IReadOnlyList<ConflictedFile> conflictedFiles,
        string baseBranch,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in conflictedFiles)
        {
            result[f.Path] = StripConflictMarkers(f.Content);
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    private static string StripConflictMarkers(string content)
    {
        var sb = new StringBuilder();
        var lines = content.Split('\n');
        var inConflict = false;
        var keepOurs = true;
        foreach (var line in lines)
        {
            if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                inConflict = true;
                keepOurs = true;
                continue;
            }
            if (line.StartsWith("=======", StringComparison.Ordinal))
            {
                keepOurs = false;
                continue;
            }
            if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                inConflict = false;
                keepOurs = true;
                continue;
            }
            if (inConflict && !keepOurs) continue;
            sb.AppendLine(line.TrimEnd('\r'));
        }
        return sb.ToString();
    }

    private static string Pascal(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return "Ws";
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(p[0])).Append(p.AsSpan(1));
        }
        return sb.Length == 0 ? "Ws" : sb.ToString();
    }
}
