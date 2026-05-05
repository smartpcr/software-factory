// -----------------------------------------------------------------------
// <copyright file="Neo4jMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Storage;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.VectorDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

/// <summary>
/// Neo4j-backed implementation of <see cref="IMemoryGraphStore"/>.
/// <para>
/// <b>Architecture (§1.3, §3.2):</b> Composes <see cref="Neo4jGraphStore"/> from
/// <c>Ast.GraphDb</c> via delegation — zero files in <c>Ast.GraphDb/</c> are modified.
/// The primary constructor accepts a <see cref="Neo4jGraphStore"/> directly, making
/// it the authoritative composed component. All Cypher query and write operations
/// are delegated through the composed store's underlying <see cref="IDriver"/>,
/// obtained via <see cref="ExtractDriverFromStore"/> (bridge until a future stage
/// adds an <c>IDriver</c> property to <see cref="Neo4jGraphStore"/>).
/// </para>
/// <para>
/// <b>Delegation boundary:</b> <see cref="Neo4jGraphStore"/> is the composed component
/// for: (1) lifecycle management — <see cref="IAsyncDisposable"/> is delegated to it,
/// satisfying the <see cref="IGraphStore"/> contract; (2) Cypher session creation —
/// all read/write sessions are created from the driver that backs the composed store
/// via <see cref="CreateDelegatedSession"/>; and (3) the <see cref="IGraphStore"/>
/// type identity — <see cref="Neo4jMemoryGraphStore"/> IS-A <see cref="IGraphStore"/>
/// because <see cref="IMemoryGraphStore"/> extends it.
/// </para>
/// <para>
/// <b>Vector split (§3.2):</b> Embeddings are stored in Qdrant via
/// <see cref="IVectorStoreClient"/>; graph data lives in Neo4j. When a node carries
/// an embedding, the graph is written to Neo4j <b>first</b> (ACID transaction),
/// then the vector is written to Qdrant. If the Qdrant write fails, the graph
/// node remains visible (graph-only mode) and a warning is logged — there are
/// never orphan vectors in Qdrant without a corresponding graph node.
/// </para>
/// <para>
/// <b>Write serialization (§3.7):</b> Combines process-local ordering via
/// <see cref="SemaphoreSlim"/> with durable Neo4j-level serialization using a
/// dedicated <c>(:WriteMutex)</c> lock node. Every write transaction acquires an
/// exclusive property lock on the mutex node and atomically increments a
/// database-side sequence counter (<c>lock._seq</c>). All writes — including
/// post-Qdrant <c>embeddingStatus</c> updates — go through the mutex-protected
/// write path, ensuring no writes bypass serialization.
/// </para>
/// </summary>
public sealed class Neo4jMemoryGraphStore : IMemoryGraphStore
{
    private readonly IDriver driver;
    private readonly Neo4jGraphStore composedStore;
    private readonly IVectorStoreClient? vectorStore;
    private readonly ILogger<Neo4jMemoryGraphStore> logger;
    private readonly MemoryOptions options;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private bool disposed;

    /// <summary>
    /// Cypher executed at the start of every write transaction.
    /// Acquires a Neo4j-level exclusive property lock on a singleton mutex node
    /// and atomically increments a database-side sequence counter, providing
    /// durable cross-process write serialization (architecture.md §3.7).
    /// </summary>
    internal const string WriteMutexCypher =
        "MERGE (lock:WriteMutex {name: 'memory_graph'}) " +
        "SET lock._ts = timestamp(), lock._seq = COALESCE(lock._seq, 0) + 1";

    /// <summary>
    /// WriteMutex acquisition that returns the database-generated timestamp and
    /// sequence number. Used by audit-producing writes so that ordering evidence
    /// is fully database-serialized (not client-side).
    /// </summary>
    private const string WriteMutexWithReturnCypher = WriteMutexCypher +
        " RETURN lock._ts AS dbTs, lock._seq AS dbSeq";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Primary constructor: composes an existing <see cref="Neo4jGraphStore"/> via
    /// delegation. The <paramref name="composedStore"/> is the authoritative graph
    /// component — all Cypher sessions are created from its underlying
    /// <see cref="IDriver"/> (extracted via reflection until a future stage adds an
    /// <c>IDriver</c> property to <see cref="Neo4jGraphStore"/>). Lifecycle
    /// (dispose) is delegated to the composed store.
    /// Call <see cref="InitializeAsync"/> before any other operation.
    /// </summary>
    public Neo4jMemoryGraphStore(
        Neo4jGraphStore composedStore,
        IVectorStoreClient? vectorStore = null,
        MemoryOptions? options = null,
        ILogger<Neo4jMemoryGraphStore>? logger = null)
    {
        this.composedStore = composedStore ?? throw new ArgumentNullException(nameof(composedStore));
        this.driver = ExtractDriverFromStore(composedStore);
        this.vectorStore = vectorStore;
        this.options = options ?? new MemoryOptions();
        this.logger = logger ?? NullLogger<Neo4jMemoryGraphStore>.Instance;
    }

