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
    /// <summary>ADO first sync pulls items changed in the last 365 days (Q23.5).</summary>
    public const int AdoInitialWindowDays = 365;

    /// <summary>ADO hard cap: 200 IDs per <c>_apis/wit/workitems?ids=…</c> request (Q23.6).</summary>
    public const int AdoMaxIdsPerWorkItemBatch = 200;

    /// <summary>ADO WIQL returns at most 20,000 work-item references (Q23.6).</summary>
    public const int AdoMaxWiqlReferences = 20000;

    /// <summary>GitHub issues endpoint page size (Q23.6).</summary>
    public const int GithubPerPage = 100;

    /// <summary>Default sync interval in seconds — 20 minutes (Q23.1).</summary>
    public const int WorkItemSyncIntervalSeconds = 1200;

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

    /// <summary>Phase 24 — base tick interval for the PR sync loop (20 min).</summary>
    public const int PrSyncIntervalSeconds = 1200;

    /// <summary>Phase 24 — max parallel PR sync operations per tick.</summary>
    public const int PrSyncMaxParallel = 8;

    /// <summary>Phase 24 — comment page size per provider request.</summary>
    public const int PrCommentPageSize = 100;

    // ─── forge_implementation.md Phase 27 — build sync ────────────────────

    /// <summary>Phase 27 Q27.1 — build sync tick interval, default 20 min.</summary>
    public const int BuildSyncIntervalSeconds = 1200;

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
}
