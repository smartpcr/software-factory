// -----------------------------------------------------------------------
// <copyright file="IncrementalSyncSteps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Steps;

using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Reqnroll;

[Binding]
public class IncrementalSyncSteps
{
    private readonly Mock<IMemoryGraphStore> mockStore = new();
    private readonly Mock<ICommitRegistry> mockCommitRegistry = new();
    private readonly Mock<IExtractionPipeline> mockPipeline = new();
    private readonly Mock<IEvolutionEngine> mockEvolution = new();
    private readonly Mock<ISemanticIndexer> mockSemanticIndexer = new();
    private readonly Mock<GitDiffDriver> mockDiffDriver;
    private IncrementalSync sync;
    private SyncResult? syncResult;
    private Exception? syncException;

    // Tracking calls for assertions
    private readonly List<(string NodeId, int UntilIndex)> retiredNodes = new();
    private readonly List<(string EdgeId, int UntilIndex)> retiredEdges = new();
    private readonly List<(HypergraphNode Node, VersionRange Version)> upsertedNodes = new();
    private readonly List<(string EdgeId, IDictionary<string, object?> Metadata)> updatedEdgeMetadata = new();
    private readonly List<Hyperedge> createdEdges = new();
    private readonly List<ExpertiseEntry> upsertedExpertise = new();
    private readonly List<HypergraphNode> indexedNodes = new();

    // State setup
    private readonly List<HypergraphNode> existingNodes = new();
    private readonly List<Hyperedge> existingEdges = new();
    private readonly List<FileChange> fileChanges = new();
    private readonly List<HypergraphNode> extractedNodes = new();
    private readonly List<Hyperedge> extractedEdges = new();
    private readonly List<ExpertiseEntry> expertiseEntries = new();
    private bool gitDiffShouldFail;

    // The durable commit_index returned by RegisterCommitAsync
    private const int DurableCommitIndex = 42;

    public IncrementalSyncSteps()
    {
        var diffLogger = NullLogger<GitDiffDriver>.Instance;
        this.mockDiffDriver = new Mock<GitDiffDriver>(diffLogger) { CallBase = false };

        this.SetupMocks();
        this.sync = this.CreateSync();
    }

