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

    /// <summary>QA's `.feature` files for the spawn.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
        StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default);

    /// <summary>Engineer's step-definition files for a single workstream after the QA PR merges.</summary>
    Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
        StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default);
}

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
    IReadOnlyList<string>? UnresolvedDeps = null);

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
