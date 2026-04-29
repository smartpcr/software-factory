// -----------------------------------------------------------------------
// <copyright file="SyncConstants.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Phase 24 — tuning knobs for the PR sync sweep.
/// </summary>
public static class SyncConstants
{
    /// <summary>Base tick interval for the PR sync loop (20 min).</summary>
    public const int PrSyncIntervalSeconds = 1200;

    /// <summary>Max parallel PR sync operations per tick.</summary>
    public const int PrSyncMaxParallel = 8;

    /// <summary>Comment page size per provider request.</summary>
    public const int PrCommentPageSize = 100;
}
