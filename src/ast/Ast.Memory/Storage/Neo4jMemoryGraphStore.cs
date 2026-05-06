// -----------------------------------------------------------------------
// <copyright file="Neo4jMemoryGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Storage;

using System.Text.Json;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Evolution;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

/// <summary>
/// Production <see cref="IMemoryGraphStore"/> backed by Neo4j.
/// Extends the canonical <see cref="IGraphStore"/> (delegated to the inner store)
/// with memory-specific Cypher queriesfor semantic-relation filtering and
/// metadata round-tripping. Bridges canonical CALLS edges (created by
/// <c>Neo4jGraphStore</c> using <c>:CALLS</c> relationship labels) with
/// memory-layer queries by matching both Neo4j relationship type labels AND
/// the <c>semanticRelation</c> property.
/// Per architecture.md §3.2.1 — team / CI mode.
/// </summary>
public sealed class Neo4jMemoryGraphStore : IMemoryGraphStore
{
    private readonly IGraphStore innerStore;
    private readonly IDriver driver;
    private readonly Neo4jMemoryStoreOptions options;
    private readonly ILogger<Neo4jMemoryGraphStore> logger;
    private bool disposed;

    public Neo4jMemoryGraphStore(
        IGraphStore innerStore,
        IDriver driver,
        IOptions<Neo4jMemoryStoreOptions> options,
        ILogger<Neo4jMemoryGraphStore> logger)
    {
        this.innerStore = innerStore;
        this.driver = driver;
        this.options = options.Value;
        this.logger = logger;
    }

    // ── IGraphStore delegation ───────────────────────────────────────────

    /// <inheritdoc/>
    public string Name => this.innerStore.Name;

