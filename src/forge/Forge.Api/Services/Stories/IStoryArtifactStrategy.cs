// -----------------------------------------------------------------------
// <copyright file="IStoryArtifactStrategy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 28 — pluggable strategy for the artifacts
/// each crew produces during a story spawn. The default implementation
/// generates deterministic, realistic markdown / feature / step files so
/// integration tests can drive the full lifecycle without an LLM. A
/// production deployment can swap a different binding into DI to call a
/// real generator (e.g. crew agents via <c>IAgentRunner</c>).
/// </summary>
public interface IStoryArtifactStrategy
{
    /// <summary>
    /// Architect's plan-phase artifacts. Returns relative-path → file content.
    /// When <paramref name="forceResume"/> is true, the workspace-cache
    /// short-circuit (which skips the LLM if the file is already on disk)
    /// is bypassed — used by the spawn-retry endpoint after a non-pass
    /// verdict so the architect crew re-iterates instead of replaying the
    /// stalled artifact unchanged.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
        StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default);

    /// <summary>
    /// Phase 53 — produce ONE plan-doc artifact on a caller-owned branch
    /// + worktree. Used by the orchestrator's sequential plan path: it
    /// creates the per-doc branch + worktree, calls this once for the
    /// active doc, then commits + opens the per-doc PR itself. Returns
    /// the repo-relative file path and the file content.
    ///
    /// <para><paramref name="docKey"/> is one of
    /// <c>architecture</c> / <c>tech-spec</c> / <c>implementation-plan</c>
    /// / <c>e2e-scenarios</c> (the canonical order in
    /// <see cref="PlanDocStateHelpers.PlanDocOrder"/>).
    /// <paramref name="forceResume"/> behaves identically to its
    /// <see cref="ProducePlanArtifactsAsync"/> counterpart but is scoped
    /// to the single active doc — already-merged upstream docs are not
    /// touched. Sibling docs for the agent prompt are computed from the
    /// other three canonical keys.</para>
    /// </summary>
    Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
        StorySpawnRow spawn,
        WorkItemRow story,
        string docKey,
        string worktreePath,
        string planBranch,
        RepoRow? repo,
        bool forceResume = false,
        CancellationToken ct = default);

    /// <summary>
    /// Resume a single plan-doc artifact's gen↔eval loop. Unlike
    /// <see cref="ProducePlanArtifactsAsync"/> (which runs all 4 docs),
    /// this re-runs only the specified <paramref name="key"/> with
    /// forceResume semantics (workspace cache bypassed). Used by the
    /// per-doc Resume button on the conversation tab.
    ///
    /// <para>When <paramref name="restartFromScratch"/> is <c>true</c>,
    /// the strategy MUST treat this as an operator-initiated Restart
    /// (not a resume): delete any existing on-disk doc file in the
    /// plan-branch worktree and purge any stale per-doc DB rows
    /// (deliverables / transcripts / queue) BEFORE the architect's
    /// first iteration, so the agent regenerates the file from scratch
    /// instead of incrementally editing the previous run's content.
    /// Defaults to <c>false</c> so the ordinary Resume path
    /// (workspace-cache bypass for a mid-iter resumption) is preserved.
    /// Set by <c>POST /api/stories/{id}/spawn/artifacts/{key}/restart</c>
    /// via <c>StoriesEndpoints.RunResumeWithCleanupAsync</c>.</para>
    /// </summary>
    Task ResumeSingleArtifactAsync(
        StorySpawnRow spawn,
        WorkItemRow story,
        string key,
        bool restartFromScratch = false,
        CancellationToken ct = default);

    /// <summary>Architect's decomposition — split the plan into N parallel workstreams.</summary>
    Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
        StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default);

    /// <summary>Engineer's per-workstream change-set. Returns relative-path → file content.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);

    /// <summary>
    /// Read cached workspace files for a workstream whose gen/eval loop
    /// already passed. Used by the orchestrator to commit + open PR without
    /// re-running the loop.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);

    /// <summary>Reviewer's comments to post on a workstream PR. Empty = approve.</summary>
    Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default);

    /// <summary>Engineer's reply / fix to a single open review comment.</summary>
    Task<ReviewFix> ProduceReviewFixAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default);

    /// <summary>
    /// docs/forge/e2e.md Stage 4.1 — produce every artifact a per-stage
    /// qa-e2e workstream owns: one Gherkin <c>.feature</c> file plus its
    /// matching step-definition source AND (for <c>compose</c> / <c>lab-*</c>
    /// setup types per docs/forge/e2e.md §8) the pipeline YAML stubs the
    /// CI runner needs.
    ///
    /// <para>Return contract — keys are repo-relative paths:</para>
    /// <list type="bullet">
    /// <item><c>tests/Features/{storyId}/{phaseSlug}/{stageSlug}.feature</c>
    /// — Gherkin scenarios derived from the stage's
    /// <c>### Test Scenarios</c> block. Always emitted.</item>
    /// <item><c>tests/Steps/{storyId}/{phaseSlug}/{stageSlug}Steps.cs</c>
    /// — Reqnroll <c>[Binding]</c> step-defs that exercise the impl
    /// workstream's changes. Always emitted.</item>
    /// <item><c>tests/e2e/{phaseSlug}/docker-compose.yml</c> +
    /// <c>tests/e2e/{phaseSlug}/azure-pipeline.yml</c> +
    /// <c>.github/workflows/e2e-{phaseSlug}.yml</c> — emitted ONLY when
    /// the parent phase's <c>e2e-scenarios.md</c> <c>### Setup</c> block
    /// declares <c>Type: compose</c> or <c>Type: lab-*</c>.</item>
    /// </list>
    ///
    /// <para>Called by the orchestrator's Stage 5.1 impl-driver branch
    /// for any workstream where <see cref="WorkstreamRow.Category"/> is
    /// <see cref="WorkstreamCategories.QaE2e"/>. The new code path
    /// supersedes the legacy <see cref="ProduceQaFeatureFilesAsync"/>
    /// (story-batch feature files) + <see cref="ProduceStepDefinitionsAsync"/>
    /// (per-ws step-defs) pair, which run only against paused
    /// <c>Phase='qa'</c> legacy spawns for back-compat.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);

    /// <summary>QA's `.feature` files for the spawn.</summary>
    /// <remarks>
    /// docs/forge/e2e.md Stage 4.1 — replaced by
    /// <see cref="ProduceStageE2eAsync"/>, which emits per-stage feature
    /// + step-def + pipeline artifacts in one call. Retained for one
    /// release so paused legacy <c>Phase='qa'</c> spawns drain through
    /// <c>StorySpawnOrchestrator.DriveQaAsync</c> unchanged; removed in
    /// the Phase R cleanup.
    /// </remarks>
    [Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]
    Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default);

    /// <summary>Engineer's step-definition files for a single workstream after the QA PR merges.</summary>
    /// <remarks>
    /// docs/forge/e2e.md Stage 4.1 — replaced by
    /// <see cref="ProduceStageE2eAsync"/>. Retained for legacy-drain
    /// only; removed in the Phase R cleanup.
    /// </remarks>
    [Obsolete("Use ProduceStageE2eAsync (docs/forge/e2e.md Stage 4.1). Removed in Phase R cleanup.")]
    Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);

    /// <summary>
    /// Phase 79 — agent-driven merge-conflict resolver. Called by
    /// <c>StorySpawnOrchestrator.TryResolveConflictsAndRetryMergeAsync</c>
    /// when an auto-merge attempt failed with a real merge-conflict 405
    /// AND <see cref="IStoryGitOps.MergeBaseIntoBranchAndDetectConflictsAsync"/>
    /// reported <see cref="MergeState.Conflicted"/>. The Engineer agent
    /// reads each <see cref="ConflictedFile"/>'s on-disk content
    /// (markers preserved), the workstream's original task description,
    /// and a tail of the workstream's recent deliverable Notes (so it
    /// knows its own intent), then writes a resolved version of each
    /// file. The returned dict maps path → resolved content; the
    /// orchestrator hands this to
    /// <see cref="IStoryGitOps.ContinueMergeAndPushAsync"/>.
    ///
    /// <para>Per-file fast-path: lockfile / generated-file paths
    /// (<c>*.lock</c>, <c>package-lock.json</c>, <c>Cargo.lock</c>,
    /// <c>yarn.lock</c>, etc.) are NOT sent to the agent. The strategy
    /// regenerates them via the repo's
    /// <see cref="Forge.Api.Persistence.RepoRow.RestoreCommand"/> instead;
    /// agents are bad at semantic merges of large dependency manifests.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
        StorySpawnRow spawn,
        WorkstreamRow workstream,
        IReadOnlyList<ConflictedFile> conflictedFiles,
        string baseBranch,
        CancellationToken ct = default);

    /// <summary>
    /// 2026-05-27 recovery path — score an ALREADY-MERGED workstream's landed
    /// code with the evaluator. Used by <c>MergedPrRescoreReconciler</c> when a
    /// workstream's PR merged but its latest deliverable is a non-pass (the
    /// dominant case: the generator hit its wall-clock budget and the
    /// gen↔eval loop force-scored 0 / verdict=iterate even though the work
    /// actually landed and merged). The evaluator opens the merged files in a
    /// git worktree on the workstream/base branch and returns a real score so
    /// the work item can close (score ≥ <see cref="ScoringConstants.PassThreshold"/>)
    /// or a follow-up can be filed (&lt; threshold) — instead of the system
    /// re-driving merged work into the same timeout forever.
    ///
    /// <para>This is an evaluator-ONLY invocation: no generator runs and no
    /// files are written to the PR. <paramref name="mergedPrId"/> is the
    /// <c>PrRow.Id</c> of the merged PR whose changes should be scored.</para>
    /// </summary>
    Task<MergedPrScore> ScoreMergedPrAsync(
        StorySpawnRow spawn,
        WorkstreamRow workstream,
        string mergedPrId,
        CancellationToken ct = default);
}

