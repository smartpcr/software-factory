// -----------------------------------------------------------------------
// <copyright file="EvolutionEngineIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Linq;
using Ast.Core;
using Ast.Embedding;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

/// <summary>
/// Integration-style tests that use a concrete <see cref="InMemoryMemoryGraphStore"/>
/// to verify end-to-end persistence of expertise entries, edges, and audit log records.
/// These prove the acceptance criteria without mocking the graph store.
/// </summary>
[TestClass]
public class EvolutionEngineIntegrationTests
{
    private const string TargetNodeId = "/src/Services/PaymentService.cs::PaymentService.processRefund";

    private InMemoryMemoryGraphStore store = null!;
    private Mock<IEmbeddingGenerator> embeddingGeneratorMock = null!;
    private Mock<IVectorStore> vectorStoreMock = null!;
    private Mock<IExpertiseMergeService> mergeServiceMock = null!;
    private Mock<ILogger<EvolutionEngine>> loggerMock = null!;
    private IOptions<MemoryOptions> optionsAccessor = null!;
    private EvolutionEngine engine = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryMemoryGraphStore();
        this.embeddingGeneratorMock = new Mock<IEmbeddingGenerator>();
        this.vectorStoreMock = new Mock<IVectorStore>();
        this.mergeServiceMock = new Mock<IExpertiseMergeService>();
        this.loggerMock = new Mock<ILogger<EvolutionEngine>>();
        this.optionsAccessor = Options.Create(new MemoryOptions());

        // Seed the target node in the concrete store
        this.store.CreateNodeAsync(new HypergraphNode
        {
            Id = TargetNodeId,
            Name = "processRefund",
            NodeType = "Method",
            Language = "C#",
            FilePath = "/src/Services/PaymentService.cs"
        }).GetAwaiter().GetResult();

