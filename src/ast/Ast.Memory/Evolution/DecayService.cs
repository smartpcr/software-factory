// -----------------------------------------------------------------------
// <copyright file="DecayService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Provides normalized PageRank scores for graph nodes.
/// Consumers should supply a real implementation backed by the graph store;
/// <see cref="NullCentralityProvider"/> returns 0.0 for all nodes (no boost).
/// </summary>
public interface ICentralityProvider
{
    /// <summary>
    /// Returns the normalized PageRank of <paramref name="nodeId"/> in [0.0, 1.0].
    /// </summary>
    Task<double> GetNormalizedPageRankAsync(string nodeId, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICentralityProvider"/> that returns 0.0 for every node,
/// yielding a <c>centrality_boost</c> of 1.0 (no extension of half-life).
/// </summary>
public sealed class NullCentralityProvider : ICentralityProvider
{
    public Task<double> GetNormalizedPageRankAsync(string nodeId, CancellationToken cancellationToken)
    {
        return Task.FromResult(0.0);
    }
}

/// <summary>
/// Configuration for the decay subsystem. All defaults from architecture.md §2.4.
/// </summary>
public class DecayServiceOptions
{
    /// <summary>Half-life in days for time-based confidence decay (≈3 years).</summary>
    public int DecayHalfLifeDays { get; set; } = 1095;

    /// <summary>Additive boost multiplied by normalized PageRank. Range [1.0, 1.0 + CentralityBoostFactor].</summary>
    public double CentralityBoostFactor { get; set; } = 0.5;

    /// <summary>Confidence floor below which expertise entries are auto-archived.</summary>
    public double ArchiveConfidenceFloor { get; set; } = 0.1;

    /// <summary>Multiplicative factor applied on substantive code changes.</summary>
    public double StaleDecayFactor { get; set; } = 0.5;

    /// <summary>Multiplicative factor applied on whitespace/comment-only changes.</summary>
    public double MinorDecayFactor { get; set; } = 0.9;
}

/// <summary>
/// Per-run options for decay operations.
/// Per architecture.md §5.3.
/// </summary>
public record DecayOptions(
    int HalfLifeDays = 1095,
    double CentralityBoostFactor = 0.5,
    double ArchiveConfidenceFloor = 0.1);

/// <summary>
/// Per-entry result exposing the computed effective confidence.
/// </summary>
public record DecayedEntryInfo(
    string EntryId,
    string TargetNodeId,
    double StoredConfidence,
    double EffectiveConfidence,
    bool WasArchived);

/// <summary>
/// Result of a decay run.
/// Per architecture.md §5.3: <c>DecayResult(EntriesEvaluated, EntriesDecayed, EntriesArchived)</c>.
/// </summary>
public record DecayResult(
    int EntriesEvaluated,
    int EntriesDecayed,
    int EntriesArchived,
    IReadOnlyList<DecayedEntryInfo> Entries);

/// <summary>
/// Result of an event-driven decay operation for a single node.
/// </summary>
public record EventDrivenDecayResult(
    string NodeId,
    int EntriesDecayed,
    int EntriesArchived,
    IReadOnlyList<string> ArchivedIds);

/// <summary>
/// Reduces expertise confidence over time and on code changes.
/// Per architecture.md §3.5, §3.5.1.
/// </summary>
public interface IDecayService
{
    /// <summary>
    /// Evaluates all non-archived expertise entries across every node,
    /// computing <c>effective_confidence</c> and archiving entries that fall
    /// below <see cref="DecayServiceOptions.ArchiveConfidenceFloor"/>.
    /// This is the store-wide stale scan.
    /// Accepts optional <see cref="DecayOptions"/> to override defaults per architecture.md §5.3.
    /// </summary>
    Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates non-archived expertise entries for the specified nodes,
    /// computing <c>effective_confidence</c> and archiving entries that fall
    /// below <see cref="DecayServiceOptions.ArchiveConfidenceFloor"/>.
    /// Time-based decay is idempotent — it never persists confidence changes,
    /// only archives entries whose effective confidence is too low.
    /// </summary>
    Task<DecayResult> DecayStaleExpertiseAsync(
        IReadOnlyList<string> targetNodeIds,
        DecayOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies event-driven decay to all expertise entries targeting
    /// <paramref name="nodeId"/>. Called by <see cref="IIncrementalSyncDecayHook"/>
    /// when a code node's ContentHash changes. Uses <paramref name="contentHash"/>
    /// for idempotence — repeated calls with the same hash for a given
    /// node are no-ops, preventing duplicate decay on re-sync or restart.
    /// Idempotence state is persisted via <see cref="IMemoryGraphStore"/> so
    /// it survives process restarts.
    /// Per architecture.md §3.5.1(a): each ContentHash change triggers
    /// exactly one event-driven decay.
    /// </summary>
    Task<EventDrivenDecayResult> ApplyEventDrivenDecayForNodeAsync(
        string nodeId,
        bool isSubstantiveChange,
        string contentHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes the effective (transient) confidence for a single entry.
    /// Per architecture.md §3.5.1(b):
    /// <c>effective_confidence = stored_confidence × 2^(−age_days / effective_half_life)</c>
    /// where <c>effective_half_life = DecayHalfLifeDays × centrality_boost</c>.
    /// This value is never persisted.
    /// </summary>
    double ComputeEffectiveConfidence(
        double storedConfidence,
        DateTimeOffset createdAt,
        double normalizedPageRank,
        DateTimeOffset? asOf = null);
}

/// <summary>
/// Hook invoked by IncrementalSync when a code node's content hash changes.
/// Wires the IncrementalSync subsystem to the <see cref="IDecayService"/>.
/// Per architecture.md §3.5 / implementation-plan Stage 5.3:
/// "Event-driven decay (called by IncrementalSync)".
/// </summary>
public interface IIncrementalSyncDecayHook
{
    /// <summary>
    /// Called by IncrementalSync after detecting that a node's content hash
    /// has changed. Applies event-driven decay to all expertise entries
    /// targeting <paramref name="nodeId"/>.
    /// </summary>
    /// <param name="nodeId">The graph node whose code changed.</param>
    /// <param name="isSubstantiveChange">
    /// True for substantive code changes (applies <see cref="DecayServiceOptions.StaleDecayFactor"/>);
    /// false for whitespace/comment-only changes (applies <see cref="DecayServiceOptions.MinorDecayFactor"/>).
    /// </param>
    /// <param name="newContentHash">
    /// The new content hash of the node after the code change.
    /// Used for idempotence — repeated calls with the same hash are no-ops.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the event-driven decay operation.</returns>
    Task<EventDrivenDecayResult> OnNodeContentChangedAsync(
        string nodeId,
        bool isSubstantiveChange,
        string newContentHash,
        CancellationToken cancellationToken);
}

/// <summary>
/// Implements time-based and event-driven expertise decay.
/// Also implements <see cref="IIncrementalSyncDecayHook"/> so that
/// IncrementalSync can call <see cref="OnNodeContentChangedAsync"/>
/// to trigger event-driven decay when a code node changes.
/// Per architecture.md §3.5, §3.5.1.
/// </summary>
public sealed class DecayService : IDecayService, IIncrementalSyncDecayHook
{
    private readonly IMemoryGraphStore store;
    private readonly ICentralityProvider centralityProvider;
    private readonly DecayServiceOptions options;
    private readonly ILogger<DecayService>? logger;

    /// <summary>Maximum centrality boost per architecture.md §2.4: range [1.0, 1.5].</summary>
    private const double MaxCentralityBoost = 1.5;

    public DecayService(
        IMemoryGraphStore store,
        ICentralityProvider centralityProvider,
        IOptions<DecayServiceOptions> options,
        ILogger<DecayService>? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.centralityProvider = centralityProvider ?? throw new ArgumentNullException(nameof(centralityProvider));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;
    }

    /// <summary>
    /// Internal constructor for direct options injection (used by tests).
    /// </summary>
    internal DecayService(
        IMemoryGraphStore store,
        ICentralityProvider centralityProvider,
        DecayServiceOptions options,
        ILogger<DecayService>? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.centralityProvider = centralityProvider ?? throw new ArgumentNullException(nameof(centralityProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecayResult> DecayStaleExpertiseAsync(
        DecayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = this.ResolveOptions(options);

        var allEntries = await this.store.GetAllActiveExpertiseAsync(cancellationToken)
            .ConfigureAwait(false);

        return await this.EvaluateAndArchiveAsync(allEntries, opts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DecayResult> DecayStaleExpertiseAsync(
        IReadOnlyList<string> targetNodeIds,
        DecayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetNodeIds);
        var opts = this.ResolveOptions(options);

        var allEntries = new List<ExpertiseEntry>();
        foreach (var nodeId in targetNodeIds)
        {
            var entries = await this.store.GetExpertiseForNodeAsync(nodeId, 0.0, cancellationToken)
                .ConfigureAwait(false);
            allEntries.AddRange(entries);
        }

        return await this.EvaluateAndArchiveAsync(allEntries, opts, cancellationToken)
            .ConfigureAwait(false);
    }

    private (int halfLifeDays, double centralityBoostFactor, double archiveFloor) ResolveOptions(DecayOptions? options)
    {
        return (
            options?.HalfLifeDays ?? this.options.DecayHalfLifeDays,
            options?.CentralityBoostFactor ?? this.options.CentralityBoostFactor,
            options?.ArchiveConfidenceFloor ?? this.options.ArchiveConfidenceFloor);
    }

    private async Task<DecayResult> EvaluateAndArchiveAsync(
        IReadOnlyList<ExpertiseEntry> entries,
        (int halfLifeDays, double centralityBoostFactor, double archiveFloor) opts,
        CancellationToken cancellationToken)
    {
        var archivedCount = 0;
        var decayedCount = 0;
        var entryInfos = new List<DecayedEntryInfo>();

        foreach (var entry in entries)
        {
            var pageRank = await this.centralityProvider
                .GetNormalizedPageRankAsync(entry.TargetNodeId, cancellationToken)
                .ConfigureAwait(false);

            var effectiveConfidence = this.ComputeEffectiveConfidenceInternal(
                entry.Confidence,
                entry.CreatedAt,
                pageRank,
                opts.halfLifeDays,
                opts.centralityBoostFactor);

            if (effectiveConfidence < entry.Confidence)
            {
                decayedCount++;
            }

            var wasArchived = false;
            if (effectiveConfidence < opts.archiveFloor)
            {
                await this.store.ArchiveExpertiseAsync(entry.Id, cancellationToken)
                    .ConfigureAwait(false);
                archivedCount++;
                wasArchived = true;

                this.logger?.LogInformation(
                    "Archived expertise {EntryId} for node {NodeId}: effective_confidence={EffectiveConfidence:F4} < floor={Floor}",
                    entry.Id, entry.TargetNodeId, effectiveConfidence, opts.archiveFloor);
            }

            entryInfos.Add(new DecayedEntryInfo(
                entry.Id,
                entry.TargetNodeId,
                entry.Confidence,
                effectiveConfidence,
                wasArchived));
        }

        this.logger?.LogInformation(
            "Decay run complete: evaluated={Evaluated}, decayed={Decayed}, archived={Archived}",
            entries.Count, decayedCount, archivedCount);

        return new DecayResult(entries.Count, decayedCount, archivedCount, entryInfos);
    }

    /// <inheritdoc />
    public async Task<EventDrivenDecayResult> ApplyEventDrivenDecayForNodeAsync(
        string nodeId,
        bool isSubstantiveChange,
        string contentHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(contentHash);

        // Idempotence guard: query store-persisted hash to survive process restarts
        // and prevent concurrent syncs from double-applying decay.
        // Per architecture.md §3.5.1(a): "each ContentHash change triggers
        // exactly one event-driven decay."
        var previousHash = await this.store.GetLastAppliedContentHashAsync(nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(previousHash, contentHash, StringComparison.Ordinal))
        {
            this.logger?.LogDebug(
                "Skipping duplicate event-driven decay for node {NodeId} with unchanged contentHash {Hash}",
                nodeId, contentHash);
            return new EventDrivenDecayResult(nodeId, 0, 0, Array.Empty<string>());
        }

        // Persist the content hash BEFORE applying decay so that a crash
        // mid-application does not re-apply the same decay on restart.
        // This is a deliberate "at-most-once" choice: if the process crashes
        // after recording the hash but before finishing upserts, some entries
        // may miss this decay cycle. That is safer than the alternative
        // "at-least-once" which compounds confidence reductions on retry.
        await this.store.SetLastAppliedContentHashAsync(nodeId, contentHash, cancellationToken)
            .ConfigureAwait(false);

        var factor = isSubstantiveChange
            ? this.options.StaleDecayFactor
            : this.options.MinorDecayFactor;

        var entries = await this.store.GetExpertiseForNodeAsync(nodeId, 0.0, cancellationToken)
            .ConfigureAwait(false);

        var archivedIds = new List<string>();
        var decayedCount = 0;

        foreach (var entry in entries)
        {
            entry.ApplyEventDecay(factor);
            decayedCount++;

            await this.store.UpsertExpertiseAsync(entry, cancellationToken)
                .ConfigureAwait(false);

            var pageRank = await this.centralityProvider
                .GetNormalizedPageRankAsync(entry.TargetNodeId, cancellationToken)
                .ConfigureAwait(false);

            var effectiveConfidence = this.ComputeEffectiveConfidence(
                entry.Confidence,
                entry.CreatedAt,
                pageRank);

            if (effectiveConfidence < this.options.ArchiveConfidenceFloor)
            {
                await this.store.ArchiveExpertiseAsync(entry.Id, cancellationToken)
                    .ConfigureAwait(false);
                archivedIds.Add(entry.Id);

                this.logger?.LogInformation(
                    "Event-driven decay archived expertise {EntryId} for node {NodeId}: stored={Stored:F4}, effective={Effective:F4}",
                    entry.Id, nodeId, entry.Confidence, effectiveConfidence);
            }
            else
            {
                this.logger?.LogDebug(
                    "Event-driven decay applied to {EntryId} for node {NodeId}: stored_confidence={Stored:F4}, factor={Factor}",
                    entry.Id, nodeId, entry.Confidence, factor);
            }
        }

        return new EventDrivenDecayResult(nodeId, decayedCount, archivedIds.Count, archivedIds);
    }

    /// <inheritdoc cref="IIncrementalSyncDecayHook.OnNodeContentChangedAsync" />
    public Task<EventDrivenDecayResult> OnNodeContentChangedAsync(
        string nodeId,
        bool isSubstantiveChange,
        string newContentHash,
        CancellationToken cancellationToken)
    {
        return this.ApplyEventDrivenDecayForNodeAsync(
            nodeId,
            isSubstantiveChange,
            newContentHash,
            cancellationToken);
    }

    /// <inheritdoc />
    public double ComputeEffectiveConfidence(
        double storedConfidence,
        DateTimeOffset createdAt,
        double normalizedPageRank,
        DateTimeOffset? asOf = null)
    {
        return this.ComputeEffectiveConfidenceInternal(
            storedConfidence,
            createdAt,
            normalizedPageRank,
            this.options.DecayHalfLifeDays,
            this.options.CentralityBoostFactor,
            asOf);
    }

    private double ComputeEffectiveConfidenceInternal(
        double storedConfidence,
        DateTimeOffset createdAt,
        double normalizedPageRank,
        int halfLifeDays,
        double centralityBoostFactor,
        DateTimeOffset? asOf = null)
    {
        if (storedConfidence <= 0.0)
        {
            return 0.0;
        }

        var now = asOf ?? DateTimeOffset.UtcNow;
        var ageDays = (now - createdAt).TotalDays;
        if (ageDays < 0.0)
        {
            ageDays = 0.0;
        }

        // centrality_boost = 1.0 + CentralityBoostFactor × normalized_pagerank
        // Clamped to [1.0, 1.5] per architecture.md §2.4 regardless of options
        var clampedPageRank = Math.Clamp(normalizedPageRank, 0.0, 1.0);
        var centralityBoost = 1.0 + (centralityBoostFactor * clampedPageRank);
        centralityBoost = Math.Clamp(centralityBoost, 1.0, MaxCentralityBoost);

        // effective_half_life = DecayHalfLifeDays × centrality_boost
        var effectiveHalfLife = halfLifeDays * centralityBoost;

        // time_factor = 2^(−age_days / effective_half_life)
        var timeFactor = Math.Pow(2.0, -ageDays / effectiveHalfLife);

        // effective_confidence = stored_confidence × time_factor
        return storedConfidence * timeFactor;
    }
}