/// <summary>
/// 2026-05-27 — return shape of
/// <see cref="IStoryArtifactStrategy.ScoreMergedPrAsync"/>. Mirrors the
/// fields the gen↔eval loop persists on a <c>DeliverableRow</c> so the
/// reconciler can write a deliverable that <see cref="StorySpawnOrchestrator.ClassifyPassGate"/>
/// treats identically to a natural pass when the score clears the threshold.
/// </summary>
/// <param name="Score">0–100 evaluator score for the landed code.</param>
/// <param name="Dims">Per-dimension breakdown (correctness/quality/tests/scope/risk); may be empty when the evaluator omitted it.</param>
/// <param name="Verdict">Derived verdict (<c>pass</c> when <see cref="Score"/> ≥ <see cref="ScoringConstants.PassThreshold"/>, else <c>iterate</c>).</param>
/// <param name="Notes">Evaluator notes / rationale; surfaced on the deliverable and (sub-pass) as the follow-up signal.</param>
/// <param name="EvalCostUsd">Best-effort USD cost of the evaluator invocation (0 when the runner did not report it; the authoritative figure is in <c>agent_usage</c>).</param>
/// <param name="EvaluatorAgentId">The agent that produced the score (for the deliverable's <c>EvaluatorAgentId</c> stamp).</param>
/// <param name="ParseFailed">True when the evaluator's output could not be parsed into a score; the reconciler then leaves the workstream untouched for the next tick rather than writing a bogus 0.</param>
public sealed record MergedPrScore(
    int Score,
    IReadOnlyDictionary<string, int> Dims,
    string Verdict,
    string Notes,
    decimal EvalCostUsd,
    string? EvaluatorAgentId,
    bool ParseFailed = false);

