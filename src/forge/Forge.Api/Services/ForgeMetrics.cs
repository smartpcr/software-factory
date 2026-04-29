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
}
