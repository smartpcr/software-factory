// -----------------------------------------------------------------------
// <copyright file="SqliteGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IMemoryGraphStore"/>.
/// Uses FTS5 for full-text search, sqlite-vss for vector similarity
/// (when the native extension is available), WAL mode for concurrency,
/// and <see cref="SemaphoreSlim"/> for write serialization (architecture.md §3.7).
/// </summary>
public sealed class SqliteGraphStore : IMemoryGraphStore
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;
    private bool _vssAvailable;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Indicates whether the sqlite-vss extension was loaded and
    /// the <c>vss_nodes</c> virtual table is available.
    /// </summary>
    public bool VssAvailable => _vssAvailable;

    /// <summary>
    /// Creates a new <see cref="SqliteGraphStore"/> with the given connection string.
    /// Call <see cref="InitializeAsync"/> before any other operation.
    /// </summary>
    public SqliteGraphStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
    }

    /// <summary>
    /// Creates a new in-memory <see cref="SqliteGraphStore"/> for testing.
    /// </summary>
    public static async Task<SqliteGraphStore> CreateInMemoryAsync(CancellationToken ct = default)
    {
        var store = new SqliteGraphStore("Data Source=:memory:");
        await store.InitializeAsync(ct).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// Opens the connection, enables WAL mode, and creates all tables,
    /// FTS5 virtual tables, indexes, and optionally sqlite-vss tables.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        // ── Core tables ─────────────────────────────────────────────────
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS nodes (
                id            TEXT    PRIMARY KEY,
                name          TEXT    NOT NULL,
                node_type     TEXT    NOT NULL,
                language      TEXT    NOT NULL,
                file_path     TEXT    NOT NULL,
                scope         TEXT    NOT NULL DEFAULT '',
                line_start    INTEGER NOT NULL DEFAULT 0,
                line_end      INTEGER NOT NULL DEFAULT 0,
                modifiers     TEXT    NOT NULL DEFAULT '[]',
                metadata      TEXT    NOT NULL DEFAULT '{}',
                memory_layer  TEXT    NOT NULL,
                content_hash  TEXT,
                since_commit  TEXT,
                since_index   INTEGER,
                until_commit  TEXT,
                until_index   INTEGER
            );

            CREATE TABLE IF NOT EXISTS edges (
                id                TEXT    PRIMARY KEY,
                edge_type         INTEGER NOT NULL,
                node_ids          TEXT    NOT NULL DEFAULT '[]',
                roles             TEXT    NOT NULL DEFAULT '{}',
                languages         TEXT    NOT NULL DEFAULT '[]',
                weight            REAL    NOT NULL DEFAULT 1.0,
                metadata          TEXT    NOT NULL DEFAULT '{}',
                semantic_relation TEXT    NOT NULL,
                memory_layer      TEXT    NOT NULL,
                since_commit      TEXT,
                since_index       INTEGER,
                until_commit      TEXT,
                until_index       INTEGER
            );

            CREATE TABLE IF NOT EXISTS edge_nodes (
                edge_id  TEXT NOT NULL,
                node_id  TEXT NOT NULL,
                PRIMARY KEY (edge_id, node_id),
                FOREIGN KEY (edge_id) REFERENCES edges(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS expertise (
                id              TEXT    PRIMARY KEY,
                kind            INTEGER NOT NULL,
                title           TEXT    NOT NULL,
                body            TEXT    NOT NULL,
                target_node_id  TEXT    NOT NULL,
                scope           TEXT    NOT NULL DEFAULT 'exact',
                confidence      REAL    NOT NULL DEFAULT 1.0,
                archived        INTEGER NOT NULL DEFAULT 0,
                source_agent    TEXT    NOT NULL,
                evidence        TEXT    NOT NULL DEFAULT '[]',
                embedding       BLOB,
                content_hash    TEXT    NOT NULL,
                created_at      TEXT    NOT NULL,
                updated_at      TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS commits (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                commit_sha    TEXT    NOT NULL UNIQUE,
                commit_index  INTEGER NOT NULL UNIQUE,
                created_at    TEXT    NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT    NOT NULL,
                operation   TEXT    NOT NULL,
                detail      TEXT    NOT NULL,
                created_at  TEXT    NOT NULL DEFAULT (datetime('now'))
            );

            -- Indexes per architecture.md §4.4
            CREATE INDEX IF NOT EXISTS idx_nodes_layer     ON nodes(memory_layer);
            CREATE INDEX IF NOT EXISTS idx_nodes_type      ON nodes(node_type);
            CREATE INDEX IF NOT EXISTS idx_nodes_file      ON nodes(file_path);
            CREATE INDEX IF NOT EXISTS idx_nodes_since     ON nodes(since_index);
            CREATE INDEX IF NOT EXISTS idx_edges_semantic  ON edges(semantic_relation);
            CREATE INDEX IF NOT EXISTS idx_edges_layer     ON edges(memory_layer);
            CREATE INDEX IF NOT EXISTS idx_edges_since     ON edges(since_index);
            CREATE INDEX IF NOT EXISTS idx_expertise_target   ON expertise(target_node_id);
            CREATE INDEX IF NOT EXISTS idx_expertise_kind     ON expertise(kind);
            CREATE INDEX IF NOT EXISTS idx_expertise_archived ON expertise(archived);
            CREATE INDEX IF NOT EXISTS idx_audit_session      ON audit_log(session_id);
            CREATE INDEX IF NOT EXISTS idx_edge_nodes_node    ON edge_nodes(node_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // ── FTS5 virtual tables ─────────────────────────────────────────
        await InitializeFts5Async(ct).ConfigureAwait(false);

        // ── sqlite-vss (vector similarity) ──────────────────────────────
        await InitializeVssAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates FTS5 virtual tables for full-text search on nodes and expertise,
    /// plus triggers to keep the FTS indexes synchronized with the content tables.
    /// </summary>
    private async Task InitializeFts5Async(CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
                name, node_type, file_path, scope,
                content='nodes', content_rowid='rowid'
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS expertise_fts USING fts5(
                title, body,
                content='expertise', content_rowid='rowid'
            );

            -- Triggers to keep nodes_fts in sync with nodes
            CREATE TRIGGER IF NOT EXISTS nodes_ai AFTER INSERT ON nodes BEGIN
                INSERT INTO nodes_fts(rowid, name, node_type, file_path, scope)
                VALUES (new.rowid, new.name, new.node_type, new.file_path, new.scope);
            END;

            CREATE TRIGGER IF NOT EXISTS nodes_ad AFTER DELETE ON nodes BEGIN
                INSERT INTO nodes_fts(nodes_fts, rowid, name, node_type, file_path, scope)
                VALUES ('delete', old.rowid, old.name, old.node_type, old.file_path, old.scope);
            END;

            CREATE TRIGGER IF NOT EXISTS nodes_au AFTER UPDATE ON nodes BEGIN
                INSERT INTO nodes_fts(nodes_fts, rowid, name, node_type, file_path, scope)
                VALUES ('delete', old.rowid, old.name, old.node_type, old.file_path, old.scope);
                INSERT INTO nodes_fts(rowid, name, node_type, file_path, scope)
                VALUES (new.rowid, new.name, new.node_type, new.file_path, new.scope);
            END;

            -- Triggers to keep expertise_fts in sync with expertise
            CREATE TRIGGER IF NOT EXISTS expertise_ai AFTER INSERT ON expertise BEGIN
                INSERT INTO expertise_fts(rowid, title, body)
                VALUES (new.rowid, new.title, new.body);
            END;

            CREATE TRIGGER IF NOT EXISTS expertise_ad AFTER DELETE ON expertise BEGIN
                INSERT INTO expertise_fts(expertise_fts, rowid, title, body)
                VALUES ('delete', old.rowid, old.title, old.body);
            END;

            CREATE TRIGGER IF NOT EXISTS expertise_au AFTER UPDATE ON expertise BEGIN
                INSERT INTO expertise_fts(expertise_fts, rowid, title, body)
                VALUES ('delete', old.rowid, old.title, old.body);
                INSERT INTO expertise_fts(rowid, title, body)
                VALUES (new.rowid, new.title, new.body);
            END;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Rebuild FTS indexes so pre-existing rows (e.g. opening an older DB) are indexed.
        using var rebuildCmd = _connection.CreateCommand();
        rebuildCmd.CommandText = """
            INSERT INTO nodes_fts(nodes_fts) VALUES('rebuild');
            INSERT INTO expertise_fts(expertise_fts) VALUES('rebuild');
            """;
        await rebuildCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to load the sqlite-vss extension and create the
    /// <c>vss_nodes</c> virtual table for vector similarity search.
    /// Falls back to a plain <c>vector_index</c> table when the extension
    /// is not available at runtime.
    /// </summary>
    private async Task InitializeVssAsync(CancellationToken ct)
    {
        // Always create the plain vector_index fallback table for embedding storage
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS vector_index (
                    node_id    TEXT PRIMARY KEY,
                    embedding  BLOB NOT NULL,
                    dimension  INTEGER NOT NULL,
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Attempt to load sqlite-vss (vector0 + vss0) and create the virtual table.
        // This is capability-gated: VssAvailable reflects whether it succeeded.
        try
        {
            _connection.EnableExtensions(true);
            _connection.LoadExtension("vector0");
            _connection.LoadExtension("vss0");

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS vss_nodes USING vss0(
                    embedding(384)
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _vssAvailable = true;
        }
        catch (SqliteException)
        {
            // sqlite-vss extension not available — vector search will use
            // the plain vector_index table with application-level cosine similarity.
            _vssAvailable = false;
        }
    }

    // ── Node queries ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex,
        NodeFilter? filter,
        int limit,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        var sql = "SELECT * FROM nodes WHERE memory_layer = @layer";
        cmd.Parameters.AddWithValue("@layer", memoryLayer);

        if (atCommitIndex.HasValue)
        {
            // Required temporal predicate per architecture.md §4.3:
            // NULL since_index = External boundary node → always included unconditionally.
            // Non-null since_index → apply half-open interval [since, until).
            sql += " AND (since_index IS NULL" +
                   " OR (since_index <= @target AND (until_index IS NULL OR until_index > @target)))";
            cmd.Parameters.AddWithValue("@target", atCommitIndex.Value);
        }
        else
        {
            // No temporal target: return only active (non-retired) nodes.
            sql += " AND until_index IS NULL";
        }

        if (filter is not null)
        {
            if (filter.NodeType is not null)
            {
                sql += " AND node_type = @nodeType";
                cmd.Parameters.AddWithValue("@nodeType", filter.NodeType);
            }

            if (filter.Language is not null)
            {
                sql += " AND language = @language";
                cmd.Parameters.AddWithValue("@language", filter.Language);
            }

            if (filter.FilePathPrefix is not null)
            {
                sql += " AND file_path LIKE @filePrefix";
                cmd.Parameters.AddWithValue("@filePrefix", filter.FilePathPrefix + "%");
            }

            if (filter.NodeIds is { Count: > 0 })
            {
                var placeholders = new List<string>();
                for (int i = 0; i < filter.NodeIds.Count; i++)
                {
                    var param = $"@nid{i}";
                    placeholders.Add(param);
                    cmd.Parameters.AddWithValue(param, filter.NodeIds[i]);
                }

                sql += $" AND id IN ({string.Join(",", placeholders)})";
            }
        }

        sql += " LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        cmd.CommandText = sql;

        var results = new List<MemoryNode>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMemoryNode(reader));
        }

        return results;
    }

    // ── Edge queries ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEdge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer,
        int? atCommitIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        var sql = "SELECT * FROM edges WHERE semantic_relation = @rel";
        cmd.Parameters.AddWithValue("@rel", semanticRelation);

        if (memoryLayer is not null)
        {
            sql += " AND memory_layer = @layer";
            cmd.Parameters.AddWithValue("@layer", memoryLayer);
        }

        if (atCommitIndex.HasValue)
        {
            sql += " AND since_index <= @target" +
                   " AND (until_index IS NULL OR until_index > @target)";
            cmd.Parameters.AddWithValue("@target", atCommitIndex.Value);
        }
        else
        {
            // No temporal target: return only active (non-retired) edges.
            sql += " AND until_index IS NULL";
        }

        sql += " LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        cmd.CommandText = sql;

        var results = new List<MemoryEdge>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMemoryEdge(reader));
        }

        return results;
    }

    // ── Node mutations ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(version);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string memoryLayer = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) && ml is not null
                ? ml.ToString()!
                : "unknown";

            string? contentHash = node.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var ch) && ch is not null
                ? ch.ToString()
                : null;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO nodes (id, name, node_type, language, file_path, scope,
                    line_start, line_end, modifiers, metadata, memory_layer, content_hash,
                    since_commit, since_index, until_commit, until_index)
                VALUES (@id, @name, @nodeType, @language, @filePath, @scope,
                    @lineStart, @lineEnd, @modifiers, @metadata, @memoryLayer, @contentHash,
                    @sinceCommit, @sinceIndex, @untilCommit, @untilIndex)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    node_type = excluded.node_type,
                    language = excluded.language,
                    file_path = excluded.file_path,
                    scope = excluded.scope,
                    line_start = excluded.line_start,
                    line_end = excluded.line_end,
                    modifiers = excluded.modifiers,
                    metadata = excluded.metadata,
                    memory_layer = excluded.memory_layer,
                    content_hash = excluded.content_hash,
                    since_commit = excluded.since_commit,
                    since_index = excluded.since_index,
                    until_commit = excluded.until_commit,
                    until_index = excluded.until_index
                """;

            cmd.Parameters.AddWithValue("@id", node.Id);
            cmd.Parameters.AddWithValue("@name", node.Name);
            cmd.Parameters.AddWithValue("@nodeType", node.NodeType);
            cmd.Parameters.AddWithValue("@language", node.Language);
            cmd.Parameters.AddWithValue("@filePath", node.FilePath);
            cmd.Parameters.AddWithValue("@scope", node.Scope);
            cmd.Parameters.AddWithValue("@lineStart", node.LineStart);
            cmd.Parameters.AddWithValue("@lineEnd", node.LineEnd);
            cmd.Parameters.AddWithValue("@modifiers", JsonSerializer.Serialize(node.Modifiers, JsonOptions));
            cmd.Parameters.AddWithValue("@metadata", SerializeMetadata(node.Metadata));
            cmd.Parameters.AddWithValue("@memoryLayer", memoryLayer);
            cmd.Parameters.AddWithValue("@contentHash", (object?)contentHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sinceCommit", (object?)version.Since ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sinceIndex", (object?)version.SinceIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@untilCommit", (object?)version.Until ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@untilIndex", (object?)version.UntilIndex ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                // Retire the node
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = untilCommit is not null
                        ? "UPDATE nodes SET until_index = @until, until_commit = @untilCommit WHERE id = @id"
                        : "UPDATE nodes SET until_index = @until WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", nodeId);
                    cmd.Parameters.AddWithValue("@until", untilCommitIndex);
                    if (untilCommit is not null)
                    {
                        cmd.Parameters.AddWithValue("@untilCommit", untilCommit);
                    }

                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Cascade: retire all connected edges whose lifetime extends
                // past the node's retirement. Edges already retired earlier
                // (until_index <= @until) keep their original value.
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        UPDATE edges SET until_index = @until
                        WHERE id IN (
                            SELECT edge_id FROM edge_nodes WHERE node_id = @nodeId
                        )
                        AND (until_index IS NULL OR until_index > @until)
                        """;
                    cmd.Parameters.AddWithValue("@nodeId", nodeId);
                    cmd.Parameters.AddWithValue("@until", untilCommitIndex);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Edge mutations ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        string? untilCommit,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE edges SET until_index = @until WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", edgeId);
            cmd.Parameters.AddWithValue("@until", untilCommitIndex);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read existing metadata, merge, write back
            using var readCmd = _connection.CreateCommand();
            readCmd.CommandText = "SELECT metadata FROM edges WHERE id = @id";
            readCmd.Parameters.AddWithValue("@id", edgeId);

            var existingJson = (string?)await readCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var existing = existingJson is not null
                ? DeserializeMetadata(existingJson)
                : new Dictionary<string, object?>();

            foreach (var kvp in metadata)
            {
                existing[kvp.Key] = kvp.Value;
            }

            using var writeCmd = _connection.CreateCommand();
            writeCmd.CommandText = "UPDATE edges SET metadata = @metadata WHERE id = @id";
            writeCmd.Parameters.AddWithValue("@id", edgeId);
            writeCmd.Parameters.AddWithValue("@metadata", SerializeMetadata(existing));
            await writeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertEdgeAsync(
        Hyperedge edge,
        VersionRange version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(version);

        string semanticRelation = edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) && sr is not null
            ? sr.ToString()!
            : "unknown";

        string memoryLayer = edge.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) && ml is not null
            ? ml.ToString()!
            : "unknown";

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT INTO edges (id, edge_type, node_ids, roles, languages, weight,
                            metadata, semantic_relation, memory_layer,
                            since_commit, since_index, until_commit, until_index)
                        VALUES (@id, @edgeType, @nodeIds, @roles, @languages, @weight,
                            @metadata, @semanticRelation, @memoryLayer,
                            @sinceCommit, @sinceIndex, @untilCommit, @untilIndex)
                        ON CONFLICT(id) DO UPDATE SET
                            edge_type = excluded.edge_type,
                            node_ids = excluded.node_ids,
                            roles = excluded.roles,
                            languages = excluded.languages,
                            weight = excluded.weight,
                            metadata = excluded.metadata,
                            semantic_relation = excluded.semantic_relation,
                            memory_layer = excluded.memory_layer,
                            since_commit = excluded.since_commit,
                            since_index = excluded.since_index,
                            until_commit = excluded.until_commit,
                            until_index = excluded.until_index
                        """;

                    cmd.Parameters.AddWithValue("@id", edge.Id);
                    cmd.Parameters.AddWithValue("@edgeType", (int)edge.EdgeType);
                    cmd.Parameters.AddWithValue("@nodeIds", JsonSerializer.Serialize(edge.NodeIds, JsonOptions));
                    cmd.Parameters.AddWithValue("@roles", JsonSerializer.Serialize(edge.Roles, JsonOptions));
                    cmd.Parameters.AddWithValue("@languages", JsonSerializer.Serialize(edge.Languages, JsonOptions));
                    cmd.Parameters.AddWithValue("@weight", edge.Weight);
                    cmd.Parameters.AddWithValue("@metadata", SerializeMetadata(edge.Metadata));
                    cmd.Parameters.AddWithValue("@semanticRelation", semanticRelation);
                    cmd.Parameters.AddWithValue("@memoryLayer", memoryLayer);
                    cmd.Parameters.AddWithValue("@sinceCommit", (object?)version.Since ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@sinceIndex", (object?)version.SinceIndex ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@untilCommit", (object?)version.Until ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@untilIndex", (object?)version.UntilIndex ?? DBNull.Value);

                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Populate edge_nodes join table: delete old, re-insert
                using (var delCmd = _connection.CreateCommand())
                {
                    delCmd.Transaction = transaction;
                    delCmd.CommandText = "DELETE FROM edge_nodes WHERE edge_id = @edgeId";
                    delCmd.Parameters.AddWithValue("@edgeId", edge.Id);
                    await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach (var nodeId in edge.NodeIds.Distinct())
                {
                    using var insertCmd = _connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = "INSERT INTO edge_nodes (edge_id, node_id) VALUES (@edgeId, @nodeId)";
                    insertCmd.Parameters.AddWithValue("@edgeId", edge.Id);
                    insertCmd.Parameters.AddWithValue("@nodeId", nodeId);
                    await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Expertise ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM expertise
            WHERE target_node_id = @targetNodeId
              AND archived = 0
              AND confidence >= @minConfidence
            """;
        cmd.Parameters.AddWithValue("@targetNodeId", targetNodeId);
        cmd.Parameters.AddWithValue("@minConfidence", minConfidence);

        var results = new List<ExpertiseEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadExpertiseEntry(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO expertise (id, kind, title, body, target_node_id, scope,
                    confidence, archived, source_agent, evidence, embedding,
                    content_hash, created_at, updated_at)
                VALUES (@id, @kind, @title, @body, @targetNodeId, @scope,
                    @confidence, @archived, @sourceAgent, @evidence, @embedding,
                    @contentHash, @createdAt, @updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    title = excluded.title,
                    body = excluded.body,
                    target_node_id = excluded.target_node_id,
                    scope = excluded.scope,
                    confidence = excluded.confidence,
                    archived = excluded.archived,
                    source_agent = excluded.source_agent,
                    evidence = excluded.evidence,
                    embedding = excluded.embedding,
                    content_hash = excluded.content_hash,
                    updated_at = excluded.updated_at
                """;

            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@kind", (int)entry.Kind);
            cmd.Parameters.AddWithValue("@title", entry.Title);
            cmd.Parameters.AddWithValue("@body", entry.Body);
            cmd.Parameters.AddWithValue("@targetNodeId", entry.TargetNodeId);
            cmd.Parameters.AddWithValue("@scope", entry.Scope);
            cmd.Parameters.AddWithValue("@confidence", entry.Confidence);
            cmd.Parameters.AddWithValue("@archived", entry.Archived ? 1 : 0);
            cmd.Parameters.AddWithValue("@sourceAgent", entry.SourceAgent);
            cmd.Parameters.AddWithValue("@evidence", JsonSerializer.Serialize(entry.Evidence, JsonOptions));
            cmd.Parameters.AddWithValue("@embedding", entry.Embedding is not null
                ? (object)SerializeEmbedding(entry.Embedding)
                : DBNull.Value);
            cmd.Parameters.AddWithValue("@contentHash", entry.ContentHash);
            cmd.Parameters.AddWithValue("@createdAt", entry.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@updatedAt", entry.UpdatedAt.ToString("o"));

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE expertise SET archived = 1 WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", expertiseId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId,
        int limit,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        var sql = "SELECT * FROM expertise WHERE archived = 1";

        if (targetNodeId is not null)
        {
            sql += " AND target_node_id = @targetNodeId";
            cmd.Parameters.AddWithValue("@targetNodeId", targetNodeId);
        }

        sql += " LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        cmd.CommandText = sql;

        var results = new List<ExpertiseEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadExpertiseEntry(reader));
        }

        return results;
    }

    // ── Diagnostics ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken cancellationToken)
    {
        var layerStats = new Dictionary<string, LayerStatistics>();

        // Node counts by layer (active only: until_index IS NULL)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT memory_layer, COUNT(*) as cnt
                FROM nodes
                WHERE until_index IS NULL
                GROUP BY memory_layer
                """;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var layer = reader.GetString(0);
                var count = reader.GetInt32(1);
                layerStats[layer] = new LayerStatistics(count, 0);
            }
        }

        // Edge counts by layer (active only)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT memory_layer, COUNT(*) as cnt
                FROM edges
                WHERE until_index IS NULL
                GROUP BY memory_layer
                """;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var layer = reader.GetString(0);
                var edgeCount = reader.GetInt32(1);
                if (layerStats.TryGetValue(layer, out var existing))
                {
                    layerStats[layer] = new LayerStatistics(existing.NodeCount, edgeCount);
                }
                else
                {
                    layerStats[layer] = new LayerStatistics(0, edgeCount);
                }
            }
        }

        int totalNodes = 0, totalEdges = 0, totalExpertise = 0, archivedExpertise = 0;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE until_index IS NULL";
            totalNodes = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM edges WHERE until_index IS NULL";
            totalEdges = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM expertise WHERE archived = 0";
            totalExpertise = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM expertise WHERE archived = 1";
            archivedExpertise = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        DateTimeOffset? lastUpdated = null;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT MAX(created_at) FROM audit_log";
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is string ts && DateTimeOffset.TryParse(ts, out var parsed))
            {
                lastUpdated = parsed;
            }
        }

        return new MemoryGraphStatistics
        {
            LayerStats = layerStats,
            TotalNodes = totalNodes,
            TotalEdges = totalEdges,
            TotalExpertiseEntries = totalExpertise,
            ArchivedExpertiseEntries = archivedExpertise,
            LastUpdated = lastUpdated,
        };
    }

    /// <inheritdoc />
    public async Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_log (session_id, operation, detail, created_at)
                VALUES (@sessionId, @operation, @detail, @createdAt)
                """;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@operation", operation);
            cmd.Parameters.AddWithValue("@detail", JsonSerializer.Serialize(detail, JsonOptions));
            cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── FTS5 Full-Text Search ───────────────────────────────────────────

    /// <summary>
    /// Searches the FTS5 <c>nodes_fts</c> index for active nodes matching the query.
    /// Uses FTS5 MATCH syntax (e.g. "MyClass", "src/Foo*", "Class OR Method").
    /// Only returns nodes that are not retired (<c>until_index IS NULL</c>).
    /// </summary>
    public async Task<IReadOnlyList<MemoryNode>> SearchNodesFtsAsync(
        string ftsQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT n.* FROM nodes n
            INNER JOIN nodes_fts f ON n.rowid = f.rowid
            WHERE nodes_fts MATCH @query
              AND n.until_index IS NULL
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<MemoryNode>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMemoryNode(reader));
        }

        return results;
    }

    /// <summary>
    /// Searches the FTS5 <c>expertise_fts</c> index for non-archived expertise matching the query.
    /// </summary>
    public async Task<IReadOnlyList<ExpertiseEntry>> SearchExpertiseFtsAsync(
        string ftsQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.* FROM expertise e
            INNER JOIN expertise_fts f ON e.rowid = f.rowid
            WHERE expertise_fts MATCH @query
              AND e.archived = 0
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ExpertiseEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadExpertiseEntry(reader));
        }

        return results;
    }

    // ── Vector Index ────────────────────────────────────────────────────

    /// <summary>
    /// Stores or updates the embedding vector for a node in the
    /// <c>vector_index</c> table and, when the sqlite-vss extension
    /// is available, also in the <c>vss_nodes</c> virtual table.
    /// </summary>
    public async Task UpsertVectorAsync(
        string nodeId,
        float[] embedding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Always store in the fallback vector_index table
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO vector_index (node_id, embedding, dimension, updated_at)
                    VALUES (@nodeId, @embedding, @dimension, @updatedAt)
                    ON CONFLICT(node_id) DO UPDATE SET
                        embedding = excluded.embedding,
                        dimension = excluded.dimension,
                        updated_at = excluded.updated_at
                    """;
                cmd.Parameters.AddWithValue("@nodeId", nodeId);
                cmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(embedding));
                cmd.Parameters.AddWithValue("@dimension", embedding.Length);
                cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // When sqlite-vss is loaded, also upsert into the vss0 virtual table.
            // vss0 uses integer rowid, so we read the vector_index rowid as the key.
            if (_vssAvailable)
            {
                long rowid;
                using (var rowidCmd = _connection.CreateCommand())
                {
                    rowidCmd.CommandText = "SELECT rowid FROM vector_index WHERE node_id = @nodeId";
                    rowidCmd.Parameters.AddWithValue("@nodeId", nodeId);
                    rowid = (long)(await rowidCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                }

                using var vssCmd = _connection.CreateCommand();
                vssCmd.CommandText = """
                    INSERT OR REPLACE INTO vss_nodes(rowid, embedding)
                    VALUES (@rowid, @embedding)
                    """;
                vssCmd.Parameters.AddWithValue("@rowid", rowid);
                vssCmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(embedding));
                await vssCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Retrieves the embedding vector for a node from the <c>vector_index</c> table.
    /// Returns null if no embedding is stored for the given node.
    /// </summary>
    public async Task<float[]?> GetVectorAsync(
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM vector_index WHERE node_id = @nodeId";
        cmd.Parameters.AddWithValue("@nodeId", nodeId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is byte[] bytes)
        {
            return DeserializeEmbedding(bytes);
        }

        return null;
    }

    /// <summary>
    /// Finds the <paramref name="topK"/> most similar node embeddings to the
    /// given <paramref name="queryEmbedding"/>. When the sqlite-vss extension
    /// is loaded, uses the <c>vss_nodes</c> virtual table for hardware-accelerated
    /// vector search. Otherwise, falls back to brute-force cosine similarity
    /// over the <c>vector_index</c> table.
    /// </summary>
    public async Task<IReadOnlyList<(string NodeId, double Score)>> SearchSimilarVectorsAsync(
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        if (_vssAvailable)
        {
            return await SearchVssAsync(queryEmbedding, topK, cancellationToken).ConfigureAwait(false);
        }

        return await SearchBruteForceAsync(queryEmbedding, topK, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<(string NodeId, double Score)>> SearchVssAsync(
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT vi.node_id, v.distance
            FROM vss_nodes v
            INNER JOIN vector_index vi ON vi.rowid = v.rowid
            WHERE vss_search(v.embedding, vss_search_params(@query, @topK))
            """;
        cmd.Parameters.AddWithValue("@query", SerializeEmbedding(queryEmbedding));
        cmd.Parameters.AddWithValue("@topK", topK);

        var results = new List<(string NodeId, double Score)>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var nodeId = reader.GetString(0);
            var distance = reader.GetDouble(1);
            results.Add((nodeId, 1.0 - distance));
        }

        return results;
    }

    private async Task<IReadOnlyList<(string NodeId, double Score)>> SearchBruteForceAsync(
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT node_id, embedding FROM vector_index";

        var scored = new List<(string NodeId, double Score)>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var nodeId = reader.GetString(0);
            var stored = DeserializeEmbedding((byte[])reader.GetValue(1));
            var score = CosineSimilarity(queryEmbedding, stored);
            scored.Add((nodeId, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(topK).ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-12 ? 0.0 : dot / denom;
    }

    /// <summary>
    /// Exposes the underlying <see cref="SqliteConnection"/> for raw SQL
    /// operations in tests (e.g. schema verification). Not intended for
    /// production use outside of diagnostics.
    /// </summary>
    internal SqliteConnection Connection => _connection;

    // ── IAsyncDisposable ────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // ── Row readers ─────────────────────────────────────────────────────

    private static MemoryNode ReadMemoryNode(SqliteDataReader reader)
    {
        var metadata = DeserializeMetadata(reader.GetString(reader.GetOrdinal("metadata")));

        // Reconstruct temporal metadata from columns into the metadata dict
        // so MemoryNode's constructor (which calls VersionRangeHelper) works correctly.
        var sinceCommit = reader.IsDBNull(reader.GetOrdinal("since_commit"))
            ? null : reader.GetString(reader.GetOrdinal("since_commit"));
        var sinceIndexOrd = reader.GetOrdinal("since_index");
        int? sinceIndex = reader.IsDBNull(sinceIndexOrd) ? null : reader.GetInt32(sinceIndexOrd);
        var untilCommit = reader.IsDBNull(reader.GetOrdinal("until_commit"))
            ? null : reader.GetString(reader.GetOrdinal("until_commit"));
        var untilIndexOrd = reader.GetOrdinal("until_index");
        int? untilIndex = reader.IsDBNull(untilIndexOrd) ? null : reader.GetInt32(untilIndexOrd);

        if (sinceCommit is not null)
        {
            metadata[MemoryMetadataKeys.SinceCommit] = sinceCommit;
        }

        if (sinceIndex.HasValue)
        {
            metadata[MemoryMetadataKeys.SinceIndex] = sinceIndex.Value;
        }

        if (untilCommit is not null)
        {
            metadata[MemoryMetadataKeys.UntilCommit] = untilCommit;
        }

        if (untilIndex.HasValue)
        {
            metadata[MemoryMetadataKeys.UntilIndex] = untilIndex.Value;
        }

        // Ensure MemoryLayer and ContentHash are in metadata
        metadata[MemoryMetadataKeys.MemoryLayer] = reader.GetString(reader.GetOrdinal("memory_layer"));
        var contentHashOrd = reader.GetOrdinal("content_hash");
        if (!reader.IsDBNull(contentHashOrd))
        {
            metadata[MemoryMetadataKeys.ContentHash] = reader.GetString(contentHashOrd);
        }

        var node = new HypergraphNode
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            NodeType = reader.GetString(reader.GetOrdinal("node_type")),
            Language = reader.GetString(reader.GetOrdinal("language")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            Scope = reader.GetString(reader.GetOrdinal("scope")),
            LineStart = reader.GetInt32(reader.GetOrdinal("line_start")),
            LineEnd = reader.GetInt32(reader.GetOrdinal("line_end")),
            Modifiers = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(reader.GetOrdinal("modifiers")), JsonOptions) ?? [],
            Metadata = metadata,
        };

        return new MemoryNode(node);
    }

    private static MemoryEdge ReadMemoryEdge(SqliteDataReader reader)
    {
        var metadata = DeserializeMetadata(reader.GetString(reader.GetOrdinal("metadata")));

        // Reconstruct temporal + relation metadata
        metadata[MemoryMetadataKeys.SemanticRelation] = reader.GetString(reader.GetOrdinal("semantic_relation"));
        metadata[MemoryMetadataKeys.MemoryLayer] = reader.GetString(reader.GetOrdinal("memory_layer"));

        var sinceCommitOrd = reader.GetOrdinal("since_commit");
        var sinceIndexOrd = reader.GetOrdinal("since_index");
        string? sinceCommit = reader.IsDBNull(sinceCommitOrd) ? null : reader.GetString(sinceCommitOrd);
        int? sinceIndex = reader.IsDBNull(sinceIndexOrd) ? null : reader.GetInt32(sinceIndexOrd);

        if (sinceCommit is not null)
        {
            metadata[MemoryMetadataKeys.SinceCommit] = sinceCommit;
        }

        if (sinceIndex.HasValue)
        {
            metadata[MemoryMetadataKeys.SinceIndex] = sinceIndex.Value;
        }

        var untilCommitOrd = reader.GetOrdinal("until_commit");
        var untilIndexOrd = reader.GetOrdinal("until_index");
        string? untilCommit = reader.IsDBNull(untilCommitOrd) ? null : reader.GetString(untilCommitOrd);
        int? untilIndex = reader.IsDBNull(untilIndexOrd) ? null : reader.GetInt32(untilIndexOrd);

        if (untilCommit is not null)
        {
            metadata[MemoryMetadataKeys.UntilCommit] = untilCommit;
        }

        if (untilIndex.HasValue)
        {
            metadata[MemoryMetadataKeys.UntilIndex] = untilIndex.Value;
        }

        var edge = new Hyperedge
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            EdgeType = (HyperedgeType)reader.GetInt32(reader.GetOrdinal("edge_type")),
            NodeIds = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(reader.GetOrdinal("node_ids")), JsonOptions) ?? [],
            Roles = JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.GetString(reader.GetOrdinal("roles")), JsonOptions) ?? [],
            Languages = JsonSerializer.Deserialize<List<string>>(
                reader.GetString(reader.GetOrdinal("languages")), JsonOptions) ?? [],
            Weight = reader.GetDouble(reader.GetOrdinal("weight")),
            Metadata = metadata,
        };

        var version = new VersionRange(sinceCommit, sinceIndex, untilCommit, untilIndex);
        return new MemoryEdge(edge, version);
    }

    private static ExpertiseEntry ReadExpertiseEntry(SqliteDataReader reader)
    {
        var kind = (ExpertiseKind)reader.GetInt32(reader.GetOrdinal("kind"));
        var title = reader.GetString(reader.GetOrdinal("title"));
        var body = reader.GetString(reader.GetOrdinal("body"));
        var targetNodeId = reader.GetString(reader.GetOrdinal("target_node_id"));
        var sourceAgent = reader.GetString(reader.GetOrdinal("source_agent"));

        var evidence = JsonSerializer.Deserialize<List<Citation>>(
            reader.GetString(reader.GetOrdinal("evidence")), JsonOptions) ?? [];

        float[]? embedding = null;
        var embeddingOrd = reader.GetOrdinal("embedding");
        if (!reader.IsDBNull(embeddingOrd))
        {
            embedding = DeserializeEmbedding((byte[])reader.GetValue(embeddingOrd));
        }

        return new ExpertiseEntry(kind, title, body, targetNodeId, sourceAgent)
        {
            Scope = reader.GetString(reader.GetOrdinal("scope")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            Archived = reader.GetInt32(reader.GetOrdinal("archived")) != 0,
            Evidence = evidence,
            Embedding = embedding,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        };
    }

    // ── Serialization helpers ───────────────────────────────────────────

    private static string SerializeMetadata(Dictionary<string, object?> metadata)
    {
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static Dictionary<string, object?> DeserializeMetadata(string json)
    {
        var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = MaterializeJsonElement(prop.Value);
        }

        return dict;
    }

    /// <summary>
    /// Recursively converts a <see cref="JsonElement"/> to CLR primitives,
    /// avoiding the round-trip problem where <c>System.Text.Json</c> would
    /// return <see cref="JsonElement"/> instances in a <c>Dictionary&lt;string, object?&gt;</c>.
    /// </summary>
    private static object? MaterializeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i
                                 : element.TryGetInt64(out var l) ? l
                                 : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(MaterializeJsonElement)
                .ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => MaterializeJsonElement(p.Value)),
            _ => element.GetRawText(),
        };
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