/// <summary>
/// One workstream entry in a decomposition. <see cref="PlanAnchor"/> +
/// <see cref="PlanContentHash"/> (Phase 42) are populated when the entry came
/// from <c>implementation-plan.md</c>; null on the LLM-only fallback path.
/// When non-null, <see cref="StorySpawnOrchestrator.PersistDecompositionAsync"/>
/// uses the anchor to build a stable workstream id so the reconciler can
/// later find the row.
///
/// <para>Phase 46 — <see cref="PhaseAnchor"/> is the cached
/// <c>phase-{slug}</c> prefix of <see cref="PlanAnchor"/>; the swarm
/// DAG view uses it to group stage cards into collapsible phase
/// containers without re-parsing the plan. <see cref="UnresolvedDeps"/>
/// holds dep anchor refs that didn't resolve to a known phase or stage
/// anchor; the UI surfaces them as a yellow "unresolved" banner.</para>
/// </summary>
public sealed record DecompositionWorkstream(
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Deps,
    IReadOnlyList<string> Deliverables,
    string Timeline,
    string EstimatedEffort,
    string? PlanAnchor = null,
    string? PlanContentHash = null,
    string? PhaseAnchor = null,
    IReadOnlyList<string>? UnresolvedDeps = null,
    string Category = WorkstreamCategories.Impl);