        this.embeddingGeneratorMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[768]);

        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        this.engine = new EvolutionEngine(
            this.store,
            this.embeddingGeneratorMock.Object,
            this.vectorStoreMock.Object,
            this.mergeServiceMock.Object,
            this.optionsAccessor,
            this.loggerMock.Object);
    }

    // ───────────────────────────────────────────────────────────────────
    // Acceptance: Record expertise — end-to-end persistence
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertise_EndToEnd_PersistsEntryEdgeAndAudit()
    {
        // Act
        var result = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — expertise entry persisted in the store
        var storedEntry = await this.store.GetExpertiseByIdAsync(result.Id);
        storedEntry.Should().NotBeNull();
        storedEntry!.Kind.Should().Be(ExpertiseKind.Pitfall);
        storedEntry.Title.Should().Be("Fragile null-handling");
        storedEntry.Body.Should().Be("processRefund assumes non-null return from DB");
        storedEntry.TargetNodeId.Should().Be(TargetNodeId);
        storedEntry.Confidence.Should().Be(1.0);
        storedEntry.Archived.Should().BeFalse();
        storedEntry.SourceAgent.Should().Be("agent-session-1");

        // Assert — APPLIES_TO edge persisted with correct roles
        var appliesToEdge = this.store.Edges.Values
            .FirstOrDefault(e =>
                e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) &&
                (string?)sr == "APPLIES_TO");
        appliesToEdge.Should().NotBeNull();
        appliesToEdge!.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
        appliesToEdge.NodeIds.Should().Contain(result.Id);
        appliesToEdge.NodeIds.Should().Contain(TargetNodeId);
        appliesToEdge.Roles[result.Id].Should().Be("expertise");
        appliesToEdge.Roles[TargetNodeId].Should().Be("target");

        // Assert — audit_log records the operation
        this.store.AuditLog.Should().ContainSingle(a =>
            a.SessionId == "agent-session-1" &&
            a.Operation == "RecordExpertise");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertise_TargetNotFound_ThrowsArgumentException()
    {
        var act = () => this.engine.RecordExpertiseAsync(
            "/nonexistent/node",
            ExpertiseKind.Pitfall,
            "Test",
            "Test body",
            "agent-1");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*does not exist*");
    }

    // ───────────────────────────────────────────────────────────────────
    // Acceptance: Duplicate detection — end-to-end
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DuplicateDetection_EndToEnd_ReturnsSameEntryAndAudits()
    {
        // Act — first call creates the entry
        var first = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Act — second call with same content should deduplicate
        var second = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-2");

        // Assert — same entry returned
        second.Id.Should().Be(first.Id);
        second.ContentHash.Should().Be(first.ContentHash);

        // Assert — only one expertise entry in store
        this.store.ExpertiseEntries.Should().HaveCount(1);

        // Assert — only one APPLIES_TO edge
        var appliesToEdges = this.store.Edges.Values
            .Where(e =>
                e.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) &&
                (string?)sr == "APPLIES_TO")
            .ToList();
        appliesToEdges.Should().HaveCount(1);

        // Assert — audit log records both: original write and duplicate attempt
        this.store.AuditLog.Should().Contain(a => a.Operation == "RecordExpertise");
        this.store.AuditLog.Should().Contain(a => a.Operation == "RecordExpertise_Duplicate");
    }

    // ───────────────────────────────────────────────────────────────────
    // Acceptance: RestoreArchivedExpertiseAsync — end-to-end
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RestoreArchived_EndToEnd_SetsArchivedFalseAndAudits()
    {
        // Arrange — record expertise then archive it
        var entry = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        await this.store.ArchiveExpertiseAsync(entry.Id);
        var archived = await this.store.GetExpertiseByIdAsync(entry.Id);
        archived!.Archived.Should().BeTrue();

        // Act
        var restored = await this.engine.RestoreArchivedExpertiseAsync(entry.Id, 0.5);

        // Assert — entry is no longer archived with updated confidence
        restored.Archived.Should().BeFalse();
        restored.Confidence.Should().Be(0.5);

        // Verify in store
        var fromStore = await this.store.GetExpertiseByIdAsync(entry.Id);
        fromStore!.Archived.Should().BeFalse();
        fromStore.Confidence.Should().Be(0.5);

        // Assert — audit logged
        this.store.AuditLog.Should().Contain(a => a.Operation == "RestoreArchivedExpertise");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RestoreArchived_AlreadyActive_AuditsAndReturnsEntry()
    {
        // Arrange — record expertise (not archived)
        var entry = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Some expertise",
            "Some body text",
            "agent-1");

        // Act — restore a non-archived entry
        var result = await this.engine.RestoreArchivedExpertiseAsync(entry.Id, 0.5);

        // Assert — returned as-is with original confidence
        result.Archived.Should().BeFalse();
        result.Confidence.Should().Be(1.0); // unchanged

        // Assert — audit logged even for already-active
        this.store.AuditLog.Should().Contain(a =>
            a.Operation == "RestoreArchivedExpertise_AlreadyActive");
    }

    // ───────────────────────────────────────────────────────────────────
    // Acceptance: Scope validation
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertise_ValidatesScope_AcceptsExactAndSubtree()
    {
        var exact = await this.engine.RecordExpertiseAsync(
            TargetNodeId, ExpertiseKind.Pitfall, "T1", "B1", "agent-1", scope: "exact");
        exact.Scope.Should().Be("exact");

        var subtree = await this.engine.RecordExpertiseAsync(
            TargetNodeId, ExpertiseKind.Pitfall, "T2", "B2", "agent-1", scope: "subtree");
        subtree.Scope.Should().Be("subtree");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertise_InvalidScope_ThrowsArgumentException()
    {
        var act = () => this.engine.RecordExpertiseAsync(
            TargetNodeId, ExpertiseKind.Pitfall, "T", "B", "agent-1", scope: "global");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exact*subtree*");
    }

    // ───────────────────────────────────────────────────────────────────
    // Acceptance: DecayStaleExpertiseAsync — transient, no confidence persistence
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DecayStale_EndToEnd_DoesNotMutateStoredConfidence()
    {
        // Arrange — insert an expertise entry directly with old CreatedAt
        var contentHash = ExpertiseEntry.ComputeContentHash("Old entry", "Old body", TargetNodeId);
        var expertiseId = ExpertiseEntry.GenerateId(ExpertiseKind.Pitfall, "Old entry", "Old body", TargetNodeId);
        var entry = new ExpertiseEntry
        {
            Id = expertiseId,
            Kind = ExpertiseKind.Pitfall,
            Title = "Old entry",
            Body = "Old body",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 1.0,
            Archived = false,
            ContentHash = contentHash,
            CreatedAt = DateTime.UtcNow.AddDays(-500)
        };
        await this.store.UpsertExpertiseAsync(entry);

        // Act
        var result = await this.engine.DecayStaleExpertiseAsync();

        // Assert — stored confidence unchanged (time-decay is transient)
        var fromStore = await this.store.GetExpertiseByIdAsync(entry.Id);
        fromStore!.Confidence.Should().Be(1.0);

        result.EntriesEvaluated.Should().Be(1);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DecayStale_EndToEnd_ArchivesEntriesBelowFloor()
    {
        // Arrange — very old entry with low stored confidence
        var contentHash = ExpertiseEntry.ComputeContentHash("Ancient entry", "Ancient body", TargetNodeId);
        var expertiseId = ExpertiseEntry.GenerateId(ExpertiseKind.Pitfall, "Ancient entry", "Ancient body", TargetNodeId);
        var entry = new ExpertiseEntry
        {
            Id = expertiseId,
            Kind = ExpertiseKind.Pitfall,
            Title = "Ancient entry",
            Body = "Ancient body",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.15,
            Archived = false,
            ContentHash = contentHash,
            CreatedAt = DateTime.UtcNow.AddDays(-3000)
        };
        await this.store.UpsertExpertiseAsync(entry);

        // Act
        var result = await this.engine.DecayStaleExpertiseAsync();

        // Assert — entry archived in store
        var fromStore = await this.store.GetExpertiseByIdAsync(entry.Id);
        fromStore!.Archived.Should().BeTrue();

        result.EntriesArchived.Should().Be(1);

        // Assert — audit logged
        this.store.AuditLog.Should().Contain(a => a.Operation == "DecayStaleExpertise");
    }
}