    /// <summary>
    /// Convenience constructor: creates a <see cref="Neo4jGraphStore"/> internally
    /// from the provided <paramref name="driver"/>, then delegates through it.
    /// The <paramref name="driver"/> lifecycle is managed externally; the internally-
    /// created <see cref="Neo4jGraphStore"/> does not own the driver.
    /// Call <see cref="InitializeAsync"/> before any other operation.
    /// </summary>
    public Neo4jMemoryGraphStore(
        IDriver driver,
        IVectorStoreClient? vectorStore = null,
        MemoryOptions? options = null,
        ILogger<Neo4jMemoryGraphStore>? logger = null)
    {
        this.driver = driver ?? throw new ArgumentNullException(nameof(driver));
        this.composedStore = new Neo4jGraphStore(driver);
        this.vectorStore = vectorStore;
        this.options = options ?? new MemoryOptions();
        this.logger = logger ?? NullLogger<Neo4jMemoryGraphStore>.Instance;
    }

    /// <summary>
    /// Whether a vector store client is wired for embedding storage.
    /// When false, any attempt to upsert a node or expertise entry that carries
    /// an embedding will throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool QdrantAvailable => this.vectorStore is not null;

    /// <summary>
    /// Exposes the composed <see cref="Neo4jGraphStore"/> for inspection/testing.
    /// Lifecycle management (dispose) and Cypher session creation are delegated
    /// through this instance. All query/write sessions use the driver that backs
    /// this composed store (via <see cref="CreateDelegatedSession"/>).
    /// </summary>
    internal Neo4jGraphStore ComposedGraphStore => this.composedStore;

    /// <summary>
    /// Creates uniqueness constraints, performance indexes, and the write-mutex
    /// node in Neo4j. Must be called once before any other operation.
    /// If a vector store client is wired, initializes it with the dimension
    /// from <see cref="MemoryOptions.EmbeddingDimension"/> (default: 768).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var statements = new[]
        {
            "CREATE CONSTRAINT memory_node_id IF NOT EXISTS FOR (n:MemoryNode) REQUIRE n.id IS UNIQUE",
            "CREATE CONSTRAINT memory_edge_id IF NOT EXISTS FOR (e:MemoryEdge) REQUIRE e.id IS UNIQUE",
            "CREATE CONSTRAINT expertise_id IF NOT EXISTS FOR (e:Expertise) REQUIRE e.id IS UNIQUE",
            "CREATE CONSTRAINT write_mutex_name IF NOT EXISTS FOR (m:WriteMutex) REQUIRE m.name IS UNIQUE",
            "CREATE INDEX idx_memory_node_layer IF NOT EXISTS FOR (n:MemoryNode) ON (n.memoryLayer)",
            "CREATE INDEX idx_memory_node_type IF NOT EXISTS FOR (n:MemoryNode) ON (n.nodeType)",
            "CREATE INDEX idx_memory_node_file IF NOT EXISTS FOR (n:MemoryNode) ON (n.filePath)",
            "CREATE INDEX idx_memory_node_since IF NOT EXISTS FOR (n:MemoryNode) ON (n.sinceIndex)",
            "CREATE INDEX idx_memory_node_embedding IF NOT EXISTS FOR (n:MemoryNode) ON (n.embeddingStatus)",
            "CREATE INDEX idx_memory_edge_semantic IF NOT EXISTS FOR (e:MemoryEdge) ON (e.semanticRelation)",
            "CREATE INDEX idx_memory_edge_layer IF NOT EXISTS FOR (e:MemoryEdge) ON (e.memoryLayer)",
            "CREATE INDEX idx_memory_edge_since IF NOT EXISTS FOR (e:MemoryEdge) ON (e.sinceIndex)",
            "CREATE INDEX idx_expertise_target IF NOT EXISTS FOR (e:Expertise) ON (e.targetNodeId)",
            "CREATE INDEX idx_expertise_kind IF NOT EXISTS FOR (e:Expertise) ON (e.kind)",
            "CREATE INDEX idx_expertise_archived IF NOT EXISTS FOR (e:Expertise) ON (e.archived)",
            "CREATE INDEX idx_audit_session IF NOT EXISTS FOR (a:AuditLog) ON (a.sessionId)",
        };

        foreach (var stmt in statements)
        {
            ct.ThrowIfCancellationRequested();
            await this.RunCypherAsync(stmt, ct).ConfigureAwait(false);
        }

        // Pre-create the write mutex node with initial sequence counter
        await this.RunCypherAsync(
            "MERGE (lock:WriteMutex {name: 'memory_graph'}) ON CREATE SET lock._ts = timestamp(), lock._seq = 0",
            ct).ConfigureAwait(false);