/// <summary>
/// docs/forge/e2e.md Stage 1.2 — closed set of values for
/// <see cref="DecompositionWorkstream.Category"/> /
/// <c>WorkstreamRow.Category</c>. Centralised so the YAML round-trip,
/// orchestrator persist path, and (later) the qa-e2e routing all
/// compare against the same string.
/// </summary>
public static class WorkstreamCategories
{
    /// <summary>Normal Stage implementation work (default). Every workstream
    /// produced by the decomposer was "impl" before Stage 1.2; the column
    /// backfills to this value.</summary>
    public const string Impl = "impl";

    /// <summary>Trailing per-Stage QA workstream owning Gherkin
    /// <c>.feature</c> files, step definitions, and the e2e run for a
    /// single Stage. Emitted by the decomposer (Stage 3.1) when a Stage
    /// declares <c>### Test Scenarios</c>; routed through a different
    /// orchestrator path in Stage 5.1.</summary>
    public const string QaE2e = "qa-e2e";

    /// <summary>The full set, exposed for validators / linters / tests
    /// that need to enumerate or assert membership.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Impl, QaE2e };

    /// <summary>True when <paramref name="value"/> is one of the known
    /// categories (case-sensitive — the column stores lower-kebab).</summary>
    public static bool IsValid(string? value) =>
        value is not null && (value == Impl || value == QaE2e);
}

/// <summary>One review-comment payload the strategy wants posted.</summary>
public sealed record ReviewComment(string FilePath, int Line, string Body, string Severity);

/// <summary>Context handed to the engineer when responding to a review comment.</summary>
public sealed record ReviewCommentContext(string FilePath, int Line, string Body, string AuthoringAgentId);

/// <summary>Engineer's response: a single file edit + a reply body.</summary>
/// <summary>
/// Phase 28 — output of <see cref="IStoryArtifactStrategy.ProduceReviewFixAsync"/>.
///
/// <para>Phase 49 follow-up — the engineer agent now picks ONE of two
/// shapes per reviewer comment:</para>
/// <list type="number">
/// <item><b>Code change</b> — both <see cref="FilePath"/> and
///   <see cref="NewContent"/> are non-null; orchestrator commits the
///   file and posts <see cref="ReplyBody"/> as a thread reply
///   explaining what changed.</item>
/// <item><b>Reply-only</b> — <see cref="FilePath"/> and
///   <see cref="NewContent"/> are null; orchestrator skips the commit
///   and posts <see cref="ReplyBody"/> explaining why no change is
///   needed (comment is wrong, out of scope, addressed elsewhere,
///   intentional, etc.).</item>
/// </list>
/// <para><see cref="ReplyBody"/> is ALWAYS required so every reviewer
/// comment gets an explicit response on the PR thread, never silently
/// resolved without explanation.</para>
/// </summary>
public sealed record ReviewFix(string? FilePath, string? NewContent, string ReplyBody)
{
    /// <summary>True when no code change is committed — the response is
    /// the <see cref="ReplyBody"/> alone.</summary>
    public bool IsReplyOnly => string.IsNullOrEmpty(FilePath) || NewContent is null;
}

/// <summary>
/// Thrown by <see cref="IStoryArtifactStrategy.ProduceWorkstreamChangesAsync"/>
/// when the engineer agent has produced zero file changes for several
/// consecutive iterations. Treated by the orchestrator as a triage signal:
/// the workstream is moved to <c>Status="stalled"</c> (Phase 49 — was
/// <c>"blocked"</c> before the split), the gen↔eval loop is not re-spawned,
/// and the operator must <c>Restart</c> the workstream to retry. Exists to
/// short-circuit the livelock observed 2026-05-09 where one workstream burned
/// $9,825 across 139 zero-diff iterations because the orchestrator only
/// checked for changes after the full <c>maxIter</c> run completed and the
/// resume logic restarted the loop from <c>iter=1</c> on every <c>stalled</c>
/// verdict.
/// </summary>
public sealed class NoProgressException : Exception
{
    public NoProgressException(string message) : base(message) { }

