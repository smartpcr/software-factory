// -----------------------------------------------------------------------
// <copyright file="EvolutionEngineTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.Embedding;
using Ast.GraphDb;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

[TestClass]
public class EvolutionEngineTests
{
    private const string TargetNodeId = "/src/Services/PaymentService.cs::PaymentService.processRefund";

    private Mock<IMemoryGraphStore> graphStoreMock = null!;
    private Mock<IEmbeddingGenerator> embeddingGeneratorMock = null!;
    private Mock<IVectorStore> vectorStoreMock = null!;
    private Mock<IExpertiseMergeService> mergeServiceMock = null!;
    private Mock<ILogger<EvolutionEngine>> loggerMock = null!;
    private IOptions<MemoryOptions> optionsAccessor = null!;
    private EvolutionEngine engine = null!;

    [TestInitialize]
    public void Setup()
    {
        this.graphStoreMock = new Mock<IMemoryGraphStore>();
        this.embeddingGeneratorMock = new Mock<IEmbeddingGenerator>();
        this.vectorStoreMock = new Mock<IVectorStore>();
        this.mergeServiceMock = new Mock<IExpertiseMergeService>();
        this.loggerMock = new Mock<ILogger<EvolutionEngine>>();
        this.optionsAccessor = Options.Create(new MemoryOptions());

        // Default: target node exists
        this.graphStoreMock
            .Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HypergraphNode
            {
                Id = TargetNodeId,
                Name = "processRefund",
                NodeType = "Method",
                Language = "C#",
                FilePath = "/src/Services/PaymentService.cs"
            });

