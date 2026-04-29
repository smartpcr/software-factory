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

    /// <summary>Phase 24 — base tick interval for the PR sync loop (20 min).</summary>
    public const int PrSyncIntervalSeconds = 1200;

    /// <summary>Phase 24 — max parallel PR sync operations per tick.</summary>
    public const int PrSyncMaxParallel = 8;

    /// <summary>Phase 24 — comment page size per provider request.</summary>
    public const int PrCommentPageSize = 100;
}
