// -----------------------------------------------------------------------
// <copyright file="Neo4jMemoryGraphStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Storage;
using Ast.VectorDb;
using Neo4j.Driver;

/// <summary>
/// Integration tests for <see cref="Neo4jMemoryGraphStore"/>.
/// Requires a running Neo4j instance. Set environment variable
/// <c>NEO4J_URI</c> (default: bolt://localhost:7687),
/// <c>NEO4J_USER</c> (default: neo4j),
/// <c>NEO4J_PASSWORD</c> (default: testpassword).
/// Tests are skipped when Neo4j is not available.
/// </summary>
[TestClass]
public class Neo4jMemoryGraphStoreTests
{
    private static string Neo4jUri =>
        Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687";

    private static string Neo4jUser =>
        Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";

    private static string Neo4jPassword =>
        Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "testpassword";

    private static bool _neo4jAvailable;
    private static IDriver? _sharedDriver;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        try
        {
            _sharedDriver = GraphDatabase.Driver(Neo4jUri, AuthTokens.Basic(Neo4jUser, Neo4jPassword));
            _sharedDriver.VerifyConnectivityAsync().Wait(TimeSpan.FromSeconds(5));
            _neo4jAvailable = true;
        }
        catch
        {
            _neo4jAvailable = false;
            _sharedDriver?.Dispose();
            _sharedDriver = null;
        }
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _sharedDriver?.Dispose();
    }

    private static void SkipIfNeo4jUnavailable()
    {
        if (!_neo4jAvailable)
        {
            Assert.Inconclusive("Neo4j is not available — skipping integration test.");
        }
    }

    /// <summary>
    /// Creates a fresh Neo4jMemoryGraphStore via the IDriver constructor.
    /// Cleans all test data before each test for isolation, preserving the WriteMutex node.
    /// </summary>
    private static async Task<Neo4jMemoryGraphStore> CreateStoreAsync(
        IVectorStoreClient? vectorStore = null,
        Microsoft.Extensions.Logging.ILogger<Neo4jMemoryGraphStore>? logger = null)
    {
        var store = new Neo4jMemoryGraphStore(_sharedDriver!, vectorStore: vectorStore, logger: logger);
        await store.InitializeAsync();

        // Clean test data for isolation — preserve the WriteMutex (it's infrastructure, not test data)
        await using var session = _sharedDriver!.AsyncSession();
        await session.RunAsync("MATCH (n) WHERE NOT n:WriteMutex DETACH DELETE n");

        return store;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HypergraphNode MakeNode(
        string id,
        string memoryLayer,
        string nodeType = "Class",
        string language = "C#",
        string filePath = "src/Foo.cs",
        string? contentHash = "abc123",
        float[]? embedding = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = memoryLayer,
        };

        if (contentHash is not null)
        {
            metadata[MemoryMetadataKeys.ContentHash] = contentHash;
        }

        if (embedding is not null)
        {
            metadata[MemoryMetadataKeys.Embedding] = embedding;
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

    // ── In-memory IVectorStoreClient for testing ────────────────────────

    /// <summary>
    /// In-memory stub implementing <see cref="IVectorStoreClient"/> for
    /// unit-level testing without a real Qdrant instance.
    /// </summary>
    private sealed class InMemoryVectorStore : IVectorStoreClient
    {
        private readonly Dictionary<string, float[]> _vectors = new();
        private int _initDimension;

        public int InitDimension => _initDimension;
        public IReadOnlyDictionary<string, float[]> Vectors => _vectors;

        public Task InitializeAsync(int dimension, CancellationToken ct = default)
        {
            _initDimension = dimension;
            return Task.CompletedTask;
        }

        public Task UpsertVectorAsync(string nodeId, float[] embedding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _vectors[nodeId] = embedding;
            return Task.CompletedTask;
        }

        public Task<float[]?> GetVectorAsync(string nodeId, CancellationToken ct = default)
        {
            return Task.FromResult(_vectors.TryGetValue(nodeId, out var v) ? v : null);
        }

        public Task<IReadOnlyList<(string NodeId, double Score)>> SearchAsync(
            float[] queryEmbedding, int topK, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<(string NodeId, double Score)>>(
                new List<(string, double)>());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// IVectorStoreClient that always throws on upsert — used to test
    /// Qdrant failure resilience (graph must still succeed).
    /// </summary>
    private sealed class FailingVectorStore : IVectorStoreClient
    {
        public int UpsertAttempts { get; private set; }

        public Task InitializeAsync(int dimension, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertVectorAsync(string nodeId, float[] embedding, CancellationToken ct = default)
        {
            this.UpsertAttempts++;
            throw new InvalidOperationException("Simulated Qdrant failure");
        }

        public Task<float[]?> GetVectorAsync(string nodeId, CancellationToken ct = default)
            => Task.FromResult<float[]?>(null);

        public Task<IReadOnlyList<(string NodeId, double Score)>> SearchAsync(
            float[] queryEmbedding, int topK, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string NodeId, double Score)>>(new List<(string, double)>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// In-memory ILogger that captures log entries for assertion in tests.
    /// </summary>
    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger<Neo4jMemoryGraphStore>
    {
        private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> entries = new();

        public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries => this.entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Scenario 1: Node upsert round-trip
    // ══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_And_TemporalQuery_AtCommitIndex1_ReturnsNode()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var node = MakeNode("node-1", "structural");
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("node-1");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_And_TemporalQuery_AtCommitIndex0_ReturnsNothing()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var node = MakeNode("node-1", "structural");
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 0, filter: null, limit: 100, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_And_TemporalQuery_NodeRetiredBeforeTarget_IsExcluded()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

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

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ExternalNode_WithNullSinceIndex_AlwaysIncluded()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var externalNode = MakeNode("ext-1", "structural");
        var version = new VersionRange(null, null, null, null);

        await store.UpsertNodeAsync(externalNode, version, CancellationToken.None);

        // Should be included at any commit index (external/boundary node per §4.3)
        var resultsAt5 = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 5, filter: null, limit: 100, CancellationToken.None);
        resultsAt5.Should().HaveCount(1);
        resultsAt5[0].Inner.Id.Should().Be("ext-1");

        // Also included at index 0
        var resultsAt0 = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 0, filter: null, limit: 100, CancellationToken.None);
        resultsAt0.Should().HaveCount(1);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task HeadQuery_WithoutCommitIndex_ReturnsOnlyActiveNodes()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var activeNode = MakeNode("active-1", "structural");
        var retiredNode = MakeNode("retired-1", "structural");

        await store.UpsertNodeAsync(activeNode, new VersionRange("a", 1, null, null), CancellationToken.None);
        await store.UpsertNodeAsync(retiredNode, new VersionRange("b", 1, "c", 5), CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: null, filter: null, limit: 100, CancellationToken.None);

        results.Should().Contain(n => n.Inner.Id == "active-1");
        results.Should().NotContain(n => n.Inner.Id == "retired-1");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Scenario 2: Write serialization
    // ══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ConcurrentUpsertNodes_BothSucceed_AuditLogShowsSerializedTimestamps()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var node1 = MakeNode("concurrent-1", "structural");
        var node2 = MakeNode("concurrent-2", "structural");
        var version = new VersionRange("abc", 1, null, null);

        // Fire two concurrent upserts — each creates an inline audit record
        var task1 = store.UpsertNodeAsync(node1, version, CancellationToken.None);
        var task2 = store.UpsertNodeAsync(node2, version, CancellationToken.None);
        await Task.WhenAll(task1, task2);

        // Both nodes should exist
        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(2);

        // Verify write serialization using DB-generated sequence numbers.
        // The sequence counter lives on the WriteMutex node and is atomically
        // incremented inside each write transaction — it proves database-level
        // serialization, not just in-process ordering.
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (a:AuditLog) WHERE a.operation = 'upsert_node' RETURN a.createdAt AS ts, a.seq AS seq");
        var records = await cursor.ToListAsync();

        records.Should().HaveCount(2, "each UpsertNodeAsync creates one inline audit record");

        // Sequence numbers are atomically incremented on the WriteMutex node
        // inside the holding Neo4j transaction — they must be consecutive.
        var sequences = records
            .Select(r => r["seq"].As<long>())
            .OrderBy(s => s)
            .ToList();

        sequences[1].Should().Be(sequences[0] + 1,
            "DB-serialized sequence numbers from WriteMutex must be consecutive, proving serialization");

        // Timestamps are Neo4j-generated (milliseconds since epoch) inside the
        // holding transaction. They must be non-decreasing when ordered by sequence.
        var timestampsBySeq = records
            .OrderBy(r => r["seq"].As<long>())
            .Select(r => r["ts"].As<long>())
            .ToList();

        timestampsBySeq[0].Should().BeLessThanOrEqualTo(timestampsBySeq[1],
            "DB-generated timestamps must be non-decreasing when ordered by DB sequence");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task WriteMutex_ExistsAfterInitialize_DurableWriteSerialization()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        // The WriteMutex node provides Neo4j-level exclusive property locking
        // for durable cross-process write serialization (architecture.md §3.7).
        // It carries a _seq counter for database-serialized audit ordering.
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._ts AS ts, lock._seq AS seq");
        var records = await cursor.ToListAsync();

        records.Should().HaveCount(1, "WriteMutex node must exist after InitializeAsync");
        records[0]["ts"].Should().NotBeNull("WriteMutex _ts property must be set");
        records[0]["seq"].Should().NotBeNull("WriteMutex _seq counter must be initialized");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task WriteMutex_BlocksConcurrentTransaction_ProvesDurableSerialization()
    {
        SkipIfNeo4jUnavailable();

        // Set up a clean store
        var store = await CreateStoreAsync();

        // STEP 1: Open a separate session and begin a write transaction that acquires
        // the WriteMutex lock — hold it open to simulate a concurrent writer (another process).
        var blockingSession = _sharedDriver!.AsyncSession();
        var blockingTx = await blockingSession.BeginTransactionAsync();
        await blockingTx.RunAsync(Neo4jMemoryGraphStore.WriteMutexCypher);
        // The blocking transaction now holds an exclusive property lock on the WriteMutex node.
        // Any other write transaction that tries WriteMutexCypher will block until this tx commits/rolls back.

        // STEP 2: Launch UpsertNodeAsync from the store — it will acquire _writeLock (SemaphoreSlim),
        // then attempt WriteMutexCypher in its Neo4j transaction, which will block on the mutex.
        var node = MakeNode("mutex-test-1", "structural");
        var version = new VersionRange("abc", 1, null, null);
        var upsertTask = store.UpsertNodeAsync(node, version, CancellationToken.None);

        // STEP 3: Give the upsert a brief window to attempt the write — it should NOT complete
        // because it's blocked on the WriteMutex held by blockingTx.
        var completed = await Task.WhenAny(upsertTask, Task.Delay(2000));
        bool wasBlocked = completed != upsertTask;

        // STEP 4: Release the blocking transaction
        await blockingTx.CommitAsync();
        await blockingSession.DisposeAsync();

        // STEP 5: Now the upsert should complete
        await upsertTask;

        // STEP 6: Verify the node was written
        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("mutex-test-1");

        // The key assertion: the upsert was blocked while the mutex was held.
        // This proves durable Neo4j-level write serialization, not just in-process ordering.
        wasBlocked.Should().BeTrue(
            "UpsertNodeAsync should be blocked on the Neo4j WriteMutex until the holding transaction commits, " +
            "proving durable cross-process write serialization (architecture.md §3.7)");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CrossInstance_WriteSerialization_TwoStoreInstancesSameDriver()
    {
        SkipIfNeo4jUnavailable();

        // Create TWO separate Neo4jMemoryGraphStore instances on the same IDriver,
        // simulating two independent consumers (like two processes sharing a DB).
        var store1 = await CreateStoreAsync();
        var store2 = new Neo4jMemoryGraphStore(_sharedDriver!);
        await store2.InitializeAsync();

        var node1 = MakeNode("cross-inst-1", "structural");
        var node2 = MakeNode("cross-inst-2", "structural");
        var version = new VersionRange("abc", 1, null, null);

        // Fire concurrent upserts from different store instances
        var task1 = store1.UpsertNodeAsync(node1, version, CancellationToken.None);
        var task2 = store2.UpsertNodeAsync(node2, version, CancellationToken.None);
        await Task.WhenAll(task1, task2);

        // Both nodes should exist
        var results = await store1.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(2);

        // Verify that the WriteMutex _ts and _seq were updated
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._ts AS ts, lock._seq AS seq");
        var records = await cursor.ToListAsync();
        records.Should().HaveCount(1);
        records[0]["ts"].Should().NotBeNull("WriteMutex _ts must be updated by the last writer");
        records[0]["seq"].As<long>().Should().BeGreaterThan(0, "WriteMutex _seq must have been incremented");

        // Audit records from both instances must have DB-generated sequences.
        // The WriteMutex counter guarantees serialization even across store instances.
        var auditCursor = await session.RunAsync(
            "MATCH (a:AuditLog) WHERE a.operation = 'upsert_node' RETURN a.createdAt AS ts, a.seq AS seq ORDER BY a.seq");
        var auditRecords = await auditCursor.ToListAsync();
        auditRecords.Should().HaveCountGreaterOrEqualTo(2,
            "both store instances should have created audit records through the WriteMutex");

        // DB-generated sequences must be strictly increasing
        var seqs = auditRecords.Select(r => r["seq"].As<long>()).ToList();
        for (int i = 1; i < seqs.Count; i++)
        {
            seqs[i].Should().BeGreaterThan(seqs[i - 1],
                "DB-serialized sequences from WriteMutex must be strictly increasing across instances");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Scenario 3: Neo4j+Qdrant vector storage
    // ══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNodeWithEmbedding_StoresVectorInQdrant_AndGraphInNeo4j()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        // Verify Qdrant is wired
        store.QdrantAvailable.Should().BeTrue();

        // Create a node with a float[768] embedding
        var embedding = new float[768];
        for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

        var node = MakeNode("embed-node-1", "semantic", embedding: embedding);
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        // Verify: graph node exists in Neo4j with embeddingStatus='ready'
        var graphResults = await store.GetNodesByLayerAsync(
            "semantic", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        graphResults.Should().HaveCount(1);
        graphResults[0].Inner.Id.Should().Be("embed-node-1");

        // Verify embeddingStatus is 'ready' (Neo4j written first, Qdrant succeeded, status updated)
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
            new { id = "embed-node-1" });
        var record = await cursor.SingleAsync();
        record["status"].As<string>().Should().Be("ready",
            "embeddingStatus should be 'ready' because Qdrant write succeeded after Neo4j write");

        // Verify: embedding was stored in vector store
        vectorStore.Vectors.Should().ContainKey("embed-node-1");
        var storedEmbedding = vectorStore.Vectors["embed-node-1"];
        storedEmbedding.Should().HaveCount(768);
        storedEmbedding[0].Should().BeApproximately(0.0f, 0.0001f);
        storedEmbedding[767].Should().BeApproximately(0.767f, 0.001f);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNodeWithoutEmbedding_DoesNotWriteToQdrant()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        var node = MakeNode("no-embed-1", "structural");
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        // Graph node should exist
        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(1);

        // No vector should be stored
        vectorStore.Vectors.Should().BeEmpty();

        // No embeddingStatus should be set (nodes without embeddings have no status)
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
            new { id = "no-embed-1" });
        var record = await cursor.SingleAsync();
        record["status"].Should().BeNull("nodes without embeddings should not have embeddingStatus");
    }

    // ── Scenario 3b: Orchestration test with InMemoryVectorStore ──────────
    // Validates the Neo4jMemoryGraphStore orchestration code with a real Neo4j
    // backend. Uses InMemoryVectorStore for the vector side — not an acceptance
    // test. The real Neo4j+Qdrant acceptance test is below.
    // Neo4j-first pattern: graph committed first, then Qdrant write, then
    // embeddingStatus updated to 'ready'.

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Orchestration_NodeWithEmbedding_QdrantFirst_ThenNeo4j()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        // Verify vector store was initialized with dimension 768
        vectorStore.InitDimension.Should().Be(768);

        var embedding = new float[768];
        for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

        var node = MakeNode("orch-1", "semantic", embedding: embedding);
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        // Graph node exists in Neo4j (real database query)
        var graphResults = await store.GetNodesByLayerAsync(
            "semantic", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        graphResults.Should().HaveCount(1);
        graphResults[0].Inner.Id.Should().Be("orch-1");
        graphResults[0].MemoryLayer.Should().Be("semantic");

        // Embedding stored in vector store (written after graph, in Phase 2)
        vectorStore.Vectors.Should().ContainKey("orch-1");
        vectorStore.Vectors["orch-1"].Should().HaveCount(768);
        vectorStore.Vectors["orch-1"][0].Should().BeApproximately(0.0f, 0.0001f);
        vectorStore.Vectors["orch-1"][767].Should().BeApproximately(0.767f, 0.001f);

        // Embedding is NOT stored in the Neo4j graph metadata (graph/vector split)
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.metadata AS metadata, n.embeddingStatus AS status",
            new { id = "orch-1" });
        var record = await cursor.SingleAsync();
        var metadataJson = record["metadata"].As<string>();
        metadataJson.Should().NotContain("Embedding", "embedding should not leak into Neo4j graph metadata");

        // embeddingStatus='ready' — Neo4j written first with 'pending', then updated to 'ready' after Qdrant succeeds
        record["status"].As<string>().Should().Be("ready",
            "embeddingStatus should be 'ready' because Qdrant write succeeded after Neo4j write");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Acceptance_QdrantVectorStore_TypeWiresIntoNeo4jMemoryGraphStore()
    {
        SkipIfNeo4jUnavailable();

        // Prove that the real QdrantVectorStore type (from Ast.VectorDb) composes
        // into Neo4jMemoryGraphStore. Graph-only operations work regardless of
        // Qdrant server availability.
        var qdrantEndpoint = Environment.GetEnvironmentVariable("QDRANT_ENDPOINT") ?? "localhost";
        QdrantVectorStore? qdrantStore = null;
        try
        {
            qdrantStore = new QdrantVectorStore(qdrantEndpoint, "test_memory_embeddings");
        }
        catch
        {
            Assert.Inconclusive("QdrantVectorStore could not be instantiated.");
            return;
        }

        try
        {
            var store = new Neo4jMemoryGraphStore(_sharedDriver!, vectorStore: qdrantStore);
            store.QdrantAvailable.Should().BeTrue("QdrantVectorStore is wired");

            // Graph-only upsert (no embedding) — works regardless of Qdrant server state
            await using var cleanSession = _sharedDriver!.AsyncSession();
            await cleanSession.RunAsync("MATCH (n:MemoryNode) DETACH DELETE n");

            await using var setupSession = _sharedDriver!.AsyncSession();
            await setupSession.RunAsync(
                "CREATE CONSTRAINT memory_node_id IF NOT EXISTS FOR (n:MemoryNode) REQUIRE n.id IS UNIQUE");
            await setupSession.RunAsync(Neo4jMemoryGraphStore.WriteMutexCypher);

            var node = MakeNode("qdrant-wired-1", "structural");
            var version = new VersionRange("abc", 1, null, null);
            await store.UpsertNodeAsync(node, version, CancellationToken.None);

            var results = await store.GetNodesByLayerAsync(
                "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
            results.Should().HaveCount(1);
            results[0].Inner.Id.Should().Be("qdrant-wired-1");
        }
        finally
        {
            await qdrantStore.DisposeAsync();
        }
    }

    /// <summary>
    /// PRIMARY ACCEPTANCE TEST for Neo4j+Qdrant scenario.
    /// Requires both Neo4j and Qdrant to be available. Validates that:
    /// 1. Neo4j graph node is written FIRST (ACID transaction)
    /// 2. Qdrant vector is written SECOND (after graph commit)
    /// 3. embeddingStatus transitions from 'pending' to 'ready' after Qdrant success
    /// 4. Real Qdrant stores and retrieves the embedding
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task Acceptance_Neo4jAndQdrant_FullRoundTrip()
    {
        SkipIfNeo4jUnavailable();

        var qdrantEndpoint = Environment.GetEnvironmentVariable("QDRANT_ENDPOINT") ?? "localhost";
        QdrantVectorStore? qdrantStore = null;

        try
        {
            qdrantStore = new QdrantVectorStore(qdrantEndpoint, "test_memory_embeddings_rt");
            await qdrantStore.InitializeAsync(768);
        }
        catch
        {
            if (qdrantStore is not null) await qdrantStore.DisposeAsync();
            Assert.Inconclusive("Qdrant is not available — Neo4j+Qdrant acceptance test requires both services.");
            return;
        }

        try
        {
            var store = new Neo4jMemoryGraphStore(_sharedDriver!, vectorStore: qdrantStore);
            await store.InitializeAsync();
            await using var cleanSession = _sharedDriver!.AsyncSession();
            await cleanSession.RunAsync("MATCH (n) WHERE NOT n:WriteMutex DETACH DELETE n");

            var embedding = new float[768];
            for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

            var node = MakeNode("qdrant-rt-1", "semantic", embedding: embedding);
            await store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);

            // ASSERTION 1: Graph node in Neo4j with embeddingStatus='ready'
            var results = await store.GetNodesByLayerAsync(
                "semantic", 1, null, 100, CancellationToken.None);
            results.Should().HaveCount(1);
            results[0].Inner.Id.Should().Be("qdrant-rt-1");

            await using var session = _sharedDriver!.AsyncSession();
            var cursor = await session.RunAsync(
                "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
                new { id = "qdrant-rt-1" });
            var record = await cursor.SingleAsync();
            record["status"].As<string>().Should().Be("ready",
                "embeddingStatus must be 'ready' after Neo4j-first write and successful Qdrant follow-up");

            // ASSERTION 2: Embedding round-trips through real Qdrant
            var vector = await qdrantStore!.GetVectorAsync("qdrant-rt-1");
            vector.Should().NotBeNull("embedding must be stored in Qdrant");
            vector!.Length.Should().Be(768);
            vector[0].Should().BeApproximately(0.0f, 0.001f);
            vector[767].Should().BeApproximately(0.767f, 0.001f);
        }
        finally
        {
            if (qdrantStore is not null) await qdrantStore.DisposeAsync();
        }
    }

    // ── Scenario 3c: Qdrant failure — graph succeeds, warning logged ────
    // With the Neo4j-first pattern, a vector failure is non-fatal: the graph
    // node is committed first, and a warning is logged. There are NEVER orphan
    // vectors in Qdrant without a corresponding graph node.

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QdrantFailure_GraphNodeStillCreated_WarningLogged()
    {
        SkipIfNeo4jUnavailable();
        var failingVector = new FailingVectorStore();
        var testLogger = new TestLogger();
        var store = await CreateStoreAsync(failingVector, testLogger);

        var embedding = new float[768];
        for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

        var node = MakeNode("qdrant-fail-1", "semantic", embedding: embedding);
        var version = new VersionRange("abc", 1, null, null);

        // Qdrant failure is non-fatal — graph node is created, no exception thrown
        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        // Graph node should exist with embeddingStatus='pending' (Qdrant failed)
        var results = await store.GetNodesByLayerAsync(
            "semantic", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(1, "Neo4j-first pattern: graph node created even when Qdrant fails");
        results[0].Inner.Id.Should().Be("qdrant-fail-1");

        // Verify embeddingStatus remains 'pending' (Qdrant write did not succeed)
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
            new { id = "qdrant-fail-1" });
        var record = await cursor.SingleAsync();
        record["status"].As<string>().Should().Be("pending",
            "embeddingStatus should remain 'pending' when Qdrant write fails");

        // Qdrant was attempted
        failingVector.UpsertAttempts.Should().Be(1);

        // Warning was logged via ILogger
        testLogger.Entries.Should().Contain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            e.Message.Contains("qdrant-fail-1") &&
            e.Message.Contains("graph-only mode"),
            "a warning must be logged when Qdrant write fails");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QdrantSuccess_RetryIsIdempotent_EmbeddingStatusReady()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        var embedding = new float[768];
        for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

        var node = MakeNode("retry-node", "semantic", embedding: embedding);
        var version = new VersionRange("abc", 1, null, null);

        // First upsert succeeds (both graph + vector)
        await store.UpsertNodeAsync(node, version, CancellationToken.None);
        vectorStore.Vectors.Should().ContainKey("retry-node");

        // Verify embeddingStatus='ready' after successful Neo4j+Qdrant upsert
        await using var session1 = _sharedDriver!.AsyncSession();
        var cursor1 = await session1.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
            new { id = "retry-node" });
        var record1 = await cursor1.SingleAsync();
        record1["status"].As<string>().Should().Be("ready");

        // Second upsert (retry) also succeeds — MERGE is idempotent
        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        // Still exactly one graph node with embeddingStatus='ready'
        var results = await store.GetNodesByLayerAsync(
            "semantic", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(1);

        await using var session2 = _sharedDriver!.AsyncSession();
        var cursor2 = await session2.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
            new { id = "retry-node" });
        var record2 = await cursor2.SingleAsync();
        record2["status"].As<string>().Should().Be("ready",
            "retry should set embeddingStatus back to 'ready' after successful Qdrant write");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task QdrantFailure_ExpertiseUpsert_GraphStillSucceeds()
    {
        SkipIfNeo4jUnavailable();
        var failingVector = new FailingVectorStore();
        var testLogger = new TestLogger();
        var store = await CreateStoreAsync(failingVector, testLogger);

        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Test Pattern", "Body text", "target-1", "agent")
        {
            Embedding = new float[768],
        };

        // Qdrant failure is non-fatal — expertise node should still be created
        await store.UpsertExpertiseAsync(entry, CancellationToken.None);

        // Expertise should exist in Neo4j
        var results = await store.GetExpertiseForNodeAsync("target-1", 0.0, CancellationToken.None);
        results.Should().HaveCount(1, "expertise should be stored in Neo4j even when Qdrant fails");
        results[0].Title.Should().Be("Test Pattern");

        // Warning was logged via ILogger
        testLogger.Entries.Should().Contain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            e.Message.Contains("graph-only mode"),
            "a warning must be logged when Qdrant write fails for expertise");
    }

    // ── Scenario 4: RetireNode cascades ─────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetireNodeAsync_CascadesToConnectedEdges()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var nodeC = MakeNode("C", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeC, ver, CancellationToken.None);

        var edgeAB = MakeEdge("e-AB", ["A", "B"]);
        var edgeBC = MakeEdge("e-BC", ["B", "C"]);

        await store.UpsertEdgeAsync(edgeAB, ver, CancellationToken.None);
        await store.UpsertEdgeAsync(edgeBC, ver, CancellationToken.None);

        // Retire node B at index 5 → edges e-AB and e-BC should cascade
        await store.RetireNodeAsync("B", 5, null, CancellationToken.None);

        var edgesAt5 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", atCommitIndex: 5, limit: 100, CancellationToken.None);
        edgesAt5.Should().BeEmpty();

        var edgesAt4 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", atCommitIndex: 4, limit: 100, CancellationToken.None);
        edgesAt4.Should().HaveCount(2);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetireNodeAsync_SetsUntilIndexAndOptionalCommit()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var node = MakeNode("retire-check", "structural");
        await store.UpsertNodeAsync(node, new VersionRange("c1", 1, null, null), CancellationToken.None);

        // Retire with untilCommit=null — only untilIndex is set
        await store.RetireNodeAsync("retire-check", 5, null, CancellationToken.None);

        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.untilIndex AS ui, n.untilCommit AS uc",
            new { id = "retire-check" });
        var record = await cursor.SingleAsync();

        record["ui"].As<long>().Should().Be(5, "untilIndex should be set to the integer value");
        record["uc"].Should().BeNull("untilCommit should not be set when null is passed");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetireNodeAsync_WithUntilCommit_SetsBothFields()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var node = MakeNode("retire-sha", "structural");
        await store.UpsertNodeAsync(node, new VersionRange("c1", 1, null, null), CancellationToken.None);

        await store.RetireNodeAsync("retire-sha", 5, "abc123def", CancellationToken.None);

        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.untilIndex AS ui, n.untilCommit AS uc",
            new { id = "retire-sha" });
        var record = await cursor.SingleAsync();

        record["ui"].As<long>().Should().Be(5, "untilIndex should be set");
        record["uc"].As<string>().Should().Be("abc123def", "untilCommit should be set when SHA string is passed");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetireNodeAsync_EdgesAlreadyRetiredEarlier_KeepOriginalUntilIndex()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge = MakeEdge("e-AB", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, new VersionRange("c1", 1, "c3", 3), CancellationToken.None);

        await store.RetireNodeAsync("A", 5, null, CancellationToken.None);

        var edgesAt2 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", atCommitIndex: 2, limit: 100, CancellationToken.None);
        edgesAt2.Should().HaveCount(1);

        var edgesAt3 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", atCommitIndex: 3, limit: 100, CancellationToken.None);
        edgesAt3.Should().BeEmpty("edge was retired at index 3, before node retirement at 5");
    }

    // ── Edge mutations ──────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertEdge_And_RetireEdge_RoundTrip()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge = MakeEdge("e-AB", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, ver, CancellationToken.None);

        var active = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, null, 100, CancellationToken.None);
        active.Should().HaveCount(1);

        await store.RetireEdgeAsync("e-AB", 3, null, CancellationToken.None);

        var afterRetire= await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, null, 100, CancellationToken.None);
        afterRetire.Should().BeEmpty();

        var atTwo = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, 2, 100, CancellationToken.None);
        atTwo.Should().HaveCount(1);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetireEdgeAsync_SetsUntilIndex()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge = MakeEdge("e-AB", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, ver, CancellationToken.None);

        await store.RetireEdgeAsync("e-AB", 7, null, CancellationToken.None);

        // Verify the Neo4j property directly
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (e:MemoryEdge {id: $id}) RETURN e.untilIndex AS ui",
            new { id = "e-AB" });
        var record = await cursor.SingleAsync();

        record["ui"].As<long>().Should().Be(7, "untilIndex should be set to the integer value");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpdateEdgeMetadataAsync_MergesNewKeys()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge = MakeEdge("e-AB", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, ver, CancellationToken.None);

        await store.UpdateEdgeMetadataAsync("e-AB",
            new Dictionary<string, object?> { ["newKey"] = "newValue" },
            CancellationToken.None);

        var edges = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", null, null, 100, CancellationToken.None);
        edges.Should().HaveCount(1);
        edges[0].Inner.Metadata.Should().ContainKey("newKey");
    }

    // ── Edge temporal filtering with NULL sinceIndex ─────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ExternalEdge_WithNullSinceIndex_AlwaysIncluded()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        // Edge with NULL sinceIndex (external/timeless edge)
        var edge = MakeEdge("e-ext", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, new VersionRange(null, null, null, null), CancellationToken.None);

        // Should be included at any commit index (architecture.md §4.3)
        var edgesAt5 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", atCommitIndex: 5, limit: 100, CancellationToken.None);
        edgesAt5.Should().HaveCount(1);
        edgesAt5[0].Inner.Id.Should().Be("e-ext");

        var edgesAt0 = await store.GetEdgesBySemanticRelationAsync(
            "CALLS", "structural", atCommitIndex: 0, limit: 100, CancellationToken.None);
        edgesAt0.Should().HaveCount(1);
    }

    // ── NodeFilter ──────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetNodesByLayer_WithNodeTypeFilter_FiltersCorrectly()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var classNode = MakeNode("n1", "structural", nodeType: "Class");
        var methodNode = MakeNode("n2", "structural", nodeType: "Method");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(classNode, ver, CancellationToken.None);
        await store.UpsertNodeAsync(methodNode, ver, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(NodeType: "Class"), 100, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Inner.NodeType.Should().Be("Class");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetNodesByLayer_WithNodeIdsFilter_FiltersCorrectly()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var n1 = MakeNode("n1", "structural");
        var n2 = MakeNode("n2", "structural");
        var n3 = MakeNode("n3", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(n1, ver, CancellationToken.None);
        await store.UpsertNodeAsync(n2, ver, CancellationToken.None);
        await store.UpsertNodeAsync(n3, ver, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(NodeIds: new List<string> { "n1", "n3" }), 100, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Select(n => n.Inner.Id).Should().BeEquivalentTo(["n1", "n3"]);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetNodesByLayer_WithFilePathPrefixFilter_FiltersCorrectly()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var n1 = MakeNode("n1", "structural", filePath: "src/controllers/UserController.cs");
        var n2 = MakeNode("n2", "structural", filePath: "src/models/User.cs");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(n1, ver, CancellationToken.None);
        await store.UpsertNodeAsync(n2, ver, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", 1, new NodeFilter(FilePathPrefix: "src/controllers/"), 100, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("n1");
    }

    // ── Expertise CRUD ──────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ExpertiseCrud_UpsertGetArchiveRoundTrip()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern,
            "Singleton Pattern",
            "This class uses the singleton pattern for thread safety.",
            "node-1",
            "test-agent")
        {
            Confidence = 0.9,
        };

        await store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var results = await store.GetExpertiseForNodeAsync("node-1", 0.5, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Singleton Pattern");
        results[0].Confidence.Should().Be(0.9);

        await store.ArchiveExpertiseAsync(entry.Id, CancellationToken.None);

        var afterArchive = await store.GetExpertiseForNodeAsync("node-1", 0.0, CancellationToken.None);
        afterArchive.Should().BeEmpty();

        var archived = await store.GetArchivedExpertiseAsync("node-1", 100, CancellationToken.None);
        archived.Should().HaveCount(1);
        archived[0].Title.Should().Be("Singleton Pattern");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetExpertiseForNode_ConfidenceFilter_ExcludesLow()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var highConf = new ExpertiseEntry(
            ExpertiseKind.Insight, "High Insight", "body1", "node-1", "agent")
        { Confidence = 0.9 };

        var lowConf = new ExpertiseEntry(
            ExpertiseKind.Pitfall, "Low Pitfall", "body2", "node-1", "agent")
        { Confidence = 0.3 };

        await store.UpsertExpertiseAsync(highConf, CancellationToken.None);
        await store.UpsertExpertiseAsync(lowConf, CancellationToken.None);

        var results = await store.GetExpertiseForNodeAsync("node-1", 0.5, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("High Insight");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetArchivedExpertise_WithoutTargetFilter_ReturnsAll()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var e1 = new ExpertiseEntry(
            ExpertiseKind.Pattern, "P1", "body1", "node-1", "agent") { Archived = true };
        var e2 = new ExpertiseEntry(
            ExpertiseKind.Pattern, "P2", "body2", "node-2", "agent") { Archived = true };

        await store.UpsertExpertiseAsync(e1, CancellationToken.None);
        await store.UpsertExpertiseAsync(e2, CancellationToken.None);

        var archived = await store.GetArchivedExpertiseAsync(null, 100, CancellationToken.None);
        archived.Should().HaveCount(2);
    }

    // ── Diagnostics ─────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetMemoryStatistics_ReturnsCorrectCounts()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var n1 = MakeNode("n1", "structural");
        var n2 = MakeNode("n2", "semantic");
        var n3 = MakeNode("n3", "structural"); // will be retired
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(n1, ver, CancellationToken.None);
        await store.UpsertNodeAsync(n2, ver, CancellationToken.None);
        await store.UpsertNodeAsync(n3, new VersionRange("c1", 1, "c2", 3), CancellationToken.None);

        var edgeAB = MakeEdge("e1", ["n1", "n2"]);
        await store.UpsertEdgeAsync(edgeAB, ver, CancellationToken.None);

        var expertise = new ExpertiseEntry(
            ExpertiseKind.Pattern, "P1", "body", "n1", "agent");
        await store.UpsertExpertiseAsync(expertise, CancellationToken.None);

        var stats = await store.GetMemoryStatisticsAsync(CancellationToken.None);
        stats.TotalNodes.Should().Be(2, "n3 is retired");
        stats.TotalEdges.Should().Be(1);
        stats.TotalExpertiseEntries.Should().Be(1);
        stats.ArchivedExpertiseEntries.Should().Be(0);
        stats.LayerStats.Should().ContainKey("structural");
        stats.LayerStats["structural"].NodeCount.Should().Be(1, "n3 is retired, only n1 active in structural");
        stats.LayerStats.Should().ContainKey("semantic");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LogAuditAsync_CreatesAuditRecord_AffectsLastUpdated()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        await store.LogAuditAsync("session-1", "test-op", new { foo = "bar" }, CancellationToken.None);

        var stats = await store.GetMemoryStatisticsAsync(CancellationToken.None);
        stats.LastUpdated.Should().NotBeNull();
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LogAuditAsync_RecordsSequenceNumber()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        await store.LogAuditAsync("s1", "op1", new { a = 1 }, CancellationToken.None);
        await store.LogAuditAsync("s1", "op2", new { b = 2 }, CancellationToken.None);

        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (a:AuditLog) WHERE a.sessionId = 's1' RETURN a.seq AS seq, a.operation AS op");
        var records = await cursor.ToListAsync();

        records.Should().HaveCount(2);
        var seqs = records.Select(r => r["seq"].As<long>()).OrderBy(s => s).ToList();
        seqs[1].Should().Be(seqs[0] + 1, "audit entries should have consecutive sequence numbers");
    }

    // ── Upsert idempotency ──────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_Twice_UpdatesExisting()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var node = MakeNode("n1", "structural", nodeType: "Class");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(node, ver, CancellationToken.None);

        var updated = MakeNode("n1", "structural", nodeType: "Interface");
        await store.UpsertNodeAsync(updated, ver, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", 1, null, 100, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Inner.NodeType.Should().Be("Interface");
    }

    // ── QdrantAvailable property ────────────────────────────────────────

    [TestMethod]
    public void QdrantAvailable_WhenNoClient_ReturnsFalse()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: null);
            store.QdrantAvailable.Should().BeFalse();
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public void QdrantAvailable_WhenClientProvided_ReturnsTrue()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        var vectorStore = new InMemoryVectorStore();
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: vectorStore);
            store.QdrantAvailable.Should().BeTrue();
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    // ── Embedding without Qdrant — must fail loudly ─────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_EmbeddingWithoutVectorStore_ThrowsInvalidOperation()
    {
        SkipIfNeo4jUnavailable();

        // Create store WITHOUT a vector store
        var store = await CreateStoreAsync(vectorStore: null);

        var embedding = new float[768];
        for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

        var node = MakeNode("loud-fail-1", "semantic", embedding: embedding);
        var version = new VersionRange("abc", 1, null, null);

        // MUST throw — embeddings are never silently dropped
        var act = () => store.UpsertNodeAsync(node, version, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no IVectorStoreClient is wired*");

        // Verify no graph node was created
        var results = await store.GetNodesByLayerAsync(
            "semantic", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().BeEmpty("embedding failure should prevent graph write");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertExpertise_EmbeddingWithoutVectorStore_ThrowsInvalidOperation()
    {
        SkipIfNeo4jUnavailable();

        // Create store WITHOUT a vector store
        var store = await CreateStoreAsync(vectorStore: null);

        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Test Pattern", "Body text", "target-1", "agent")
        {
            Embedding = new float[768],
        };

        // MUST throw — embeddings are never silently dropped
        var act = () => store.UpsertExpertiseAsync(entry, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no IVectorStoreClient is wired*");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_NoEmbedding_NoVectorStore_Succeeds()
    {
        SkipIfNeo4jUnavailable();

        // Create store WITHOUT a vector store — graph-only mode
        var store = await CreateStoreAsync(vectorStore: null);

        // Node WITHOUT embedding should work fine in graph-only mode
        var node = MakeNode("graph-only-1", "structural");
        var version = new VersionRange("abc", 1, null, null);

        await store.UpsertNodeAsync(node, version, CancellationToken.None);

        var results = await store.GetNodesByLayerAsync(
            "structural", atCommitIndex: 1, filter: null, limit: 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("graph-only-1");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertExpertise_NoEmbedding_NoVectorStore_Succeeds()
    {
        SkipIfNeo4jUnavailable();

        // Create store WITHOUT a vector store — graph-only mode
        var store = await CreateStoreAsync(vectorStore: null);

        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Pattern No Embed", "Body text", "target-1", "agent");
        // No embedding set — should succeed in graph-only mode

        await store.UpsertExpertiseAsync(entry, CancellationToken.None);

        var results = await store.GetExpertiseForNodeAsync("target-1", 0.0, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Pattern No Embed");
    }

    // ── Cancellation propagation ────────────────────────────────────────

    [TestMethod]
    public async Task UpsertNodeAsync_RespectsPreCancelledToken()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver);
            var cts = new CancellationTokenSource();
            cts.Cancel();

            var node = MakeNode("cancel-1", "structural");
            var version = new VersionRange("abc", 1, null, null);

            // Pre-cancelled token should throw before any Neo4j operation
            var act = () => store.UpsertNodeAsync(node, version, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    // ── IMemoryGraphStore contract ──────────────────────────────────────

    [TestMethod]
    public void Neo4jMemoryGraphStore_Implements_IMemoryGraphStore()
    {
        typeof(Neo4jMemoryGraphStore).Should().Implement<IMemoryGraphStore>();
    }

    [TestMethod]
    public void Neo4jMemoryGraphStore_Has_Expected_MethodCount()
    {
        // IMemoryGraphStore declares 13 methods (12 per architecture.md §5.1 + UpsertEdgeAsync)
        var interfaceMethods = typeof(IMemoryGraphStore).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        interfaceMethods.Should().HaveCount(13,
            "IMemoryGraphStore should declare exactly 13 methods");
    }

    // ── Delegation to Neo4jGraphStore (composition) ─────────────────────

    [TestMethod]
    public void Neo4jMemoryGraphStore_InternallyComposesNeo4jGraphStore()
    {
        // Convenience constructor: takes IDriver, creates Neo4jGraphStore internally.
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver);
            store.QdrantAvailable.Should().BeFalse();
            (store is IGraphStore).Should().BeTrue(
                "Neo4jMemoryGraphStore should satisfy IGraphStore via IMemoryGraphStore");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public void Neo4jMemoryGraphStore_PrimaryConstructor_ComposesProvidedStore()
    {
        // Primary constructor: takes existing Neo4jGraphStore directly (true delegation).
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var graphStore = new Neo4jGraphStore(mockDriver);
            var store = new Neo4jMemoryGraphStore(graphStore);
            store.ComposedGraphStore.Should().BeSameAs(graphStore,
                "Primary constructor should use the provided Neo4jGraphStore, not create a new one");
            (store is IGraphStore).Should().BeTrue(
                "Neo4jMemoryGraphStore should satisfy IGraphStore via composed Neo4jGraphStore");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public void Neo4jMemoryGraphStore_ComposedGraphStore_IsNotNull()
    {
        // The internal ComposedGraphStore property exposes the composed Neo4jGraphStore,
        // proving that lifecycle delegation flows through the composed instance.
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver);
            store.ComposedGraphStore.Should().NotBeNull(
                "Neo4jMemoryGraphStore must compose a Neo4jGraphStore instance internally");
            store.ComposedGraphStore.Should().BeOfType<Neo4jGraphStore>(
                "Composed store must be Neo4jGraphStore from Ast.GraphDb");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public async Task DisposeAsync_DelegatesToComposedNeo4jGraphStore()
    {
        // Disposal flows: Neo4jMemoryGraphStore.DisposeAsync() →
        //   composedStore.DisposeAsync() (Neo4jGraphStore from Ast.GraphDb)
        //   + vectorStore?.DisposeAsync()
        // This test verifies no exception occurs and double-dispose is safe.
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        var vectorStore = new InMemoryVectorStore();
        var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: vectorStore);

        // First dispose should succeed
        await store.DisposeAsync();

        // Second dispose should be a no-op (idempotent)
        await store.DisposeAsync();

        mockDriver.Dispose();
    }

    [TestMethod]
    public void Neo4jMemoryGraphStore_WithVectorStore_ComposesCorrectly()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        var vectorStore = new InMemoryVectorStore();
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: vectorStore);
            store.QdrantAvailable.Should().BeTrue();
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public void Neo4jMemoryGraphStore_WithQdrantVectorStore_ComposesCorrectly()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var qdrantStore = new QdrantVectorStore("localhost");
            var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: qdrantStore);
            store.QdrantAvailable.Should().BeTrue("real QdrantVectorStore should wire into Neo4jMemoryGraphStore");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    // ── Embedding without Qdrant — pre-Neo4j validation (unit-level) ────

    [TestMethod]
    public async Task UpsertNode_EmbeddingNoVectorStore_FailsBeforeAnyIO()
    {
        // Unit test: doesn't need Neo4j. The validation happens before any I/O.
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: null);

            var embedding = new float[768];
            var node = MakeNode("unit-loud-fail", "semantic", embedding: embedding);

            var act = () => store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*no IVectorStoreClient is wired*")
                .WithMessage("*Qdrant*");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public async Task UpsertExpertise_EmbeddingNoVectorStore_FailsBeforeAnyIO()
    {
        // Unit test: doesn't need Neo4j. The validation happens before any I/O.
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var store = new Neo4jMemoryGraphStore(mockDriver, vectorStore: null);

            var entry = new ExpertiseEntry(
                ExpertiseKind.Pattern, "Test", "Body", "target", "agent")
            {
                Embedding = new float[768],
            };

            var act = () => store.UpsertExpertiseAsync(entry, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*no IVectorStoreClient is wired*")
                .WithMessage("*Qdrant*");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    // ── MemoryOptions.EmbeddingDimension ─────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task InitializeAsync_UsesConfigurableEmbeddingDimension()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var customOptions = new MemoryOptions { EmbeddingDimension = 1536 };
        var store = new Neo4jMemoryGraphStore(
            _sharedDriver!, vectorStore: vectorStore, options: customOptions);
        await store.InitializeAsync();

        vectorStore.InitDimension.Should().Be(1536,
            "InitializeAsync should use MemoryOptions.EmbeddingDimension instead of hardcoded 768");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task InitializeAsync_DefaultEmbeddingDimension_Is768()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        // No custom options → default MemoryOptions with EmbeddingDimension=768
        var store = new Neo4jMemoryGraphStore(_sharedDriver!, vectorStore: vectorStore);
        await store.InitializeAsync();

        vectorStore.InitDimension.Should().Be(768,
            "default EmbeddingDimension should be 768 when no MemoryOptions are provided");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Neo4jGraphStore-primary constructor delegation tests
    // ══════════════════════════════════════════════════════════════════════
    // These tests prove that the primary constructor accepting Neo4jGraphStore
    // directly enables FULL operational delegation — all CRUD queries flow
    // through the composed store's IDriver, not a separately-provided one.

    [TestMethod]
    public void PrimaryConstructor_TakesNeo4jGraphStore_ComposesCorrectly()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        try
        {
            var graphStore = new Neo4jGraphStore(mockDriver);
            var store = new Neo4jMemoryGraphStore(graphStore);

            store.ComposedGraphStore.Should().BeSameAs(graphStore,
                "Primary constructor should use the provided Neo4jGraphStore as the composed store, not create a new one");

            (store is IGraphStore).Should().BeTrue(
                "Neo4jMemoryGraphStore should satisfy IGraphStore through the composed Neo4jGraphStore");
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    public void PrimaryConstructor_WithVectorStore_ComposesCorrectly()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        var graphStore = new Neo4jGraphStore(mockDriver);
        var vectorStore = new InMemoryVectorStore();
        try
        {
            var store = new Neo4jMemoryGraphStore(graphStore, vectorStore: vectorStore);
            store.QdrantAvailable.Should().BeTrue("vector store should be wired through primary constructor");
            store.ComposedGraphStore.Should().BeSameAs(graphStore);
        }
        finally
        {
            mockDriver.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PrimaryConstructor_DelegatesAllCrudOperations()
    {
        SkipIfNeo4jUnavailable();

        // Construct via Neo4jGraphStore-primary constructor (true delegation)
        var graphStore = new Neo4jGraphStore(_sharedDriver!);
        var store = new Neo4jMemoryGraphStore(graphStore);
        await store.InitializeAsync();

        // Clean test data
        await using var cleanSession = _sharedDriver!.AsyncSession();
        await cleanSession.RunAsync("MATCH (n) WHERE NOT n:WriteMutex DETACH DELETE n");

        // Upsert node
        var node = MakeNode("delegation-n1", "structural");
        await store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);

        // Query node — proves read delegation works through composed store's driver
        var results = await store.GetNodesByLayerAsync("structural", 1, null, 100, CancellationToken.None);
        results.Should().HaveCount(1);
        results[0].Inner.Id.Should().Be("delegation-n1");

        // Upsert edge
        var nodeB = MakeNode("delegation-n2", "structural");
        await store.UpsertNodeAsync(nodeB, new VersionRange("abc", 1, null, null), CancellationToken.None);
        var edge = MakeEdge("delegation-e1", ["delegation-n1", "delegation-n2"]);
        await store.UpsertEdgeAsync(edge, new VersionRange("abc", 1, null, null), CancellationToken.None);

        // Query edge
        var edges = await store.GetEdgesBySemanticRelationAsync("CALLS", null, 1, 100, CancellationToken.None);
        edges.Should().HaveCount(1);

        // Retire node
        await store.RetireNodeAsync("delegation-n1", 5, "def456", CancellationToken.None);

        // Expertise CRUD
        var expertise = new ExpertiseEntry(ExpertiseKind.Pattern, "Delegation Test", "body", "delegation-n2", "agent");
        await store.UpsertExpertiseAsync(expertise, CancellationToken.None);
        var expertiseResults = await store.GetExpertiseForNodeAsync("delegation-n2", 0.0, CancellationToken.None);
        expertiseResults.Should().HaveCount(1);

        // Statistics
        var stats = await store.GetMemoryStatisticsAsync(CancellationToken.None);
        stats.TotalNodes.Should().BeGreaterThan(0);

        // Audit
        await store.LogAuditAsync("session-delegation", "test", new { delegated = true }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PrimaryConstructor_WithVectorStore_HandlesEmbeddings()
    {
        SkipIfNeo4jUnavailable();

        // Construct via Neo4jGraphStore-primary constructor with vector store
        var graphStore = new Neo4jGraphStore(_sharedDriver!);
        var vectorStore = new InMemoryVectorStore();
        var store = new Neo4jMemoryGraphStore(graphStore, vectorStore: vectorStore);
        await store.InitializeAsync();

        // Clean test data
        await using var cleanSession = _sharedDriver!.AsyncSession();
        await cleanSession.RunAsync("MATCH (n) WHERE NOT n:WriteMutex DETACH DELETE n");

        var embedding = new float[768];
        for (int i = 0; i < 768; i++) embedding[i] = (float)(i * 0.001);

        var node = MakeNode("delegation-embed-1", "semantic", embedding: embedding);
        await store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);

        // Graph node in Neo4j (through composed store's driver)
        var results = await store.GetNodesByLayerAsync("semantic", 1, null, 100, CancellationToken.None);
        results.Should().HaveCount(1);

        // Embedding in vector store
        vectorStore.Vectors.Should().ContainKey("delegation-embed-1");
        vectorStore.Vectors["delegation-embed-1"].Should().HaveCount(768);

        // embeddingStatus='ready' (updated inside write transaction with mutex)
        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:MemoryNode {id: $id}) RETURN n.embeddingStatus AS status",
            new { id = "delegation-embed-1" });
        var record = await cursor.SingleAsync();
        record["status"].As<string>().Should().Be("ready",
            "embeddingStatus should be 'ready' because Qdrant write succeeded and ready-update ran through write transaction");
    }

    [TestMethod]
    public async Task PrimaryConstructor_DisposeAsync_DelegatesToComposedStore()
    {
        var mockDriver = GraphDatabase.Driver("bolt://localhost:9999");
        var graphStore = new Neo4jGraphStore(mockDriver);
        var vectorStore = new InMemoryVectorStore();
        var store = new Neo4jMemoryGraphStore(graphStore, vectorStore: vectorStore);

        // Dispose should not throw
        await store.DisposeAsync();
        // Double dispose is safe (idempotent)
        await store.DisposeAsync();

        mockDriver.Dispose();
    }

    // ── embeddingStatus write-mutex coverage ─────────────────────────────
    // Verifies that the embeddingStatus='ready' update runs inside a serialized
    // write transaction with the WriteMutex (not a plain auto-commit query).

    [TestMethod]
    [TestCategory("Integration")]
    public async Task EmbeddingStatusReady_UpdatesBumpWriteMutexSequence()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        // Get initial WriteMutex sequence after InitializeAsync
        await using var session1 = _sharedDriver!.AsyncSession();
        var cursor1 = await session1.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._seq AS seq");
        var rec1 = await cursor1.SingleAsync();
        var seqBefore = rec1["seq"].As<long>();

        // Upsert a node with embedding — this does:
        // 1. WriteMutex (for graph MERGE + audit) — increments _seq
        // 2. Qdrant upsert
        // 3. WriteMutex (for embeddingStatus='ready') — increments _seq again
        var embedding = new float[768];
        var node = MakeNode("mutex-embed-1", "semantic", embedding: embedding);
        await store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);

        // WriteMutex should have been incremented at least 2 times:
        // once for the graph+audit write, once for the embeddingStatus update
        await using var session2 = _sharedDriver!.AsyncSession();
        var cursor2 = await session2.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._seq AS seq");
        var rec2 = await cursor2.SingleAsync();
        var seqAfter = rec2["seq"].As<long>();

        (seqAfter - seqBefore).Should().BeGreaterThanOrEqualTo(2,
            "embeddingStatus='ready' update should go through WriteMutex (2 increments: graph+audit, then ready-status)");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ExpertiseEmbeddingStatusReady_UpdatesBumpWriteMutexSequence()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        // Get initial sequence
        await using var session1 = _sharedDriver!.AsyncSession();
        var cursor1 = await session1.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._seq AS seq");
        var rec1 = await cursor1.SingleAsync();
        var seqBefore = rec1["seq"].As<long>();

        // Upsert expertise with embedding
        var entry = new ExpertiseEntry(
            ExpertiseKind.Pattern, "Mutex Test", "body", "target-x", "agent")
        {
            Embedding = new float[768],
        };

        await store.UpsertExpertiseAsync(entry, CancellationToken.None);

        // WriteMutex should have been incremented at least 2 times
        await using var session2 = _sharedDriver!.AsyncSession();
        var cursor2 = await session2.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._seq AS seq");
        var rec2 = await cursor2.SingleAsync();
        var seqAfter = rec2["seq"].As<long>();

        (seqAfter - seqBefore).Should().BeGreaterThanOrEqualTo(2,
            "expertise embeddingStatus='ready' update should go through WriteMutex");
    }

    // ── Vector dimension edge cases ─────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_SmallEmbedding_StoresCorrectly()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var customOptions = new MemoryOptions { EmbeddingDimension = 384 };
        var store = new Neo4jMemoryGraphStore(_sharedDriver!, vectorStore: vectorStore, options: customOptions);
        await store.InitializeAsync();
        await using var cleanSession = _sharedDriver!.AsyncSession();
        await cleanSession.RunAsync("MATCH (n) WHERE NOT n:WriteMutex DETACH DELETE n");

        vectorStore.InitDimension.Should().Be(384, "custom dimension should be passed to vector store");

        var embedding = new float[384];
        for (int i = 0; i < 384; i++) embedding[i] = (float)(i * 0.002);
        var node = MakeNode("small-embed-1", "semantic", embedding: embedding);
        await store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);

        vectorStore.Vectors.Should().ContainKey("small-embed-1");
        vectorStore.Vectors["small-embed-1"].Should().HaveCount(384);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UpsertNode_LargeEmbedding1536_StoresCorrectly()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var customOptions = new MemoryOptions { EmbeddingDimension = 1536 };
        var store = new Neo4jMemoryGraphStore(_sharedDriver!, vectorStore: vectorStore, options: customOptions);
        await store.InitializeAsync();
        await using var cleanSession = _sharedDriver!.AsyncSession();
        await cleanSession.RunAsync("MATCH (n) WHERE NOT n:WriteMutex DETACH DELETE n");

        var embedding = new float[1536];
        for (int i = 0; i < 1536; i++) embedding[i] = (float)(i * 0.0005);
        var node = MakeNode("large-embed-1", "semantic", embedding: embedding);
        await store.UpsertNodeAsync(node, new VersionRange("abc", 1, null, null), CancellationToken.None);

        vectorStore.Vectors.Should().ContainKey("large-embed-1");
        vectorStore.Vectors["large-embed-1"].Should().HaveCount(1536);
        vectorStore.Vectors["large-embed-1"][1535].Should().BeApproximately(1536 * 0.0005f, 0.01f);
    }

    // ── RetireEdge with untilCommit ──────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RetireEdgeAsync_WithUntilCommit_SetsBothFields()
    {
        SkipIfNeo4jUnavailable();
        var store = await CreateStoreAsync();

        var nodeA = MakeNode("A", "structural");
        var nodeB = MakeNode("B", "structural");
        var ver = new VersionRange("c1", 1, null, null);

        await store.UpsertNodeAsync(nodeA, ver, CancellationToken.None);
        await store.UpsertNodeAsync(nodeB, ver, CancellationToken.None);

        var edge = MakeEdge("e-AB", ["A", "B"]);
        await store.UpsertEdgeAsync(edge, ver, CancellationToken.None);

        await store.RetireEdgeAsync("e-AB", 7, "sha789abc", CancellationToken.None);

        await using var session = _sharedDriver!.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (e:MemoryEdge {id: $id}) RETURN e.untilIndex AS ui, e.untilCommit AS uc",
            new { id = "e-AB" });
        var record = await cursor.SingleAsync();

        record["ui"].As<long>().Should().Be(7, "untilIndex should be set");
        record["uc"].As<string>().Should().Be("sha789abc",
            "untilCommit should be set when SHA string is passed to RetireEdgeAsync");
    }

    // ── Write serialization across ALL write paths ──────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    public async Task AllWritePaths_IncrementWriteMutexSequence()
    {
        SkipIfNeo4jUnavailable();
        var vectorStore = new InMemoryVectorStore();
        var store = await CreateStoreAsync(vectorStore);

        // Get initial sequence
        await using var session1 = _sharedDriver!.AsyncSession();
        var cursor1 = await session1.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._seq AS seq");
        var rec1 = await cursor1.SingleAsync();
        var seqBefore = rec1["seq"].As<long>();

        // Execute ALL write paths:
        // 1. UpsertNode (with embedding → 2 mutex increments: graph+audit, status)
        var embedding = new float[768];
        var node = MakeNode("all-paths-1", "structural", embedding: embedding);
        await store.UpsertNodeAsync(node, new VersionRange("c1", 1, null, null), CancellationToken.None);

        // 2. UpsertNode without embedding (1 mutex increment)
        var node2 = MakeNode("all-paths-2", "structural");
        await store.UpsertNodeAsync(node2, new VersionRange("c1", 1, null, null), CancellationToken.None);

        // 3. UpsertEdge (1 mutex increment)
        var edge = MakeEdge("all-paths-e1", ["all-paths-1", "all-paths-2"]);
        await store.UpsertEdgeAsync(edge, new VersionRange("c1", 1, null, null), CancellationToken.None);

        // 4. RetireNode (1 mutex increment)
        await store.RetireNodeAsync("all-paths-1", 5, null, CancellationToken.None);

        // 5. RetireEdge (1 mutex increment)
        await store.RetireEdgeAsync("all-paths-e1", 5, null, CancellationToken.None);

        // 6. UpdateEdgeMetadata (1 mutex increment)
        await store.UpdateEdgeMetadataAsync("all-paths-e1",
            new Dictionary<string, object?> { ["x"] = "y" }, CancellationToken.None);

        // 7. UpsertExpertise (1 mutex increment)
        var exp = new ExpertiseEntry(ExpertiseKind.Pattern, "AP", "body", "all-paths-2", "agent");
        await store.UpsertExpertiseAsync(exp, CancellationToken.None);

        // 8. ArchiveExpertise (1 mutex increment)
        await store.ArchiveExpertiseAsync(exp.Id, CancellationToken.None);

        // 9. LogAudit (1 mutex increment)
        await store.LogAuditAsync("s1", "test", new { x = 1 }, CancellationToken.None);

        // Get final sequence — should have incremented for every write path
        await using var session2 = _sharedDriver!.AsyncSession();
        var cursor2 = await session2.RunAsync(
            "MATCH (lock:WriteMutex {name: 'memory_graph'}) RETURN lock._seq AS seq");
        var rec2 = await cursor2.SingleAsync();
        var seqAfter = rec2["seq"].As<long>();

        var totalIncrements = seqAfter - seqBefore;
        totalIncrements.Should().BeGreaterThanOrEqualTo(10,
            "ALL write paths must go through the WriteMutex: " +
            "UpsertNode+embed(2) + UpsertNode(1) + UpsertEdge(1) + RetireNode(1) + " +
            "RetireEdge(1) + UpdateEdgeMeta(1) + UpsertExpertise(1) + ArchiveExpertise(1) + LogAudit(1) = 10");
    }
}
