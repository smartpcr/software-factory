// -----------------------------------------------------------------------
// <copyright file="SqliteGraphStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Storage;
using Microsoft.Data.Sqlite;

[TestClass]
public class SqliteGraphStoreTests
{
    // -- Helpers --

    private static HypergraphNode MakeNode(
        string id,
        string memoryLayer,
        string nodeType = "Class",
        string language = "C#",
        string filePath = "src/Foo.cs",
        string? contentHash = "abc123")
    {
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = memoryLayer,
        };

        if (contentHash is not null)
        {
            metadata[MemoryMetadataKeys.ContentHash] = contentHash;
        }

        return new HypergraphNode
        {
            Id = id,
            Name = id,
            NodeType = nodeType,
            Language = language,
            FilePath = filePath,
            Metadata = metadata,
        };
    }

    private static Hyperedge MakeEdge(
        string id,
        List<string> nodeIds,
        string semanticRelation = "CALLS",
        string memoryLayer = "structural")
    {
        return new Hyperedge
        {
            Id = id,
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = nodeIds,
            Roles = new Dictionary<string, string>
            {
                [nodeIds[0]] = "source",
                [nodeIds[1]] = "target",
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = semanticRelation,
                [MemoryMetadataKeys.MemoryLayer] = memoryLayer,
            },
        };
    }

    private static async Task<object?> ReadRawColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string idColumn,
        string idValue)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {table} WHERE {idColumn} = @id";
        cmd.Parameters.AddWithValue("@id", idValue);
        return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    }

    // -- Scenario 1: Node upsert and temporal query --

    [TestMethod]
    public async Task UpsertNode_And_TemporalQuery_AtCommitIndex1_ReturnsNode()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var node = MakeNode("node-1", "structural");
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("node-1");
    }

    [TestMethod]
    public async Task UpsertNode_And_TemporalQuery_AtCommitIndex0_ReturnsNothing()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var node = MakeNode("node-1", "structural");
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 0, filter: null, limit: 100, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [TestMethod]
    public async Task UpsertNode_And_TemporalQuery_NodeRetiredBeforeTarget_IsExcluded()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var node = MakeNode("node-1", "structural");
        var version = new VersionRange("abc", 1, "def", 3);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        var resultsAt2 = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 2, filter: null, limit: 100, CancellationToken.None);
        resultsAt2.Should().HaveCount(1);

        var resultsAt3 = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 3, filter: null, limit: 100, CancellationToken.None);
        resultsAt3.Should().BeEmpty();
    }

    // -- Scenario 2: RetireNodeAsync cascades --

    [TestMethod]
    public async Task RetireNodeAsync_CascadesToConnectedEdges()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var nodeC = MakeNode("C", "structural");
        var nodeD = MakeNode("D", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeC, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeD, ver, CancellationToken.None);

        var edge1 = MakeEdge("e1", ["A", "B"]);
        var edge2 = MakeEdge("e2", ["A", "C"]);
        var edge3 = MakeEdge("e3", ["D", "A"]);
        var edge4 = MakeEdge("e4", ["B", "C"]);

        await store.UpsertEdgeAsync(edge1, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edge2, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edge3, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edge4, ver, CancellationToken.None);

        await store.RetireNodeAsync("A", untilCommitIndex: 5, CancellationToken.None);

        var nodesAfter = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 6, filter: null, limit: 100, CancellationToken.None);
        nodesAfter.Select(n => n.Inner.Id).Should().NotContain("A");

        var allEdges = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, atCommitIndex: 6, limit: 100, CancellationToken.None);
        var edgeIds = allEdges.Select(e => e.Inner.Id).ToList();
        edgeIds.Should().NotContain("e1");
        edgeIds.Should().NotContain("e2");
        edgeIds.Should().NotContain("e3");
        edgeIds.Should().Contain("e4");
    }

    [TestMethod]
    public async Task RetireNodeAsync_CascadesToConnectedEdges_VerifyPersistedValues()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var nodeC = MakeNode("C", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeC, ver, CancellationToken.None);

        var edge1 = MakeEdge("e1", ["A", "B"]);
        var edge2 = MakeEdge("e2", ["A", "C"]);
        var edgeUnrelated = MakeEdge("e3", ["B", "C"]);

        await store.UpsertEdgeAsync(edge1, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edge2, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edgeUnrelated, ver, CancellationToken.None);

        await store.RetireNodeAsync("A", untilCommitIndex: 5, CancellationToken.None);

        var conn = store.Connection;
        var nodeUntil = await ReadRawColumnAsync(conn, "nodes", "until_index", "id", "A");
        nodeUntil.Should().Be(5L, "node A should have until_index=5 persisted");

        var e1Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e1");
        e1Until.Should().Be(5L, "edge e1 connected to A should have until_index=5");

        var e2Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e2");
        e2Until.Should().Be(5L, "edge e2 connected to A should have until_index=5");

        var e3Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e3");
        e3Until.Should().BeOfType<DBNull>("unrelated edge e3 should have NULL until_index");
    }

    [TestMethod]
    public async Task RetireNodeAsync_DoesNotRetireAlreadyRetiredEdges()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge1 = MakeEdge("e1", ["A", "B"]);
        await store.UpsertEdgeAsync(edge1, ver, CancellationToken.None);

        await store.RetireEdgeAsync("e1", untilCommitIndex: 3, CancellationToken.None);
        await store.RetireNodeAsync("A", untilCommitIndex: 5, CancellationToken.None);

        var conn = store.Connection;
        var e1Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e1");
        e1Until.Should().Be(3L, "already-retired edge should keep its original until_index");

        var edgesAt2 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, atCommitIndex: 2, limit: 100, CancellationToken.None);
        edgesAt2.Should().HaveCount(1);

        var edgesAt4 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, atCommitIndex: 4, limit: 100, CancellationToken.None);
        edgesAt4.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RetireNodeAsync_BringsForwardLaterRetiredEdge()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge1 = MakeEdge("e1", ["A", "B"]);
        await store.UpsertEdgeAsync(edge1, ver, CancellationToken.None);

        // Edge retired at 7, then node retired at 5 (earlier).
        // The edge cannot outlive the node, so until_index must be brought forward to 5.
        await store.RetireEdgeAsync("e1", untilCommitIndex: 7, CancellationToken.None);
        await store.RetireNodeAsync("A", untilCommitIndex: 5, CancellationToken.None);

        var conn = store.Connection;
        var e1Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e1");
        e1Until.Should().Be(5L, "edge retired later than node should be brought forward to node's until_index");
    }

    [TestMethod]
    public async Task RetireNodeAsync_ThreeConnectedEdges_AllGetUntilIndex()
    {
        // Exact scenario: "Given a node with 3 connected edges,
        // When RetireNodeAsync(nodeId, untilIndex=5) is called,
        // Then the node and all 3 edges have until_index=5."
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var nodeC = MakeNode("C", "structural");
        var nodeD = MakeNode("D", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeC, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeD, ver, CancellationToken.None);

        var edge1 = MakeEdge("e1", ["A", "B"]);
        var edge2 = MakeEdge("e2", ["A", "C"]);
        var edge3 = MakeEdge("e3", ["D", "A"]);

        await store.UpsertEdgeAsync(edge1, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edge2, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edge3, ver, CancellationToken.None);

        await store.RetireNodeAsync("A", untilCommitIndex: 5, CancellationToken.None);

        var conn = store.Connection;

        var nodeUntil = await ReadRawColumnAsync(conn, "nodes", "until_index", "id", "A");
        nodeUntil.Should().Be(5L, "node A should have until_index=5");

        var e1Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e1");
        e1Until.Should().Be(5L, "edge e1 connected to A should have until_index=5");

        var e2Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e2");
        e2Until.Should().Be(5L, "edge e2 connected to A should have until_index=5");

        var e3Until = await ReadRawColumnAsync(conn, "edges", "until_index", "id", "e3");
        e3Until.Should().Be(5L, "edge e3 connected to A should have until_index=5");
    }

    // -- Scenario 3: Expertise archive lifecycle --

    [TestMethod]
    public async Task ExpertiseArchiveLifecycle()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern,
            "Dispose Pattern",
            "Always implement IDisposable with the standard dispose pattern.",
            "node-1",
            "copilot-agent")
        {
            Confidence = 1.0,
        };

        await store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var active = await store.GetExpertiseForNodeAsync("node-1", 0.0, CancellationToken.None);
        active.Should().HaveCount(1);
        active[0].Title.Should().Be("Dispose Pattern");
        active[0].Confidence.Should().Be(1.0);

        var archivedBefore = await store.GetArchivedExpertiseAsync("node-1", 100, CancellationToken.None);
        archivedBefore.Should().BeEmpty();

        await store.ArchiveExpertiseAsync(entry.Id, CancellationToken.None);

        var activeAfter = await store.GetExpertiseForNodeAsync("node-1", 0.0, CancellationToken.None);
        activeAfter.Should().BeEmpty();

        var archivedAfter = await store.GetArchivedExpertiseAsync("node-1", 100, CancellationToken.None);
        archivedAfter.Should().HaveCount(1);
        archivedAfter[0].Title.Should().Be("Dispose Pattern");
    }

    // -- Scenario 4: External node temporal inclusion --

    [TestMethod]
    public async Task ExternalNode_WithNullSinceIndex_AlwaysIncluded()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        // External boundary node via production API with null SinceIndex
        var node = MakeNode("external-1", "structural", nodeType: "External");
        var externalVersion = new VersionRange(null, null, null, null);
        await store.UpsertNodeAsync(node, externalVersion, CancellationToken.None);

        // Verify since_index is actually NULL in the DB
        var sinceIndex = await ReadRawColumnAsync(store.Connection, "nodes", "since_index", "id", "external-1");
        sinceIndex.Should().BeOfType<DBNull>("External node should have NULL since_index persisted");

        foreach (var idx in new[] { 0, 1, 5, 100 })
        {
            var results = await store.GetNodesByLayerAsync(
                "structural", atCommitIndex: idx, filter: null, limit: 100, CancellationToken.None);
            results.Should().Contain(n => n.Inner.Id == "external-1",
                $"External node should be visible at commit index {idx}");
        }

        var allResults = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: null, filter: null, limit: 100, CancellationToken.None);
        allResults.Should().Contain(n => n.Inner.Id == "external-1");
    }

    [TestMethod]
    public async Task ExternalNode_AlwaysIncluded_EvenAfterRetirement()
    {
        // Per architecture.md 4.3: External boundary nodes with NULL since_index
        // are always included by the temporal predicate unconditionally.
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var node = MakeNode("external-retired", "structural", nodeType: "External");
        await store.UpsertNodeAsync(node, new VersionRange(null, null, null, null), CancellationToken.None);

        await store.RetireNodeAsync("external-retired", untilCommitIndex: 5, CancellationToken.None);

        var untilIndex = await ReadRawColumnAsync(store.Connection, "nodes", "until_index", "id", "external-retired");
        untilIndex.Should().Be(5L, "retirement should persist until_index=5");

        // External nodes with NULL since_index are unconditionally included
        var atIdx4 = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 4, filter: null, limit: 100, CancellationToken.None);
        atIdx4.Should().Contain(n => n.Inner.Id == "external-retired",
            "External node always included per architecture.md");

        var atIdx10 = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 10, filter: null, limit: 100, CancellationToken.None);
        atIdx10.Should().Contain(n => n.Inner.Id == "external-retired",
            "External node always included even after until_index");
    }

    // -- Additional tests --

    [TestMethod]
    public async Task GetNodesByLayerAsync_NullCommitIndex_ExcludesRetiredNodes()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(MakeNode("active", "structural"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("retired", "structural"), ver, CancellationToken.None);

        await store.RetireNodeAsync("retired", untilCommitIndex: 5, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: null, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("active",
            "retired nodes must not appear when atCommitIndex is null (active-node query)");
    }

    [TestMethod]
    public async Task GetNodesByLayerAsync_WithNodeFilter()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(MakeNode("n1", "structural", nodeType: "Class", language: "C#", filePath: "src/A.cs"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n2", "structural", nodeType: "Function", language: "C#", filePath: "src/B.cs"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n3", "structural", nodeType: "Class", language: "PS", filePath: "lib/C.ps1"), ver, CancellationToken.None);

        var classes = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(NodeType: "Class"), 100, CancellationToken.None);
        classes.Should().HaveCount(2);

        var csharp = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(Language: "C#"), 100, CancellationToken.None);
        csharp.Should().HaveCount(2);

        var srcNodes = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(FilePathPrefix: "src/"), 100, CancellationToken.None);
        srcNodes.Should().HaveCount(2);

        var specific = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(NodeIds: ["n1", "n3"]), 100, CancellationToken.None);
        specific.Should().HaveCount(2);
        specific.Select(n => n.Inner.Id).Should().BeEquivalentTo(["n1", "n3"]);
    }

    [TestMethod]
    public async Task UpsertNodeAsync_UpdatesExistingNode()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver1 = new VersionRange("c1", 1, null, null);
        var ver2 = new VersionRange("c2", 2, null, null);

        var node = MakeNode("n1", "structural");
        await store.UpsertNodeAsync(node, ver1, CancellationToken.None);

        var updated = MakeNode("n1", "structural", nodeType: "Function");
        await store.UpsertNodeAsync(updated, ver2, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", 2, null, 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.NodeType.Should().Be("Function");
    }

    [TestMethod]
    public async Task GetEdgesBySemanticRelationAsync_FiltersCorrectly()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        var e1 = MakeEdge("e1", ["A", "B"], "CALLS", "structural");
        var e2 = MakeEdge("e2", ["C", "D"], "IMPORTS", "structural");
        var e3 = MakeEdge("e3", ["E", "F"], "CALLS", "semantic");

        await store.UpsertEdgeAsync(e1, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(e2, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(e3, ver, CancellationToken.None);

        var calls = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, 1, 100, CancellationToken.None);
        calls.Should().HaveCount(2);

        var callsStructural = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", 1, 100, CancellationToken.None);
        callsStructural.Should().HaveCount(1);
        callsStructural[0].Inner.Id.Should().Be("e1");
    }

    [TestMethod]
    public async Task UpdateEdgeMetadataAsync_MergesMetadata()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        var edge = MakeEdge("e1", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, ver, CancellationToken.None);

        await store.UpdateEdgeMetadataAsync("e1", new Dictionary<string, object?>
        {
            ["extra_field"] = "extra_value",
        }, CancellationToken.None);

        var edges = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, 1, 100, CancellationToken.None);
        edges.Should().HaveCount(1);
        edges[0].Inner.Metadata.Should().ContainKey("extra_field");
        edges[0].Inner.Metadata["extra_field"]!.ToString().Should().Be("extra_value");
    }

    [TestMethod]
    public async Task LogAuditAsync_InsertsRecordWithJsonDetail()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var detail = new { nodeId = "n1", action = "created" };
        await store.LogAuditAsync("session-1", "upsert_node", detail, CancellationToken.None);

        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT session_id, operation, detail FROM audit_log WHERE session_id = 'session-1'";
        using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("session-1");
        reader.GetString(1).Should().Be("upsert_node");
        var json = reader.GetString(2);
        json.Should().Contain("nodeId");
        json.Should().Contain("n1");

        var stats = await store.GetMemoryStatisticsAsync(CancellationToken.None);
        stats.LastUpdated.Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetMemoryStatisticsAsync_ReturnsCorrectCounts()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(MakeNode("n1", "structural"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n2", "structural"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n3", "semantic"), ver, CancellationToken.None);

        var edge = MakeEdge("e1", ["n1", "n2"]);
        await store.UpsertEdgeAsync(edge, ver, CancellationToken.None);

        var expertise = new ExpertiseEntry(
            ExpertiseKind.Insight, "Test", "Test body", "n1", "agent");
        await store.UpsertExpertiseAsync(expertise, CancellationToken.None);

        var stats = await store.GetMemoryStatisticsAsync(CancellationToken.None);
        stats.TotalNodes.Should().Be(3);
        stats.TotalEdges.Should().Be(1);
        stats.TotalExpertiseEntries.Should().Be(1);
        stats.ArchivedExpertiseEntries.Should().Be(0);
        stats.LayerStats.Should().ContainKey("structural");
        stats.LayerStats["structural"].NodeCount.Should().Be(2);
        stats.LayerStats.Should().ContainKey("semantic");
        stats.LayerStats["semantic"].NodeCount.Should().Be(1);
    }

    [TestMethod]
    public async Task Expertise_MinConfidenceFilter()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var low = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Low conf", "Body1", "n1", "agent")
        {
            Confidence = 0.3,
        };
        var high = new ExpertiseEntry(
            ExpertiseKind.Pattern, "High conf", "Body2", "n1", "agent")
        {
            Confidence = 0.9,
        };

        await store.UpsertExpertiseAsync(low, CancellationToken.None);
        await store.UpsertExpertiseAsync(high, CancellationToken.None);

        var highOnly = await store.GetExpertiseForNodeAsync("n1", 0.5, CancellationToken.None);
        highOnly.Should().HaveCount(1);
        highOnly[0].Title.Should().Be("High conf");
    }

    [TestMethod]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var store = await SqliteGraphStore.CreateInMemoryAsync();
        await store.DisposeAsync();
        await store.DisposeAsync();
    }

    [TestMethod]
    public async Task MetadataRoundTrip_PreservesTypes()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "deadbeef",
            ["intVal"] = 42,
            ["doubleVal"] = 3.14,
            ["boolVal"] = true,
            ["nullVal"] = null,
            ["stringVal"] = "hello",
        };

        var node = new HypergraphNode
        {
            Id = "roundtrip-node",
            Name = "RoundTrip",
            NodeType = "Class",
            Language = "C#",
            FilePath = "test.cs",
            Metadata = metadata,
        };

        await store.UpsertNodeAsync(node, new VersionRange("c1", 1, null, null), CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", 1, null, 100, CancellationToken.None);
        results.Should().HaveCount(1);

        var m = results[0].Inner.Metadata;
        m["intVal"].Should().Be(42);
        m["doubleVal"].Should().Be(3.14);
        m["boolVal"].Should().Be(true);
        m.Should().ContainKey("nullVal");
        m["stringVal"].Should().Be("hello");
    }

    // -- Schema and Index verification --

    [TestMethod]
    public async Task Schema_AllRequiredTablesExist()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var tables = await GetTableNamesAsync(store.Connection);

        tables.Should().Contain("nodes");
        tables.Should().Contain("edges");
        tables.Should().Contain("edge_nodes");
        tables.Should().Contain("expertise");
        tables.Should().Contain("commits");
        tables.Should().Contain("audit_log");
        tables.Should().Contain("vector_index");
    }

    [TestMethod]
    public async Task Schema_Fts5VirtualTablesExist()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT name, sql FROM sqlite_master
            WHERE type = 'table' AND sql LIKE '%fts5%'
            ORDER BY name";

        var fts5Tables = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            fts5Tables.Add(reader.GetString(0));
        }

        fts5Tables.Should().Contain("nodes_fts", "FTS5 virtual table for node search");
        fts5Tables.Should().Contain("expertise_fts", "FTS5 virtual table for expertise search");
    }

    [TestMethod]
    public async Task Schema_AllRequiredIndexesExist()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var indexes = await GetIndexNamesAsync(store.Connection);

        indexes.Should().Contain("idx_nodes_layer");
        indexes.Should().Contain("idx_nodes_type");
        indexes.Should().Contain("idx_nodes_file");
        indexes.Should().Contain("idx_nodes_since");
        indexes.Should().Contain("idx_edges_semantic");
        indexes.Should().Contain("idx_edges_layer");
        indexes.Should().Contain("idx_edges_since");
        indexes.Should().Contain("idx_expertise_target");
        indexes.Should().Contain("idx_expertise_kind");
        indexes.Should().Contain("idx_expertise_archived");
        indexes.Should().Contain("idx_audit_session");
    }

    [TestMethod]
    public async Task Schema_Fts5TriggersExist()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";

        var triggers = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            triggers.Add(reader.GetString(0));
        }

        triggers.Should().Contain("nodes_ai");
        triggers.Should().Contain("nodes_ad");
        triggers.Should().Contain("nodes_au");
        triggers.Should().Contain("expertise_ai");
        triggers.Should().Contain("expertise_ad");
        triggers.Should().Contain("expertise_au");
    }

    // -- FTS5 Search tests --

    [TestMethod]
    public async Task SearchNodesFts_ReturnsMatchingNodes()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(MakeNode("n1", "structural", nodeType: "Class", filePath: "src/UserService.cs"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n2", "structural", nodeType: "Interface", filePath: "src/IUserRepo.cs"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n3", "structural", nodeType: "Class", filePath: "src/OrderService.cs"), ver, CancellationToken.None);

        var results = await store.SearchNodesFtsAsync("n1", 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("n1");
    }

    [TestMethod]
    public async Task SearchNodesFts_MatchesByNodeType()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(MakeNode("n1", "structural", nodeType: "Class"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n2", "structural", nodeType: "Interface"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("n3", "structural", nodeType: "Class"), ver, CancellationToken.None);

        var classes = await store.SearchNodesFtsAsync("Class", 100, CancellationToken.None);
        classes.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task SearchNodesFts_ExcludesRetiredNodes()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(MakeNode("active", "structural", nodeType: "Class"), ver, CancellationToken.None);
        await store.UpsertNodeAsync(MakeNode("retired", "structural", nodeType: "Class"), ver, CancellationToken.None);

        await store.RetireNodeAsync("retired", untilCommitIndex: 5, CancellationToken.None);

        var results = await store.SearchNodesFtsAsync("Class", 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("active");
    }

    [TestMethod]
    public async Task SearchExpertiseFts_ReturnsMatchingEntries()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var entry1 = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Dispose Pattern", "Always use IDisposable pattern.", "n1", "agent");
        var entry2 = new ExpertiseEntry(
            ExpertiseKind.Pitfall, "Null Reference", "Check for null before accessing.", "n2", "agent");

        await store.UpsertExpertiseAsync(entry1, CancellationToken.None);
        await store.UpsertExpertiseAsync(entry2, CancellationToken.None);

        var results = await store.SearchExpertiseFtsAsync("Dispose", 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Dispose Pattern");
    }

    [TestMethod]
    public async Task SearchExpertiseFts_ExcludesArchivedEntries()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Dispose Pattern", "Always use IDisposable pattern.", "n1", "agent");
        await store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var beforeArchive = await store.SearchExpertiseFtsAsync("Dispose", 100, CancellationToken.None);
        beforeArchive.Should().HaveCount(1);

        await store.ArchiveExpertiseAsync(entry.Id, CancellationToken.None);

        var afterArchive = await store.SearchExpertiseFtsAsync("Dispose", 100, CancellationToken.None);
        afterArchive.Should().BeEmpty("archived expertise should not appear in FTS search");
    }

    [TestMethod]
    public async Task SearchNodesFts_UpdatedNode_ReflectsInFts()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var ver1 = new VersionRange("c1", 1, null, null);
        var ver2 = new VersionRange("c2", 2, null, null);

        var node1 = new HypergraphNode
        {
            Id = "n1",
            Name = "OriginalName",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Foo.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "abc",
            },
        };
        await store.UpsertNodeAsync(node1, ver1, CancellationToken.None);

        var node2 = new HypergraphNode
        {
            Id = "n1",
            Name = "UpdatedName",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/Foo.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "def",
            },
        };
        await store.UpsertNodeAsync(node2, ver2, CancellationToken.None);

        var oldResults = await store.SearchNodesFtsAsync("OriginalName", 100, CancellationToken.None);
        oldResults.Should().BeEmpty();

        var newResults = await store.SearchNodesFtsAsync("UpdatedName", 100, CancellationToken.None);
        newResults.Should().HaveCount(1);
    }

    // -- Vector index tests --

    [TestMethod]
    public async Task VectorIndex_UpsertAndRetrieve()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        await store.UpsertVectorAsync("node-1", embedding, CancellationToken.None);

        var retrieved = await store.GetVectorAsync("node-1", CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved.Should().BeEquivalentTo(embedding);
    }

    [TestMethod]
    public async Task VectorIndex_ReturnsNullForMissingNode()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        var result = await store.GetVectorAsync("nonexistent", CancellationToken.None);
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task VssAvailable_IsFalseWithoutExtension()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();
        store.VssAvailable.Should().BeFalse(
            "sqlite-vss requires a native extension that is not bundled in test environments");
    }

    [TestMethod]
    public async Task SearchSimilarVectors_BruteForce_ReturnsTopK()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        // Insert 3 embeddings with known vectors
        await store.UpsertVectorAsync("n1", [1.0f, 0.0f, 0.0f, 0.0f], CancellationToken.None);
        await store.UpsertVectorAsync("n2", [0.9f, 0.1f, 0.0f, 0.0f], CancellationToken.None);
        await store.UpsertVectorAsync("n3", [0.0f, 0.0f, 1.0f, 0.0f], CancellationToken.None);

        var query = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var results = await store.SearchSimilarVectorsAsync(query, 2, CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].NodeId.Should().Be("n1", "exact match should be first");
        results[0].Score.Should().BeApproximately(1.0, 1e-6);
        results[1].NodeId.Should().Be("n2", "nearly parallel vector should be second");
        results[1].Score.Should().BeGreaterThan(0.9);
    }

    [TestMethod]
    public async Task SearchSimilarVectors_EmptyIndex_ReturnsEmpty()
    {
        await using var store = await SqliteGraphStore.CreateInMemoryAsync();

        var results = await store.SearchSimilarVectorsAsync(
            [1.0f, 0.0f, 0.0f], 10, CancellationToken.None);
        results.Should().BeEmpty();
    }

    // -- Private helpers --

    private static async Task<List<string>> GetTableNamesAsync(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<List<string>> GetIndexNamesAsync(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var indexes = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }
}