// -----------------------------------------------------------------------
// <copyright file="SqliteMemoryGraphStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Sync;

using Ast.Core;
using Ast.Memory.Models;
using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Real graph-state round-trip tests using <see cref="SqliteMemoryGraphStore"/>.
/// Validates that node/edge/expertise persistence is correct and that
/// cascade behaviors work as required by the memory store contract.
/// </summary>
[TestClass]
public class SqliteMemoryGraphStoreTests
{
    private string tempDir = null!;
    private string dbPath = null!;

    [TestInitialize]
    public void Setup()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"sqlite_graph_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.dbPath = Path.Combine(this.tempDir, "test_graph.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.tempDir))
        {
            try { Directory.Delete(this.tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ================================================================
    // Edge round-trip: roles, languages, weight preserved
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task Edge_RoundTrip_PreservesRolesLanguagesWeight()
    {
        using var store = new SqliteMemoryGraphStore(this.dbPath);

        var node1 = new HypergraphNode
        {
            Id = "src/A.cs::ClassA",
            Name = "ClassA",
            NodeType = "Class",
            Language = "C#",
            FilePath = "src/A.cs",
            Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
        };
        var node2 = new HypergraphNode
        {
            Id = "src/B.ps1::FunctionB",
            Name = "FunctionB",
            NodeType = "Function",
            Language = "PowerShell",
            FilePath = "src/B.ps1",
            Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
        };
        await store.CreateNodeAsync(node1);
        await store.CreateNodeAsync(node2);

        var edge = new Hyperedge
        {
            Id = "edge-cross-lang-1",
            EdgeType = HyperedgeType.CrossLanguageMethodCall,
            NodeIds = ["src/A.cs::ClassA", "src/B.ps1::FunctionB"],
            Roles = new Dictionary<string, string>
            {
                ["src/A.cs::ClassA"] = "caller",
                ["src/B.ps1::FunctionB"] = "callee"
            },
            Languages = ["C#", "PowerShell"],
            Weight = 0.75,
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "call_chain",
                [MemoryMetadataKeys.SourceFile] = "src/A.cs"
            }
        };
        await store.CreateEdgeAsync(edge);

        // Round-trip: read it back
        var retrieved = await store.GetEdgeAsync("edge-cross-lang-1");
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be("edge-cross-lang-1");
        retrieved.EdgeType.Should().Be(HyperedgeType.CrossLanguageMethodCall);
        retrieved.NodeIds.Should().BeEquivalentTo(["src/A.cs::ClassA", "src/B.ps1::FunctionB"]);

        // Roles must be preserved (not placeholder source/target)
        retrieved.Roles.Should().ContainKey("src/A.cs::ClassA")
            .WhoseValue.Should().Be("caller");
        retrieved.Roles.Should().ContainKey("src/B.ps1::FunctionB")
            .WhoseValue.Should().Be("callee");

        // Languages must be preserved
        retrieved.Languages.Should().BeEquivalentTo(["C#", "PowerShell"]);

        // Weight must be preserved
        retrieved.Weight.Should().BeApproximately(0.75, 0.001);
    }

    // ================================================================
    // Edge versioning: SinceCommit/SinceIndex persisted
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task Edge_Versioning_SinceCommitAndSinceIndex_Persisted()
    {
        using var store = new SqliteMemoryGraphStore(this.dbPath);

        var node = new HypergraphNode
        {
            Id = "v/A.cs::ClassA",
            Name = "ClassA",
            NodeType = "Class",
            Language = "C#",
            FilePath = "v/A.cs",
            Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
        };
        await store.CreateNodeAsync(node);

        var edge = new Hyperedge
        {
            Id = "edge-versioned-1",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["v/A.cs::ClassA", "v/B.cs::ClassB"],
            Roles = new Dictionary<string, string>
            {
                ["v/A.cs::ClassA"] = "caller",
                ["v/B.cs::ClassB"] = "callee"
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "call_chain",
                [MemoryMetadataKeys.SinceCommit] = "abc123",
                [MemoryMetadataKeys.SinceIndex] = 5
            }
        };
        await store.CreateEdgeAsync(edge);

        // Read it back and verify version metadata
        var retrieved = await store.GetEdgeAsync("edge-versioned-1");
        retrieved.Should().NotBeNull();

        // Version info should be accessible through metadata
        retrieved!.Metadata.Should().ContainKey(MemoryMetadataKeys.SinceIndex);
        Convert.ToInt32(retrieved.Metadata[MemoryMetadataKeys.SinceIndex]).Should().Be(5);
    }

    // ================================================================
    // GetExpertiseForNodeAsync: archived expertise NOT returned
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task GetExpertiseForNode_ExcludesArchivedEntries()
    {
        using var store = new SqliteMemoryGraphStore(this.dbPath);

        var node = new HypergraphNode
        {
            Id = "arch/A.cs::ClassA",
            Name = "ClassA",
            NodeType = "Class",
            Language = "C#",
            FilePath = "arch/A.cs",
            Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
        };
        await store.CreateNodeAsync(node);

        // Active expertise
        var active = new ExpertiseEntry
        {
            Id = "exp-active",
            Kind = ExpertiseKind.Pattern,
            Title = "Active Pattern",
            Body = "Still relevant",
            TargetNodeId = "arch/A.cs::ClassA",
            Confidence = 0.8,
            SourceAgent = "test-agent",
            ContentHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await store.UpsertExpertiseAsync(active);

        // Archived expertise
        var archived = new ExpertiseEntry
        {
            Id = "exp-archived",
            Kind = ExpertiseKind.Pitfall,
            Title = "Archived Pitfall",
            Body = "No longer relevant",
            TargetNodeId = "arch/A.cs::ClassA",
            Confidence = 0.3,
            Archived = true,
            SourceAgent = "test-agent",
            ContentHash = "hash-2",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await store.UpsertExpertiseAsync(archived);

        // GetExpertiseForNodeAsync should NOT include archived entries
        var results = await store.GetExpertiseForNodeAsync("arch/A.cs::ClassA");
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("exp-active");

        // GetArchivedExpertiseAsync should return the archived entry
        var archivedResults = await store.GetArchivedExpertiseAsync("arch/A.cs::ClassA");
        archivedResults.Should().HaveCount(1);
        archivedResults[0].Id.Should().Be("exp-archived");
    }

    // ================================================================
    // RetireNodeAsync: cascades to edges and expertise
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task RetireNode_CascadesToEdgesAndExpertise()
    {
        using var store = new SqliteMemoryGraphStore(this.dbPath);

        // Create two nodes
        var nodeA = new HypergraphNode
        {
            Id = "cas/A.cs::ClassA",
            Name = "ClassA",
            NodeType = "Class",
            Language = "C#",
            FilePath = "cas/A.cs",
            Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
        };
        var nodeB = new HypergraphNode
        {
            Id = "cas/B.cs::ClassB",
            Name = "ClassB",
            NodeType = "Class",
            Language = "C#",
            FilePath = "cas/B.cs",
            Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
        };
        await store.CreateNodeAsync(nodeA);
        await store.CreateNodeAsync(nodeB);

        // Create an edge connecting them
        var edge = new Hyperedge
        {
            Id = "edge-cascade-1",
            EdgeType = HyperedgeType.MethodInvocation,
            NodeIds = ["cas/A.cs::ClassA", "cas/B.cs::ClassB"],
            Roles = new Dictionary<string, string>
            {
                ["cas/A.cs::ClassA"] = "caller",
                ["cas/B.cs::ClassB"] = "callee"
            },
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.SemanticRelation] = "CALLS",
                [MemoryMetadataKeys.MemoryLayer] = "call_chain"
            }
        };
        await store.CreateEdgeAsync(edge);

        // Create expertise for nodeA
        var expertise = new ExpertiseEntry
        {
            Id = "exp-cascade",
            Kind = ExpertiseKind.Pattern,
            Title = "Pattern for A",
            Body = "About ClassA",
            TargetNodeId = "cas/A.cs::ClassA",
            Confidence = 0.9,
            SourceAgent = "test-agent",
            ContentHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await store.UpsertExpertiseAsync(expertise);

        // Retire nodeA — should cascade to edges and expertise
        await store.RetireNodeAsync("cas/A.cs::ClassA", untilCommitIndex: 5);

        // Verify node is retired (not returned by active query)
        var stats = await store.GetStatisticsAsync();
        stats.TotalNodes.Should().Be(1, "only nodeB should be active (nodeA retired)");

        // Verify edge is retired (cascaded)
        stats.TotalEdges.Should().Be(0, "edge should be retired when connected node is retired");

        // Verify the edge was soft-retired, not deleted
        var retiredEdge = await store.GetEdgeAsync("edge-cascade-1");
        retiredEdge.Should().NotBeNull("edge should still exist as soft-retired");
        retiredEdge!.Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex);

        // Verify expertise was archived (cascaded)
        var activeExpertise = await store.GetExpertiseForNodeAsync("cas/A.cs::ClassA");
        activeExpertise.Should().BeEmpty("expertise should be archived when node is retired");

        var archivedExpertise = await store.GetArchivedExpertiseAsync("cas/A.cs::ClassA");
        archivedExpertise.Should().HaveCount(1, "expertise should be archived, not deleted");
        archivedExpertise[0].Id.Should().Be("exp-cascade");
    }

    // ================================================================
    // UpsertNodeAsync: version range persisted and queryable
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task UpsertNode_PersistsVersionRange()
    {
        using var store = new SqliteMemoryGraphStore(this.dbPath);

        var node = new HypergraphNode
        {
            Id = "ver/A.cs::ClassA",
            Name = "ClassA",
            NodeType = "Class",
            Language = "C#",
            FilePath = "ver/A.cs",
            Metadata = new Dictionary<string, object?>
            {
                [MemoryMetadataKeys.MemoryLayer] = "structural",
                [MemoryMetadataKeys.ContentHash] = "hash-1"
            }
        };

        var version = new VersionRange("abc123", 3, null, null);
        await store.UpsertNodeAsync(node, version);

        // Read back and verify version metadata
        var retrieved = await store.GetNodeAsync("ver/A.cs::ClassA");
        retrieved.Should().NotBeNull();
        retrieved!.Metadata.Should().ContainKey(MemoryMetadataKeys.SinceIndex);
        Convert.ToInt32(retrieved.Metadata[MemoryMetadataKeys.SinceIndex]).Should().Be(3);
    }

    // ================================================================
    // Full sync round-trip: IncrementalSync + SqliteMemoryGraphStore
    // Validates real upsert/retire through SQLite, not mocks
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task FullSync_RoundTrip_WithRealSqliteStore()
    {
        using var store = new SqliteMemoryGraphStore(this.dbPath);
        var syncState = new InMemoryFileSyncStateStore();
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<IncrementalSync>();
        var pipelineLogger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<RoslynExtractionPipeline>();
        var pipeline = new RoslynExtractionPipeline(pipelineLogger);
        var opts = Microsoft.Extensions.Options.Options.Create(new MemoryOptions
        {
            IncrementalSyncDebounceMs = 50
        });

        // Create a real C# file
        var srcDir = Path.Combine(this.tempDir, "src");
        Directory.CreateDirectory(srcDir);
        var file1 = Path.Combine(srcDir, "Hello.cs");
        await File.WriteAllTextAsync(file1, @"
namespace TestProject;

public class HelloWorld
{
    public void SayHello()
    {
        Console.WriteLine(""Hello, World!"");
    }
}
");

        using var sync = new IncrementalSync(
            srcDir,
            pipeline,
            store,
            syncState,
            logger,
            opts,
            initialCommitIndex: 0);

        var tcs = new TaskCompletionSource<BatchProcessedEventArgs>();
        sync.BatchProcessed += (_, args) => tcs.TrySetResult(args);

        // Trigger extraction
        sync.InjectChange("Hello.cs");
        var result = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        result.Should().Be(tcs.Task, "batch should complete");

        var args = tcs.Task.Result;
        args.NodesAdded.Should().BeGreaterThan(0, "real Roslyn extraction should produce nodes");

        // Verify nodes are in the SQLite store
        var stats = await store.GetMemoryStatisticsAsync();
        stats.TotalNodes.Should().BeGreaterThan(0, "nodes should be persisted in SQLite");

        // Verify nodes have correct canonical IDs (filePath::qualifiedName)
        var nodes = await store.GetNodesAsync(
            new Ast.GraphDb.NodeFilter { FilePath = "Hello.cs" },
            limit: 100);
        nodes.Count.Should().BeGreaterThan(0, "nodes should be queryable by file path");

        foreach (var node in nodes)
        {
            node.Id.Should().Contain("Hello.cs::", "node IDs should follow canonical format");
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.ContentHash);
            node.Metadata.Should().ContainKey(MemoryMetadataKeys.MemoryLayer);
        }

        // Now modify the file and trigger a second sync
        await File.WriteAllTextAsync(file1, @"
namespace TestProject;

public class HelloWorld
{
    public void SayHello()
    {
        Console.WriteLine(""Hello, Updated World!"");
    }

    public void NewMethod()
    {
        Console.WriteLine(""New method!"");
    }
}
");

        var tcs2 = new TaskCompletionSource<BatchProcessedEventArgs>();
        sync.BatchProcessed += (_, a) => tcs2.TrySetResult(a);
        sync.InjectChange("Hello.cs");
        var result2 = await Task.WhenAny(tcs2.Task, Task.Delay(10000));
        result2.Should().Be(tcs2.Task, "second batch should complete");

        // Verify the new method node was added
        var updatedNodes = await store.GetNodesAsync(
            new Ast.GraphDb.NodeFilter { FilePath = "Hello.cs" },
            limit: 100);
        // Filter to active nodes only
        var activeNodes = updatedNodes
            .Where(n => !n.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) || ui == null)
            .ToList();
        activeNodes.Should().Contain(n => n.Name == "NewMethod" || n.Name == "NewMethod()",
            "NewMethod should be present in the updated graph");
    }

    // ================================================================
    // SQLite store persists across instances (simulates restart)
    // ================================================================
    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task SqliteStore_PersistsAcrossInstances()
    {
        // First instance: create a node
        {
            using var store = new SqliteMemoryGraphStore(this.dbPath);
            var node = new HypergraphNode
            {
                Id = "persist/A.cs::ClassA",
                Name = "ClassA",
                NodeType = "Class",
                Language = "C#",
                FilePath = "persist/A.cs",
                Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" }
            };
            var version = new VersionRange("commit-1", 1, null, null);
            await store.UpsertNodeAsync(node, version);
        }

        // Second instance (simulates restart): node should still be there
        {
            using var store = new SqliteMemoryGraphStore(this.dbPath);
            var retrieved = await store.GetNodeAsync("persist/A.cs::ClassA");
            retrieved.Should().NotBeNull("node should persist across store instances");
            retrieved!.Name.Should().Be("ClassA");
            retrieved.Metadata.Should().ContainKey(MemoryMetadataKeys.SinceIndex);
        }
    }
}
