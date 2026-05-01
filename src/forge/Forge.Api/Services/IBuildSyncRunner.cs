// -----------------------------------------------------------------------
// <copyright file="IBuildSyncRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 27 — provider abstraction for build sync.
/// One implementation per provider (<see cref="AdoBuildSyncRunner"/> /
/// <see cref="GitHubBuildSyncRunner"/>); <see cref="BuildSyncJob"/> resolves
/// the right implementation by <see cref="Provider"/>.
/// <para>
/// Two surface methods:
/// </para>
/// <list type="number">
/// <item><see cref="ListBuildsForBranchAsync"/> — list every build (running or
/// terminal) for the PR's branch. Used to upsert the <c>builds</c> table.</item>
/// <item><see cref="FetchStageDetailAsync"/> — for a build that just transitioned
/// to terminal-and-failed, fetch the per-stage timeline + log tails. Skips
/// successful builds entirely (Q27.3).</item>
/// </list>
/// </summary>
public interface IBuildSyncRunner
{
    /// <summary>One of <c>ado</c> | <c>github</c>.</summary>
    string Provider { get; }

    /// <summary>True when the runner has the credentials it needs.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Q27.4 / Q27.5 — list builds for a PR's source branch. Returns one
    /// snapshot per upstream build run; the caller upserts into the
    /// <c>builds</c> table keyed (<c>provider</c>, <c>providerBuildId</c>).
    /// </summary>
    Task<IReadOnlyList<BuildSyncSnapshot>> ListBuildsForBranchAsync(
        RepoRow repo, string branch, CancellationToken ct);

    /// <summary>
    /// Q27.4 — fetch per-stage timeline and tail-truncated logs for a
    /// terminal-and-failed build. Returns one <see cref="BuildStageSnapshot"/>
    /// per failed leaf node. Successful builds should never call this method.
    /// </summary>
    Task<IReadOnlyList<BuildStageSnapshot>> FetchStageDetailAsync(
        RepoRow repo, string providerBuildId, CancellationToken ct);

    /// <summary>
    /// Q27.5 — list per-build artifacts. Used as a fallback by Pass 1.5 of
    /// the failure parser when the per-stage tail returns no parsed
    /// failures.
    /// </summary>
    Task<IReadOnlyList<BuildArtifactSnapshot>> ListArtifactsAsync(
        RepoRow repo, string providerBuildId, CancellationToken ct);
}

/// <summary>
/// Phase 27 — flat snapshot of one upstream build run, ready to upsert into
/// the <c>builds</c> table.
/// </summary>
public sealed record BuildSyncSnapshot(
    string Provider,
    string ProviderBuildId,
    string? PipelineId,
    string? PipelineName,
    string State,
    string? Result,
    string? TriggeringCommit,
    string? Branch,
    string? WebUrl,
    string? StartedAt,
    string? FinishedAt);

/// <summary>
/// Phase 27 — flat snapshot of one failed stage, ready to upsert into the
/// <c>build_stages</c> table along with its tail-truncated log.
/// </summary>
public sealed record BuildStageSnapshot(
    string ProviderStageRef,
    string StageName,
    int StageOrder,
    string State,
    string? Result,
    string? StartedAt,
    string? FinishedAt,
    string? LogRef,
    string LogContent,
    int OriginalLogSizeBytes);

/// <summary>
/// Phase 27 — flat snapshot of one artifact, ready to hand to
/// <see cref="BuildArtifactExpander.ExtractAsync"/>.
/// </summary>
public sealed record BuildArtifactSnapshot(
    string Name,
    string DownloadUrl,
    long? SizeBytesHint);