    /// <summary>How many consecutive zero-change iterations triggered the abort.</summary>
    public int ConsecutiveIterations { get; init; }

    /// <summary>The iteration number that triggered the abort (last zero-change iter).</summary>
    public int LastIteration { get; init; }
}

/// <summary>
/// Thrown by <c>StorySpawnOrchestrator.ApproveAndMergeAsync</c> when the PR's
/// branch carries no diff vs its base — merging it would land nothing and
/// falsely mark the stage "done with tests". Observed on
/// <c>local:REFACTOR-INTEGRATION</c> 2026-05-20: qa-e2e PRs opened before the
/// open-side empty-PR guard existed got auto-merged empty, marking stages
/// complete with zero e2e artifacts. The merge caller catches this distinctly
/// from a merge conflict (which routes to the resolver) and stalls the
/// workstream with a clear reason instead.
/// </summary>
public sealed class EmptyPrMergeRefusedException : Exception
{
    public EmptyPrMergeRefusedException(string prId, string branch, string baseBranch)
        : base($"Refused to merge PR {prId}: branch '{branch}' has no diff vs '{baseBranch}'.")
    {
        this.PrId = prId;
        this.Branch = branch;
        this.BaseBranch = baseBranch;
    }

    public string PrId { get; }

    public string Branch { get; }

    public string BaseBranch { get; }
}

/// <summary>
/// Phase 47e — thrown when the workstream's worktree fails its build gate.
/// Routed by the orchestrator to <c>Status="stalled"</c> (Phase 49 split —
/// was <c>"blocked"</c> before) on the same path as
/// <see cref="NoProgressException"/>: re-spawning would just re-burn
/// agent budget on a non-compiling diff that the evaluator silently passed.
/// Two trip points produce this exception:
/// <list type="number">
///   <item>
///     <description>
///       <b>Per-iter</b> — three consecutive iterations whose post-generator
///       worktree has uncommitted changes that don't compile. Caught early
///       so the loop doesn't burn the full <c>maxIter</c> budget on broken code.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Post-pass</b> — the evaluator scored the iteration as <c>pass</c>,
///       but a fresh <c>dotnet build</c> still fails. The compiler is the
///       authoritative source of truth here; the evaluator cannot see
///       compile errors on a deleted/renamed type.
///     </description>
///   </item>
/// </list>
/// </summary>
public sealed class BuildFailedException : Exception
{
    public BuildFailedException(string message) : base(message) { }

    /// <summary><c>dotnet build</c> exit code.</summary>
    public int ExitCode { get; init; }

    /// <summary>Number of <c>): error CSnnnn:</c> lines parsed from the build output.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Number of <c>): warning CSnnnn:</c> lines parsed from the build output.</summary>
    public int WarningCount { get; init; }

    /// <summary>First few error lines, capped at ~2400 chars; suitable for transcript display.</summary>
    public string ErrorSummary { get; init; } = string.Empty;

    /// <summary>True when the trip point was per-iter (consecutive broken-build counter); false for post-pass single fail.</summary>
    public bool TrippedByConsecutiveIters { get; init; }

    /// <summary>How many consecutive broken-build iters (per-iter trip) or 0 (post-pass trip).</summary>
    public int ConsecutiveBrokenIters { get; init; }

    /// <summary>The iteration number that triggered the abort.</summary>
    public int LastIteration { get; init; }

    /// <summary>
    /// Phase 56 — which gate stage tripped: <c>"build"</c>, <c>"quality"</c>
    /// (fmt/clippy/lint), or <c>"test"</c>. <c>null</c> on legacy /
    /// build-only paths that pre-date the unified gate chain.
    /// </summary>
    public string? Stage { get; init; }

    /// <summary>
    /// Phase 56 — exact command that tripped the gate (e.g.
    /// <c>cargo fmt --check --all</c>). Used to enrich the workstream's
    /// <c>StallReason</c> so operators can see WHICH command failed
    /// without spelunking the transcript.
    /// </summary>
    public string? Command { get; init; }
}
