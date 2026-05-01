// -----------------------------------------------------------------------
// <copyright file="ForgeMetrics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;

/// <summary>
/// Phase 24 — singleton metrics shim re-using the existing Phase 16 meter.
/// Wrapped as an injectable service so consumers (linker, runner, queue
/// hook) can be tested without statics.
/// </summary>
public sealed class ForgeMetrics
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;

    /// <summary>Phase 24 — duration of one PR sync, ms. Tags: <c>provider</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<double> PrSyncDurationMs =
        Meter.CreateHistogram<double>("forge.sync.pr.duration_ms", unit: "ms");

    /// <summary>Phase 24 — PR state transition counter. Tags: <c>from</c>, <c>to</c>.</summary>
    public static readonly Counter<long> PrStateTransitions =
        Meter.CreateCounter<long>("forge.sync.pr.state_transitions");

    /// <summary>Phase 24 — comment delta counter. Tag: <c>kind</c> ∈ <c>added|updated|resolved|reopened</c>.</summary>
    public static readonly Counter<long> PrComments =
        Meter.CreateCounter<long>("forge.sync.pr.comments");

    /// <summary>Phase 24 — RequiresAttention toggles. Tag: <c>direction</c> ∈ <c>up|down</c>.</summary>
    public static readonly Counter<long> AttentionFlips =
        Meter.CreateCounter<long>("forge.sync.pr.attention_flips");

    /// <summary>Phase 24 — 403s on the resolve endpoint. Tag: <c>actorKind</c>.</summary>
    public static readonly Counter<long> AuthorityRefused =
        Meter.CreateCounter<long>("forge.pr.comment.authority_refused");

    /// <summary>Phase 24 — body-parse vs position-heuristic link successes. Tag: <c>heuristic</c>.</summary>
    public static readonly Counter<long> LinkerMatches =
        Meter.CreateCounter<long>("forge.pr.comment.linker_matched");

    /// <summary>Phase 24 — worktree cleanup outcomes. Tags: <c>outcome</c>, <c>prState</c>.</summary>
    public static readonly Counter<long> WorktreeCleanup =
        Meter.CreateCounter<long>("forge.worktree.cleanup");

    /// <summary>
    /// Phase 24 Q24.14 — fired when a comment-response queue row goes
    /// <c>blocked</c> because both the preferred agent and the lead are
    /// unavailable. Tags: <c>crewId</c>, <c>reason</c>
    /// (preferred-agent-paused|preferred-agent-not-in-crew|preferred-agent-missing).
    /// </summary>
    public static readonly Counter<long> NoOriginalAgentBlocked =
        Meter.CreateCounter<long>("forge.queue.no_original_agent_blocked");

    /// <summary>
    /// Phase 24 Q24.13 — fired when an auto-enqueue refuses to re-enter the
    /// loop because the work item has stalled <c>CommentResponseStallCap</c>
    /// times in a row. Tag: <c>workItemId</c>.
    /// </summary>
    public static readonly Counter<long> CommentResponseRepeatedStall =
        Meter.CreateCounter<long>("forge.queue.comment_response_repeated_stall");

    public void RecordPrSync(string provider, string outcome, double durationMs)
    {
        PrSyncDurationMs.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void StateTransition(string fromState, string toState) =>
        PrStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", fromState),
            new KeyValuePair<string, object?>("to", toState));

    public void Comments(string kind, long delta = 1) =>
        PrComments.Add(delta, new KeyValuePair<string, object?>("kind", kind));

    public void AttentionFlip(bool toTrue) =>
        AttentionFlips.Add(1, new KeyValuePair<string, object?>("direction", toTrue ? "up" : "down"));

    public void AuthorityRefused_(string actorKind) =>
        AuthorityRefused.Add(1, new KeyValuePair<string, object?>("actorKind", actorKind));

    public void LinkerMatched(string heuristic) =>
        LinkerMatches.Add(1, new KeyValuePair<string, object?>("heuristic", heuristic));

    public void Worktree(string outcome, string prState) =>
        WorktreeCleanup.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("prState", prState));

    // ─── forge_implementation.md Phase 27 — build sync ────────────────────

    /// <summary>Phase 27 — duration of one build sync, ms. Tags: <c>provider</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<double> BuildSyncDurationMs =
        Meter.CreateHistogram<double>("forge.sync.build.duration_ms", unit: "ms");

    /// <summary>Phase 27 — build state transition counter. Tags: <c>from</c>, <c>to</c>.</summary>
    public static readonly Counter<long> BuildStateTransitions =
        Meter.CreateCounter<long>("forge.sync.build.state_transitions");

    /// <summary>Phase 27 — failure-points parsed counter. Tags: <c>pass</c> (1 | 1.5), <c>kind</c>.</summary>
    public static readonly Counter<long> BuildFailuresParsed =
        Meter.CreateCounter<long>("forge.sync.build.failures_parsed");

    /// <summary>Phase 27 — unparseable build failure counter. Tag: <c>provider</c>.</summary>
    public static readonly Counter<long> BuildUnparseable =
        Meter.CreateCounter<long>("forge.sync.build.unparseable");

    /// <summary>Phase 27 — successful artifact extractions.</summary>
    public static readonly Counter<long> BuildArtifactsExtracted =
        Meter.CreateCounter<long>("forge.sync.build.artifacts_extracted");

    /// <summary>Phase 27 — artifact size over cap counter. Tag: <c>sizeBytes</c>.</summary>
    public static readonly Counter<long> BuildArtifactOverCap =
        Meter.CreateCounter<long>("forge.sync.build.artifact.over_cap");

    /// <summary>Phase 27 — zip-slip attempt blocked counter (security signal — alarm when non-zero).</summary>
    public static readonly Counter<long> BuildArtifactZipSlipBlocked =
        Meter.CreateCounter<long>("forge.sync.build.artifact.zip_slip_blocked");

    /// <summary>Phase 27 — over-cap eviction counter.</summary>
    public static readonly Counter<long> BuildArtifactEvicted =
        Meter.CreateCounter<long>("forge.sync.build.artifact.evicted");

    /// <summary>Phase 27 — partial / corrupt download counter.</summary>
    public static readonly Counter<long> BuildArtifactCorrupted =
        Meter.CreateCounter<long>("forge.build.artifact.corrupted");

    /// <summary>Phase 27 Q27.12 — defer counter (build-fix held behind comment-response/coding).</summary>
    public static readonly Counter<long> BuildFixDeferred =
        Meter.CreateCounter<long>("forge.build.fix_deferred");

    /// <summary>Phase 27 — original-agent missing counter (audit-only blocked path).</summary>
    public static readonly Counter<long> BuildNoOriginalAgent =
        Meter.CreateCounter<long>("forge.build.no_original_agent");

    /// <summary>Phase 27 — manual `promote-now` counter.</summary>
    public static readonly Counter<long> BuildFixPromotedNow =
        Meter.CreateCounter<long>("forge.queue.build_fix_promoted_now");

    /// <summary>Phase 27 — work item missing on enqueue counter.</summary>
    public static readonly Counter<long> BuildWorkItemMissing =
        Meter.CreateCounter<long>("forge.build.work_item_missing");

    /// <summary>Phase 27 — read_file path traversal refusal counter.</summary>
    public static readonly Counter<long> BuildFixPathTraversalRefused =
        Meter.CreateCounter<long>("forge.build_fix.path_traversal_refused");

    public void RecordBuildSync(string provider, string outcome, double durationMs) =>
        BuildSyncDurationMs.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void BuildStateTransition(string fromState, string toState) =>
        BuildStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", fromState),
            new KeyValuePair<string, object?>("to", toState));

    public void BuildFailuresParsedAdd(string pass, string kind) =>
        BuildFailuresParsed.Add(1,
            new KeyValuePair<string, object?>("pass", pass),
            new KeyValuePair<string, object?>("kind", kind));

    public void BuildUnparseableAdd(string provider) =>
        BuildUnparseable.Add(1, new KeyValuePair<string, object?>("provider", provider));

    public void BuildArtifactsExtractedAdd() =>
        BuildArtifactsExtracted.Add(1);

    public void BuildArtifactOverCapAdd(long sizeBytes) =>
        BuildArtifactOverCap.Add(1, new KeyValuePair<string, object?>("sizeBytes", sizeBytes));

    public void BuildArtifactZipSlipBlockedAdd() =>
        BuildArtifactZipSlipBlocked.Add(1);

    public void BuildArtifactEvictedAdd() =>
        BuildArtifactEvicted.Add(1);

    public void BuildArtifactCorruptedAdd() =>
        BuildArtifactCorrupted.Add(1);

    public void BuildFixDeferredAdd() =>
        BuildFixDeferred.Add(1);

    public void BuildNoOriginalAgentAdd() =>
        BuildNoOriginalAgent.Add(1);

    public void BuildFixPromotedNowAdd() =>
        BuildFixPromotedNow.Add(1);

    public void BuildWorkItemMissingAdd() =>
        BuildWorkItemMissing.Add(1);

    public void BuildFixPathTraversalRefusedAdd() =>
        BuildFixPathTraversalRefused.Add(1);
}