        // Default: no existing expertise
        this.graphStoreMock
            .Setup(s => s.GetExpertiseByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExpertiseEntry?)null);

        // Default: no duplicate by content hash
        this.graphStoreMock
            .Setup(s => s.GetExpertiseByContentHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExpertiseEntry?)null);

        // Default: embedding generation returns a vector
        this.embeddingGeneratorMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[768]);

        // Default: vector store collection exists
        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        this.engine = new EvolutionEngine(
            this.graphStoreMock.Object,
            this.embeddingGeneratorMock.Object,
            this.vectorStoreMock.Object,
            this.mergeServiceMock.Object,
            this.optionsAccessor,
            this.loggerMock.Object);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: Record expertise
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_StoresEntryWithCorrectKind()
    {
        // Act
        var result = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — entry stored with correct Kind
        result.Should().NotBeNull();
        result.Kind.Should().Be(ExpertiseKind.Pitfall);
        result.Title.Should().Be("Fragile null-handling");
        result.Body.Should().Be("processRefund assumes non-null return from DB");
        result.TargetNodeId.Should().Be(TargetNodeId);
        result.Confidence.Should().Be(1.0);
        result.Archived.Should().BeFalse();
        result.SourceAgent.Should().Be("agent-session-1");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_GeneratesCorrectId()
    {
        // Act
        var result = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — ID format matches "expertise:{kind}:{hash8}"
        var expectedId = ExpertiseEntry.GenerateId(
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            TargetNodeId);
        result.Id.Should().Be(expectedId);
        result.Id.Should().StartWith("expertise:pitfall:");
        result.Id.Should().HaveLength("expertise:pitfall:".Length + 8);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_CreatesAppliesToEdge()
    {
        // Act
        await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — APPLIES_TO edge created with correct roles
        this.graphStoreMock.Verify(
            s => s.CreateEdgeAsync(
                It.Is<Hyperedge>(e =>
                    e.EdgeType == HyperedgeType.MemoryRelation &&
                    e.NodeIds.Count == 2 &&
                    e.Roles.Values.Contains("expertise") &&
                    e.Roles.Values.Contains("target") &&
                    e.Metadata.ContainsKey(MemoryMetadataKeys.SemanticRelation) &&
                    (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]! == "APPLIES_TO" &&
                    e.Metadata.ContainsKey(MemoryMetadataKeys.MemoryLayer) &&
                    (string)e.Metadata[MemoryMetadataKeys.MemoryLayer]! == "expertise"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_LogsAuditEntry()
    {
        // Act
        await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — audit log entry recorded
        this.graphStoreMock.Verify(
            s => s.LogAuditAsync(
                "agent-session-1",
                "RecordExpertise",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_PersistsViaUpsertExpertise()
    {
        // Act
        await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — persisted via UpsertExpertiseAsync
        this.graphStoreMock.Verify(
            s => s.UpsertExpertiseAsync(
                It.Is<ExpertiseEntry>(e =>
                    e.Kind == ExpertiseKind.Pitfall &&
                    e.TargetNodeId == TargetNodeId &&
                    !string.IsNullOrEmpty(e.ContentHash)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_UpsertsVectorToExpertiseCollection()
    {
        // Act
        await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — vector upserted to memory_expertise collection
        this.vectorStoreMock.Verify(
            v => v.UpsertAsync(
                "memory_expertise",
                It.Is<IEnumerable<VectorPoint>>(points =>
                    points.Any(p => p.Id.StartsWith("expertise:pitfall:"))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_EmbedsTitlePlusBody()
    {
        // Act
        await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — embedding generated from "Title + ' ' + Body"
        this.embeddingGeneratorMock.Verify(
            e => e.GenerateEmbeddingAsync(
                "Fragile null-handling processRefund assumes non-null return from DB",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_ThrowsArgumentExceptionWhenTargetNodeNotFound()
    {
        this.graphStoreMock
            .Setup(s => s.GetNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HypergraphNode?)null);

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
    // Scenario: Duplicate detection
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_ReturnsDuplicateWhenSameContentExists()
    {
        // Arrange — same entry already exists (dedup via full ContentHash)
        var contentHash = ExpertiseEntry.ComputeContentHash(
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            TargetNodeId);

        var existingEntry = new ExpertiseEntry
        {
            Id = ExpertiseEntry.GenerateId(
                ExpertiseKind.Pitfall,
                "Fragile null-handling",
                "processRefund assumes non-null return from DB",
                TargetNodeId),
            Kind = ExpertiseKind.Pitfall,
            Title = "Fragile null-handling",
            Body = "processRefund assumes non-null return from DB",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-session-1",
            ContentHash = contentHash
        };

        this.graphStoreMock
            .Setup(s => s.GetExpertiseByContentHashAsync(contentHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingEntry);

        // Act
        var result = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — returns existing entry, no new persistence
        result.Should().BeSameAs(existingEntry);
        this.graphStoreMock.Verify(
            s => s.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);
        this.graphStoreMock.Verify(
            s => s.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_ContentHashIsStableForSameInput()
    {
        // Act — call twice with the same inputs
        var hash1 = ExpertiseEntry.ComputeContentHash(
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            TargetNodeId);
        var hash2 = ExpertiseEntry.ComputeContentHash(
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            TargetNodeId);

        // Assert — deterministic
        hash1.Should().Be(hash2);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_DifferentInputProducesDifferentHash()
    {
        var hash1 = ExpertiseEntry.ComputeContentHash("Title1", "Body1", TargetNodeId);
        var hash2 = ExpertiseEntry.ComputeContentHash("Title2", "Body2", TargetNodeId);

        hash1.Should().NotBe(hash2);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: RestoreArchivedExpertiseAsync
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RestoreArchivedExpertiseAsync_SetsArchivedFalseAndConfidence()
    {
        // Arrange — archived entry
        var archivedEntry = new ExpertiseEntry
        {
            Id = "expertise:pitfall:abcd1234",
            Kind = ExpertiseKind.Pitfall,
            Title = "Some pitfall",
            Body = "Body text",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.05,
            Archived = true,
            ContentHash = "dummyhash"
        };

        this.graphStoreMock
            .Setup(s => s.GetExpertiseByIdAsync("expertise:pitfall:abcd1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivedEntry);

        // Act
        var result = await this.engine.RestoreArchivedExpertiseAsync("expertise:pitfall:abcd1234", 0.5);

        // Assert
        result.Archived.Should().BeFalse();
        result.Confidence.Should().Be(0.5);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RestoreArchivedExpertiseAsync_PersistsRestoredEntry()
    {
        // Arrange
        var archivedEntry = new ExpertiseEntry
        {
            Id = "expertise:pitfall:abcd1234",
            Kind = ExpertiseKind.Pitfall,
            Title = "Some pitfall",
            Body = "Body text",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.05,
            Archived = true,
            ContentHash = "dummyhash"
        };

        this.graphStoreMock
            .Setup(s => s.GetExpertiseByIdAsync("expertise:pitfall:abcd1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivedEntry);

        // Act
        await this.engine.RestoreArchivedExpertiseAsync("expertise:pitfall:abcd1234", 0.5);

        // Assert — persisted with updated values
        this.graphStoreMock.Verify(
            s => s.UpsertExpertiseAsync(
                It.Is<ExpertiseEntry>(e =>
                    e.Id == "expertise:pitfall:abcd1234" &&
                    e.Archived == false &&
                    e.Confidence == 0.5),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RestoreArchivedExpertiseAsync_LogsAudit()
    {
        // Arrange
        var archivedEntry = new ExpertiseEntry
        {
            Id = "expertise:pitfall:abcd1234",
            Kind = ExpertiseKind.Pitfall,
            Title = "Some pitfall",
            Body = "Body text",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.05,
            Archived = true,
            ContentHash = "dummyhash"
        };

        this.graphStoreMock
            .Setup(s => s.GetExpertiseByIdAsync("expertise:pitfall:abcd1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivedEntry);

        // Act
        await this.engine.RestoreArchivedExpertiseAsync("expertise:pitfall:abcd1234", 0.5);

        // Assert — audit logged
        this.graphStoreMock.Verify(
            s => s.LogAuditAsync(
                It.IsAny<string>(),
                "RestoreArchivedExpertise",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RestoreArchivedExpertiseAsync_ThrowsWhenNotFound()
    {
        // Arrange
        this.graphStoreMock
            .Setup(s => s.GetExpertiseByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExpertiseEntry?)null);

        // Act & Assert
        var act = () => this.engine.RestoreArchivedExpertiseAsync("expertise:pitfall:nonexist");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: ExpertiseEntry model
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public void ExpertiseEntry_IdFormat_MatchesSpec()
    {
        var id = ExpertiseEntry.GenerateId(
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            TargetNodeId);

        id.Should().MatchRegex(@"^expertise:pitfall:[a-f0-9]{8}$");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public void ExpertiseEntry_ContentHash_IsSha256()
    {
        var hash = ExpertiseEntry.ComputeContentHash("Title", "Body", "NodeId");

        // SHA-256 produces 64 hex chars
        hash.Should().HaveLength(64);
        hash.Should().MatchRegex(@"^[a-f0-9]{64}$");
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: EvolutionResults types (§5.3 shapes)
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public void ConsolidationResult_HasExpectedProperties()
    {
        var result = new ConsolidationResult(
            CandidatesPaired: 5,
            MergedCount: 3,
            SupersededCount: 6,
            MergedEntryIds: ["e1", "e2", "e3"]);

        result.CandidatesPaired.Should().Be(5);
        result.MergedCount.Should().Be(3);
        result.SupersededCount.Should().Be(6);
        result.MergedEntryIds.Should().HaveCount(3);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public void DecayResult_HasExpectedProperties()
    {
        var result = new DecayResult(
            EntriesEvaluated: 10,
            EntriesDecayed: 5,
            EntriesArchived: 2);

        result.EntriesEvaluated.Should().Be(10);
        result.EntriesDecayed.Should().Be(5);
        result.EntriesArchived.Should().Be(2);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public void CommunityResult_HasExpectedProperties()
    {
        var result = new CommunityResult(
            CommunitiesDetected: 3,
            TopicNodesCreated: 3,
            ClusterEdgesCreated: 9);

        result.CommunitiesDetected.Should().Be(3);
        result.TopicNodesCreated.Should().Be(3);
        result.ClusterEdgesCreated.Should().Be(9);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: Scope validation
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_AcceptsExactScope()
    {
        var result = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Test",
            "Body",
            "agent-1",
            scope: "exact");

        result.Scope.Should().Be("exact");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_AcceptsSubtreeScope()
    {
        var result = await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Test",
            "Body",
            "agent-1",
            scope: "subtree");

        result.Scope.Should().Be("subtree");
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_RejectsInvalidScope()
    {
        var act = () => this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Test",
            "Body",
            "agent-1",
            scope: "invalid");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exact*subtree*");
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: Duplicate audit logging
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_AuditsDuplicateWriteBackAttempt()
    {
        // Arrange — same entry already exists
        var contentHash = ExpertiseEntry.ComputeContentHash(
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            TargetNodeId);

        var existingEntry = new ExpertiseEntry
        {
            Id = ExpertiseEntry.GenerateId(
                ExpertiseKind.Pitfall,
                "Fragile null-handling",
                "processRefund assumes non-null return from DB",
                TargetNodeId),
            Kind = ExpertiseKind.Pitfall,
            Title = "Fragile null-handling",
            Body = "processRefund assumes non-null return from DB",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-session-1",
            ContentHash = contentHash
        };

        this.graphStoreMock
            .Setup(s => s.GetExpertiseByContentHashAsync(contentHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingEntry);

        // Act
        await this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null return from DB",
            "agent-session-1");

        // Assert — audit entry logged for the duplicate attempt
        this.graphStoreMock.Verify(
            s => s.LogAuditAsync(
                "agent-session-1",
                "RecordExpertise_Duplicate",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: Decay is transient — stored_confidence never modified
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DecayStaleExpertiseAsync_DoesNotPersistTimeDecayToConfidence()
    {
        // Arrange — entry created 500 days ago with Confidence=1.0
        var entry = new ExpertiseEntry
        {
            Id = "expertise:pitfall:abc12345",
            Kind = ExpertiseKind.Pitfall,
            Title = "Test",
            Body = "Test body",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 1.0,
            Archived = false,
            ContentHash = "hash1",
            CreatedAt = DateTime.UtcNow.AddDays(-500)
        };

        this.graphStoreMock
            .Setup(s => s.GetAllActiveExpertiseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpertiseEntry> { entry });

        // Act
        var result = await this.engine.DecayStaleExpertiseAsync();

        // Assert — UpsertExpertiseAsync is NEVER called (time-based decay is transient)
        this.graphStoreMock.Verify(
            s => s.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert — effective_confidence > ArchiveConfidenceFloor (0.1), so no archiving
        this.graphStoreMock.Verify(
            s => s.ArchiveExpertiseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.EntriesDecayed.Should().Be(1);
        result.EntriesArchived.Should().Be(0);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DecayStaleExpertiseAsync_ArchivesViaArchiveExpertiseAsync_WhenBelowFloor()
    {
        // Arrange — entry with low stored_confidence and very old age
        var entry = new ExpertiseEntry
        {
            Id = "expertise:pitfall:abc12345",
            Kind = ExpertiseKind.Pitfall,
            Title = "Old entry",
            Body = "Very old",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.15,
            Archived = false,
            ContentHash = "hash1",
            CreatedAt = DateTime.UtcNow.AddDays(-3000) // ~8 years old
        };

        this.graphStoreMock
            .Setup(s => s.GetAllActiveExpertiseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpertiseEntry> { entry });

        // Act
        var result = await this.engine.DecayStaleExpertiseAsync();

        // Assert — archived via ArchiveExpertiseAsync, NOT via UpsertExpertiseAsync
        this.graphStoreMock.Verify(
            s => s.ArchiveExpertiseAsync("expertise:pitfall:abc12345", It.IsAny<CancellationToken>()),
            Times.Once);
        this.graphStoreMock.Verify(
            s => s.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.EntriesArchived.Should().Be(1);
    }

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DecayStaleExpertiseAsync_IsIdempotent()
    {
        var entry = new ExpertiseEntry
        {
            Id = "expertise:pitfall:abc12345",
            Kind = ExpertiseKind.Pitfall,
            Title = "Test",
            Body = "Body",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.8,
            Archived = false,
            ContentHash = "hash1",
            CreatedAt = DateTime.UtcNow.AddDays(-100)
        };

        this.graphStoreMock
            .Setup(s => s.GetAllActiveExpertiseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpertiseEntry> { entry });

        var result1 = await this.engine.DecayStaleExpertiseAsync();
        var result2 = await this.engine.DecayStaleExpertiseAsync();

        result1.EntriesDecayed.Should().Be(result2.EntriesDecayed);
        result1.EntriesArchived.Should().Be(result2.EntriesArchived);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: Consolidation uses LLM merge and canonical roles
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task ConsolidateAsync_CreatesLlmMergedEntryWithSupersedesEdges()
    {
        // Arrange — two similar entries
        var entryA = new ExpertiseEntry
        {
            Id = "expertise:pitfall:aaaa1111",
            Kind = ExpertiseKind.Pitfall,
            Title = "Null check missing",
            Body = "processRefund misses null check",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-1",
            Confidence = 0.9,
            Archived = false,
            ContentHash = "hashA",
            Embedding = new float[768],
            Evidence = []
        };

        var entryB = new ExpertiseEntry
        {
            Id = "expertise:pitfall:bbbb2222",
            Kind = ExpertiseKind.Pitfall,
            Title = "Missing null guard",
            Body = "processRefund can crash on null",
            TargetNodeId = TargetNodeId,
            SourceAgent = "agent-2",
            Confidence = 0.8,
            Archived = false,
            ContentHash = "hashB",
            Embedding = new float[768],
            Evidence = []
        };

        this.graphStoreMock
            .Setup(s => s.GetAllActiveExpertiseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpertiseEntry> { entryA, entryB });

        this.vectorStoreMock
            .Setup(v => v.CollectionExistsAsync("memory_expertise", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Vector search returns entryB as similar to entryA
        this.vectorStoreMock
            .Setup(v => v.SearchAsync("memory_expertise", entryA.Embedding!, It.IsAny<int>(), It.IsAny<VectorFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new() { Id = entryB.Id, Score = 0.92f }
            });

        this.graphStoreMock
            .Setup(s => s.GetExpertiseByIdAsync("expertise:pitfall:bbbb2222", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entryB);

        // LLM merge returns combined content
        this.mergeServiceMock
            .Setup(m => m.MergeExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("Merged: null-handling", "Combined body about null checks in processRefund"));

        // Act
        var result = await this.engine.ConsolidateAsync();

        // Assert — merged entry created
        result.MergedCount.Should().Be(1);
        result.SupersededCount.Should().BeGreaterThanOrEqualTo(2);

        // Assert — LLM merge service was called
        this.mergeServiceMock.Verify(
            m => m.MergeExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — SUPERSEDES edges created with canonical newer/older roles
        this.graphStoreMock.Verify(
            s => s.CreateEdgeAsync(
                It.Is<Hyperedge>(e =>
                    e.Roles.Values.Contains("newer") &&
                    e.Roles.Values.Contains("older") &&
                    e.Metadata.ContainsKey(MemoryMetadataKeys.SemanticRelation) &&
                    (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]! == "SUPERSEDES"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // Assert — merged entry persisted via UpsertExpertiseAsync
        this.graphStoreMock.Verify(
            s => s.UpsertExpertiseAsync(
                It.Is<ExpertiseEntry>(e => e.Title == "Merged: null-handling"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — originals archived
        this.graphStoreMock.Verify(
            s => s.ArchiveExpertiseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: DetectCommunities uses Louvain and topic:{hash8} IDs
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task DetectCommunitiesAsync_RunsLouvainAndCreatesTopicNodes()
    {
        // Arrange — nodes connected by SIMILAR_TO edges
        var nodeA = new HypergraphNode { Id = "node:A", Name = "A", NodeType = "Method", Language = "C#", FilePath = "/a.cs" };
        var nodeB = new HypergraphNode { Id = "node:B", Name = "B", NodeType = "Method", Language = "C#", FilePath = "/b.cs" };
        var nodeC = new HypergraphNode { Id = "node:C", Name = "C", NodeType = "Method", Language = "C#", FilePath = "/c.cs" };

        this.graphStoreMock
            .Setup(s => s.GetNodesAsync(It.IsAny<NodeFilter?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { nodeA, nodeB, nodeC });

        // SIMILAR_TO edges form a cluster: A<->B, B<->C
        var similarEdges = new List<Hyperedge>
        {
            new()
            {
                Id = "edge:similar:1",
                EdgeType = HyperedgeType.MemoryRelation,
                NodeIds = ["node:A", "node:B"],
                Roles = new Dictionary<string, string> { ["node:A"] = "node_a", ["node:B"] = "node_b" },
                Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.SemanticRelation] = "SIMILAR_TO" }
            },
            new()
            {
                Id = "edge:similar:2",
                EdgeType = HyperedgeType.MemoryRelation,
                NodeIds = ["node:B", "node:C"],
                Roles = new Dictionary<string, string> { ["node:B"] = "node_a", ["node:C"] = "node_b" },
                Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.SemanticRelation] = "SIMILAR_TO" }
            }
        };

        this.graphStoreMock
            .Setup(s => s.GetEdgesBySemanticRelationAsync("SIMILAR_TO", It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(similarEdges);

        // No CALLS edges
        this.graphStoreMock
            .Setup(s => s.GetEdgesForNodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Hyperedge>());

        // Act
        var result = await this.engine.DetectCommunitiesAsync();

        // Assert — Louvain detected at least one community
        result.CommunitiesDetected.Should().BeGreaterThanOrEqualTo(1);
        result.TopicNodesCreated.Should().BeGreaterThanOrEqualTo(1);

        // Assert — Topic node created with topic:{hash8} format
        this.graphStoreMock.Verify(
            s => s.CreateNodeAsync(
                It.Is<HypergraphNode>(n =>
                    n.Id.StartsWith("topic:") &&
                    !n.Id.Contains("cluster:") && // NOT topic:cluster:...
                    n.NodeType == "Topic"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // Assert — BELONGS_TO_CLUSTER edges created
        this.graphStoreMock.Verify(
            s => s.CreateEdgeAsync(
                It.Is<Hyperedge>(e =>
                    e.Roles.Values.Contains("member") &&
                    e.Roles.Values.Contains("cluster") &&
                    e.Metadata.ContainsKey(MemoryMetadataKeys.SemanticRelation) &&
                    (string)e.Metadata[MemoryMetadataKeys.SemanticRelation]! == "BELONGS_TO_CLUSTER"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ───────────────────────────────────────────────────────────────────
    // Scenario: RecordExpertise rollback on edge/vector failure
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("evolution")]
    public async Task RecordExpertiseAsync_RollsBackOnEdgeCreationFailure()
    {
        // Arrange — edge creation throws
        this.graphStoreMock
            .Setup(s => s.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Edge creation failed"));

        // Act & Assert
        var act = () => this.engine.RecordExpertiseAsync(
            TargetNodeId,
            ExpertiseKind.Pitfall,
            "Test",
            "Body",
            "agent-1");

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert — expertise was archived to roll back partial state
        this.graphStoreMock.Verify(
            s => s.ArchiveExpertiseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}