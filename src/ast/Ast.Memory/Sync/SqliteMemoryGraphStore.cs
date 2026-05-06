// -----------------------------------------------------------------------
// <copyright file="SqliteMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Data.SQLite;
using System.Text.Json;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// SQLite-backed implementation of <see cref="IMemoryGraphStore"/>.
/// Stores nodes, edges, expertise entries, and audit log per architecture.md §4.4.
/// Uses WAL mode and busy timeout for concurrent access resilience.
/// </summary>
public sealed class SqliteMemoryGraphStore : IMemoryGraphStore
{
    private readonly string connectionString;
    private bool disposed;

    public SqliteMemoryGraphStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        this.connectionString = $"Data Source={dbPath};Version=3;";
        this.EnsureSchema();
    }

    public string Name => "SqliteMemoryGraphStore";

    // ─── IGraphStore: Node CRUD ───────────────────────────────────────

    public async Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InsertNodeAsync(conn, node, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        var count = 0;
        foreach (var node in nodes)
        {
            await InsertNodeAsync(conn, node, cancellationToken).ConfigureAwait(false);
            count++;
        }

        tx.Commit();
        return count;
    }

    public async Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InsertEdgeAsync(conn, edge, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        var count = 0;
        foreach (var edge in edges)
        {
            await InsertEdgeAsync(conn, edge, cancellationToken).ConfigureAwait(false);
            count++;
        }

        tx.Commit();
        return count;
    }

    public async Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadNode(reader) : null;
    }

    public async Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(
        NodeFilter? filter = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (filter?.FilePath != null)
        {
            where.Add("file_path = @filePath");
            cmd.Parameters.AddWithValue("@filePath", filter.FilePath);
        }

        if (filter?.Language != null)
        {
            where.Add("language = @language");
            cmd.Parameters.AddWithValue("@language", filter.Language);
        }

        if (filter?.Scope != null)
        {
            where.Add("scope = @scope");
            cmd.Parameters.AddWithValue("@scope", filter.Scope);
        }

        if (filter?.NamePattern != null)
        {
            where.Add("name LIKE @namePattern");
            cmd.Parameters.AddWithValue("@namePattern", filter.NamePattern.Replace('*', '%'));
        }

        if (filter?.NodeTypes?.Any() == true)
        {
            var typeList = filter.NodeTypes.ToList();
            var typePlaceholders = new List<string>();
            for (var i = 0; i < typeList.Count; i++)
            {
                var param = $"@nt{i}";
                typePlaceholders.Add(param);
                cmd.Parameters.AddWithValue(param, typeList[i]);
            }

            where.Add($"node_type IN ({string.Join(", ", typePlaceholders)})");
        }

        cmd.CommandText = $"SELECT * FROM nodes{(where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")} LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<HypergraphNode>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadNode(reader));
        }

        return results;
    }

    public async Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM edges WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", edgeId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEdge(reader) : null;
    }

    public async Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(
        string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT * FROM edges WHERE (source_id = @nodeId OR target_id = @nodeId)";
        cmd.Parameters.AddWithValue("@nodeId", nodeId);

        if (edgeTypes?.Any() == true)
        {
            var types = edgeTypes.ToList();
            var placeholders = new List<string>();
            for (var i = 0; i < types.Count; i++)
            {
                var param = $"@et{i}";
                placeholders.Add(param);
                cmd.Parameters.AddWithValue(param, types[i].ToString());
            }

            cmd.CommandText += $" AND edge_type IN ({string.Join(", ", placeholders)})";
        }

        var results = new List<Hyperedge>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadEdge(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default)
    {
        // Simple BFS for short paths — adequate for sync testing
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string nodeId, List<HypergraphNode> nodes, List<Hyperedge> edges)>();
        var startNode = await this.GetNodeAsync(startNodeId, cancellationToken).ConfigureAwait(false);
        if (startNode == null)
        {
            return [];
        }

        queue.Enqueue((startNodeId, [startNode], []));
        var paths = new List<GraphPath>();

        while (queue.Count > 0 && paths.Count < 10)
        {
            var (currentId, pathNodes, pathEdges) = queue.Dequeue();
            if (pathEdges.Count >= maxDepth)
            {
                continue;
            }

            var edges = await this.GetEdgesForNodeAsync(currentId, null, cancellationToken).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                var nextId = edge.NodeIds.FirstOrDefault(id => !string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase));
                if (nextId == null || visited.Contains(nextId))
                {
                    continue;
                }

                var nextNode = await this.GetNodeAsync(nextId, cancellationToken).ConfigureAwait(false);
                if (nextNode == null)
                {
                    continue;
                }

                var newPathNodes = new List<HypergraphNode>(pathNodes) { nextNode };
                var newPathEdges = new List<Hyperedge>(pathEdges) { edge };

                if (string.Equals(nextId, endNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(new GraphPath { Nodes = newPathNodes, Edges = newPathEdges });
                }
                else
                {
                    visited.Add(nextId);
                    queue.Enqueue((nextId, newPathNodes, newPathEdges));
                }
            }
        }

        return paths;
    }

    public async Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(
        string nodeId, int depth = 1, CancellationToken cancellationToken = default)
    {
        return await this.GetRelatedNodesAsync(nodeId, depth, isCallers: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(
        string nodeId, int depth = 1, CancellationToken cancellationToken = default)
    {
        return await this.GetRelatedNodesAsync(nodeId, depth, isCallers: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default)
    {
        var source = await this.GetNodeAsync(nodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Node {nodeId} not found");

        var callers = await this.GetCallersAsync(nodeId, maxDepth, cancellationToken).ConfigureAwait(false);
        var direct = callers.Where(c => c.Depth == 1).ToList();
        var indirect = callers.Where(c => c.Depth > 1).ToList();

        return new ImpactAnalysis
        {
            SourceNode = source,
            DirectlyAffected = direct,
            IndirectlyAffected = indirect,
            AffectedFiles = new HashSet<string>(callers.Select(c => c.Node.FilePath)),
            CrossLanguageImpact = callers.Any(c => c.Node.Language != source.Language),
            AffectedByType = callers.GroupBy(c => c.Node.NodeType).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    public async Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();

        using (var edgeCmd = conn.CreateCommand())
        {
            edgeCmd.CommandText = "DELETE FROM edges WHERE source_id = @id OR target_id = @id";
            edgeCmd.Parameters.AddWithValue("@id", nodeId);
            await edgeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var nodeCmd = conn.CreateCommand())
        {
            nodeCmd.CommandText = "DELETE FROM nodes WHERE id = @id";
            nodeCmd.Parameters.AddWithValue("@id", nodeId);
            await nodeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        tx.Commit();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM edges; DELETE FROM nodes; DELETE FROM expertise; DELETE FROM audit_log;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        var stats = new GraphStatistics();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE until_index IS NULL";
            stats.TotalNodes = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM edges WHERE until_index IS NULL";
            stats.TotalEdges = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        return stats;
    }

    public Task CreateIndexesAsync(CancellationToken cancellationToken = default)
    {
        // Indexes are created in EnsureSchema
        return Task.CompletedTask;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = await this.OpenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─── IMemoryGraphStore extensions ─────────────────────────────────

    public async Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer, int? atCommitIndex = null, NodeFilter? filter = null,
        int limit = 100, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var where = new List<string> { "memory_layer = @layer" };
        cmd.Parameters.AddWithValue("@layer", memoryLayer);

        if (atCommitIndex.HasValue)
        {
            where.Add("(since_index IS NULL OR since_index <= @idx)");
            where.Add("(until_index IS NULL OR until_index > @idx)");
            cmd.Parameters.AddWithValue("@idx", atCommitIndex.Value);
        }

        if (filter?.FilePath != null)
        {
            where.Add("file_path = @fp");
            cmd.Parameters.AddWithValue("@fp", filter.FilePath);
        }

        cmd.CommandText = $"SELECT * FROM nodes WHERE {string.Join(" AND ", where)} LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<HypergraphNode>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadNode(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation, string? memoryLayer = null, int? atCommitIndex = null,
        int limit = 100, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        var where = new List<string> { "semantic_relation = @sr" };
        cmd.Parameters.AddWithValue("@sr", semanticRelation);

        if (memoryLayer != null)
        {
            where.Add("memory_layer = @ml");
            cmd.Parameters.AddWithValue("@ml", memoryLayer);
        }

        if (atCommitIndex.HasValue)
        {
            where.Add("(since_index IS NULL OR since_index <= @idx)");
            where.Add("(until_index IS NULL OR until_index > @idx)");
            cmd.Parameters.AddWithValue("@idx", atCommitIndex.Value);
        }

        cmd.CommandText = $"SELECT * FROM edges WHERE {string.Join(" AND ", where)} LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<Hyperedge>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadEdge(reader));
        }

        return results;
    }

    public async Task UpsertNodeAsync(HypergraphNode node, VersionRange version, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);

        // Set version metadata on the node
        node.Metadata[MemoryMetadataKeys.SinceCommit] = version.SinceCommit;
        node.Metadata[MemoryMetadataKeys.SinceIndex] = version.SinceIndex;
        if (version.UntilCommit != null)
        {
            node.Metadata[MemoryMetadataKeys.UntilCommit] = version.UntilCommit;
        }

        if (version.UntilIndex != null)
        {
            node.Metadata[MemoryMetadataKeys.UntilIndex] = version.UntilIndex;
        }

        var memoryLayer = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) ? ml as string ?? "structural" : "structural";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodes (id, node_type, name, language, file_path, scope, line_start, line_end,
                               memory_layer, since_commit, since_index, until_commit, until_index, metadata)
            VALUES (@id, @nodeType, @name, @lang, @fp, @scope, @ls, @le,
                    @ml, @sc, @si, @uc, @ui, @meta)
            ON CONFLICT(id) DO UPDATE SET
                node_type = @nodeType, name = @name, language = @lang, file_path = @fp,
                scope = @scope, line_start = @ls, line_end = @le, memory_layer = @ml,
                since_commit = @sc, since_index = @si, until_commit = @uc, until_index = @ui,
                metadata = @meta";
        cmd.Parameters.AddWithValue("@id", node.Id);
        cmd.Parameters.AddWithValue("@nodeType", node.NodeType);
        cmd.Parameters.AddWithValue("@name", node.Name);
        cmd.Parameters.AddWithValue("@lang", (object?)node.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fp", (object?)node.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scope", (object?)node.Scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ls", node.LineStart);
        cmd.Parameters.AddWithValue("@le", node.LineEnd);
        cmd.Parameters.AddWithValue("@ml", memoryLayer);
        cmd.Parameters.AddWithValue("@sc", (object?)version.SinceCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@si", (object?)version.SinceIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uc", (object?)version.UntilCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ui", (object?)version.UntilIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(node.Metadata));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RetireNodeAsync(string nodeId, int untilCommitIndex, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();

        // Retire the node itself
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE nodes SET until_index = @ui WHERE id = @id AND until_index IS NULL";
            cmd.Parameters.AddWithValue("@id", nodeId);
            cmd.Parameters.AddWithValue("@ui", untilCommitIndex);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Cascade: retire all active edges connected to this node
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE edges SET until_index = @ui WHERE (source_id = @id OR target_id = @id) AND until_index IS NULL";
            cmd.Parameters.AddWithValue("@id", nodeId);
            cmd.Parameters.AddWithValue("@ui", untilCommitIndex);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Cascade: archive all active expertise targeting this node
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE expertise SET archived = 1, updated_at = @now WHERE target_node_id = @id AND archived = 0";
            cmd.Parameters.AddWithValue("@id", nodeId);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        tx.Commit();
    }

    public async Task RetireEdgeAsync(string edgeId, int untilCommitIndex, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE edges SET until_index = @ui WHERE id = @id AND until_index IS NULL";
        cmd.Parameters.AddWithValue("@id", edgeId);
        cmd.Parameters.AddWithValue("@ui", untilCommitIndex);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateEdgeMetadataAsync(string edgeId, IDictionary<string, object?> updates, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);

        // Read current metadata
        string? currentMeta;
        using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT metadata FROM edges WHERE id = @id";
            readCmd.Parameters.AddWithValue("@id", edgeId);
            currentMeta = (await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string;
        }

        var dict = string.IsNullOrEmpty(currentMeta)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(currentMeta) ?? [];

        foreach (var kv in updates)
        {
            dict[kv.Key] = kv.Value;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE edges SET metadata = @meta WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", edgeId);
        cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(dict));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId, double minConfidence = 0.0, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM expertise WHERE target_node_id = @tid AND confidence >= @mc AND archived = 0";
        cmd.Parameters.AddWithValue("@tid", targetNodeId);
        cmd.Parameters.AddWithValue("@mc", minConfidence);

        var results = new List<ExpertiseEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadExpertise(reader));
        }

        return results;
    }

    public async Task UpsertExpertiseAsync(ExpertiseEntry entry, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO expertise (id, kind, title, body, target_node_id, scope, confidence,
                                   archived, source_agent, evidence, embedding, content_hash, created_at, updated_at)
            VALUES (@id, @kind, @title, @body, @tid, @scope, @conf, @arch, @agent,
                    @evidence, @embedding, @hash, @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
                confidence = @conf, archived = @arch, updated_at = @updated,
                embedding = @embedding";
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@kind", entry.Kind.ToString());
        cmd.Parameters.AddWithValue("@title", entry.Title);
        cmd.Parameters.AddWithValue("@body", entry.Body);
        cmd.Parameters.AddWithValue("@tid", entry.TargetNodeId);
        cmd.Parameters.AddWithValue("@scope", entry.Scope);
        cmd.Parameters.AddWithValue("@conf", entry.Confidence);
        cmd.Parameters.AddWithValue("@arch", entry.Archived ? 1 : 0);
        cmd.Parameters.AddWithValue("@agent", entry.SourceAgent);
        cmd.Parameters.AddWithValue("@evidence", JsonSerializer.Serialize(entry.Evidence));
        cmd.Parameters.AddWithValue("@embedding", entry.Embedding != null ? (object)SerializeEmbedding(entry.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", entry.ContentHash);
        cmd.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", entry.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ArchiveExpertiseAsync(string expertiseId, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE expertise SET archived = 1, updated_at = @now WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", expertiseId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null, int limit = 50, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        var sql = "SELECT * FROM expertise WHERE archived = 1";
        if (targetNodeId != null)
        {
            sql += " AND target_node_id = @tid";
            cmd.Parameters.AddWithValue("@tid", targetNodeId);
        }

        sql += " LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.CommandText = sql;

        var results = new List<ExpertiseEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadExpertise(reader));
        }

        return results;
    }

    public async Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        var stats = new MemoryGraphStatistics();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE until_index IS NULL";
            stats.TotalNodes = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM edges WHERE until_index IS NULL";
            stats.TotalEdges = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM expertise WHERE archived = 0";
            stats.TotalExpertise = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        return stats;
    }

    public async Task LogAuditAsync(string sessionId, string operation, object detail, CancellationToken ct = default)
    {
        using var conn = await this.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO audit_log (session_id, timestamp, operation, detail)
            VALUES (@sid, @ts, @op, @detail)";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@op", operation);
        cmd.Parameters.AddWithValue("@detail", JsonSerializer.Serialize(detail));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ─── Private helpers ──────────────────────────────────────────────

    private async Task<IReadOnlyList<NodeWithDepth>> GetRelatedNodesAsync(
        string nodeId, int depth, bool isCallers, CancellationToken ct)
    {
        var results = new List<NodeWithDepth>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nodeId };
        var frontier = new List<string> { nodeId };

        for (var d = 1; d <= depth && frontier.Count > 0; d++)
        {
            var nextFrontier = new List<string>();
            foreach (var currentId in frontier)
            {
                var edges = await this.GetEdgesForNodeAsync(currentId, null, ct).ConfigureAwait(false);
                foreach (var edge in edges)
                {
                    // For callers: we want nodes that have this node as target
                    // For callees: we want nodes that this node calls
                    var otherId = isCallers
                        ? (edge.NodeIds.Count >= 2 && string.Equals(edge.NodeIds[1], currentId, StringComparison.OrdinalIgnoreCase) ? edge.NodeIds[0] : null)
                        : (edge.NodeIds.Count >= 2 && string.Equals(edge.NodeIds[0], currentId, StringComparison.OrdinalIgnoreCase) ? edge.NodeIds[1] : null);

                    if (otherId == null || !visited.Add(otherId))
                    {
                        continue;
                    }

                    var otherNode = await this.GetNodeAsync(otherId, ct).ConfigureAwait(false);
                    if (otherNode != null)
                    {
                        results.Add(new NodeWithDepth { Node = otherNode, Depth = d, RelationshipType = edge.EdgeType, EdgeId = edge.Id });
                        nextFrontier.Add(otherId);
                    }
                }
            }

            frontier = nextFrontier;
        }

        return results;
    }

    private async Task<SQLiteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SQLiteConnection(this.connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task InsertNodeAsync(SQLiteConnection conn, HypergraphNode node, CancellationToken ct)
    {
        var memoryLayer = node.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) ? ml as string ?? "structural" : "structural";
        var sinceCommit = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sc) ? sc as string : null;
        var sinceIndex = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si) ? si : null;
        var untilCommit = node.Metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var uc) ? uc as string : null;
        var untilIndex = node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) ? ui : null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO nodes (id, node_type, name, language, file_path, scope, line_start, line_end,
                                          memory_layer, since_commit, since_index, until_commit, until_index, metadata)
            VALUES (@id, @nodeType, @name, @lang, @fp, @scope, @ls, @le,
                    @ml, @sc, @si, @uc, @ui, @meta)";
        cmd.Parameters.AddWithValue("@id", node.Id);
        cmd.Parameters.AddWithValue("@nodeType", node.NodeType);
        cmd.Parameters.AddWithValue("@name", node.Name);
        cmd.Parameters.AddWithValue("@lang", (object?)node.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fp", (object?)node.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scope", (object?)node.Scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ls", node.LineStart);
        cmd.Parameters.AddWithValue("@le", node.LineEnd);
        cmd.Parameters.AddWithValue("@ml", memoryLayer);
        cmd.Parameters.AddWithValue("@sc", (object?)sinceCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@si", sinceIndex ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@uc", (object?)untilCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ui", untilIndex ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(node.Metadata));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertEdgeAsync(SQLiteConnection conn, Hyperedge edge, CancellationToken ct)
    {
        var semanticRelation = edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var sr) ? sr as string ?? "REFERENCES" : "REFERENCES";
        var memoryLayer = edge.Metadata.TryGetValue(MemoryMetadataKeys.MemoryLayer, out var ml) ? ml as string ?? "structural" : "structural";
        var sinceCommit = edge.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sc) ? sc as string : null;
        var sinceIndex = edge.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si) ? si : null;
        var untilCommit = edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilCommit, out var uc) ? uc as string : null;
        var untilIndex = edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) ? ui : null;

        var sourceId = edge.NodeIds.Count > 0 ? edge.NodeIds[0] : string.Empty;
        var targetId = edge.NodeIds.Count > 1 ? edge.NodeIds[1] : string.Empty;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO edges (id, edge_type, semantic_relation, memory_layer,
                                          source_id, target_id, roles_json, languages_json, weight,
                                          since_commit, since_index,
                                          until_commit, until_index, metadata)
            VALUES (@id, @et, @sr, @ml, @src, @tgt, @roles, @langs, @wt, @sc, @si, @uc, @ui, @meta)";
        cmd.Parameters.AddWithValue("@id", edge.Id);
        cmd.Parameters.AddWithValue("@et", edge.EdgeType.ToString());
        cmd.Parameters.AddWithValue("@sr", semanticRelation);
        cmd.Parameters.AddWithValue("@ml", memoryLayer);
        cmd.Parameters.AddWithValue("@src", sourceId);
        cmd.Parameters.AddWithValue("@tgt", targetId);
        cmd.Parameters.AddWithValue("@roles", JsonSerializer.Serialize(edge.Roles));
        cmd.Parameters.AddWithValue("@langs", JsonSerializer.Serialize(edge.Languages));
        cmd.Parameters.AddWithValue("@wt", edge.Weight);
        cmd.Parameters.AddWithValue("@sc", (object?)sinceCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@si", sinceIndex ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@uc", (object?)untilCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ui", untilIndex ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@meta", JsonSerializer.Serialize(edge.Metadata));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static HypergraphNode ReadNode(System.Data.Common.DbDataReader reader)
    {
        var metaJson = reader["metadata"] as string;
        var metadata = string.IsNullOrEmpty(metaJson)
            ? new Dictionary<string, object?>()
            : DeserializeMetadata(metaJson);

        // Merge column-level temporal fields into metadata for consistency.
        // Columns are authoritative over JSON-serialised metadata because
        // RetireNodeAsync / RetireEdgeAsync update columns directly.
        var untilIndex = reader["until_index"];
        if (untilIndex != null && untilIndex != DBNull.Value)
        {
            metadata[MemoryMetadataKeys.UntilIndex] = Convert.ToInt32(untilIndex);
        }

        var sinceIndex = reader["since_index"];
        if (sinceIndex != null && sinceIndex != DBNull.Value)
        {
            metadata[MemoryMetadataKeys.SinceIndex] = Convert.ToInt32(sinceIndex);
        }

        var sinceCommit = reader["since_commit"] as string;
        if (!string.IsNullOrEmpty(sinceCommit))
        {
            metadata[MemoryMetadataKeys.SinceCommit] = sinceCommit;
        }

        var untilCommit = reader["until_commit"] as string;
        if (!string.IsNullOrEmpty(untilCommit))
        {
            metadata[MemoryMetadataKeys.UntilCommit] = untilCommit;
        }

        var memoryLayer = reader["memory_layer"] as string;
        if (!string.IsNullOrEmpty(memoryLayer))
        {
            metadata[MemoryMetadataKeys.MemoryLayer] = memoryLayer;
        }

        return new HypergraphNode
        {
            Id = (string)reader["id"],
            NodeType = (string)reader["node_type"],
            Name = (string)reader["name"],
            Language = reader["language"] as string ?? string.Empty,
            FilePath = reader["file_path"] as string ?? string.Empty,
            Scope = reader["scope"] as string ?? string.Empty,
            LineStart = Convert.ToInt32(reader["line_start"]),
            LineEnd = Convert.ToInt32(reader["line_end"]),
            Metadata = metadata
        };
    }

    private static Hyperedge ReadEdge(System.Data.Common.DbDataReader reader)
    {
        var metaJson = reader["metadata"] as string;
        var metadata = string.IsNullOrEmpty(metaJson)
            ? new Dictionary<string, object?>()
            : DeserializeMetadata(metaJson);

        var untilIndex = reader["until_index"];
        if (untilIndex != null && untilIndex != DBNull.Value)
        {
            metadata[MemoryMetadataKeys.UntilIndex] = Convert.ToInt32(untilIndex);
        }

        var sinceIndex = reader["since_index"];
        if (sinceIndex != null && sinceIndex != DBNull.Value)
        {
            metadata[MemoryMetadataKeys.SinceIndex] = Convert.ToInt32(sinceIndex);
        }

        var sinceCommitEdge = reader["since_commit"] as string;
        if (!string.IsNullOrEmpty(sinceCommitEdge))
        {
            metadata[MemoryMetadataKeys.SinceCommit] = sinceCommitEdge;
        }

        var untilCommitEdge = reader["until_commit"] as string;
        if (!string.IsNullOrEmpty(untilCommitEdge))
        {
            metadata[MemoryMetadataKeys.UntilCommit] = untilCommitEdge;
        }

        var sourceId = reader["source_id"] as string ?? string.Empty;
        var targetId = reader["target_id"] as string ?? string.Empty;

        var edgeTypeStr = reader["edge_type"] as string ?? "DataFlow";
        if (!Enum.TryParse<HyperedgeType>(edgeTypeStr, out var edgeType))
        {
            edgeType = HyperedgeType.DataFlow;
        }

        // Restore roles from persisted JSON column; fall back to source/target placeholders
        var rolesJson = reader["roles_json"] as string;
        Dictionary<string, string> roles;
        if (!string.IsNullOrEmpty(rolesJson))
        {
            try
            {
                roles = JsonSerializer.Deserialize<Dictionary<string, string>>(rolesJson) ?? new Dictionary<string, string>();
            }
            catch
            {
                roles = new Dictionary<string, string> { [sourceId] = "source", [targetId] = "target" };
            }
        }
        else
        {
            roles = new Dictionary<string, string> { [sourceId] = "source", [targetId] = "target" };
        }

        // Restore languages from persisted JSON column
        var languagesJson = reader["languages_json"] as string;
        List<string> languages;
        if (!string.IsNullOrEmpty(languagesJson))
        {
            try
            {
                languages = JsonSerializer.Deserialize<List<string>>(languagesJson) ?? [];
            }
            catch
            {
                languages = [];
            }
        }
        else
        {
            languages = [];
        }

        // Restore weight from persisted column
        var weightObj = reader["weight"];
        var weight = weightObj != null && weightObj != DBNull.Value ? Convert.ToDouble(weightObj) : 1.0;

        return new Hyperedge
        {
            Id = (string)reader["id"],
            EdgeType = edgeType,
            NodeIds = [sourceId, targetId],
            Roles = roles,
            Languages = languages,
            Weight = weight,
            Metadata = metadata
        };
    }

    private static ExpertiseEntry ReadExpertise(System.Data.Common.DbDataReader reader)
    {
        var kindStr = reader["kind"] as string ?? "Pattern";
        if (!Enum.TryParse<ExpertiseKind>(kindStr, out var kind))
        {
            kind = ExpertiseKind.Pattern;
        }

        var evidenceJson = reader["evidence"] as string;
        var evidence = string.IsNullOrEmpty(evidenceJson)
            ? (IReadOnlyList<Citation>)[]
            : JsonSerializer.Deserialize<List<Citation>>(evidenceJson) ?? [];

        float[]? embedding = null;
        if (reader["embedding"] != DBNull.Value && reader["embedding"] is byte[] embBytes)
        {
            embedding = DeserializeEmbedding(embBytes);
        }

        return new ExpertiseEntry
        {
            Id = (string)reader["id"],
            Kind = kind,
            Title = (string)reader["title"],
            Body = (string)reader["body"],
            TargetNodeId = (string)reader["target_node_id"],
            Scope = (string)reader["scope"],
            Confidence = Convert.ToDouble(reader["confidence"]),
            Archived = Convert.ToInt32(reader["archived"]) != 0,
            SourceAgent = (string)reader["source_agent"],
            Evidence = evidence,
            Embedding = embedding,
            ContentHash = (string)reader["content_hash"],
            CreatedAt = DateTime.Parse((string)reader["created_at"]),
            UpdatedAt = DateTime.Parse((string)reader["updated_at"])
        };
    }

    private static Dictionary<string, object?> DeserializeMetadata(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : (object)prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText()
                };
            }

            return result;
        }
        catch
        {
            return [];
        }
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

    private void EnsureSchema()
    {
        using var conn = new SQLiteConnection(this.connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS nodes (
                id              TEXT PRIMARY KEY,
                node_type       TEXT NOT NULL,
                name            TEXT NOT NULL,
                language        TEXT,
                file_path       TEXT,
                scope           TEXT,
                line_start      INTEGER,
                line_end        INTEGER,
                memory_layer    TEXT NOT NULL DEFAULT 'structural',
                since_commit    TEXT,
                since_index     INTEGER,
                until_commit    TEXT,
                until_index     INTEGER,
                metadata        TEXT
            );

            CREATE TABLE IF NOT EXISTS edges (
                id                  TEXT PRIMARY KEY,
                edge_type           TEXT NOT NULL,
                semantic_relation   TEXT NOT NULL,
                memory_layer        TEXT NOT NULL DEFAULT 'structural',
                source_id           TEXT NOT NULL,
                target_id           TEXT NOT NULL,
                roles_json          TEXT,
                languages_json      TEXT,
                weight              REAL NOT NULL DEFAULT 1.0,
                since_commit        TEXT,
                since_index         INTEGER,
                until_commit        TEXT,
                until_index         INTEGER,
                metadata            TEXT
            );

            CREATE TABLE IF NOT EXISTS expertise (
                id              TEXT PRIMARY KEY,
                kind            TEXT NOT NULL,
                title           TEXT NOT NULL,
                body            TEXT NOT NULL,
                target_node_id  TEXT NOT NULL,
                scope           TEXT NOT NULL DEFAULT 'exact',
                confidence      REAL NOT NULL DEFAULT 1.0,
                archived        INTEGER NOT NULL DEFAULT 0,
                source_agent    TEXT NOT NULL,
                evidence        TEXT,
                embedding       BLOB,
                content_hash    TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT NOT NULL,
                timestamp   TEXT NOT NULL,
                operation   TEXT NOT NULL,
                detail      TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_nodes_layer ON nodes(memory_layer);
            CREATE INDEX IF NOT EXISTS idx_nodes_type ON nodes(node_type);
            CREATE INDEX IF NOT EXISTS idx_nodes_file ON nodes(file_path);
            CREATE INDEX IF NOT EXISTS idx_nodes_since ON nodes(since_index);
            CREATE INDEX IF NOT EXISTS idx_edges_semantic ON edges(semantic_relation);
            CREATE INDEX IF NOT EXISTS idx_edges_layer ON edges(memory_layer);
            CREATE INDEX IF NOT EXISTS idx_edges_since ON edges(since_index);
            CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_id);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON edges(target_id);
            CREATE INDEX IF NOT EXISTS idx_expertise_target ON expertise(target_node_id);
            CREATE INDEX IF NOT EXISTS idx_expertise_kind ON expertise(kind);
            CREATE INDEX IF NOT EXISTS idx_expertise_archived ON expertise(archived);
            CREATE INDEX IF NOT EXISTS idx_audit_session ON audit_log(session_id);
        ";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        this.disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        this.disposed = true;
        return ValueTask.CompletedTask;
    }
}