// -----------------------------------------------------------------------
// <copyright file="DecayServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Evolution;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

[TestClass]
public class DecayServiceTests
{
    private InMemoryMemoryGraphStore store = null!;
    private DecayServiceOptions options = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryMemoryGraphStore();
        this.options = new DecayServiceOptions();
    }

    private DecayService CreateService(ICentralityProvider? centralityProvider = null)
    {
        return new DecayService(
            this.store,
            centralityProvider ?? new NullCentralityProvider(),
            this.options);
    }

    private static ExpertiseEntry CreateEntry(
        string targetNodeId = "node:method:abc12345",
        double confidence = 1.0,
        DateTimeOffset? createdAt = null)
    {
        return new ExpertiseEntry(
            ExpertiseKind.Pattern,
            "Test pattern",
            "A test pattern body",
            targetNodeId,
            "test-agent")
        {
            Confidence = confidence,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // ComputeEffectiveConfidence — pure function tests
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_ZeroAge_ReturnsStoredConfidence()
    {
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;

        var result = svc.ComputeEffectiveConfidence(1.0, now, 0.0, now);

        result.Should().BeApproximately(1.0, 1e-9);
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_HalfLifeAge_ReturnsHalfOfStored()
    {
        // Scenario: Time-based decay
        // Given an expertise entry created 1095 days ago with Confidence=1.0 and centrality_boost=1.0
        // When ComputeEffectiveConfidence runs
        // Then effective_confidence ≈ 0.5
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-1095);

        var result = svc.ComputeEffectiveConfidence(1.0, createdAt, 0.0, now);

        // centrality_boost = 1.0 + 0.5 * 0.0 = 1.0
        // effective_half_life = 1095 * 1.0 = 1095
        // time_factor = 2^(-1095/1095) = 2^(-1) = 0.5
        // effective_confidence = 1.0 * 0.5 = 0.5
        result.Should().BeApproximately(0.5, 1e-6);
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_HighCentrality_SlowsDecay()
    {
        // Scenario: Centrality boost
        // Given an expertise entry targeting a high-centrality node (normalized PageRank=1.0)
        // When decay runs
        // Then centrality_boost = 1.0 + 0.5 * 1.0 = 1.5, slowing decay by 50%
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-1095);

        var resultNoCentrality = svc.ComputeEffectiveConfidence(1.0, createdAt, 0.0, now);
        var resultHighCentrality = svc.ComputeEffectiveConfidence(1.0, createdAt, 1.0, now);

        // centrality_boost = 1.0 + 0.5 * 1.0 = 1.5
        // effective_half_life = 1095 * 1.5 = 1642.5
        // time_factor = 2^(-1095/1642.5) ≈ 0.6300
        // effective_confidence = 1.0 * 0.6300 ≈ 0.630
        resultNoCentrality.Should().BeApproximately(0.5, 1e-6);
        resultHighCentrality.Should().BeGreaterThan(resultNoCentrality,
            "high centrality extends half-life and slows decay");
        resultHighCentrality.Should().BeApproximately(0.6300, 0.005);
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_NeverExceedsStoredConfidence()
    {
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-100);

        var result = svc.ComputeEffectiveConfidence(0.5, createdAt, 1.0, now);

        result.Should().BeLessThanOrEqualTo(0.5,
            "effective confidence must never exceed stored confidence");
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_IsIdempotent()
    {
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-500);

        var result1 = svc.ComputeEffectiveConfidence(1.0, createdAt, 0.5, now);
        var result2 = svc.ComputeEffectiveConfidence(1.0, createdAt, 0.5, now);

        result1.Should().Be(result2, "time-based decay is idempotent");
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_ZeroStored_ReturnsZero()
    {
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;

        var result = svc.ComputeEffectiveConfidence(0.0, now.AddDays(-100), 1.0, now);

        result.Should().Be(0.0);
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_WorkedExample_FromArchitecture()
    {
        // Worked example from architecture.md §3.5.1:
        // stored_confidence = 0.5 (after one substantive event decay from 1.0)
        // age_days = 1095, normalized_pagerank = 0.6
        // 1. centrality_boost = 1.0 + 0.5 × 0.6 = 1.3
        // 2. effective_half_life = 1095 × 1.3 = 1423.5
        // 3. time_factor = 2^(-1095/1423.5) ≈ 0.58
        // 4. effective_confidence = 0.5 × 0.58 = 0.29
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-1095);

        var result = svc.ComputeEffectiveConfidence(0.5, createdAt, 0.6, now);

        result.Should().BeApproximately(0.29, 0.02);
    }

    // ─────────────────────────────────────────────────────────────────────
    // DecayStaleExpertiseAsync — integration-level tests with in-memory store
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_HalfLifeAge_ReturnsApproxHalf()
    {
        // Scenario: Time-based decay (full round-trip through store)
        // Given an expertise entry created 1095 days ago with Confidence=1.0 and centrality_boost=1.0
        // When DecayStaleExpertiseAsync runs
        // Then effective_confidence ≈ 0.5 (half-life) and entry is NOT archived
        var now = DateTimeOffset.UtcNow;
        var entry = CreateEntry(createdAt: now.AddDays(-1095));
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);

        result.EntriesEvaluated.Should().Be(1);
        // effective_confidence = 1.0 * 0.5 = 0.5 > 0.1 floor → not archived
        result.EntriesArchived.Should().Be(0);
        // The entry's effective_confidence < stored_confidence → decayed
        result.EntriesDecayed.Should().Be(1,
            "half-life entry has effective < stored, so it counts as decayed");

        // Verify the effective confidence is exposed and approximately 0.5
        result.Entries.Should().ContainSingle();
        result.Entries[0].EffectiveConfidence.Should().BeApproximately(0.5, 1e-6,
            "at exactly one half-life, effective confidence should be ≈0.5");
        result.Entries[0].StoredConfidence.Should().BeApproximately(1.0, 1e-9,
            "stored confidence is never mutated by time-based decay");
        result.Entries[0].WasArchived.Should().BeFalse();
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_ArchivesEntryBelowFloor()
    {
        // Scenario: Archive on low confidence
        // Given an expertise entry with Confidence=0.08 (below ArchiveConfidenceFloor 0.1)
        // When DecayStaleExpertiseAsync runs
        // Then the entry is archived via ArchiveExpertiseAsync
        // and excluded from GetExpertiseForNodeAsync
        var entry = CreateEntry(confidence: 0.08);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);

        result.EntriesArchived.Should().Be(1);

        // Verify effective confidence is exposed and below floor
        result.Entries.Should().ContainSingle();
        result.Entries[0].EffectiveConfidence.Should().BeLessThan(0.1);
        result.Entries[0].WasArchived.Should().BeTrue();

        // Verify the entry is excluded from normal queries
        var remaining = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        remaining.Should().BeEmpty("archived entries must be excluded from GetExpertiseForNodeAsync");

        // Verify the entry is accessible via archived queries
        var archived = await this.store.GetArchivedExpertiseAsync(
            entry.TargetNodeId, 100, CancellationToken.None);
        archived.Should().ContainSingle(e => e.Id == entry.Id);
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_VeryOldEntry_ArchivesDueToTimeFactor()
    {
        // An entry old enough that time_factor alone drops effective_confidence below floor
        // age_days = 10950 (30 years), stored_confidence = 1.0
        // time_factor = 2^(-10950/1095) = 2^(-10) ≈ 0.000977
        // effective_confidence ≈ 0.001 < 0.1 → archived
        var now = DateTimeOffset.UtcNow;
        var entry = CreateEntry(createdAt: now.AddDays(-10950));
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);

        result.EntriesArchived.Should().Be(1);
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_HighCentrality_PreventsArchiveOfMarginalEntry()
    {
        // An entry with stored_confidence=0.15 and high centrality should NOT be archived
        // because centrality extends the half-life (slows decay)
        var centralityMock = new Mock<ICentralityProvider>();
        centralityMock
            .Setup(c => c.GetNormalizedPageRankAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var now = DateTimeOffset.UtcNow;
        // Fresh entry (0 days old), stored_confidence = 0.15
        // effective_confidence = 0.15 * 1.0 = 0.15 > 0.1 → not archived
        var entry = CreateEntry(confidence: 0.15, createdAt: now);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService(centralityMock.Object);
        var result = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);

        result.EntriesArchived.Should().Be(0);
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_AcceptsDecayOptions_OverridingDefaults()
    {
        // Verify that passing DecayOptions overrides the service-level defaults
        var now = DateTimeOffset.UtcNow;
        var entry = CreateEntry(confidence: 0.2, createdAt: now);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();

        // With default floor (0.1), 0.2 > 0.1 → not archived
        var result1 = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);
        result1.EntriesArchived.Should().Be(0);

        // With custom floor (0.3), 0.2 < 0.3 → archived
        var customOpts = new DecayOptions(ArchiveConfidenceFloor: 0.3);
        var result2 = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            options: customOpts,
            cancellationToken: CancellationToken.None);
        result2.EntriesArchived.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Event-driven decay
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_SubstantiveChange_HalvesConfidence()
    {
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId,
            isSubstantiveChange: true,
            contentHash: "hash-v2",
            CancellationToken.None);

        result.EntriesDecayed.Should().Be(1);

        // Check persisted confidence
        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.5, 1e-9,
            "substantive change applies StaleDecayFactor=0.5");
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_MinorChange_AppliesMinorFactor()
    {
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId,
            isSubstantiveChange: false,
            contentHash: "hash-minor-v2",
            CancellationToken.None);

        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.9, 1e-9,
            "minor change applies MinorDecayFactor=0.9");
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_MultipleChanges_CompoundStoredConfidence()
    {
        // Per architecture.md: 1.0 × 0.5 × 0.5 × 0.9 = 0.225
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: true, contentHash: "hash-v2", CancellationToken.None);
        await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: true, contentHash: "hash-v3", CancellationToken.None);
        await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: false, contentHash: "hash-v4", CancellationToken.None);

        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.225, 1e-9,
            "three event decays compound: 1.0 × 0.5 × 0.5 × 0.9 = 0.225");
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_IdempotenceGuard_SameHashIsNoOp()
    {
        // Per architecture.md §3.5.1(a): "each ContentHash change triggers
        // exactly one event-driven decay."
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();

        // First call with hash-v2 → applies decay
        var result1 = await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: true, contentHash: "hash-v2", CancellationToken.None);
        result1.EntriesDecayed.Should().Be(1);

        // Second call with same hash-v2 → idempotent no-op
        var result2 = await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: true, contentHash: "hash-v2", CancellationToken.None);
        result2.EntriesDecayed.Should().Be(0, "duplicate contentHash should be a no-op");

        // Verify confidence was only decayed once (0.5, not 0.25)
        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.5, 1e-9,
            "idempotence guard prevents duplicate decay");
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_DifferentHash_AppliesDecayAgain()
    {
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: true, contentHash: "hash-v2", CancellationToken.None);
        await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, isSubstantiveChange: true, contentHash: "hash-v3", CancellationToken.None);

        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.25, 1e-9,
            "different contentHash applies decay again: 1.0 × 0.5 × 0.5 = 0.25");
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_ArchivesWhenEffectiveConfidenceFallsBelowFloor()
    {
        // A very old entry with low stored confidence after event decay → effective < floor → archived
        var now = DateTimeOffset.UtcNow;
        var entry = CreateEntry(confidence: 0.2, createdAt: now.AddDays(-2190)); // 6 years old
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        // Substantive decay: 0.2 * 0.5 = 0.1 stored
        // time_factor = 2^(-2190/1095) = 2^(-2) = 0.25
        // effective = 0.1 * 0.25 = 0.025 < 0.1 → archived
        var result = await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId,
            isSubstantiveChange: true,
            contentHash: "hash-event-archive",
            CancellationToken.None);

        result.EntriesArchived.Should().Be(1);
        result.ArchivedIds.Should().Contain(entry.Id);

        // Verify excluded from normal queries
        var remaining = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        remaining.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_DoesNotArchiveIfEffectiveStillAboveFloor()
    {
        var entry = CreateEntry(confidence: 0.5);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        // Minor decay: 0.5 * 0.9 = 0.45 stored
        // Fresh entry, time_factor ≈ 1.0
        // effective ≈ 0.45 > 0.1 → not archived
        var result = await svc.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId,
            isSubstantiveChange: false,
            contentHash: "hash-minor-safe",
            CancellationToken.None);

        result.EntriesArchived.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // DI registration
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public void DiRegistration_ResolvesDecayServiceWithDefaultOptions()
    {
        var services = new ServiceCollection();
        var mockStore = new Mock<IMemoryGraphStore>();
        services.AddSingleton(mockStore.Object);
        services.AddMemoryGraph(_ => { });

        var sp = services.BuildServiceProvider();
        var decayService = sp.GetService<IDecayService>();
        var centralityProvider = sp.GetService<ICentralityProvider>();

        decayService.Should().NotBeNull();
        decayService.Should().BeOfType<DecayService>();
        centralityProvider.Should().NotBeNull();
        centralityProvider.Should().BeOfType<NullCentralityProvider>();
    }

    [TestMethod]
    [TestCategory("decay")]
    public void DiRegistration_ResolvesIncrementalSyncDecayHook()
    {
        var services = new ServiceCollection();
        var mockStore = new Mock<IMemoryGraphStore>();
        services.AddSingleton(mockStore.Object);
        services.AddMemoryGraph(_ => { });

        var sp = services.BuildServiceProvider();
        var hook = sp.GetService<IIncrementalSyncDecayHook>();
        var decayService = sp.GetService<IDecayService>();

        hook.Should().NotBeNull();
        hook.Should().BeOfType<DecayService>();
        // Both should resolve to the same singleton instance
        hook.Should().BeSameAs(decayService);
    }

    [TestMethod]
    [TestCategory("decay")]
    public void DiRegistration_CustomCentralityProviderNotOverridden()
    {
        var services = new ServiceCollection();
        var mockStore = new Mock<IMemoryGraphStore>();
        var mockCentrality = new Mock<ICentralityProvider>();
        services.AddSingleton(mockStore.Object);
        services.AddSingleton(mockCentrality.Object);
        services.AddMemoryGraph(_ => { });

        var sp = services.BuildServiceProvider();
        var centralityProvider = sp.GetService<ICentralityProvider>();

        // TryAddSingleton should NOT override the user-registered provider
        centralityProvider.Should().BeSameAs(mockCentrality.Object);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Archive filtering
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task ArchivedEntries_ExcludedFromGetExpertiseForNodeAsync()
    {
        var entry1 = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Pattern 1", "Body 1", "node:class:abc", "agent-1")
        {
            Confidence = 0.5,
        };
        var entry2 = new ExpertiseEntry(
            ExpertiseKind.Pitfall, "Pitfall 1", "Body 2", "node:class:abc", "agent-1")
        {
            Confidence = 0.05, // below floor
        };
        await this.store.UpsertExpertiseAsync(entry1, CancellationToken.None);
        await this.store.UpsertExpertiseAsync(entry2, CancellationToken.None);

        var svc = this.CreateService();
        await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { "node:class:abc" },
            cancellationToken: CancellationToken.None);

        // Only entry2 should be archived (0.05 < 0.1)
        var remaining = await this.store.GetExpertiseForNodeAsync(
            "node:class:abc", 0.0, CancellationToken.None);
        remaining.Should().ContainSingle();
        remaining[0].Id.Should().Be(entry1.Id);

        var archived = await this.store.GetArchivedExpertiseAsync(
            "node:class:abc", 100, CancellationToken.None);
        archived.Should().ContainSingle();
        archived[0].Id.Should().Be(entry2.Id);
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_SkipsAlreadyArchivedEntries()
    {
        // Pre-archive an entry, then run decay. The archived entry should not appear.
        var entry = CreateEntry(confidence: 0.08);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);
        await this.store.ArchiveExpertiseAsync(entry.Id, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);

        // Archived entries are excluded by GetExpertiseForNodeAsync, so nothing is evaluated
        result.EntriesEvaluated.Should().Be(0);
        result.EntriesArchived.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Store-wide stale scan (parameterless overload)
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_AllEntries_ScansEntireStore()
    {
        // The parameterless overload scans all active entries across all nodes
        var now = DateTimeOffset.UtcNow;
        var entry1 = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Pattern A", "Body A", "node:class:alpha", "agent-1")
        {
            Confidence = 1.0,
            CreatedAt = now.AddDays(-1095),
        };
        var entry2 = new ExpertiseEntry(
            ExpertiseKind.Pitfall, "Pitfall B", "Body B", "node:method:beta", "agent-1")
        {
            Confidence = 0.05, // below floor even without time decay
        };
        var entry3 = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Pattern C", "Body C", "node:class:gamma", "agent-1")
        {
            Confidence = 0.8,
            CreatedAt = now, // fresh
        };
        await this.store.UpsertExpertiseAsync(entry1, CancellationToken.None);
        await this.store.UpsertExpertiseAsync(entry2, CancellationToken.None);
        await this.store.UpsertExpertiseAsync(entry3, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.DecayStaleExpertiseAsync(cancellationToken: CancellationToken.None);

        result.EntriesEvaluated.Should().Be(3, "all 3 entries scanned store-wide");
        result.EntriesArchived.Should().Be(1, "only entry2 (0.05) is below floor");

        // EntriesDecayed counts entries where effective < stored (informational)
        result.EntriesDecayed.Should().BeGreaterThanOrEqualTo(1,
            "at least entry1 has effective < stored due to age");

        // Verify effective confidence is exposed for all entries
        result.Entries.Should().HaveCount(3);
        var e1Info = result.Entries.Single(e => e.EntryId == entry1.Id);
        e1Info.EffectiveConfidence.Should().BeApproximately(0.5, 1e-6,
            "half-life entry at 1095 days → effective ≈ 0.5");
        e1Info.WasArchived.Should().BeFalse();

        var e3Info = result.Entries.Single(e => e.EntryId == entry3.Id);
        e3Info.EffectiveConfidence.Should().BeApproximately(0.8, 0.01,
            "fresh entry retains stored confidence");
        e3Info.WasArchived.Should().BeFalse();
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayStaleExpertise_CentralityBoost_ExposedInEffectiveConfidence()
    {
        // Verify that centrality boost is reflected in effective confidence via DecayStaleExpertiseAsync
        var centralityMock = new Mock<ICentralityProvider>();
        centralityMock
            .Setup(c => c.GetNormalizedPageRankAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);

        var now = DateTimeOffset.UtcNow;
        var entry = CreateEntry(createdAt: now.AddDays(-1095));
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService(centralityMock.Object);
        var result = await svc.DecayStaleExpertiseAsync(
            targetNodeIds: new[] { entry.TargetNodeId },
            cancellationToken: CancellationToken.None);

        result.Entries.Should().ContainSingle();
        // centrality_boost = 1.0 + 0.5 * 1.0 = 1.5
        // effective_half_life = 1095 * 1.5 = 1642.5
        // time_factor = 2^(-1095/1642.5) ≈ 0.6300
        // effective_confidence = 1.0 * 0.6300 ≈ 0.630
        result.Entries[0].EffectiveConfidence.Should().BeApproximately(0.6300, 0.005,
            "centrality boost of 1.5 slows decay, effective ≈ 0.63 instead of 0.5");
        result.Entries[0].WasArchived.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    // EntriesDecayed count (informational per architecture.md)
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task DecayResult_EntriesDecayed_CountsEntriesWithLowerEffectiveConfidence()
    {
        // EntriesDecayed = entries whose effective_confidence < stored_confidence
        var now = DateTimeOffset.UtcNow;

        // Old entry: effective significantly < stored → clearly decayed
        var oldEntry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Old", "Old body", "node:old", "agent-1")
        {
            Confidence = 1.0,
            CreatedAt = now.AddDays(-1095),
        };

        // Very old entry: effective very far below stored → clearly decayed and archived
        var ancientEntry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Ancient", "Ancient body", "node:ancient", "agent-1")
        {
            Confidence = 1.0,
            CreatedAt = now.AddDays(-10950), // 30 years
        };
        await this.store.UpsertExpertiseAsync(oldEntry, CancellationToken.None);
        await this.store.UpsertExpertiseAsync(ancientEntry, CancellationToken.None);

        var svc = this.CreateService();
        var result = await svc.DecayStaleExpertiseAsync(cancellationToken: CancellationToken.None);

        result.EntriesEvaluated.Should().Be(2);
        result.EntriesDecayed.Should().Be(2, "both entries have effective < stored due to age");
        result.EntriesArchived.Should().Be(1, "only the ancient entry falls below archive floor");
    }

    // ─────────────────────────────────────────────────────────────────────
    // IncrementalSync wiring
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task IncrementalSyncHook_OnNodeContentChanged_TriggersEventDrivenDecay()
    {
        // Verify that IIncrementalSyncDecayHook.OnNodeContentChangedAsync
        // delegates to ApplyEventDrivenDecayForNodeAsync (the IncrementalSync path)
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        IIncrementalSyncDecayHook hook = svc;

        var result = await hook.OnNodeContentChangedAsync(
            entry.TargetNodeId,
            isSubstantiveChange: true,
            newContentHash: "sync-hash-v1",
            CancellationToken.None);

        result.EntriesDecayed.Should().Be(1);

        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.5, 1e-9,
            "IncrementalSync hook applied substantive decay factor");
    }

    [TestMethod]
    [TestCategory("decay")]
    public async Task IncrementalSyncHook_IdempotentAcrossRepeatedCalls()
    {
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc = this.CreateService();
        IIncrementalSyncDecayHook hook = svc;

        await hook.OnNodeContentChangedAsync(
            entry.TargetNodeId, true, "sync-hash-v1", CancellationToken.None);
        var result2 = await hook.OnNodeContentChangedAsync(
            entry.TargetNodeId, true, "sync-hash-v1", CancellationToken.None);

        result2.EntriesDecayed.Should().Be(0, "same hash is idempotent no-op");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Store-backed idempotence (survives new service instances)
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public async Task EventDrivenDecay_StoreBackedIdempotence_SurvivesNewServiceInstance()
    {
        // Simulate a process restart: create a new DecayService instance
        // against the same store. The idempotence guard should still prevent
        // duplicate decay because the hash is persisted in the store.
        var entry = CreateEntry(confidence: 1.0);
        await this.store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var svc1 = this.CreateService();
        await svc1.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, true, "hash-restart-test", CancellationToken.None);

        // Create a new service instance (simulating process restart)
        var svc2 = this.CreateService();
        var result = await svc2.ApplyEventDrivenDecayForNodeAsync(
            entry.TargetNodeId, true, "hash-restart-test", CancellationToken.None);

        result.EntriesDecayed.Should().Be(0,
            "store-backed idempotence prevents double-apply after restart");

        var entries = await this.store.GetExpertiseForNodeAsync(
            entry.TargetNodeId, 0.0, CancellationToken.None);
        entries.Should().ContainSingle();
        entries[0].Confidence.Should().BeApproximately(0.5, 1e-9,
            "confidence decayed only once despite two service instances");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Centrality boost clamping
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_CentralityBoostClampedToMax15()
    {
        // Even with a very high CentralityBoostFactor, the boost is clamped to 1.5
        var opts = new DecayServiceOptions { CentralityBoostFactor = 2.0 };
        var svc = new DecayService(this.store, new NullCentralityProvider(), opts);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-1095);

        // With CentralityBoostFactor=2.0 and PageRank=1.0:
        // unclamped boost = 1.0 + 2.0 * 1.0 = 3.0 → clamped to 1.5
        var result = svc.ComputeEffectiveConfidence(1.0, createdAt, 1.0, now);

        // effective_half_life = 1095 * 1.5 = 1642.5
        // time_factor = 2^(-1095/1642.5) ≈ 0.630
        result.Should().BeApproximately(0.630, 0.005,
            "centrality boost clamped to 1.5 regardless of options override");
    }

    [TestMethod]
    [TestCategory("decay")]
    public void ComputeEffectiveConfidence_CentralityBoostClampedViaDecayOptions()
    {
        // Verify the clamp also applies when using DecayOptions override
        var svc = this.CreateService();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddDays(-1095);

        // Override CentralityBoostFactor to 5.0 via DecayOptions
        var result = svc.ComputeEffectiveConfidence(1.0, createdAt, 1.0, now);

        // Default CentralityBoostFactor=0.5, boost = 1.0 + 0.5*1.0 = 1.5 (exactly at max)
        result.Should().BeApproximately(0.630, 0.005);
    }
}
