// -----------------------------------------------------------------------
// <copyright file="SyncConstants.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Phase 23 + 24 — canonical constants for the scheduled sync jobs.
/// Values are pinned here so the spec-consistency tests can assert them
/// verbatim against the prose in <c>forge_implementation.md</c>.
/// </summary>
public static class SyncConstants
{
    /// <summary>ADO first sync pulls items changed in the last 30 days.</summary>
    public const int AdoInitialWindowDays = 30;

    /// <summary>GitHub first sync pulls items updated in the last 30 days; mirrors ADO so providers behave consistently.</summary>
    public const int GithubInitialWindowDays = 30;

    /// <summary>ADO hard cap: 200 IDs per <c>_apis/wit/workitems?ids=…</c> request (Q23.6).</summary>
    public const int AdoMaxIdsPerWorkItemBatch = 200;

    /// <summary>ADO WIQL returns at most 20,000 work-item references (Q23.6).</summary>
    public const int AdoMaxWiqlReferences = 20000;

    /// <summary>GitHub issues endpoint page size (Q23.6).</summary>
    public const int GithubPerPage = 100;

    /// <summary>Default sync interval in seconds — 2 hours. Per-repo overrides land via <c>RepoRow.WorkItemSyncIntervalSeconds</c>.</summary>
    public const int WorkItemSyncIntervalSeconds = 7200;

    /// <summary>Maximum repos syncing in parallel per tick (Q23.2).</summary>
    public const int WorkItemSyncMaxParallel = 4;

    /// <summary>A repo with <c>LastSyncStatus='running'</c> for longer than this is considered stale (Q23.2).</summary>
    public const int RunningTimeoutMinutes = 19;

    /// <summary>The tag/label literal that triggers auto-team-assign (Q23.8).</summary>
    public const string AgentReadyFlag = "agent-ready";

    /// <summary>Default team name when <c>Repo.AgentReadyDefaultTeam</c> is null (Q23.8).</summary>
    public const string AgentReadyDefaultTeam = "Engineering";

    /// <summary>Phase 23.4 — cap the upstream <c>Retry-After</c> header at 60s before retrying (Q23.6).</summary>
    public const int MaxRetryAfterSeconds = 60;

    /// <summary>Phase 23.5 — sleep until <c>X-RateLimit-Reset</c> when remaining drops below this number (Q23.6).</summary>
    public const int GithubRateLimitFloor = 100;

    /// <summary>Phase 24 — base tick interval for the PR sync loop (2 hr). Per-repo overrides land via <c>RepoRow.PrSyncIntervalSeconds</c>.</summary>
    public const int PrSyncIntervalSeconds = 7200;

    /// <summary>Phase 24 — max parallel PR sync operations per tick.</summary>
    public const int PrSyncMaxParallel = 8;

    /// <summary>Phase 24 — comment page size per provider request.</summary>
    public const int PrCommentPageSize = 100;

    // ─── forge_implementation.md Phase 27 — build sync ────────────────────

    /// <summary>Phase 27 Q27.1 — build sync tick interval, default 2 hr. Per-repo overrides land via <c>RepoRow.BuildSyncIntervalSeconds</c>.</summary>
    public const int BuildSyncIntervalSeconds = 7200;

    /// <summary>Phase 27 Q27.1 — max parallel build syncs per tick.</summary>
    public const int BuildSyncMaxParallel = 8;

    /// <summary>Phase 27 Q27.4 — per-stage log tail in bytes (256 KB).</summary>
    public const int BuildLogTailBytes = 262144;

    /// <summary>Phase 27 Q27.5 — per-artifact size cap, megabytes.</summary>
    public const int BuildArtifactSizeCapMb = 256;

    /// <summary>Phase 27 Q27.5 — per-host artifact storage cap, gigabytes.</summary>
    public const int BuildArtifactStorageCapGb = 10;

    /// <summary>Phase 27 Q27.5 — artifact retention, days. Janitor evicts older rows.</summary>
    public const int BuildArtifactRetentionDays = 30;

    // ─── 2026-05-07 — task transcript retention ───────────────────────────
    // Bound `task_transcript_event` growth by retaining only the last K
    // iterations of transcript per work item. See
    // `TranscriptRetentionJanitor` for the policy and rationale.

    /// <summary>Iterations of transcript kept for OPEN (non-completed) work items.</summary>
    public const int TranscriptKeepLastItersForOpen = 10;

    /// <summary>Iterations of transcript kept for CLOSED/COMPLETED work items.</summary>
    public const int TranscriptKeepLastItersForClosed = 1;

