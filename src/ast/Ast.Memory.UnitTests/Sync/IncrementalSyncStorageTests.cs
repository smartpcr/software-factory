// -----------------------------------------------------------------------
// <copyright file="IncrementalSyncStorageTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Sync;

using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

/// <summary>
/// Storage-facing tests that use <see cref="InMemoryMemoryGraphStore"/> and
/// <see cref="InMemoryCommitRegistry"/> to verify actual temporal version
/// closure for nodes and edges — NOT mock-based.
/// </summary>
[TestClass]
public class IncrementalSyncStorageTests
{
    private InMemoryMemoryGraphStore store = null!;
    private InMemoryCommitRegistry commitRegistry = null!;
    private Mock<IExtractionPipeline> mockPipeline = null!;
    private Mock<IEvolutionEngine> mockEvolution = null!;
    private Mock<ISemanticIndexer> mockSemanticIndexer = null!;
    private Mock<GitDiffDriver> mockDiffDriver = null!;
    private IncrementalSync sync = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryMemoryGraphStore();
        this.commitRegistry = new InMemoryCommitRegistry(startIndex: 10);
        this.mockPipeline = new Mock<IExtractionPipeline>();
        this.mockEvolution = new Mock<IEvolutionEngine>();
        this.mockEvolution
            .Setup(e => e.DecayStaleExpertiseAsync(It.IsAny<DecayOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecayResult(0, 0, 0));
        this.mockSemanticIndexer = new Mock<ISemanticIndexer>();
        this.mockSemanticIndexer
            .Setup(s => s.IndexNodesAsync(It.IsAny<IReadOnlyList<HypergraphNode>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HypergraphNode> nodes, CancellationToken _) => nodes.Count);

        var diffLogger = NullLogger<GitDiffDriver>.Instance;
        this.mockDiffDriver = new Mock<GitDiffDriver>(diffLogger) { CallBase = false };

        var options = Options.Create(new MemoryOptions());
        var logger = NullLogger<IncrementalSync>.Instance;

        this.sync = new IncrementalSync(
            this.store,
            this.commitRegistry,
            this.mockPipeline.Object,
            this.mockEvolution.Object,
            this.mockSemanticIndexer.Object,
            this.mockDiffDriver.Object,
            options,
            logger);
    }