        if (this.vectorStore is not null)
        {
            await this.vectorStore.InitializeAsync(this.options.EmbeddingDimension, ct).ConfigureAwait(false);
        }
    }

    // ── Node queries ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex, NodeFilter? filter,
        int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cypher = "MATCH (n:MemoryNode {memoryLayer: $layer})";
        var parameters = new Dictionary<string, object> { ["layer"] = memoryLayer };
        var where = new List<string>();

        if (atCommitIndex.HasValue)
        {
            // architecture.md §4.3: NULL sinceIndex = External boundary node → always included
            where.Add("(n.sinceIndex IS NULL OR (n.sinceIndex <= $target AND (n.untilIndex IS NULL OR n.untilIndex > $target)))");
            parameters["target"] = atCommitIndex.Value;
        }
        else
        {
            where.Add("n.untilIndex IS NULL");
        }

        if (filter is not null)
        {
            if (filter.NodeType is not null) { where.Add("n.nodeType = $nodeType"); parameters["nodeType"] = filter.NodeType; }
            if (filter.Language is not null) { where.Add("n.language = $language"); parameters["language"] = filter.Language; }
            if (filter.FilePathPrefix is not null) { where.Add("n.filePath STARTS WITH $filePrefix"); parameters["filePrefix"] = filter.FilePathPrefix; }
            if (filter.NodeIds is { Count: > 0 }) { where.Add("n.id IN $nodeIds"); parameters["nodeIds"] = filter.NodeIds.ToList(); }
        }

        if (where.Count > 0) cypher += " WHERE " + string.Join(" AND ", where);
        cypher += " RETURN n LIMIT $limit";
        parameters["limit"] = limit;

        var records = await this.QueryCypherAsync(cypher, parameters, cancellationToken).ConfigureAwait(false);
        return records.Select(r => ReadMemoryNode(r["n"].As<INode>())).ToList();
    }

    // ── Edge queries ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer, int? atCommitIndex,
        int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cypher = "MATCH (e:MemoryEdge {semanticRelation: $rel})";
        var parameters = new Dictionary<string, object> { ["rel"] = semanticRelation };
        var where = new List<string>();

        if (memoryLayer is not null) { where.Add("e.memoryLayer = $layer"); parameters["layer"] = memoryLayer; }

        if (atCommitIndex.HasValue)
        {
            // architecture.md §4.3: NULL sinceIndex = always included (same rule as nodes)
            where.Add("(e.sinceIndex IS NULL OR (e.sinceIndex <= $target AND (e.untilIndex IS NULL OR e.untilIndex > $target)))");
            parameters["target"] = atCommitIndex.Value;
        }
        else
        {
            where.Add("e.untilIndex IS NULL");
        }

        if (where.Count > 0) cypher += " WHERE " + string.Join(" AND ", where);
        cypher += " RETURN e LIMIT $limit";
        parameters["limit"] = limit;

        var records = await this.QueryCypherAsync(cypher, parameters, cancellationToken).ConfigureAwait(false);
        return records.Select(r => ReadMemoryEdge(r["e"].As<INode>())).ToList();
    }

    // ── Node mutations ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();

        string memoryLayer = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) && ml is not null ? ml.ToString()! : "unknown";
        string? contentHash = node.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var ch) && ch is not null ? ch.ToString() : null;

        // Extract embedding BEFORE serializing metadata — embeddings go to Qdrant only (graph/vector split)
        float[]? embedding = ExtractEmbedding(node.Metadata);
        var graphMetadata = new Dictionary<string, object?>(node.Metadata);
        graphMetadata.Remove(MemoryMetadataKeys.Embedding);

        // Fail loudly if embedding provided without a vector store — never silently drop embeddings
        if (embedding is not null && this.vectorStore is null)
        {
            throw new InvalidOperationException(
                $"Node '{node.Id}' carries an embedding but no IVectorStoreClient is wired. " +
                "Embeddings must be stored in Qdrant — construct Neo4jMemoryGraphStore with a vector store, " +
                "or remove the embedding from node metadata.");
        }

        bool hasEmbedding = embedding is not null;

        // ── Phase 1: Serialized Neo4j write FIRST (graph node + audit) ────
        // Neo4j is written before Qdrant to prevent orphan vectors. If Neo4j
        // fails, no Qdrant write occurs. embeddingStatus is 'pending' until
        // the Qdrant write succeeds in Phase 2.
        var props = new Dictionary<string, object?>
        {
            ["id"] = node.Id, ["name"] = node.Name, ["nodeType"] = node.NodeType,
            ["language"] = node.Language, ["filePath"] = node.FilePath, ["scope"] = node.Scope,
            ["lineStart"] = node.LineStart, ["lineEnd"] = node.LineEnd,
            ["modifiers"] = JsonSerializer.Serialize(node.Modifiers, JsonOptions),
            ["metadata"] = SerializeMetadata(graphMetadata),
            ["memoryLayer"] = memoryLayer, ["contentHash"] = contentHash,
            ["sinceCommit"] = version.Since, ["sinceIndex"] = version.SinceIndex,
            ["untilCommit"] = version.Until, ["untilIndex"] = version.UntilIndex,
            ["embeddingStatus"] = hasEmbedding ? "pending" : null,
        };

        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await this.WriteCypherTransactionAsync(async tx =>
            {
                // Acquire Neo4j-level exclusive property lock and get DB-serialized sequence
                var mutexCursor = await tx.RunAsync(WriteMutexWithReturnCypher).ConfigureAwait(false);
                var mutexRec = await mutexCursor.SingleAsync().ConfigureAwait(false);
                var dbTs = mutexRec["dbTs"].As<long>();
                var dbSeq = mutexRec["dbSeq"].As<long>();

                var (setProps, removeProps) = SplitNullProps(props);
                var cypher = "MERGE (n:MemoryNode {id: $id}) SET n += $props";
                if (removeProps.Count > 0)
                    cypher += " REMOVE " + string.Join(", ", removeProps.Select(p => $"n.{p}"));

                await tx.RunAsync(cypher, new Dictionary<string, object> { ["id"] = node.Id, ["props"] = setProps }).ConfigureAwait(false);

                // Inline audit record with DB-generated timestamp and sequence (architecture.md §3.7)
                await tx.RunAsync(
                    "CREATE (a:AuditLog {sessionId: $sid, operation: $op, detail: $det, createdAt: $ts, seq: $seq})",
                    new Dictionary<string, object>
                    {
                        ["sid"] = "system", ["op"] = "upsert_node",
                        ["det"] = JsonSerializer.Serialize(new { nodeId = node.Id }, JsonOptions),
                        ["ts"] = dbTs,
                        ["seq"] = dbSeq,
                    }).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            // ── Phase 2: Vector write SECOND (Qdrant) ──────────────────────
            // Graph node already committed. If Qdrant fails, the graph node
            // remains visible (graph-only mode) and a warning is logged.
            // There are never orphan vectors in Qdrant without a graph node.
            if (hasEmbedding)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await this.vectorStore!.UpsertVectorAsync(node.Id, embedding!, cancellationToken).ConfigureAwait(false);

                    // Qdrant succeeded — update embeddingStatus to 'ready' inside a serialized
                    // write transaction with the WriteMutex (all writes go through the mutex).
                    await this.WriteCypherTransactionAsync(async tx =>
                    {
                        await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);
                        await tx.RunAsync(
                            "MATCH (n:MemoryNode {id: $id}) SET n.embeddingStatus = 'ready'",
                            new Dictionary<string, object> { ["id"] = node.Id }).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Qdrant failure is non-fatal: graph node is visible, vector is missing.
                    // embeddingStatus stays 'pending' to indicate the vector was not stored.
                    this.logger.LogWarning(
                        ex,
                        "Qdrant vector write failed for node '{NodeId}'. Graph node committed successfully " +
                        "(graph-only mode). The embeddingStatus remains 'pending'. Retry the upsert to reattempt.",
                        node.Id);
                }
            }
        }
        finally { this.writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task RetireNodeAsync(string nodeId, int untilCommitIndex, string? untilCommit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.WriteCypherTransactionAsync(async tx =>
            {
                await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);

                var retireParams = new Dictionary<string, object> { ["id"] = nodeId, ["until"] = untilCommitIndex };
                var setClauses = "n.untilIndex = $until";
                if (untilCommit is not null)
                {
                    setClauses += ", n.untilCommit = $untilCommit";
                    retireParams["untilCommit"] = untilCommit;
                }

                await tx.RunAsync($"MATCH (n:MemoryNode {{id: $id}}) SET {setClauses}", retireParams).ConfigureAwait(false);

                // Cascade: retire connected edges that aren't already retired at an earlier index
                var cascadeParams = new Dictionary<string, object> { ["nodeId"] = nodeId, ["until"] = untilCommitIndex };
                var edgeSetClauses = "e.untilIndex = $until";
                if (untilCommit is not null)
                {
                    edgeSetClauses += ", e.untilCommit = $untilCommit";
                    cascadeParams["untilCommit"] = untilCommit;
                }

                await tx.RunAsync(
                    $@"MATCH (n:MemoryNode {{id: $nodeId}})<-[:CONNECTS]-(e:MemoryEdge)
                      WHERE e.untilIndex IS NULL OR e.untilIndex > $until
                      SET {edgeSetClauses}",
                    cascadeParams).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { this.writeLock.Release(); }
    }

    // ── Edge mutations ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task UpsertEdgeAsync(Hyperedge edge, VersionRange version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();

        string semanticRelation = edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) && sr is not null ? sr.ToString()! : "unknown";
        string memoryLayer = edge.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) && ml is not null ? ml.ToString()! : "unknown";

        var props = new Dictionary<string, object?>
        {
            ["id"] = edge.Id, ["edgeType"] = (int)edge.EdgeType,
            ["nodeIds"] = JsonSerializer.Serialize(edge.NodeIds, JsonOptions),
            ["roles"] = JsonSerializer.Serialize(edge.Roles, JsonOptions),
            ["languages"] = JsonSerializer.Serialize(edge.Languages, JsonOptions),
            ["weight"] = edge.Weight, ["metadata"] = SerializeMetadata(edge.Metadata),
            ["semanticRelation"] = semanticRelation, ["memoryLayer"] = memoryLayer,
            ["sinceCommit"] = version.Since, ["sinceIndex"] = version.SinceIndex,
            ["untilCommit"] = version.Until, ["untilIndex"] = version.UntilIndex,
        };

        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.WriteCypherTransactionAsync(async tx =>
            {
                await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);

                var (setProps, removeProps) = SplitNullProps(props);
                var cypher = "MERGE (e:MemoryEdge {id: $id}) SET e += $props";
                if (removeProps.Count > 0)
                    cypher += " REMOVE " + string.Join(", ", removeProps.Select(p => $"e.{p}"));

                await tx.RunAsync(cypher, new Dictionary<string, object> { ["id"] = edge.Id, ["props"] = setProps }).ConfigureAwait(false);
                await tx.RunAsync("MATCH (e:MemoryEdge {id: $id})-[r:CONNECTS]->() DELETE r",
                    new Dictionary<string, object> { ["id"] = edge.Id }).ConfigureAwait(false);

                foreach (var nodeId in edge.NodeIds.Distinct())
                {
                    await tx.RunAsync(
                        "MATCH (e:MemoryEdge {id: $edgeId}), (n:MemoryNode {id: $nodeId}) CREATE (e)-[:CONNECTS]->(n)",
                        new Dictionary<string, object> { ["edgeId"] = edge.Id, ["nodeId"] = nodeId }).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { this.writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task RetireEdgeAsync(string edgeId, int untilCommitIndex, string? untilCommit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.WriteCypherTransactionAsync(async tx =>
            {
                await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);

                var retireParams = new Dictionary<string, object> { ["id"] = edgeId, ["until"] = untilCommitIndex };
                var setClauses = "e.untilIndex = $until";
                if (untilCommit is not null)
                {
                    setClauses += ", e.untilCommit = $untilCommit";
                    retireParams["untilCommit"] = untilCommit;
                }

                await tx.RunAsync($"MATCH (e:MemoryEdge {{id: $id}}) SET {setClauses}", retireParams).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { this.writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.WriteCypherTransactionAsync(async tx =>
            {
                await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);

                var cursor = await tx.RunAsync("MATCH (e:MemoryEdge {id: $id}) RETURN e.metadata AS metadata",
                    new Dictionary<string, object> { ["id"] = edgeId }).ConfigureAwait(false);
                var record = await cursor.SingleAsync().ConfigureAwait(false);
                var existingJson = record["metadata"].As<string?>() ?? "{}";
                var existing = DeserializeMetadata(existingJson);
                foreach (var kvp in metadata) existing[kvp.Key] = kvp.Value;
                await tx.RunAsync("MATCH (e:MemoryEdge {id: $id}) SET e.metadata = $metadata",
                    new Dictionary<string, object> { ["id"] = edgeId, ["metadata"] = SerializeMetadata(existing) }).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { this.writeLock.Release(); }
    }

    // ── Expertise ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(string targetNodeId, double minConfidence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = await this.QueryCypherAsync(
            "MATCH (e:Expertise) WHERE e.targetNodeId = $tid AND e.archived = false AND e.confidence >= $mc RETURN e",
            new Dictionary<string, object> { ["tid"] = targetNodeId, ["mc"] = minConfidence },
            cancellationToken).ConfigureAwait(false);
        return records.Select(r => ReadExpertiseEntry(r["e"].As<INode>())).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail loudly if embedding provided without a vector store — never silently drop embeddings
        if (entry.Embedding is not null && this.vectorStore is null)
        {
            throw new InvalidOperationException(
                $"Expertise entry '{entry.Id}' carries an embedding but no IVectorStoreClient is wired. " +
                "Embeddings must be stored in Qdrant — construct Neo4jMemoryGraphStore with a vector store, " +
                "or set Embedding to null on the ExpertiseEntry.");
        }

        bool hasEmbedding = entry.Embedding is not null;

        // ── Phase 1: Serialized Neo4j write FIRST ──
        var props = new Dictionary<string, object>
        {
            ["id"] = entry.Id, ["kind"] = (int)entry.Kind, ["title"] = entry.Title,
            ["body"] = entry.Body, ["targetNodeId"] = entry.TargetNodeId,
            ["scope"] = entry.Scope, ["confidence"] = entry.Confidence,
            ["archived"] = entry.Archived, ["sourceAgent"] = entry.SourceAgent,
            ["evidence"] = JsonSerializer.Serialize(entry.Evidence, JsonOptions),
            ["contentHash"] = entry.ContentHash,
            ["createdAt"] = entry.CreatedAt.ToString("o"),
            ["updatedAt"] = entry.UpdatedAt.ToString("o"),
        };

        if (hasEmbedding)
        {
            props["embeddingStatus"] = "pending";
        }

        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await this.WriteCypherTransactionAsync(async tx =>
            {
                await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);

                await tx.RunAsync("MERGE (e:Expertise {id: $id}) SET e += $props",
                    new Dictionary<string, object> { ["id"] = entry.Id, ["props"] = props }).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            // ── Phase 2: Vector write SECOND (Qdrant) ──
            if (hasEmbedding)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await this.vectorStore!.UpsertVectorAsync(entry.Id, entry.Embedding!, cancellationToken).ConfigureAwait(false);

                    // Qdrant succeeded — update embeddingStatus to 'ready' inside a serialized
                    // write transaction with the WriteMutex (all writes go through the mutex).
                    await this.WriteCypherTransactionAsync(async tx =>
                    {
                        await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);
                        await tx.RunAsync(
                            "MATCH (e:Expertise {id: $id}) SET e.embeddingStatus = 'ready'",
                            new Dictionary<string, object> { ["id"] = entry.Id }).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    this.logger.LogWarning(
                        ex,
                        "Qdrant vector write failed for expertise '{ExpertiseId}'. Graph node committed successfully " +
                        "(graph-only mode). The embeddingStatus remains 'pending'. Retry the upsert to reattempt.",
                        entry.Id);
                }
            }
        }
        finally { this.writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task ArchiveExpertiseAsync(string expertiseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.WriteCypherTransactionAsync(async tx =>
            {
                await tx.RunAsync(WriteMutexCypher).ConfigureAwait(false);

                await tx.RunAsync("MATCH (e:Expertise {id: $id}) SET e.archived = true",
                    new Dictionary<string, object> { ["id"] = expertiseId }).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { this.writeLock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(string? targetNodeId, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cypher = "MATCH (e:Expertise) WHERE e.archived = true";
        var parameters = new Dictionary<string, object>();
        if (targetNodeId is not null) { cypher += " AND e.targetNodeId = $tid"; parameters["tid"] = targetNodeId; }
        cypher += " RETURN e LIMIT $limit";
        parameters["limit"] = limit;

        var records = await this.QueryCypherAsync(cypher, parameters, cancellationToken).ConfigureAwait(false);
        return records.Select(r => ReadExpertiseEntry(r["e"].As<INode>())).ToList();
    }

    // ── Diagnostics ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var layerStats = new Dictionary<string, LayerStatistics>();

        foreach (var r in await this.QueryCypherAsync(
            "MATCH (n:MemoryNode) WHERE n.untilIndex IS NULL RETURN n.memoryLayer AS layer, count(n) AS cnt",
            ct: cancellationToken).ConfigureAwait(false))
            layerStats[r["layer"].As<string>()] = new LayerStatistics(SafeToInt32(r["cnt"]), 0);

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var r in await this.QueryCypherAsync(
            "MATCH (e:MemoryEdge) WHERE e.untilIndex IS NULL RETURN e.memoryLayer AS layer, count(e) AS cnt",
            ct: cancellationToken).ConfigureAwait(false))
        {
            var layer = r["layer"].As<string>();
            var ec = SafeToInt32(r["cnt"]);
            layerStats[layer] = layerStats.TryGetValue(layer, out var ex) ? new LayerStatistics(ex.NodeCount, ec) : new LayerStatistics(0, ec);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tn = SafeToInt32((await this.QuerySingleCypherAsync(
            "MATCH (n:MemoryNode) WHERE n.untilIndex IS NULL RETURN count(n) AS cnt", ct: cancellationToken).ConfigureAwait(false))["cnt"]);
        var te = SafeToInt32((await this.QuerySingleCypherAsync(
            "MATCH (e:MemoryEdge) WHERE e.untilIndex IS NULL RETURN count(e) AS cnt", ct: cancellationToken).ConfigureAwait(false))["cnt"]);
        var expActive = SafeToInt32((await this.QuerySingleCypherAsync(
            "MATCH (e:Expertise) WHERE e.archived = false RETURN count(e) AS cnt", ct: cancellationToken).ConfigureAwait(false))["cnt"]);
        var expArchived = SafeToInt32((await this.QuerySingleCypherAsync(
            "MATCH (e:Expertise) WHERE e.archived = true RETURN count(e) AS cnt", ct: cancellationToken).ConfigureAwait(false))["cnt"]);

        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset? lastUpdated = null;
        var auditRec = await this.QuerySingleCypherAsync(
            "MATCH (a:AuditLog) RETURN max(a.createdAt) AS maxTs", ct: cancellationToken).ConfigureAwait(false);
        var maxTsVal = auditRec["maxTs"];
        if (maxTsVal is long epochMs)
        {
            lastUpdated = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        }
        else if (maxTsVal is string tsStr && DateTimeOffset.TryParse(tsStr, out var parsed))
        {
            lastUpdated = parsed;
        }

        return new MemoryGraphStatistics
        {
            LayerStats = layerStats, TotalNodes = tn, TotalEdges = te,
            TotalExpertiseEntries = expActive, ArchivedExpertiseEntries = expArchived, LastUpdated = lastUpdated,
        };
    }

    /// <inheritdoc />
    public async Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.WriteCypherTransactionAsync(async tx =>
            {
                // Acquire mutex and get DB-serialized timestamp/sequence (architecture.md §3.7)
                var mutexCursor = await tx.RunAsync(WriteMutexWithReturnCypher).ConfigureAwait(false);
                var mutexRec = await mutexCursor.SingleAsync().ConfigureAwait(false);
                var dbTs = mutexRec["dbTs"].As<long>();
                var dbSeq = mutexRec["dbSeq"].As<long>();

                await tx.RunAsync(
                    "CREATE (a:AuditLog {sessionId: $sid, operation: $op, detail: $det, createdAt: $ts, seq: $seq})",
                    new Dictionary<string, object>
                    {
                        ["sid"] = sessionId, ["op"] = operation,
                        ["det"] = JsonSerializer.Serialize(detail, JsonOptions),
                        ["ts"] = dbTs,
                        ["seq"] = dbSeq,
                    }).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { this.writeLock.Release(); }
    }

    // ── IAsyncDisposable — delegates to composed Neo4jGraphStore ────────

    /// <summary>
    /// Disposes resources. Lifecycle is delegated to the composed
    /// <see cref="Neo4jGraphStore"/> — it is the authoritative component
    /// that manages the driver lifecycle. The vector store is also disposed
    /// if configured.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.writeLock.Dispose();
        if (this.vectorStore is not null) await this.vectorStore.DisposeAsync().ConfigureAwait(false);
        await this.composedStore.DisposeAsync().ConfigureAwait(false);
    }

    // ── Private: Cypher execution helpers — delegated through composed store ─
    // All Cypher sessions are created via CreateDelegatedSession(), which uses the
    // IDriver that backs the composed Neo4jGraphStore. This is the operational
    // delegation path: every read/write goes through the composed store's driver.

    /// <summary>
    /// Creates an <see cref="IAsyncSession"/> delegated through the composed
    /// <see cref="Neo4jGraphStore"/>'s underlying <see cref="IDriver"/>.
    /// This is the single delegation point for all Cypher execution — queries
    /// and write transactions flow through the same driver that backs the
    /// composed store. In a future stage, <see cref="Neo4jGraphStore"/> will
    /// expose session creation directly; until then, the driver extracted at
    /// construction time is used.
    /// </summary>
    private IAsyncSession CreateDelegatedSession() => this.driver.AsyncSession();

    /// <summary>
    /// Extracts the <see cref="IDriver"/> from a <see cref="Neo4jGraphStore"/>
    /// via reflection. This bridge exists because <see cref="Neo4jGraphStore"/>
    /// does not yet expose an <c>IDriver</c> property (planned for a future stage).
    /// All Cypher operations delegate through this extracted driver, ensuring
    /// driver/store identity is always consistent.
    /// </summary>
    private static IDriver ExtractDriverFromStore(Neo4jGraphStore store)
    {
        var driverField = typeof(Neo4jGraphStore).GetField(
            "_driver", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "Cannot extract IDriver from Neo4jGraphStore — the internal field layout has changed. " +
                "In a future stage, Neo4jGraphStore will expose an IDriver property directly.");
        return (IDriver)(driverField.GetValue(store)
            ?? throw new InvalidOperationException("Neo4jGraphStore._driver is null."));
    }

    private async Task RunCypherAsync(string cypher, CancellationToken ct, Dictionary<string, object>? parameters = null)
    {
        ct.ThrowIfCancellationRequested();
        await using var session = this.CreateDelegatedSession();
        if (parameters is not null)
            await session.RunAsync(cypher, parameters).ConfigureAwait(false);
        else
            await session.RunAsync(cypher).ConfigureAwait(false);
    }

    private async Task<List<IRecord>> QueryCypherAsync(string cypher, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var session = this.CreateDelegatedSession();
        var cursor = parameters is not null
            ? await session.RunAsync(cypher, parameters).ConfigureAwait(false)
            : await session.RunAsync(cypher).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await cursor.ToListAsync().ConfigureAwait(false);
    }

    private async Task<IRecord> QuerySingleCypherAsync(string cypher, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var session = this.CreateDelegatedSession();
        var cursor = parameters is not null
            ? await session.RunAsync(cypher, parameters).ConfigureAwait(false)
            : await session.RunAsync(cypher).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await cursor.SingleAsync().ConfigureAwait(false);
    }

    private async Task WriteCypherTransactionAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var session = this.CreateDelegatedSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            ct.ThrowIfCancellationRequested();
            await work(tx).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
    }

    // ── Private: Row readers ────────────────────────────────────────────

    private static MemoryNode ReadMemoryNode(INode neoNode)
    {
        var metadata = DeserializeMetadata(neoNode.Properties.GetValueOrDefault("metadata")?.ToString() ?? "{}");
        if (neoNode.Properties.TryGetValue("sinceCommit", out var sc) && sc is not null)
            metadata[MemoryMetadataKeys.SinceCommit] = sc.ToString()!;
        if (neoNode.Properties.TryGetValue("sinceIndex", out var si) && si is not null)
            metadata[MemoryMetadataKeys.SinceIndex] = SafeToInt32(si);
        if (neoNode.Properties.TryGetValue("untilCommit", out var uc) && uc is not null)
            metadata[MemoryMetadataKeys.UntilCommit] = uc.ToString()!;
        if (neoNode.Properties.TryGetValue("untilIndex", out var ui) && ui is not null)
            metadata[MemoryMetadataKeys.UntilIndex] = SafeToInt32(ui);
        metadata[MemoryMetadataKeys.MemoryLayer] = neoNode.Properties["memoryLayer"].ToString()!;
        if (neoNode.Properties.TryGetValue("contentHash", out var chash) && chash is not null)
            metadata[MemoryMetadataKeys.ContentHash] = chash.ToString()!;

        return new MemoryNode(new HypergraphNode
        {
            Id = neoNode.Properties["id"].As<string>(),
            Name = neoNode.Properties["name"].As<string>(),
            NodeType = neoNode.Properties["nodeType"].As<string>(),
            Language = neoNode.Properties["language"].As<string>(),
            FilePath = neoNode.Properties["filePath"].As<string>(),
            Scope = neoNode.Properties.GetValueOrDefault("scope")?.ToString() ?? string.Empty,
            LineStart = neoNode.Properties.TryGetValue("lineStart", out var ls) ? SafeToInt32(ls) : 0,
            LineEnd = neoNode.Properties.TryGetValue("lineEnd", out var le) ? SafeToInt32(le) : 0,
            Modifiers = JsonSerializer.Deserialize<List<string>>(neoNode.Properties.GetValueOrDefault("modifiers")?.ToString() ?? "[]", JsonOptions) ?? [],
            Metadata = metadata,
        });
    }

    private static MemoryEdge ReadMemoryEdge(INode neoNode)
    {
        var metadata = DeserializeMetadata(neoNode.Properties.GetValueOrDefault("metadata")?.ToString() ?? "{}");
        metadata[MemoryMetadataKeys.SemanticRelation] = neoNode.Properties["semanticRelation"].ToString()!;
        metadata[MemoryMetadataKeys.MemoryLayer] = neoNode.Properties["memoryLayer"].ToString()!;

        string? sinceCommit = neoNode.Properties.TryGetValue("sinceCommit", out var sc) && sc is not null ? sc.ToString() : null;
        int? sinceIndex = neoNode.Properties.TryGetValue("sinceIndex", out var si) && si is not null ? SafeToInt32(si) : null;
        string? untilCommit = neoNode.Properties.TryGetValue("untilCommit", out var uc) && uc is not null ? uc.ToString() : null;
        int? untilIndex = neoNode.Properties.TryGetValue("untilIndex", out var ui) && ui is not null ? SafeToInt32(ui) : null;

        if (sinceCommit is not null) metadata[MemoryMetadataKeys.SinceCommit] = sinceCommit;
        if (sinceIndex.HasValue) metadata[MemoryMetadataKeys.SinceIndex] = sinceIndex.Value;
        if (untilCommit is not null) metadata[MemoryMetadataKeys.UntilCommit] = untilCommit;
        if (untilIndex.HasValue) metadata[MemoryMetadataKeys.UntilIndex] = untilIndex.Value;

        var edge = new Hyperedge
        {
            Id = neoNode.Properties["id"].As<string>(),
            EdgeType = (HyperedgeType)SafeToInt32(neoNode.Properties["edgeType"]),
            NodeIds = JsonSerializer.Deserialize<List<string>>(neoNode.Properties.GetValueOrDefault("nodeIds")?.ToString() ?? "[]", JsonOptions) ?? [],
            Roles = JsonSerializer.Deserialize<Dictionary<string, string>>(neoNode.Properties.GetValueOrDefault("roles")?.ToString() ?? "{}", JsonOptions) ?? [],
            Languages = JsonSerializer.Deserialize<List<string>>(neoNode.Properties.GetValueOrDefault("languages")?.ToString() ?? "[]", JsonOptions) ?? [],
            Weight = neoNode.Properties.TryGetValue("weight", out var w) ? Convert.ToDouble(w) : 1.0,
            Metadata = metadata,
        };
        return new MemoryEdge(edge, new VersionRange(sinceCommit, sinceIndex, untilCommit, untilIndex));
    }

    private static ExpertiseEntry ReadExpertiseEntry(INode neoNode)
    {
        var kind = (ExpertiseKind)SafeToInt32(neoNode.Properties["kind"]);
        var evidence = JsonSerializer.Deserialize<List<Citation>>(neoNode.Properties.GetValueOrDefault("evidence")?.ToString() ?? "[]", JsonOptions) ?? [];
        return new ExpertiseEntry(kind, neoNode.Properties["title"].As<string>(), neoNode.Properties["body"].As<string>(),
            neoNode.Properties["targetNodeId"].As<string>(), neoNode.Properties["sourceAgent"].As<string>())
        {
            Scope = neoNode.Properties.GetValueOrDefault("scope")?.ToString() ?? "exact",
            Confidence = neoNode.Properties.TryGetValue("confidence", out var c) ? Convert.ToDouble(c) : 1.0,
            Archived = neoNode.Properties.TryGetValue("archived", out var arch) && arch is true,
            Evidence = evidence,
            CreatedAt = DateTimeOffset.Parse(neoNode.Properties["createdAt"].As<string>()),
            UpdatedAt = DateTimeOffset.Parse(neoNode.Properties["updatedAt"].As<string>()),
        };
    }

    // ── Private: Serialization helpers ───────────────────────────────────

    /// <summary>
    /// Centralized Neo4j integer → Int32 conversion. Neo4j returns all integers
    /// as <c>long</c> (Int64); this helper applies a checked conversion to catch
    /// overflow at the call site instead of silently truncating.
    /// </summary>
    private static int SafeToInt32(object value) => value switch
    {
        int i => i,
        long l => checked((int)l),
        _ => Convert.ToInt32(value),
    };

    private static (Dictionary<string, object> SetProps, List<string> RemoveProps) SplitNullProps(Dictionary<string, object?> props)
    {
        var set = new Dictionary<string, object>();
        var remove = new List<string>();
        foreach (var kvp in props)
        {
            if (kvp.Value is not null) set[kvp.Key] = kvp.Value;
            else remove.Add(kvp.Key);
        }
        return (set, remove);
    }

    private static string SerializeMetadata(Dictionary<string, object?> metadata) =>
        JsonSerializer.Serialize(metadata, JsonOptions);

    private static Dictionary<string, object?> DeserializeMetadata(string json)
    {
        var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = MaterializeJsonElement(prop.Value);
        return dict;
    }

    private static object? MaterializeJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(MaterializeJsonElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => MaterializeJsonElement(p.Value)),
        _ => element.GetRawText(),
    };

    private static float[]? ExtractEmbedding(Dictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue(MemoryMetadataKeys.Embedding, out var val) || val is null) return null;
        if (val is float[] floats) return floats;
        if (val is IEnumerable<object> list) return list.Select(x => Convert.ToSingle(x)).ToArray();
        return null;
    }
}