    /// <summary>Janitor tick interval — 6 hours. Pruning is incremental, so cheaper than a daily-only run.</summary>
    public const int TranscriptJanitorIntervalSeconds = 21_600;

    /// <summary>
    /// Per-statement row cap for the bounded delete loop. Keeps the SQLite
    /// write lock short so the live runner can keep inserting transcript
    /// events alongside the prune.
    /// </summary>
    public const int TranscriptPruneBatchSize = 5_000;

    /// <summary>
    /// Phase 27 Q27.6 — locked failure-detection regex patterns. The
    /// spec-consistency test pulls Phase 27 §Q27.6 prose and asserts each
    /// entry verbatim. Order is significant — the parser stops at the first
    /// match per line, so the more specific patterns precede the general
    /// "Error … at file:line" stack-trace fallback.
    /// </summary>
    public static readonly string[] BuildFailurePatterns =
    {
        // .NET compile errors: "error CS1234: identifier not found [path/to/file.cs:42]"
        @"error CS\d+:\s+(.+?) \[(.+?):(\d+)\]",
        // cmake / make: "FAILED: src/foo.o"
        @"FAILED:?\s+(.+?)$",
        // generic stack-trace: "Error: foo at /path/file.cs:42"
        @"Error:?\s+(.+?)\sat\s+(.+?):(\d+)",
        // test framework assertion: "assert.fail at test/foo.spec.ts:88"
        @"assert.*?fail.*?at\s+(.+?):(\d+)",
        // npm: "npm ERR! code ELIFECYCLE"
        @"npm ERR! code (.+?)$",
    };

    /// <summary>
    /// Phase 27 — `task.iteration.*` budget for the build-fix loop. Same hard
    /// 5-iter cap as the implementation pipeline (Q27.8).
    /// </summary>
    public const int BuildFixMaxIterations = 5;

    // ─── 2026-05-07 — orchestrator in-flight guard ───────────────────────
    // The 15s `SpawnDependencyScheduler` tick was firing fresh agent runs
    // on workstreams that already had a `task.impl.spawn-started` event in
    // the recent past with no closing `task.impl.spawn-finished` event.
    // When the agent CLI subprocess crashed mid-stream (System.IO.IOException:
    // CLI process exited unexpectedly), `DriveOneWorkstreamImplAsync` threw,
    // the catch logged "impl failed; will retry next tick", and 15s later
    // the next tick spawned a fresh attempt — burning ~$25 of tokens every
    // cycle on a generator turn that died before the evaluator could even
    // score. Observed on WI-HIGH-MEM-CONSUMPTION iter 38 (25 spawns over
    // ~80 min before manual cancel). The guard below makes the orchestrator
    // skip a tick when an unfinished spawn is younger than the per-WI budget
    // plus slack.

    /// <summary>
    /// Floor for the in-flight spawn-guard window when the per-workstream
    /// budget is small. A workstream with very few points still needs
    /// enough time for the Copilot SDK 5-min inactivity watchdog plus
    /// retry slack to fire before the orchestrator tries again.
    /// </summary>
    public const int InflightSpawnGuardMinSeconds = 480;

    /// <summary>
    /// Extra padding added to <c>BudgetForPhase("impl")</c> when computing
    /// the in-flight spawn-guard window. Covers SDK shutdown + cleanup +
    /// the small clock skew between agent dispatch and the
    /// <c>task.impl.spawn-started</c> event landing in the transcript.
    /// </summary>
    public const int InflightSpawnGuardBudgetSlackSeconds = 120;

    // ─── 2026-05-07 — failure backoff ────────────────────────────────────
    // When `DriveOneWorkstreamImplAsync` throws, we emit a
    // `task.impl.spawn-finished` transcript event with `success=false` so
    // the in-flight guard releases. But releasing immediately would let the
    // next 15s tick respawn — exactly the storm we're trying to stop.
    // Apply a backoff between the failure and the next allowed spawn,
    // scaled by consecutive failures so a runaway crash escalates quickly.

    /// <summary>
    /// Base backoff in seconds after a `task.impl.spawn-finished` with
    /// success=false. Multiplied by the count of consecutive failures
    /// (capped at <see cref="FailureBackoffMaxSeconds"/>) before the next
    /// spawn is allowed.
    /// </summary>
    public const int FailureBackoffBaseSeconds = 90;

    /// <summary>
    /// Hard cap on the backoff window so a workstream that's been failing
    /// for hours doesn't go silent indefinitely. The operator can still
    /// cancel + restart from the UI to break the loop sooner.
    /// </summary>
    public const int FailureBackoffMaxSeconds = 1800;
}