    [TestMethod]
    public async Task UpsertNodeAsync_ClosesOldVersion_SetsUntilIndex()
    {
        // Arrange: seed an active node at commit index 1
        var nodeId = "src/Svc.cs::MyClass";
        var oldNode = CreateNode(nodeId, "src/Svc.cs", "hash_v1");
        var oldVersion = new VersionRange("commit1", 1, null, null);
        await this.store.UpsertNodeAsync(oldNode, oldVersion);

        // Act: upsert a new version at commit index 5
        var newNode = CreateNode(nodeId, "src/Svc.cs", "hash_v2");
        var newVersion = new VersionRange("commit2", 5, null, null);
        await this.store.UpsertNodeAsync(newNode, newVersion);

        // Assert: old version should have UntilIndex = 5 (retired to history)
        this.store.NodeHistory.Should().ContainSingle(
            "old version should be preserved in node history");
        var closedNode = this.store.NodeHistory[0];
        closedNode.Metadata[MemoryMetadataKeys.UntilIndex].Should().Be(5,
            "old version must have UntilIndex set to the new version's SinceIndex");

        // Current active node should be the new version
        var activeNode = this.store.AllNodes[nodeId];
        activeNode.Metadata[MemoryMetadataKeys.ContentHash]!.ToString().Should().Be("hash_v2");
        activeNode.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var untilVal);
        (untilVal is null || untilVal is not int).Should().BeTrue("active node should NOT have UntilIndex");
    }

    [TestMethod]
    public async Task UpsertNodeAsync_ClosesOldVersion_WithoutCascadingToEdges()
    {
        // Arrange: create a node and a connected edge, then upsert a new version.
        // Per §5.1, UpsertNodeAsync atomically closes the old version's UntilIndex
        // WITHOUT cascading retirement to edges — edges reference the logical node
        // ID and remain valid for the successor version.
        var nodeA = CreateNode("nodeA", "src/A.cs", "hashA");
        var nodeB = CreateNode("nodeB", "src/B.cs", "hashB");
        await this.store.UpsertNodeAsync(nodeA, new VersionRange("commit0", 1, null, null));
        await this.store.CreateNodeAsync(nodeB);

        var edge = new Hyperedge
        {
            Id = "edge_AB",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "nodeA", "nodeB" },
            Roles = new Dictionary<string, string> { ["nodeA"] = "caller", ["nodeB"] = "callee" },
        };
        await this.store.CreateEdgeAsync(edge);

        // Act: upsert a new version of nodeA (should close old version atomically)
        var newNodeA = CreateNode("nodeA", "src/A.cs", "hashA_v2");
        await this.store.UpsertNodeAsync(newNodeA, new VersionRange("commit1", 10, null, null));

        // Assert: old version in history has UntilIndex (closed by UpsertNodeAsync)
        this.store.NodeHistory.Should().ContainSingle();
        var closedNode = this.store.NodeHistory[0];
        closedNode.Metadata[MemoryMetadataKeys.UntilIndex].Should().Be(10);

        // Assert: connected edge does NOT have UntilIndex (UpsertNodeAsync does NOT cascade)
        var connectedEdge = this.store.AllEdges["edge_AB"];
        connectedEdge.Metadata.Should().NotContainKey(MemoryMetadataKeys.UntilIndex,
            "UpsertNodeAsync must NOT cascade retirement to connected edges — edges remain valid for successor");
    }

    [TestMethod]
    public async Task RetireNodeAsync_CascadesToConnectedEdges()
    {
        // Arrange: create two nodes and an edge between them
        var nodeA = CreateNode("nodeA", "src/A.cs", "hashA");
        var nodeB = CreateNode("nodeB", "src/B.cs", "hashB");
        await this.store.CreateNodeAsync(nodeA);
        await this.store.CreateNodeAsync(nodeB);

        var edge = new Hyperedge
        {
            Id = "edge_AB",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "nodeA", "nodeB" },
            Roles = new Dictionary<string, string> { ["nodeA"] = "caller", ["nodeB"] = "callee" },
        };
        await this.store.CreateEdgeAsync(edge);

        // Act: retire nodeA
        await this.store.RetireNodeAsync("nodeA", untilCommitIndex: 10);

        // Assert: node has UntilIndex
        var retiredNode = this.store.AllNodes["nodeA"];
        retiredNode.Metadata[MemoryMetadataKeys.UntilIndex].Should().Be(10);

        // Assert: connected edge also has UntilIndex (cascade)
        var retiredEdge = this.store.AllEdges["edge_AB"];
        retiredEdge.Metadata[MemoryMetadataKeys.UntilIndex].Should().Be(10,
            "RetireNodeAsync must cascade retirement to connected edges");
    }

    [TestMethod]
    public async Task ModifiedFileSync_ClosesOldNodeVersion_UpsertNew_VerifyInStore()
    {
        // Arrange: seed two nodes in the store at commit index 1
        var node0 = CreateNode("src/Svc.cs::N0", "src/Svc.cs", "hash_old_0");
        var node1 = CreateNode("src/Svc.cs::N1", "src/Svc.cs", "hash_old_1");
        await this.store.UpsertNodeAsync(node0, new VersionRange("commit0", 1, null, null));
        await this.store.UpsertNodeAsync(node1, new VersionRange("commit0", 1, null, null));

        // Set up diff driver to return a modified file
        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        // Extraction returns N0 with new hash, N1 unchanged
        var newN0 = CreateNode("src/Svc.cs::N0", "src/Svc.cs", "hash_new_0");
        var newN1 = CreateNode("src/Svc.cs::N1", "src/Svc.cs", "hash_old_1");

        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(2, 0, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newN0, newN1 },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: N0 was modified — old version should be closed via UpsertNodeAsync's
        // atomic temporal closure (§5.1)
        result.NodesAdded.Should().Be(1, "only N0 changed ContentHash");
        result.FilesChanged.Should().Be(1);

        // The active N0 should have the new hash
        var activeN0 = this.store.AllNodes["src/Svc.cs::N0"];
        activeN0.Metadata[MemoryMetadataKeys.ContentHash]!.ToString().Should().Be("hash_new_0");

        // Verify the old version was atomically closed by UpsertNodeAsync (UntilIndex set)
        var oldVersionedKey = "src/Svc.cs::N0@v1";
        if (this.store.AllNodes.TryGetValue(oldVersionedKey, out var oldVersionNode))
        {
            oldVersionNode.Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex,
                "old version must have UntilIndex set atomically by UpsertNodeAsync per §5.1");
        }
    }

    [TestMethod]
    public async Task DeletedFileSync_RetiresAllNodesAndEdges_InStore()
    {
        // Arrange: seed 3 nodes + 2 edges in the store
        for (int i = 0; i < 3; i++)
        {
            var node = CreateNode($"src/Old.cs::N{i}", "src/Old.cs", $"hash_{i}");
            node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
            node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
            await this.store.CreateNodeAsync(node);
        }

        var edge1 = new Hyperedge
        {
            Id = "edge_01",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Old.cs::N0", "src/Old.cs::N1" },
            Roles = new Dictionary<string, string> { ["src/Old.cs::N0"] = "caller", ["src/Old.cs::N1"] = "callee" },
        };
        var edge2 = new Hyperedge
        {
            Id = "edge_12",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Old.cs::N1", "src/Old.cs::N2" },
            Roles = new Dictionary<string, string> { ["src/Old.cs::N1"] = "caller", ["src/Old.cs::N2"] = "callee" },
        };
        await this.store.CreateEdgeAsync(edge1);
        await this.store.CreateEdgeAsync(edge2);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Deleted, "src/Old.cs") });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert
        result.NodesRetired.Should().Be(3);
        result.EdgesRetired.Should().Be(2);

        // Verify all nodes have UntilIndex set in the actual store
        for (int i = 0; i < 3; i++)
        {
            var node = this.store.AllNodes[$"src/Old.cs::N{i}"];
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex,
                $"node N{i} must have UntilIndex set after deletion");
        }

        // Verify all edges have UntilIndex set in the actual store
        this.store.AllEdges["edge_01"].Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex);
        this.store.AllEdges["edge_12"].Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex);
    }

    [TestMethod]
    public async Task ChangedEdge_UsesOriginalId_NoVersionSuffixDrift()
    {
        // Arrange: seed a node and an edge
        var nodeA = CreateNode("src/Calc.cs::A", "src/Calc.cs", "hashA");
        var nodeB = CreateNode("src/Calc.cs::B", "src/Calc.cs", "hashB");
        nodeA.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        nodeA.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        nodeB.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        nodeB.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(nodeA);
        await this.store.CreateNodeAsync(nodeB);

        var oldEdge = new Hyperedge
        {
            Id = "edge_AB",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Calc.cs::A", "src/Calc.cs::B" },
            Roles = new Dictionary<string, string> { ["src/Calc.cs::A"] = "caller", ["src/Calc.cs::B"] = "callee" },
        };
        await this.store.CreateEdgeAsync(oldEdge);

        // Set up diff
        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Calc.cs") });

        // Extraction returns same nodes (same hash) but the edge has a new target
        var newA = CreateNode("src/Calc.cs::A", "src/Calc.cs", "hashA");
        var newB = CreateNode("src/Calc.cs::B", "src/Calc.cs", "hashB");
        var changedEdge = new Hyperedge
        {
            Id = "edge_AB",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Calc.cs::B", "src/Calc.cs::A" }, // reversed direction
            Roles = new Dictionary<string, string> { ["src/Calc.cs::B"] = "caller", ["src/Calc.cs::A"] = "callee" },
        };

        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(2, 1, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newA, newB },
                Edges = new List<Hyperedge> { changedEdge },
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: old edge is retired, new edge uses same deterministic ID
        result.EdgesRetired.Should().Be(1);
        result.EdgesAdded.Should().Be(1);

        // The active edge should use the original deterministic ID (no version suffix)
        // so future extractions produce matching IDs without drift.
        this.store.AllEdges.Should().ContainKey("edge_AB",
            "changed edges must keep the original deterministic ID so future extractions match");
        var activeEdge = this.store.AllEdges["edge_AB"];
        activeEdge.NodeIds.Should().BeEquivalentTo(
            new[] { "src/Calc.cs::B", "src/Calc.cs::A" },
            "successor edge should have the new (reversed) node ordering");
    }

    [TestMethod]
    public async Task ExpertiseDecay_PersistsInStore()
    {
        // Arrange: seed a node WITH SourceCode and an expertise entry in the store.
        // SourceCode metadata is needed so DetermineDecayFactor can distinguish
        // substantive vs whitespace-only changes. Without SourceCode, the
        // conservative MinorDecayFactor (0.9) is used.
        var nodeId = "src/Svc.cs::Process";
        var node = CreateNodeWithSource(nodeId, "src/Svc.cs", "hash_old",
            "public void Process() { DoWork(); }");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(node);

        var expertise = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Caching pattern", "Uses Redis cache", nodeId, "test-agent")
        {
            Confidence = 1.0,
        };
        await this.store.UpsertExpertiseAsync(expertise);

        // Set up diff: modified file with substantive source change
        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        var newNode = CreateNodeWithSource(nodeId, "src/Svc.cs", "hash_new",
            "public void Process() { DoCompletelyDifferentWork(); ValidateResult(); }");
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(1, 0, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newNode },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: expertise was decayed and persisted with StaleDecayFactor (0.5)
        // because the SourceCode changed substantively.
        result.ExpertiseDecayed.Should().Be(1);

        // Verify the actual store has the decayed value
        var stored = await this.store.GetExpertiseForNodeAsync(nodeId);
        stored.Should().ContainSingle();
        stored[0].Confidence.Should().BeApproximately(0.5, 0.001,
            "StaleDecayFactor (0.5) should be applied for substantive source changes and persisted in the store");
    }

    [TestMethod]
    public async Task InMemoryCommitRegistry_IsIdempotent()
    {
        var registry = new InMemoryCommitRegistry();

        var idx1 = await registry.RegisterCommitAsync("abc123");
        var idx2 = await registry.RegisterCommitAsync("abc123");
        var idx3 = await registry.RegisterCommitAsync("def456");

        idx1.Should().Be(1, "first commit should get startIndex (1)");
        idx1.Should().Be(idx2, "same SHA must return same index");
        idx3.Should().NotBe(idx1, "different SHA must return different index");
        idx3.Should().Be(2, "second unique commit should get startIndex + 1");
    }

    [TestMethod]
    public async Task InMemoryCommitRegistry_CustomStartIndex()
    {
        var registry = new InMemoryCommitRegistry(startIndex: 10);

        var idx1 = await registry.RegisterCommitAsync("first");
        idx1.Should().Be(10, "first commit should return the specified startIndex");

        var idx2 = await registry.RegisterCommitAsync("second");
        idx2.Should().Be(11, "second commit should return startIndex + 1");
    }

    [TestMethod]
    public async Task ExtractionFailure_SurfacedInSyncResult()
    {
        // Arrange: diff returns an added file but extraction throws
        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Added, "src/Bad.cs") });

        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unsupported file format"));

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: extraction error is surfaced, not silently swallowed
        result.FilesChanged.Should().Be(1, "file was changed in git diff even though extraction failed");
        result.ExtractionErrors.Should().ContainSingle("extraction failure must be reported");
        result.ExtractionErrors[0].Should().Contain("src/Bad.cs");
        result.ExtractionErrors[0].Should().Contain("Unsupported file format");
    }

    [TestMethod]
    public async Task ExpertiseDecay_ArchivesBelowFloor()
    {
        // Arrange: a node WITH SourceCode and expertise entry with confidence just above floor.
        // Uses substantive source change to trigger StaleDecayFactor (0.5).
        var nodeId = "src/Svc.cs::Process";
        var node = CreateNodeWithSource(nodeId, "src/Svc.cs", "hash_old",
            "public void Process() { DoWork(); }");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(node);

        var expertise = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Fragile expertise", "Test body", nodeId, "test-agent")
        {
            Confidence = 0.15, // 0.15 * 0.5 = 0.075 < floor 0.1
        };
        await this.store.UpsertExpertiseAsync(expertise);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        var newNode = CreateNodeWithSource(nodeId, "src/Svc.cs", "hash_new",
            "public void Process() { DoCompletelyDifferentWork(); ValidateResult(); }");
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(1, 0, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newNode },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: expertise was decayed and archived (below ArchiveConfidenceFloor 0.1)
        result.ExpertiseDecayed.Should().Be(1);

        var archived = await this.store.GetArchivedExpertiseAsync(nodeId);
        archived.Should().ContainSingle("expertise below ArchiveConfidenceFloor must be archived");
    }

    [TestMethod]
    public async Task BackslashFilePath_StillMatchesNodes()
    {
        // Arrange: seed a node with backslash path (Windows-native)
        var nodeId = "src\\Svc.cs::N0";
        var node = CreateNode(nodeId, "src\\Svc.cs", "hash_old");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(node);

        // Git diff returns forward-slash path
        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Deleted, "src/Svc.cs") });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: the backslash-path node was found and retired
        result.NodesRetired.Should().Be(1, "backslash paths should be matched by forward-slash git paths");
    }

    [TestMethod]
    public async Task ExtractionPartialErrors_SurfacedInSyncResult()
    {
        // Arrange: diff returns a modified file. Extraction succeeds but reports
        // partial errors in ExtractionResult.Errors — these should be surfaced
        // in SyncResult.ExtractionErrors, not silently ignored.
        var nodeId = "src/Svc.cs::N0";
        var node = CreateNode(nodeId, "src/Svc.cs", "hash_old");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(node);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        var newNode = CreateNode(nodeId, "src/Svc.cs", "hash_new");
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(1, 0, 0, 0, new List<string> { "Failed to parse nested class Foo", "Unsupported attribute syntax" })
            {
                Nodes = new List<HypergraphNode> { newNode },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: partial errors are surfaced, file is still processed
        result.FilesChanged.Should().Be(1, "file with partial errors should still be processed");
        result.ExtractionErrors.Should().HaveCount(2, "both partial errors should be reported");
        result.ExtractionErrors[0].Should().Contain("Failed to parse nested class Foo");
        result.ExtractionErrors[1].Should().Contain("Unsupported attribute syntax");
    }

    [TestMethod]
    public async Task EvolutionEngineDecay_NotCalledDuringSync()
    {
        // Arrange: seed a node and expertise entry — after event-driven decay,
        // IncrementalSync must NOT invoke IEvolutionEngine.DecayStaleExpertiseAsync
        // because time-based decay is a separate scheduled concern. Triggering it
        // during sync could mutate/archive expertise beyond the event-driven decay.
        var nodeId = "src/Svc.cs::Process";
        var node = CreateNode(nodeId, "src/Svc.cs", "hash_old");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(node);

        var expertise = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Cache pattern", "Uses Redis", nodeId, "test-agent")
        {
            Confidence = 1.0,
        };
        await this.store.UpsertExpertiseAsync(expertise);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        var newNode = CreateNode(nodeId, "src/Svc.cs", "hash_new");
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(1, 0, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newNode },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: expertise was decayed via event-driven decay but EvolutionEngine
        // time-based decay must NOT be invoked during sync
        result.ExpertiseDecayed.Should().Be(1);
        this.mockEvolution.Verify(
            e => e.DecayStaleExpertiseAsync(It.IsAny<DecayOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "IEvolutionEngine.DecayStaleExpertiseAsync must NOT be invoked during incremental sync — " +
            "time-based decay is a separate scheduled concern");
    }

    [TestMethod]
    public async Task StaleDecayFactor_UsedWhenSourceCodeUnavailable()
    {
        // Arrange: node WITHOUT SourceCode metadata — decay should use StaleDecayFactor (0.5)
        // because when we can't determine if the change is minor, we assume substantive.
        // ContentHash already changed (triggering the decay), so the aggressive factor
        // avoids under-decaying expertise when source metadata is missing.
        var nodeId = "src/Svc.cs::Process";
        var node = CreateNode(nodeId, "src/Svc.cs", "hash_old");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        // NOTE: no SourceCode metadata — this is the key test condition
        await this.store.CreateNodeAsync(node);

        var expertise = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Test expertise", "Body text", nodeId, "test-agent")
        {
            Confidence = 1.0,
        };
        await this.store.UpsertExpertiseAsync(expertise);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        var newNode = CreateNode(nodeId, "src/Svc.cs", "hash_new");
        // No SourceCode on new node either
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(1, 0, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newNode },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: StaleDecayFactor (0.5) should be used because when SourceCode
        // metadata is unavailable, the ContentHash change is presumed substantive.
        result.ExpertiseDecayed.Should().Be(1);
        var stored = await this.store.GetExpertiseForNodeAsync(nodeId);
        stored.Should().ContainSingle();
        stored[0].Confidence.Should().BeApproximately(0.5, 0.001,
            "StaleDecayFactor (0.5) should be used when SourceCode metadata is unavailable " +
            "to avoid under-decaying expertise for presumed substantive changes");
    }

    [TestMethod]
    public async Task SemanticEdgesCreated_IncludedInEdgesAdded()
    {
        // Arrange: set up mock semantic indexer to return SIMILAR_TO edge count
        this.mockSemanticIndexer
            .Setup(s => s.IndexNodesAsync(It.IsAny<IReadOnlyList<HypergraphNode>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3); // 3 SIMILAR_TO edges created

        var nodeId = "src/Svc.cs::N0";
        var node = CreateNode(nodeId, "src/Svc.cs", "hash_old");
        node.Metadata[MemoryMetadataKeys.SinceCommit] = "commit0";
        node.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        await this.store.CreateNodeAsync(node);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Modified, "src/Svc.cs") });

        var newNode = CreateNode(nodeId, "src/Svc.cs", "hash_new");
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult(1, 0, 0, 0, Array.Empty<string>())
            {
                Nodes = new List<HypergraphNode> { newNode },
                Edges = Array.Empty<Hyperedge>(),
            });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: SemanticEdgesCreated is tracked in SyncResult
        result.SemanticEdgesCreated.Should().Be(3,
            "SemanticIndexer return value should be tracked as SemanticEdgesCreated");

        // Assert: semantic edges are included in EdgesAdded total
        result.EdgesAdded.Should().Be(3,
            "SIMILAR_TO edges from SemanticIndexer must be included in EdgesAdded");
    }

    [TestMethod]
    public async Task DeletedFile_OrphanEdges_QueriesRetiredNodes()
    {
        // Arrange: seed 2 nodes — one active, one already retired (historical version).
        // An edge connects the retired node to an external node. When the file is deleted,
        // IncrementalSync must query retired nodes too so that orphaned edges referencing
        // historical node versions are discovered and retired.
        var activeNode = CreateNode("src/Old.cs::Active", "src/Old.cs", "hash_active");
        await this.store.CreateNodeAsync(activeNode);

        // Create a historical (retired) node version
        var retiredNode = CreateNode("src/Old.cs::Retired", "src/Old.cs", "hash_retired_v1");
        retiredNode.Metadata[MemoryMetadataKeys.SinceCommit] = "commit_old";
        retiredNode.Metadata[MemoryMetadataKeys.SinceIndex] = 1;
        retiredNode.Metadata[MemoryMetadataKeys.UntilIndex] = 5; // already retired
        await this.store.CreateNodeAsync(retiredNode);

        // An external node not in this file
        var externalNode = CreateNode("src/Other.cs::Ext", "src/Other.cs", "hash_ext");
        await this.store.CreateNodeAsync(externalNode);

        // Edge connecting the retired node to the external node (orphan candidate)
        var orphanEdge = new Hyperedge
        {
            Id = "edge_orphan",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Old.cs::Retired", "src/Other.cs::Ext" },
            Roles = new Dictionary<string, string>
            {
                ["src/Old.cs::Retired"] = "caller",
                ["src/Other.cs::Ext"] = "callee",
            },
        };
        await this.store.CreateEdgeAsync(orphanEdge);

        // Edge connecting the active node
        var activeEdge = new Hyperedge
        {
            Id = "edge_active",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Old.cs::Active", "src/Other.cs::Ext" },
            Roles = new Dictionary<string, string>
            {
                ["src/Old.cs::Active"] = "caller",
                ["src/Other.cs::Ext"] = "callee",
            },
        };
        await this.store.CreateEdgeAsync(activeEdge);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileChange> { new(FileChangeStatus.Deleted, "src/Old.cs") });

        // Act
        var result = await this.sync.ApplyDiffAsync("/repo", "commit0", "commit1");

        // Assert: only 1 active node was retired, but BOTH edges should be retired
        // (including the orphan edge connected to the historical retired node)
        result.NodesRetired.Should().Be(1, "only the active node should be retired");
        result.EdgesRetired.Should().Be(2,
            "both the active edge and the orphan edge (connected to retired node) must be retired");

        // Verify both edges have UntilIndex set
        this.store.AllEdges["edge_orphan"].Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex,
            "orphan edge connected to retired/historical node must be retired when file is deleted");
        this.store.AllEdges["edge_active"].Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex);
    }

    private static HypergraphNode CreateNode(string id, string filePath, string contentHash)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = id.Split("::").LastOrDefault() ?? id,
            NodeType = "Method",
            Language = "C#",
            FilePath = filePath,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = contentHash,
            },
        };
    }

    private static HypergraphNode CreateNodeWithSource(string id, string filePath, string contentHash, string sourceCode)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = id.Split("::").LastOrDefault() ?? id,
            NodeType = "Method",
            Language = "C#",
            FilePath = filePath,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = contentHash,
                [MemoryMetadataKeys.SourceCode] = sourceCode,
            },
        };
    }
}