    /// <inheritdoc/>
    public Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default) =>
        this.innerStore.CreateNodeAsync(node, cancellationToken);

    /// <inheritdoc/>
    public Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default) =>
        this.innerStore.CreateNodesAsync(nodes, cancellationToken);

    /// <inheritdoc/>
    public Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default) =>
        this.innerStore.CreateEdgeAsync(edge, cancellationToken);

    /// <inheritdoc/>
    public Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default) =>
        this.innerStore.CreateEdgesAsync(edges, cancellationToken);

    /// <inheritdoc/>
    public Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default) =>
        this.innerStore.GetNodeAsync(nodeId, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(NodeFilter? filter = null, int limit = 100, CancellationToken cancellationToken = default) =>
        this.innerStore.GetNodesAsync(filter, limit, cancellationToken);

    /// <inheritdoc/>
    public Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default) =>
        this.innerStore.GetEdgeAsync(edgeId, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, IEnumerable<HyperedgeType>? edgeTypes = null, CancellationToken cancellationToken = default) =>
        this.innerStore.GetEdgesForNodeAsync(nodeId, edgeTypes, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<GraphPath>> FindPathsAsync(string startNodeId, string endNodeId, int maxDepth = 5, CancellationToken cancellationToken = default) =>
        this.innerStore.FindPathsAsync(startNodeId, endNodeId, maxDepth, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default) =>
        this.innerStore.GetCallersAsync(nodeId, depth, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(string nodeId, int depth = 1, CancellationToken cancellationToken = default) =>
        this.innerStore.GetCalleesAsync(nodeId, depth, cancellationToken);

    /// <inheritdoc/>
    public Task<ImpactAnalysis> AnalyzeImpactAsync(string nodeId, int maxDepth = 3, CancellationToken cancellationToken = default) =>
        this.innerStore.AnalyzeImpactAsync(nodeId, maxDepth, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default) =>
        this.innerStore.DeleteNodeAsync(nodeId, cancellationToken);

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        this.innerStore.ClearAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        this.innerStore.GetStatisticsAsync(cancellationToken);

    /// <inheritdoc/>
    public Task CreateIndexesAsync(CancellationToken cancellationToken = default) =>
        this.innerStore.CreateIndexesAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        this.innerStore.IsAvailableAsync(cancellationToken);

    // ── IMemoryGraphStore-specific operations ────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HypergraphNode>> GetAllNodesAsync(
        string? nodeType = null,
        CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var cypher = nodeType is not null
            ? "MATCH (n:CodeNode {nodeType: $nodeType}) RETURN n"
            : "MATCH (n:CodeNode) RETURN n";

        var parameters = nodeType is not null
            ? new Dictionary<string, object> { ["nodeType"] = nodeType }
            : new Dictionary<string, object>();

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        var nodes = new List<HypergraphNode>();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, parameters);
            return await cursor.ToListAsync();
        });

        foreach (var record in result)
        {
            var node = record["n"].As<INode>();
            nodes.Add(this.MapNode(node));
        }

        return nodes;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bridges canonical CALLS edges: matches edges by BOTH the
    /// <c>r.semanticRelation</c> property (memory-layer edges) AND the Neo4j
    /// relationship type label (canonical AST edges created by <c>Neo4jGraphStore</c>).
    /// For example, querying "CALLS" also matches <c>:CALLS</c> relationship types.
    /// </remarks>
    public async Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        IEnumerable<string> semanticRelations,
        CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var relations = semanticRelations.ToList();
        var neo4jRelTypes = SemanticRelationMap.ToNeo4jRelationshipTypes(relations).ToList();

        // Bridge: match by Neo4j relationship type label OR semanticRelation property
        var cypher = @"
            MATCH (a:CodeNode)-[r]->(b:CodeNode)
            WHERE type(r) IN $relTypes OR r.semanticRelation IN $relations
            RETURN r, a.id AS sourceId, b.id AS targetId, type(r) AS relType";

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        var edges = new List<Hyperedge>();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher,
                new Dictionary<string, object>
                {
                    ["relations"] = relations,
                    ["relTypes"] = neo4jRelTypes
                });
            return await cursor.ToListAsync();
        });

        var seenIds = new HashSet<string>();
        foreach (var record in result)
        {
            var rel = record["r"].As<IRelationship>();
            var sourceId = record["sourceId"].As<string>();
            var targetId = record["targetId"].As<string>();
            var edge = this.MapRelationship(rel, sourceId, targetId);

            if (seenIds.Add(edge.Id))
            {
                edges.Add(edge);
            }
        }

        return edges;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Hyperedge>> GetEdgesByTypeAsync(
        IEnumerable<HyperedgeType> edgeTypes,
        CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var typeStrings = edgeTypes.Select(t => t.ToString()).ToList();
        var cypher = @"
            MATCH (a:CodeNode)-[r]->(b:CodeNode)
            WHERE r.edgeType IN $edgeTypes
            RETURN r, a.id AS sourceId, b.id AS targetId";

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        var edges = new List<Hyperedge>();
        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher,
                new Dictionary<string, object> { ["edgeTypes"] = typeStrings });
            return await cursor.ToListAsync();
        });

        foreach (var record in result)
        {
            var rel = record["r"].As<IRelationship>();
            var sourceId = record["sourceId"].As<string>();
            var targetId = record["targetId"].As<string>();
            edges.Add(this.MapRelationship(rel, sourceId, targetId));
        }

        return edges;
    }

    /// <inheritdoc/>
    public async Task UpsertNodeAsync(HypergraphNode node, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var metadataJson = JsonSerializer.Serialize(node.Metadata);

        var cypher = @"
            MERGE (n:CodeNode {id: $id})
            SET n.name = $name,
                n.nodeType = $nodeType,
                n.language = $language,
                n.filePath = $filePath,
                n.scope = $scope,
                n.lineStart = $lineStart,
                n.lineEnd = $lineEnd,
                n.modifiers = $modifiers,
                n.metadata = $metadata";

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(cypher, new Dictionary<string, object>
            {
                ["id"] = node.Id,
                ["name"] = node.Name,
                ["nodeType"] = node.NodeType,
                ["language"] = node.Language,
                ["filePath"] = node.FilePath,
                ["scope"] = node.Scope,
                ["lineStart"] = node.LineStart,
                ["lineEnd"] = node.LineEnd,
                ["modifiers"] = node.Modifiers,
                ["metadata"] = metadataJson
            });
        });
    }

    /// <inheritdoc/>
    public async Task UpsertEdgeAsync(Hyperedge edge, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        if (edge.NodeIds.Count < 2)
        {
            return;
        }

        var metadataJson = JsonSerializer.Serialize(edge.Metadata);
        var rolesJson = JsonSerializer.Serialize(edge.Roles);
        var semanticRelation = edge.Metadata.TryGetValue("SemanticRelation", out var sr)
            ? sr?.ToString() ?? string.Empty
            : string.Empty;

        var cypher = @"
            MATCH (a:CodeNode {id: $sourceId}), (b:CodeNode {id: $targetId})
            MERGE (a)-[r:MEMORY_EDGE {id: $edgeId}]->(b)
            SET r.edgeType = $edgeType,
                r.weight = $weight,
                r.semanticRelation = $semanticRelation,
                r.metadata = $metadata,
                r.roles = $roles";

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(cypher, new Dictionary<string, object>
            {
                ["sourceId"] = edge.NodeIds[0],
                ["targetId"] = edge.NodeIds[1],
                ["edgeId"] = edge.Id,
                ["edgeType"] = edge.EdgeType.ToString(),
                ["weight"] = edge.Weight,
                ["semanticRelation"] = semanticRelation,
                ["metadata"] = metadataJson,
                ["roles"] = rolesJson
            });
        });
    }

    /// <inheritdoc/>
    public async Task RemoveEdgesBySemanticRelationAsync(
        string semanticRelation,
        CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var cypher = @"
            MATCH ()-[r]-()
            WHERE r.semanticRelation = $relation
            DELETE r";

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(cypher,
                new Dictionary<string, object> { ["relation"] = semanticRelation });
        });
    }

    // ── §5.1 Layer-filtered queries ─────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<HypergraphNode>> GetNodesByLayerAsync(
        string memoryLayer,
        int? atCommitIndex = null,
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        // Minimal stub — full temporal filtering in Extraction Pipeline workstream
        this.logger.LogDebug("GetNodesByLayerAsync stub invoked for layer {Layer}", memoryLayer);
        IReadOnlyList<HypergraphNode> result = [];
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Hyperedge>> GetEdgesBySemanticRelationAsync(
        string semanticRelation,
        string? memoryLayer = null,
        int? atCommitIndex = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        // Delegate to the multi-relation overload, then apply layer filter
        var edges = await this.GetEdgesBySemanticRelationAsync(
            new[] { semanticRelation }, ct);

        IEnumerable<Hyperedge> filtered = edges;
        if (memoryLayer is not null)
        {
            filtered = filtered.Where(e =>
                e.Metadata.TryGetValue("MemoryLayer", out var ml) &&
                ml is string s && s == memoryLayer);
        }

        return filtered.Take(limit).ToList();
    }

    // ── §5.1 Temporal-aware upsert and retirement ────────────────────────

    /// <inheritdoc/>
    public async Task UpsertNodeAsync(
        HypergraphNode node,
        VersionRange version,
        CancellationToken ct = default)
    {
        // Delegates to non-temporal upsert — full temporal logic in Extraction Pipeline workstream
        node.Metadata["SinceCommit"] = version.SinceCommit;
        node.Metadata["SinceIndex"] = version.SinceIndex;
        if (version.UntilCommit is not null)
        {
            node.Metadata["UntilCommit"] = version.UntilCommit;
        }

        if (version.UntilIndex is not null)
        {
            node.Metadata["UntilIndex"] = version.UntilIndex;
        }

        await this.UpsertNodeAsync(node, ct);
    }

    /// <inheritdoc/>
    public Task RetireNodeAsync(
        string nodeId,
        int untilCommitIndex,
        CancellationToken ct = default)
    {
        // Stub — full cascade logic in Incremental Sync workstream
        this.logger.LogDebug("RetireNodeAsync stub invoked for {NodeId}", nodeId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RetireEdgeAsync(
        string edgeId,
        int untilCommitIndex,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Incremental Sync workstream
        this.logger.LogDebug("RetireEdgeAsync stub invoked for {EdgeId}", edgeId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateEdgeMetadataAsync(
        string edgeId,
        IDictionary<string, object?> updates,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Evolution Engine workstream
        this.logger.LogDebug("UpdateEdgeMetadataAsync stub invoked for {EdgeId}", edgeId);
        return Task.CompletedTask;
    }

    // ── §5.1 Expertise CRUD ─────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExpertiseEntry>> GetExpertiseForNodeAsync(
        string targetNodeId,
        double minConfidence = 0.0,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Expertise Write-Back workstream (Stage 5.1)
        this.logger.LogDebug("GetExpertiseForNodeAsync stub invoked for {NodeId}", targetNodeId);
        IReadOnlyList<ExpertiseEntry> result = [];
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task UpsertExpertiseAsync(
        ExpertiseEntry entry,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Expertise Write-Back workstream (Stage 5.1)
        this.logger.LogDebug("UpsertExpertiseAsync stub invoked for {Id}", entry.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ArchiveExpertiseAsync(
        string expertiseId,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Expertise Write-Back workstream (Stage 5.1)
        this.logger.LogDebug("ArchiveExpertiseAsync stub invoked for {Id}", expertiseId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExpertiseEntry>> GetArchivedExpertiseAsync(
        string? targetNodeId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        // Stub — full implementation in Expertise Write-Back workstream (Stage 5.1)
        IReadOnlyList<ExpertiseEntry> result = [];
        return Task.FromResult(result);
    }

    // ── §5.1 Statistics and audit ───────────────────────────────────────

    /// <inheritdoc/>
    public async Task<MemoryGraphStatistics> GetMemoryStatisticsAsync(
        CancellationToken ct = default)
    {
        // Minimal implementation — queries basic node/edge counts
        this.ThrowIfDisposed();

        var stats = await this.innerStore.GetStatisticsAsync(ct);
        return new MemoryGraphStatistics
        {
            TotalNodes = (int)stats.TotalNodes,
            TotalEdges = (int)stats.TotalEdges
        };
    }

    /// <inheritdoc/>
    public Task LogAuditAsync(
        string sessionId,
        string operation,
        object detail,
        CancellationToken ct = default)
    {
        // Stub — logs to ILogger instead of persistent audit_log table
        this.logger.LogInformation(
            "Audit [{Session}] {Operation}: {Detail}",
            sessionId, operation, detail);
        return Task.CompletedTask;
    }

    // ── Community-detection helpers (active in Stage 5.4) ────────────────

    /// <inheritdoc/>
    public async Task RemoveNodesByTypeAsync(string nodeType, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var cypher = @"
            MATCH (n:CodeNode {nodeType: $nodeType})
            DETACH DELETE n";

        await using var session = this.driver.AsyncSession(
            o => o.WithDatabase(this.options.Database));

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(cypher,
                new Dictionary<string, object> { ["nodeType"] = nodeType });
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            await this.innerStore.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            this.innerStore.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }

    private HypergraphNode MapNode(INode neoNode)
    {
        var props = neoNode.Properties;
        var metadata = DeserializeMetadata(props, "metadata", this.logger,
            props.GetValueOrDefault("id", "unknown")?.ToString() ?? "unknown");

        return new HypergraphNode
        {
            Id = props.GetValueOrDefault("id", string.Empty)?.ToString() ?? string.Empty,
            Name = props.GetValueOrDefault("name", string.Empty)?.ToString() ?? string.Empty,
            NodeType = props.GetValueOrDefault("nodeType", string.Empty)?.ToString() ?? string.Empty,
            Language = props.GetValueOrDefault("language", string.Empty)?.ToString() ?? string.Empty,
            FilePath = props.GetValueOrDefault("filePath", string.Empty)?.ToString() ?? string.Empty,
            Scope = props.GetValueOrDefault("scope", string.Empty)?.ToString() ?? string.Empty,
            LineStart = Convert.ToInt32(props.GetValueOrDefault("lineStart", 0)),
            LineEnd = Convert.ToInt32(props.GetValueOrDefault("lineEnd", 0)),
            Metadata = metadata
        };
    }

    private Hyperedge MapRelationship(IRelationship rel, string sourceId, string targetId)
    {
        var props = rel.Properties;
        var metadata = DeserializeMetadata(props, "metadata", this.logger, rel.ElementId);
        var roles = DeserializeRoles(props, this.logger, rel.ElementId);

        var edgeTypeStr = props.GetValueOrDefault("edgeType", "MemoryRelation")?.ToString()
                          ?? "MemoryRelation";
        _ = Enum.TryParse<HyperedgeType>(edgeTypeStr, out var edgeType);

        return new Hyperedge
        {
            Id = props.GetValueOrDefault("id", rel.ElementId)?.ToString() ?? rel.ElementId,
            EdgeType = edgeType,
            NodeIds = [sourceId, targetId],
            Roles = roles,
            Weight = Convert.ToDouble(props.GetValueOrDefault("weight", 1.0)),
            Metadata = metadata
        };
    }

    /// <summary>
    /// Deserializes a JSON metadata string from Neo4j properties into a
    /// <c>Dictionary&lt;string, object?&gt;</c>, materializing <see cref="JsonElement"/>
    /// values to native .NET types (string, int, double, bool) so that downstream
    /// comparisons like <c>Metadata["MemoryLayer"] == "semantic"</c> work correctly
    /// after a Neo4j round-trip.
    /// </summary>
    internal static Dictionary<string, object?> DeserializeMetadata(
        IReadOnlyDictionary<string, object> props,
        string propertyKey,
        ILogger logger,
        string entityId)
    {
        var metadata = new Dictionary<string, object?>();
        if (!props.TryGetValue(propertyKey, out var metaJson) || metaJson is not string json)
        {
            return metadata;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (parsed is not null)
            {
                foreach (var kv in parsed)
                {
                    metadata[kv.Key] = MaterializeJsonElement(kv.Value);
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to deserialize metadata JSON for entity {EntityId}; metadata will be empty",
                entityId);
        }

        return metadata;
    }

    /// <summary>
    /// Deserializes the roles JSON property from Neo4j relationship properties.
    /// </summary>
    private static Dictionary<string, string> DeserializeRoles(
        IReadOnlyDictionary<string, object> props,
        ILogger logger,
        string entityId)
    {
        var roles = new Dictionary<string, string>();
        if (!props.TryGetValue("roles", out var rolesJson) || rolesJson is not string rJson)
        {
            return roles;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(rJson);
            if (parsed is not null)
            {
                foreach (var kv in parsed)
                {
                    roles[kv.Key] = kv.Value;
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to deserialize roles JSON on relationship {RelId}; edge roles will be empty",
                entityId);
        }

        return roles;
    }

    /// <summary>
    /// Converts a <see cref="JsonElement"/> to its natural .NET type so that
    /// metadata values survive Neo4j JSON round-trips as proper string/int/double/bool
    /// instead of <see cref="JsonElement"/> boxes.
    /// </summary>
    internal static object? MaterializeJsonElement(JsonElement element)
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
            JsonValueKind.Undefined => null,
            // For arrays/objects, preserve the raw JSON string
            _ => element.GetRawText()
        };
    }
}

/// <summary>
/// Configuration for the Neo4j memory graph store.
/// </summary>
public class Neo4jMemoryStoreOptions
{
    /// <summary>Neo4j connection URI.</summary>
    public string Uri { get; set; } = "bolt://localhost:7687";

    /// <summary>Neo4j username.</summary>
    public string Username { get; set; } = "neo4j";

    /// <summary>Neo4j password.</summary>
    public string Password { get; set; } = "password";

    /// <summary>Neo4j database name.</summary>
    public string Database { get; set; } = "neo4j";
}