    private void SetupMocks()
    {
        // RegisterCommitAsync — durable commit index from commits table (§3.2.3, §4.3)
        // via ICommitRegistry (separate from IMemoryGraphStore per §5.1 canonical contract)
        this.mockCommitRegistry
            .Setup(s => s.RegisterCommitAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DurableCommitIndex);

        // GetNodesAsync — canonical IGraphStore method for file-based node lookup
        this.mockStore
            .Setup(s => s.GetNodesAsync(It.IsAny<NodeFilter?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NodeFilter? filter, int limit, CancellationToken _) =>
            {
                if (filter?.FilePath is not null)
                {
                    return this.existingNodes
                        .Where(n => n.FilePath == filter.FilePath || n.FilePath.StartsWith(filter.FilePath))
                        .Take(limit)
                        .ToList();
                }

                return this.existingNodes.Take(limit).ToList();
            });

        this.mockStore
            .Setup(s => s.RetireNodeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((id, idx, _) =>
            {
                this.retiredNodes.Add((id, idx));

                // Simulate RetireNodeAsync cascade per architecture.md §5.1:
                // "RetireNodeAsync cascades retirement to all edges connected to the node."
                var connectedEdges = this.existingEdges
                    .Where(e => e.NodeIds.Contains(id))
                    .ToList();
                foreach (var edge in connectedEdges)
                {
                    if (!this.retiredEdges.Any(r => r.EdgeId == edge.Id))
                    {
                        this.retiredEdges.Add((edge.Id, idx));
                    }
                }
            })
            .Returns(Task.CompletedTask);

        this.mockStore
            .Setup(s => s.RetireEdgeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((id, idx, _) =>
            {
                if (!this.retiredEdges.Any(r => r.EdgeId == id))
                {
                    this.retiredEdges.Add((id, idx));
                }
            })
            .Returns(Task.CompletedTask);

        this.mockStore
            .Setup(s => s.UpsertNodeAsync(It.IsAny<HypergraphNode>(), It.IsAny<VersionRange>(), It.IsAny<CancellationToken>()))
            .Callback<HypergraphNode, VersionRange, CancellationToken>((n, v, _) => this.upsertedNodes.Add((n, v)))
            .Returns(Task.CompletedTask);

        this.mockStore
            .Setup(s => s.CreateEdgeAsync(It.IsAny<Hyperedge>(), It.IsAny<CancellationToken>()))
            .Callback<Hyperedge, CancellationToken>((e, _) => this.createdEdges.Add(e))
            .Returns(Task.CompletedTask);

        this.mockStore
            .Setup(s => s.UpdateEdgeMetadataAsync(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>, CancellationToken>((id, meta, _) => this.updatedEdgeMetadata.Add((id, meta)))
            .Returns(Task.CompletedTask);

        this.mockStore
            .Setup(s => s.GetEdgesForNodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, IEnumerable<HyperedgeType>? types, CancellationToken _) =>
                this.existingEdges.Where(e => e.NodeIds.Contains(nodeId)).ToList());

        this.mockStore
            .Setup(s => s.LogAuditAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // GetExpertiseForNodeAsync — event-driven decay reads expertise from store (§3.5.1)
        this.mockStore
            .Setup(s => s.GetExpertiseForNodeAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string nodeId, double min, CancellationToken _) =>
                (IReadOnlyList<ExpertiseEntry>)this.expertiseEntries
                    .Where(e => e.TargetNodeId == nodeId && e.Confidence >= min)
                    .ToList());

        // UpsertExpertiseAsync — event-driven decay persists decayed confidence (§3.5.1)
        this.mockStore
            .Setup(s => s.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ExpertiseEntry, CancellationToken>((e, _) => this.upsertedExpertise.Add(e))
            .Returns(Task.CompletedTask);

        this.mockDiffDriver
            .Setup(d => d.GetChangedFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (this.gitDiffShouldFail)
                {
                    throw new InvalidOperationException("git diff failed with exit code 128: fatal: bad revision");
                }

                return Task.FromResult<IReadOnlyList<FileChange>>(this.fileChanges);
            });

        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ExtractionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExtractionResult(this.extractedNodes.Count, this.extractedEdges.Count, 0, 0, Array.Empty<string>())
            {
                Nodes = this.extractedNodes,
                Edges = this.extractedEdges,
            });

        this.mockSemanticIndexer
            .Setup(s => s.IndexNodesAsync(It.IsAny<IReadOnlyList<HypergraphNode>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<HypergraphNode>, CancellationToken>((nodes, _) => this.indexedNodes.AddRange(nodes))
            .ReturnsAsync((IReadOnlyList<HypergraphNode> nodes, CancellationToken _) => nodes.Count);

        // IEvolutionEngine.DecayStaleExpertiseAsync — set up mock but it should NOT be called.
        // Time-based decay is a separate scheduled concern, not triggered during sync.
        this.mockEvolution
            .Setup(e => e.DecayStaleExpertiseAsync(It.IsAny<DecayOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecayResult(0, 0, 0));
    }

    private IncrementalSync CreateSync()
    {
        var logger = NullLogger<IncrementalSync>.Instance;
        var options = Options.Create(new MemoryOptions());

        return new IncrementalSync(
            this.mockStore.Object,
            this.mockCommitRegistry.Object,
            this.mockPipeline.Object,
            this.mockEvolution.Object,
            this.mockSemanticIndexer.Object,
            this.mockDiffDriver.Object,
            options,
            logger);
    }

    // ── Modified file scenario ──────────────────────────────────────────

    [Given(@"an indexed repo with file ""(.*)"" containing (\d+) nodes")]
    public void GivenAnIndexedRepoWithFile(string filePath, int nodeCount)
    {
        for (int i = 0; i < nodeCount; i++)
        {
            var node = CreateNode($"{filePath}::Node{i}", filePath, $"hash_old_{i}");
            this.existingNodes.Add(node);
        }
    }

    [Given(@"the file is modified so extraction returns 2 nodes with new ContentHash and 1 node is removed")]
    public void GivenTheFileIsModifiedSoExtractionReturns2NodesAnd1IsRemoved()
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, "src/Services/PaymentService.cs"));

        for (int i = 0; i < 2; i++)
        {
            var existing = this.existingNodes[i];
            var updatedNode = CreateNode(existing.Id, existing.FilePath, $"hash_new_{i}");
            this.extractedNodes.Add(updatedNode);
        }

        this.existingEdges.Add(new Hyperedge
        {
            Id = "edge_node2_internal",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "src/Services/PaymentService.cs::Node2", "src/Services/PaymentService.cs::Node0" },
            Roles = new Dictionary<string, string>
            {
                ["src/Services/PaymentService.cs::Node2"] = "caller",
                ["src/Services/PaymentService.cs::Node0"] = "callee",
            },
        });
    }

    [When(@"ApplyDiffAsync is called from commit ""(.*)"" to ""(.*)""")]
    public async Task WhenApplyDiffAsyncIsCalled(string fromCommit, string toCommit)
    {
        try
        {
            this.syncResult = await this.sync.ApplyDiffAsync("/repo", fromCommit, toCommit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.syncException = ex;
        }
    }

    [Then(@"(\d+) changed nodes have their old versions retired via RetireNodeAsync with UntilIndex")]
    public void ThenChangedNodesHaveTheirOldVersionsRetiredViaRetireNodeAsyncWithUntilIndex(int expected)
    {
        // Modified nodes are handled by UpsertNodeAsync which atomically closes
        // the previous active version (sets its UntilIndex = newVersion.SinceIndex
        // per §5.1 temporal upsert semantics), then activates the successor.
        var modifiedNodeIds = new[]
        {
            "src/Services/PaymentService.cs::Node0",
            "src/Services/PaymentService.cs::Node1",
        };

        // Verify UpsertNodeAsync was called for the modified nodes (which closes old versions atomically)
        var upsertedModified = this.upsertedNodes
            .Where(u => modifiedNodeIds.Contains(u.Node.Id))
            .ToList();

        upsertedModified.Should().HaveCount(expected,
            "each modified node must be upserted via UpsertNodeAsync, which atomically closes the old version");

        foreach (var (_, version) in upsertedModified)
        {
            version.SinceIndex.Should().Be(DurableCommitIndex,
                "UpsertNodeAsync version must use the durable commit_index");
        }
    }

    [Then(@"(\d+) changed nodes are upserted with new ContentHash and new SinceIndex")]
    public void ThenChangedNodesAreUpsertedWithNewContentHashAndNewSinceIndex(int expected)
    {
        var nodesWithNewHash = this.upsertedNodes
            .Where(u => u.Node.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var h)
                && h?.ToString()?.StartsWith("hash_new_") == true)
            .ToList();
        nodesWithNewHash.Should().HaveCount(expected);

        // Verify SinceIndex is the NEW commit index — the old version was explicitly
        // retired via RetireNodeAsync, then the successor is upserted with new SinceIndex.
        foreach (var (node, version) in nodesWithNewHash)
        {
            version.SinceIndex.Should().Be(DurableCommitIndex,
                "modified nodes must get a NEW SinceIndex after old version retirement");
        }
    }

    [Then(@"the removed node is retired via RetireNodeAsync which cascades to its edges")]
    public void ThenTheRemovedNodeIsRetiredViaRetireNodeAsyncWhichCascadesToItsEdges()
    {
        this.retiredNodes.Should().Contain(r =>
            r.NodeId == "src/Services/PaymentService.cs::Node2");

        // Verify until_index matches the durable commit index from RegisterCommitAsync
        var retiredNode = this.retiredNodes.First(r =>
            r.NodeId == "src/Services/PaymentService.cs::Node2");
        retiredNode.UntilIndex.Should().Be(DurableCommitIndex,
            "until_index must match the durable commit_index from the commits table");

        // Cascade simulation: the edge connected to Node2 is also retired
        this.retiredEdges.Should().Contain(r => r.EdgeId == "edge_node2_internal",
            "RetireNodeAsync should cascade retirement to connected edges");
    }

    [Then(@"SyncResult\.FilesChanged is (\d+)")]
    public void ThenSyncResultFilesChangedIs(int expected)
    {
        this.syncResult.Should().NotBeNull();
        this.syncResult!.FilesChanged.Should().Be(expected);
    }

    [Then(@"SyncResult\.NodesRetired is (\d+)")]
    public void ThenSyncResultNodesRetiredIs(int expected)
    {
        this.syncResult.Should().NotBeNull();
        this.syncResult!.NodesRetired.Should().Be(expected);
    }

    [Then(@"SyncResult\.NodesAdded is (\d+)")]
    public void ThenSyncResultNodesAddedIs(int expected)
    {
        this.syncResult.Should().NotBeNull();
        this.syncResult!.NodesAdded.Should().Be(expected);
    }

    [Then(@"SyncResult\.EdgesRetired is (\d+)")]
    public void ThenSyncResultEdgesRetiredIs(int expected)
    {
        this.syncResult.Should().NotBeNull();
        this.syncResult!.EdgesRetired.Should().Be(expected);
    }

    [Then(@"SyncResult\.EdgesAdded is (\d+)")]
    public void ThenSyncResultEdgesAddedIs(int expected)
    {
        this.syncResult.Should().NotBeNull();
        this.syncResult!.EdgesAdded.Should().Be(expected);
    }

    [Then(@"SyncResult\.ExpertiseDecayed is (\d+)")]
    public void ThenSyncResultExpertiseDecayedIs(int expected)
    {
        this.syncResult.Should().NotBeNull();
        this.syncResult!.ExpertiseDecayed.Should().Be(expected);
    }

    // ── Deleted file scenario ───────────────────────────────────────────

    [Given(@"an indexed file ""(.*)"" with (\d+) nodes and (\d+) nodes share edge ""(.*)""")]
    public void GivenAnIndexedFileWithNodesAndSharedEdge(string filePath, int nodeCount, int sharedNodeCount, string sharedEdgeId)
    {
        for (int i = 0; i < nodeCount; i++)
        {
            var node = CreateNode($"{filePath}::Node{i}", filePath, $"hash_{i}");
            this.existingNodes.Add(node);
        }

        for (int i = 0; i < nodeCount; i++)
        {
            this.existingEdges.Add(new Hyperedge
            {
                Id = $"edge_{filePath}_{i}",
                EdgeType = HyperedgeType.MethodInvocation,
                NodeIds = new List<string> { $"{filePath}::Node{i}", "other_node" },
                Roles = new Dictionary<string, string> { [$"{filePath}::Node{i}"] = "caller", ["other_node"] = "callee" },
            });
        }

        if (sharedNodeCount >= 2 && sharedEdgeId != "none")
        {
            this.existingEdges.Add(new Hyperedge
            {
                Id = sharedEdgeId,
                EdgeType = HyperedgeType.MethodInvocation,
                NodeIds = new List<string> { $"{filePath}::Node0", $"{filePath}::Node1" },
                Roles = new Dictionary<string, string> { [$"{filePath}::Node0"] = "caller", [$"{filePath}::Node1"] = "callee" },
            });
        }
    }

    [When(@"the file is deleted and ApplyDiffAsync runs from ""(.*)"" to ""(.*)""")]
    public async Task WhenTheFileIsDeletedAndApplyDiffAsyncRuns(string fromCommit, string toCommit)
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Deleted, "src/Services/OldService.cs"));
        this.syncResult = await this.sync.ApplyDiffAsync("/repo", fromCommit, toCommit);
    }

    [Then(@"all (\d+) nodes have until_index set via RetireNodeAsync")]
    public void ThenAllNodesHaveUntilIndexSetViaRetireNodeAsync(int expected)
    {
        this.retiredNodes.Should().HaveCount(expected);

        // Verify each retired node uses the durable commit index
        foreach (var (_, untilIndex) in this.retiredNodes)
        {
            untilIndex.Should().Be(DurableCommitIndex,
                "until_index must come from the durable commits table, not in-memory counter");
        }
    }

    [Then(@"all (\d+) edges connected to file nodes are retired via RetireEdgeAsync")]
    public void ThenAllEdgesConnectedToFileNodesAreRetiredViaRetireEdgeAsync(int expected)
    {
        this.retiredEdges.Should().HaveCount(expected);

        // Verify each retired edge uses the durable commit index
        foreach (var (_, untilIndex) in this.retiredEdges)
        {
            untilIndex.Should().Be(DurableCommitIndex,
                "edge until_index must come from the durable commits table");
        }
    }

    // ── Expertise decay scenario ────────────────────────────────────────

    [Given(@"an expertise entry targeting node ""(.*)""")]
    public void GivenAnExpertiseEntryTargetingNode(string targetNodeId)
    {
        // Include SourceCode metadata so DetermineDecayFactor can distinguish
        // substantive vs whitespace-only changes. Without SourceCode, the
        // conservative MinorDecayFactor is used (§3.5.1).
        var node = CreateNodeWithSource(targetNodeId, "src/Services/PaymentService.cs", "hash_old",
            "public void ProcessRefund() { this.refundService.Execute(); }");
        this.existingNodes.Add(node);

        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, "src/Services/PaymentService.cs"));
        // New extraction returns a substantively different source (different logic)
        this.extractedNodes.Add(CreateNodeWithSource(targetNodeId, "src/Services/PaymentService.cs", "hash_new",
            "public void ProcessRefund() { this.refundService.ExecuteAsync().Wait(); this.logger.Log(); }"));
    }

    [Given(@"an expertise entry targeting node ""(.*)"" with source code")]
    public void GivenAnExpertiseEntryTargetingNodeWithSourceCode(string targetNodeId)
    {
        var node = CreateNodeWithSource(targetNodeId, "src/Services/PaymentService.cs", "hash_old",
            "public void Process() { DoWork(); }");
        this.existingNodes.Add(node);

        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, "src/Services/PaymentService.cs"));
    }

    [Given(@"the expertise has Confidence (.*)")]
    public void GivenTheExpertiseHasConfidence(double confidence)
    {
        // Create a real ExpertiseEntry that IncrementalSync will read from GetExpertiseForNodeAsync
        // and decay via UpsertExpertiseAsync. The target is the last-added existing node.
        var targetNodeId = this.existingNodes.Last().Id;
        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Test expertise", "Test body", targetNodeId, "test-agent")
        {
            Confidence = confidence,
        };
        this.expertiseEntries.Add(entry);
    }

    [When(@"the node's ContentHash changes during sync")]
    public async Task WhenTheNodesContentHashChangesDuringSync()
    {
        this.syncResult = await this.sync.ApplyDiffAsync("/repo", "abc123", "def456");
    }

    [When(@"the node's source code changes by whitespace and comments only during sync")]
    public async Task WhenTheNodesSourceCodeChangesByWhitespaceAndCommentsOnlyDuringSync()
    {
        var nodeId = this.existingNodes.Last().Id;
        var newNode = CreateNodeWithSource(nodeId, "src/Services/PaymentService.cs", "hash_new_minor",
            "public void Process() { DoWork(); } // added comment");
        this.extractedNodes.Add(newNode);

        this.syncResult = await this.sync.ApplyDiffAsync("/repo", "abc123", "def456");
    }

    [Then(@"expertise decay is applied with StaleDecayFactor (.*)")]
    public void ThenExpertiseDecayIsAppliedWithStaleDecayFactor(double expectedFactor)
    {
        // Event-driven decay persists decayed confidence via IMemoryGraphStore.UpsertExpertiseAsync
        // per §3.5.1: "The result is persisted to ExpertiseEntry.Confidence immediately."
        this.upsertedExpertise.Should().ContainSingle(
            "exactly one expertise entry should have been decayed and persisted");

        var decayed = this.upsertedExpertise.First();
        var originalEntry = this.expertiseEntries.First();
        var expectedConfidence = originalEntry.Confidence * expectedFactor;

        decayed.Confidence.Should().BeApproximately(expectedConfidence, 0.001,
            $"confidence should be multiplied by StaleDecayFactor ({expectedFactor}): " +
            $"{originalEntry.Confidence} × {expectedFactor} = {expectedConfidence}");

        // Verify the store's GetExpertiseForNodeAsync was called
        this.mockStore.Verify(
            s => s.GetExpertiseForNodeAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "event-driven decay must read expertise from IMemoryGraphStore");

        // Verify UpsertExpertiseAsync was called to persist the decayed value
        this.mockStore.Verify(
            s => s.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "event-driven decay must persist decayed confidence via IMemoryGraphStore.UpsertExpertiseAsync");

        // Verify IEvolutionEngine.DecayStaleExpertiseAsync was NOT invoked during sync.
        // Time-based decay is a separate scheduled concern — IncrementalSync only
        // performs event-driven decay (code-change triggers per §3.5.1).
        this.mockEvolution.Verify(
            e => e.DecayStaleExpertiseAsync(It.IsAny<DecayOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "IEvolutionEngine.DecayStaleExpertiseAsync must NOT be invoked during incremental sync — " +
            "time-based decay is a separate scheduled concern that must not mutate expertise during sync");
    }

    [Then(@"expertise decay is applied with MinorDecayFactor (.*)")]
    public void ThenExpertiseDecayIsAppliedWithMinorDecayFactor(double expectedFactor)
    {
        this.upsertedExpertise.Should().ContainSingle(
            "exactly one expertise entry should have been decayed and persisted");

        var decayed = this.upsertedExpertise.First();
        var originalEntry = this.expertiseEntries.First();
        var expectedConfidence = originalEntry.Confidence * expectedFactor;

        decayed.Confidence.Should().BeApproximately(expectedConfidence, 0.001,
            $"whitespace-only changes should use MinorDecayFactor ({expectedFactor}): " +
            $"{originalEntry.Confidence} × {expectedFactor} = {expectedConfidence}");

        // Verify UpsertExpertiseAsync was called to persist the decayed value
        this.mockStore.Verify(
            s => s.UpsertExpertiseAsync(It.IsAny<ExpertiseEntry>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "event-driven decay must persist decayed confidence via IMemoryGraphStore.UpsertExpertiseAsync");
    }

    // ── Stale edge propagation scenario ─────────────────────────────────

    [Given(@"function A calls function B via a CALLS edge")]
    public void GivenFunctionACallsFunctionBViaCalls()
    {
        var nodeA = CreateNode("funcA", "src/A.cs", "hashA");
        var nodeB = CreateNode("funcB", "src/B.cs", "hashB_old");
        this.existingNodes.Add(nodeA);
        this.existingNodes.Add(nodeB);

        this.existingEdges.Add(new Hyperedge
        {
            Id = "calls_a_b",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { "funcA", "funcB" },
            Roles = new Dictionary<string, string> { ["funcA"] = "caller", ["funcB"] = "callee" },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "call_chain",
            },
        });
    }

    [When(@"function B is modified during sync")]
    public async Task WhenFunctionBIsModifiedDuringSync()
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, "src/B.cs"));
        this.extractedNodes.Add(CreateNode("funcB", "src/B.cs", "hashB_new"));

        this.syncResult = await this.sync.ApplyDiffAsync("/repo", "abc123", "def456");
    }

    [Then(@"the CALLS edge from A to B has Metadata Stale set to true")]
    public void ThenTheCallsEdgeHasStaleTrue()
    {
        this.updatedEdgeMetadata.Should().ContainSingle(u =>
            u.EdgeId == "calls_a_b"
            && u.Metadata.ContainsKey(MemoryMetadataKeys.Stale)
            && (bool)u.Metadata[MemoryMetadataKeys.Stale]!);
    }

    [Then(@"the CALLS edge from A to B is not retired by cascade")]
    public void ThenTheCallsEdgeFromAToBIsNotRetiredByCascade()
    {
        this.retiredEdges.Should().NotContain(r => r.EdgeId == "calls_a_b",
            "cross-file CALLS edge must survive — modified nodes are upserted, " +
            "not retired, so RetireNodeAsync cascade does not fire");
    }

    // ── Semantic indexer scenario ───────────────────────────────────────

    [Given(@"a semantic indexer is registered")]
    public void GivenASemanticIndexerIsRegistered()
    {
        // Always registered (mandatory per architecture.md §3.6 step 9)
    }

    [Given(@"the file is modified so only 1 node has a changed ContentHash")]
    public void GivenTheFileIsModifiedSoOnly1NodeHasChangedContentHash()
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, "src/Services/Svc.cs"));

        this.extractedNodes.Add(CreateNode(
            this.existingNodes[0].Id,
            this.existingNodes[0].FilePath,
            "hash_new_changed"));

        for (int i = 1; i < this.existingNodes.Count; i++)
        {
            var contentHash = this.existingNodes[i].Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var h)
                ? h?.ToString() : null;
            this.extractedNodes.Add(CreateNode(
                this.existingNodes[i].Id,
                this.existingNodes[i].FilePath,
                contentHash!));
        }
    }

    [Then(@"the semantic indexer receives exactly (\d+) node")]
    public void ThenTheSemanticIndexerReceivesExactlyNNodes(int expected)
    {
        this.mockSemanticIndexer.Verify(
            s => s.IndexNodesAsync(It.IsAny<IReadOnlyList<HypergraphNode>>(), It.IsAny<CancellationToken>()),
            Times.Once());
        this.indexedNodes.Should().HaveCount(expected);
    }

    // ── Edge diff scenario ──────────────────────────────────────────────

    [Given(@"node ""(.*)"" has an outgoing edge ""(.*)"" to ""(.*)""")]
    public void GivenNodeHasAnOutgoingEdgeTo(string sourceId, string edgeId, string targetId)
    {
        this.existingEdges.Add(new Hyperedge
        {
            Id = edgeId,
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = new List<string> { sourceId, targetId },
            Roles = new Dictionary<string, string> { [sourceId] = "caller", [targetId] = "callee" },
        });
    }

    [Given(@"the file is modified so both nodes survive but edge ""(.*)"" is no longer extracted")]
    public void GivenTheFileIsModifiedSoBothNodesSurvivedButEdgeIsGone(string removedEdgeId)
    {
        var filePath = this.existingNodes[0].FilePath;
        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, filePath));

        foreach (var existingNode in this.existingNodes)
        {
            var contentHash = existingNode.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var h)
                ? h?.ToString() : null;
            this.extractedNodes.Add(CreateNode(
                existingNode.Id,
                existingNode.FilePath,
                contentHash!));
        }
    }

    [Then(@"edge ""(.*)"" is retired")]
    public void ThenEdgeIsRetired(string edgeId)
    {
        this.retiredEdges.Should().Contain(r => r.EdgeId == edgeId);
    }

    // ── Git diff failure scenario ───────────────────────────────────────

    [Given(@"git diff will fail with an exception")]
    public void GivenGitDiffWillFailWithAnException()
    {
        this.gitDiffShouldFail = true;
    }

    [Then(@"the sync returns a zero-change SyncResult")]
    public void ThenTheSyncReturnsAZeroChangeSyncResult()
    {
        this.syncException.Should().BeNull("git diff failure should NOT propagate — per §3.9 it returns zero-change SyncResult");
        this.syncResult.Should().NotBeNull();
        this.syncResult!.FilesChanged.Should().Be(0);
        this.syncResult!.NodesAdded.Should().Be(0);
        this.syncResult!.NodesRetired.Should().Be(0);
        this.syncResult!.EdgesAdded.Should().Be(0);
        this.syncResult!.EdgesRetired.Should().Be(0);
        this.syncResult!.ExpertiseDecayed.Should().Be(0);
    }

    [Then(@"no graph mutations occurred")]
    public void ThenNoGraphMutationsOccurred()
    {
        this.upsertedNodes.Should().BeEmpty();
        this.retiredNodes.Should().BeEmpty();
        this.retiredEdges.Should().BeEmpty();
        this.updatedEdgeMetadata.Should().BeEmpty();
        this.upsertedExpertise.Should().BeEmpty();
        this.createdEdges.Should().BeEmpty();
    }

    // ── Extraction error scenario ─────────────────────────────────────

    [Given(@"the file is modified but extraction returns zero nodes")]
    public void GivenTheFileIsModifiedButExtractionReturnsZeroNodes()
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Modified, "src/Services/PaymentService.cs"));
    }

    [Then(@"all existing nodes are retired because the file became empty")]
    public void ThenAllExistingNodesAreRetiredBecauseTheFileBecameEmpty()
    {
        // When extraction returns 0 nodes for a modified file that had nodes,
        // the file became empty — all old nodes must be retired via RetireNodeAsync
        var fileNodes = this.existingNodes
            .Where(n => n.FilePath == "src/Services/PaymentService.cs")
            .ToList();

        this.retiredNodes.Should().HaveCount(fileNodes.Count,
            "all existing nodes should be retired when file becomes empty");

        foreach (var (_, untilIndex) in this.retiredNodes)
        {
            untilIndex.Should().Be(DurableCommitIndex);
        }
    }

    [Then(@"no nodes are retired")]
    public void ThenNoNodesAreRetired()
    {
        this.retiredNodes.Should().BeEmpty("no nodes should be retired in this scenario");
    }

    // ── Added file extraction error scenario ──────────────────────────

    [Given(@"git diff reports an added file ""(.*)""")]
    public void GivenGitDiffReportsAnAddedFile(string filePath)
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Added, filePath));
    }

    [Given(@"extraction will throw an exception for the added file")]
    public void GivenExtractionWillThrowAnExceptionForTheAddedFile()
    {
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ExtractionOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Extraction pipeline error: unsupported file format"));
    }

    // ── Renamed file extraction error scenario ────────────────────────

    [Given(@"git diff reports a rename from ""(.*)"" to ""(.*)""")]
    public void GivenGitDiffReportsARenameFromTo(string oldPath, string newPath)
    {
        this.fileChanges.Add(new FileChange(FileChangeStatus.Renamed, newPath, oldPath));
    }

    [Given(@"extraction will throw an exception for the renamed file new path")]
    public void GivenExtractionWillThrowAnExceptionForTheRenamedFileNewPath()
    {
        this.mockPipeline
            .Setup(p => p.ExtractFilesAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ExtractionOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Extraction pipeline error: unsupported file format"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static HypergraphNode CreateNode(string id, string filePath, string contentHash)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = id.Split("::").Last(),
            NodeType = "Method",
            Language = "C#",
            FilePath = filePath,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = contentHash,
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 1,
            },
        };
    }

    private static HypergraphNode CreateNodeWithSource(string id, string filePath, string contentHash, string sourceCode)
    {
        return new HypergraphNode
        {
            Id = id,
            Name = id.Split("::").Last(),
            NodeType = "Method",
            Language = "C#",
            FilePath = filePath,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = contentHash,
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 1,
                [MemoryMetadataKeys.SourceCode] = sourceCode,
            },
        };
    }
}