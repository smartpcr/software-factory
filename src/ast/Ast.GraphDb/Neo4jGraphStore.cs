// -----------------------------------------------------------------------
// <copyright file="Neo4jGraphStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Ast.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

/// <summary>
/// Neo4j implementation of the graph store.
/// </summary>
public sealed class Neo4jGraphStore : IGraphStore
{
    private readonly ILogger<Neo4jGraphStore> logger;
    private readonly GraphStoreOptions options;
    private readonly IDriver driver;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Neo4jGraphStore"/> class.
    /// </summary>
    /// <param name="options">The graph store options.</param>
    /// <param name="logger">The logger.</param>
    public Neo4jGraphStore(
        IOptions<GraphStoreOptions> options,
        ILogger<Neo4jGraphStore> logger)
    {
        this.options = options.Value;
        this.logger = logger;

        this.driver = GraphDatabase.Driver(
            this.options.Uri,
            AuthTokens.Basic(this.options.Username, this.options.Password),
            builder => builder
                .WithMaxConnectionPoolSize(this.options.MaxConnectionPoolSize)
                .WithConnectionTimeout(TimeSpan.FromSeconds(this.options.ConnectionTimeoutSeconds))
                .WithEncryptionLevel(this.options.Encrypted ? EncryptionLevel.Encrypted : EncryptionLevel.None));

        this.logger.LogInformation(
            "Neo4jGraphStore initialized: {Uri}, Database: {Database}",
            this.options.Uri,
            this.options.Database);
    }

    /// <inheritdoc/>
    public string Name => "Neo4j";

    /// <inheritdoc/>
    public async Task CreateNodeAsync(HypergraphNode node, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = @"
            MERGE (n:CodeNode {id: $id})
            SET n.name = $name,
                n.nodeType = $nodeType,
                n.language = $language,
                n.filePath = $filePath,
                n.scope = $scope,
                n.lineStart = $lineStart,
                n.lineEnd = $lineEnd,
                n.modifiers = $modifiers
            WITH n
            CALL apoc.create.addLabels(n, [$nodeType]) YIELD node
            RETURN node";

        var parameters = new
        {
            id = node.Id,
            name = node.Name,
            nodeType = node.NodeType,
            language = node.Language,
            filePath = node.FilePath,
            scope = node.Scope,
            lineStart = node.LineStart,
            lineEnd = node.LineEnd,
            modifiers = node.Modifiers
        };

        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(query, parameters);
            });
        }
        catch (ClientException ex) when (ex.Message.Contains("apoc"))
        {
            // Fallback query without APOC
            var fallbackQuery = @"
                MERGE (n:CodeNode {id: $id})
                SET n.name = $name,
                    n.nodeType = $nodeType,
                    n.language = $language,
                    n.filePath = $filePath,
                    n.scope = $scope,
                    n.lineStart = $lineStart,
                    n.lineEnd = $lineEnd,
                    n.modifiers = $modifiers";

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(fallbackQuery, parameters);
            });
        }
    }

    /// <inheritdoc/>
    public async Task<int> CreateNodesAsync(IEnumerable<HypergraphNode> nodes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var nodeList = nodes.ToList();
        if (nodeList.Count == 0)
        {
            return 0;
        }

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = @"
            UNWIND $nodes AS node
            MERGE (n:CodeNode {id: node.id})
            SET n.name = node.name,
                n.nodeType = node.nodeType,
                n.language = node.language,
                n.filePath = node.filePath,
                n.scope = node.scope,
                n.lineStart = node.lineStart,
                n.lineEnd = node.lineEnd,
                n.modifiers = node.modifiers";

        var created = 0;

        foreach (var batch in nodeList.Chunk(this.options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new
            {
                nodes = batch.Select(n => new
                {
                    id = n.Id,
                    name = n.Name,
                    nodeType = n.NodeType,
                    language = n.Language,
                    filePath = n.FilePath,
                    scope = n.Scope,
                    lineStart = n.LineStart,
                    lineEnd = n.LineEnd,
                    modifiers = n.Modifiers
                }).ToList()
            };

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(query, parameters);
            });

            created += batch.Length;
            this.logger.LogDebug("Created {Created}/{Total} nodes", created, nodeList.Count);
        }

        return created;
    }

    /// <inheritdoc/>
    public async Task CreateEdgeAsync(Hyperedge edge, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (edge.NodeIds.Count < 2)
        {
            return;
        }

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        // Create relationships between consecutive nodes in the hyperedge
        var relType = GetRelationshipType(edge.EdgeType);

        var query = $@"
            MATCH (source:CodeNode {{id: $sourceId}})
            MATCH (target:CodeNode {{id: $targetId}})
            MERGE (source)-[r:{relType} {{edgeId: $edgeId}}]->(target)
            SET r.edgeType = $edgeType,
                r.weight = $weight,
                r.isCrossLanguage = $isCrossLanguage";

        // For hyperedges, we create edges between the first node and all others
        // This is a simplification; for true hyperedges you'd need a different model
        var sourceId = edge.NodeIds[0];

        foreach (var targetId in edge.NodeIds.Skip(1))
        {
            var parameters = new
            {
                sourceId,
                targetId,
                edgeId = edge.Id,
                edgeType = edge.EdgeType.ToString(),
                weight = edge.Weight,
                isCrossLanguage = edge.IsCrossLanguage
            };

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(query, parameters);
            });
        }
    }

    /// <inheritdoc/>
    public async Task<int> CreateEdgesAsync(IEnumerable<Hyperedge> edges, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var edgeList = edges.ToList();
        if (edgeList.Count == 0)
        {
            return 0;
        }

        var created = 0;

        foreach (var edge in edgeList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await this.CreateEdgeAsync(edge, cancellationToken);
            created++;

            if (created % 100 == 0)
            {
                this.logger.LogDebug("Created {Created}/{Total} edges", created, edgeList.Count);
            }
        }

        return created;
    }

    /// <inheritdoc/>
    public async Task<HypergraphNode?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = @"
            MATCH (n:CodeNode {id: $nodeId})
            RETURN n";

        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { nodeId });
            var record = await cursor.SingleAsync();
            return record?["n"].As<INode>();
        });

        return result != null ? MapToHypergraphNode(result) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HypergraphNode>> GetNodesAsync(
        NodeFilter? filter = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var whereClause = BuildWhereClause(filter);
        var query = $@"
            MATCH (n:CodeNode)
            {whereClause}
            RETURN n
            LIMIT $limit";

        var parameters = BuildFilterParameters(filter, limit);

        var nodes = new List<HypergraphNode>();

        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, parameters);
            await foreach (var record in cursor)
            {
                var node = record["n"].As<INode>();
                nodes.Add(MapToHypergraphNode(node));
            }
        });

        return nodes;
    }

    /// <inheritdoc/>
    public async Task<Hyperedge?> GetEdgeAsync(string edgeId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = @"
            MATCH (source:CodeNode)-[r {edgeId: $edgeId}]->(target:CodeNode)
            RETURN source.id AS sourceId, target.id AS targetId, r.edgeType AS edgeType,
                   r.weight AS weight, r.edgeId AS edgeId";

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { edgeId });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
            {
                return null;
            }

            var nodeIds = new List<string>();
            var first = records[0];

            nodeIds.Add(first["sourceId"].As<string>());
            nodeIds.AddRange(records.Select(r => r["targetId"].As<string>()));

            var edgeTypeStr = first["edgeType"].As<string>();
            Enum.TryParse<HyperedgeType>(edgeTypeStr, out var edgeType);

            return new Hyperedge
            {
                Id = edgeId,
                EdgeType = edgeType,
                NodeIds = nodeIds,
                Roles = [],
                Weight = first["weight"].As<double>()
            };
        });
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(
        string nodeId,
        IEnumerable<HyperedgeType>? edgeTypes = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var typeFilter = edgeTypes != null
            ? $"WHERE r.edgeType IN {BuildTypeList(edgeTypes)}"
            : "";

        var query = $@"
            MATCH (n:CodeNode {{id: $nodeId}})-[r]-(other:CodeNode)
            {typeFilter}
            RETURN DISTINCT r.edgeId AS edgeId, r.edgeType AS edgeType,
                   r.weight AS weight, collect(other.id) AS connectedIds";

        var edges = new List<Hyperedge>();

        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { nodeId });
            await foreach (var record in cursor)
            {
                var edgeTypeStr = record["edgeType"].As<string>();
                Enum.TryParse<HyperedgeType>(edgeTypeStr, out var edgeType);

                var connectedIds = record["connectedIds"].As<List<string>>();
                var nodeIds = new List<string> { nodeId };
                nodeIds.AddRange(connectedIds);

                edges.Add(new Hyperedge
                {
                    Id = record["edgeId"].As<string>(),
                    EdgeType = edgeType,
                    NodeIds = nodeIds,
                    Roles = [],
                    Weight = record["weight"].As<double>()
                });
            }
        });

        return edges;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        string startNodeId,
        string endNodeId,
        int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = $@"
            MATCH path = shortestPath((start:CodeNode {{id: $startId}})-[*1..{maxDepth}]-(end:CodeNode {{id: $endId}}))
            RETURN path
            LIMIT 10";

        var paths = new List<GraphPath>();

        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { startId = startNodeId, endId = endNodeId });
            await foreach (var record in cursor)
            {
                var path = record["path"].As<IPath>();
                paths.Add(MapToGraphPath(path));
            }
        });

        return paths;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NodeWithDepth>> GetCallersAsync(
        string nodeId,
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = $@"
            MATCH (caller:CodeNode)-[r:CALLS*1..{depth}]->(target:CodeNode {{id: $nodeId}})
            RETURN caller, size(r) AS depth, r[-1].edgeType AS relType, r[-1].edgeId AS edgeId";

        var results = new List<NodeWithDepth>();

        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { nodeId });
            await foreach (var record in cursor)
            {
                var callerNode = record["caller"].As<INode>();
                var nodeDepth = record["depth"].As<int>();
                var relTypeStr = record["relType"].As<string>();
                Enum.TryParse<HyperedgeType>(relTypeStr, out var relType);

                results.Add(new NodeWithDepth
                {
                    Node = MapToHypergraphNode(callerNode),
                    Depth = nodeDepth,
                    RelationshipType = relType,
                    EdgeId = record["edgeId"].As<string>()
                });
            }
        });

        return results;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NodeWithDepth>> GetCalleesAsync(
        string nodeId,
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = $@"
            MATCH (source:CodeNode {{id: $nodeId}})-[r:CALLS*1..{depth}]->(callee:CodeNode)
            RETURN callee, size(r) AS depth, r[-1].edgeType AS relType, r[-1].edgeId AS edgeId";

        var results = new List<NodeWithDepth>();

        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { nodeId });
            await foreach (var record in cursor)
            {
                var calleeNode = record["callee"].As<INode>();
                var nodeDepth = record["depth"].As<int>();
                var relTypeStr = record["relType"].As<string>();
                Enum.TryParse<HyperedgeType>(relTypeStr, out var relType);

                results.Add(new NodeWithDepth
                {
                    Node = MapToHypergraphNode(calleeNode),
                    Depth = nodeDepth,
                    RelationshipType = relType,
                    EdgeId = record["edgeId"].As<string>()
                });
            }
        });

        return results;
    }

    /// <inheritdoc/>
    public async Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId,
        int maxDepth = 3,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var sourceNode = await this.GetNodeAsync(nodeId, cancellationToken) ?? throw new ArgumentException($"Node not found: {nodeId}", nameof(nodeId));
        var callers = await this.GetCallersAsync(nodeId, maxDepth, cancellationToken);

        var analysis = new ImpactAnalysis
        {
            SourceNode = sourceNode,
            DirectlyAffected = [.. callers.Where(c => c.Depth == 1)],
            IndirectlyAffected = [.. callers.Where(c => c.Depth > 1)]
        };

        foreach (var affected in callers)
        {
            analysis.AffectedFiles.Add(affected.Node.FilePath);

            var nodeType = affected.Node.NodeType;
            if (!analysis.AffectedByType.TryGetValue(nodeType, out var value))
            {
                value = 0;
                analysis.AffectedByType[nodeType] = value;
            }
            analysis.AffectedByType[nodeType] = ++value;
        }

        analysis.CrossLanguageImpact = callers.Any(c => c.Node.Language != sourceNode.Language);

        return analysis;
    }

    /// <inheritdoc/>
    public async Task DeleteNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var query = @"
            MATCH (n:CodeNode {id: $nodeId})
            DETACH DELETE n";

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(query, new { nodeId });
        });
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.logger.LogWarning("Clearing all graph data");

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        // Delete in batches to avoid memory issues
        var query = @"
            MATCH (n:CodeNode)
            WITH n LIMIT 10000
            DETACH DELETE n
            RETURN count(*) AS deleted";

        var totalDeleted = 0L;
        long deleted;

        do
        {
            deleted = await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync(query);
                var record = await cursor.SingleAsync();
                return record["deleted"].As<long>();
            });
            totalDeleted += deleted;
            this.logger.LogDebug("Deleted {Deleted} nodes, total: {Total}", deleted, totalDeleted);
        }
        while (deleted > 0);

        this.logger.LogInformation("Cleared {Total} nodes from graph", totalDeleted);
    }

    /// <inheritdoc/>
    public async Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var stats = new GraphStatistics();

        // Get node count
        var nodeCountQuery = "MATCH (n:CodeNode) RETURN count(n) AS count";
        stats.TotalNodes = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(nodeCountQuery);
            var record = await cursor.SingleAsync();
            return record["count"].As<long>();
        });

        // Get edge count
        var edgeCountQuery = "MATCH (:CodeNode)-[r]->(:CodeNode) RETURN count(r) AS count";
        stats.TotalEdges = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(edgeCountQuery);
            var record = await cursor.SingleAsync();
            return record["count"].As<long>();
        });

        // Get nodes by language
        var byLanguageQuery = @"
            MATCH (n:CodeNode)
            RETURN n.language AS language, count(n) AS count";
        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(byLanguageQuery);
            await foreach (var record in cursor)
            {
                var lang = record["language"].As<string>() ?? "Unknown";
                stats.NodesByLanguage[lang] = record["count"].As<long>();
            }
        });

        // Get nodes by type
        var byTypeQuery = @"
            MATCH (n:CodeNode)
            RETURN n.nodeType AS nodeType, count(n) AS count";
        await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(byTypeQuery);
            await foreach (var record in cursor)
            {
                var type = record["nodeType"].As<string>() ?? "Unknown";
                stats.NodesByType[type] = record["count"].As<long>();
            }
        });

        // Get unique files
        var filesQuery = "MATCH (n:CodeNode) RETURN count(DISTINCT n.filePath) AS count";
        stats.UniqueFiles = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(filesQuery);
            var record = await cursor.SingleAsync();
            return record["count"].As<long>();
        });

        return stats;
    }

    /// <inheritdoc/>
    public async Task CreateIndexesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.logger.LogInformation("Creating indexes");

        await using var session = this.driver.AsyncSession(o => o.WithDatabase(this.options.Database));

        var indexes = new[]
        {
            "CREATE INDEX node_id_idx IF NOT EXISTS FOR (n:CodeNode) ON (n.id)",
            "CREATE INDEX node_name_idx IF NOT EXISTS FOR (n:CodeNode) ON (n.name)",
            "CREATE INDEX node_language_idx IF NOT EXISTS FOR (n:CodeNode) ON (n.language)",
            "CREATE INDEX node_type_idx IF NOT EXISTS FOR (n:CodeNode) ON (n.nodeType)",
            "CREATE INDEX node_file_idx IF NOT EXISTS FOR (n:CodeNode) ON (n.filePath)"
        };

        foreach (var indexQuery in indexes)
        {
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync(indexQuery);
                });
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to create index: {Query}", indexQuery);
            }
        }

        this.logger.LogInformation("Indexes created");
    }

    /// <inheritdoc/>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (this.disposed)
        {
            return false;
        }

        try
        {
            await this.driver.VerifyConnectivityAsync();
            return true;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Neo4j is not available at {Uri}", this.options.Uri);
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.driver?.Dispose();
        this.logger.LogInformation("Neo4jGraphStore disposed");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        if (this.driver != null)
        {
            await this.driver.DisposeAsync();
        }
        this.logger.LogInformation("Neo4jGraphStore disposed async");
    }

    private static HypergraphNode MapToHypergraphNode(INode node)
    {
        return new HypergraphNode
        {
            Id = node["id"].As<string>(),
            Name = node["name"].As<string>(),
            NodeType = node["nodeType"].As<string>(),
            Language = node["language"].As<string>(),
            FilePath = node["filePath"].As<string>(),
            Scope = node["scope"].As<string>() ?? string.Empty,
            LineStart = node["lineStart"].As<int>(),
            LineEnd = node["lineEnd"].As<int>(),
            Modifiers = node["modifiers"].As<List<string>>() ?? []
        };
    }

    private static GraphPath MapToGraphPath(IPath path)
    {
        var graphPath = new GraphPath();

        foreach (var node in path.Nodes)
        {
            graphPath.Nodes.Add(MapToHypergraphNode(node));
        }

        foreach (var rel in path.Relationships)
        {
            var edgeTypeStr = rel["edgeType"]?.As<string>() ?? "DataFlow";
            Enum.TryParse<HyperedgeType>(edgeTypeStr, out var edgeType);

            graphPath.Edges.Add(new Hyperedge
            {
                Id = rel["edgeId"]?.As<string>() ?? Guid.NewGuid().ToString(),
                EdgeType = edgeType,
                NodeIds = [],
                Roles = [],
                Weight = rel["weight"]?.As<double>() ?? 1.0
            });
        }

        return graphPath;
    }

    private static string GetRelationshipType(HyperedgeType edgeType)
    {
        return edgeType switch
        {
            HyperedgeType.MethodInvocation => RelationshipTypes.Calls,
            HyperedgeType.CmdletCall => RelationshipTypes.Calls,
            HyperedgeType.ConstructorInvocation => RelationshipTypes.Calls,
            HyperedgeType.Implements => RelationshipTypes.Implements,
            HyperedgeType.InterfaceDefinition => RelationshipTypes.Implements,
            HyperedgeType.Extends => RelationshipTypes.Extends,
            HyperedgeType.ClassDefinition => RelationshipTypes.Contains,
            HyperedgeType.ModuleImport => RelationshipTypes.Imports,
            HyperedgeType.UsingDirective => RelationshipTypes.Imports,
            HyperedgeType.DataFlow => RelationshipTypes.FlowsTo,
            HyperedgeType.DependencyInjection => RelationshipTypes.DependsOn,
            HyperedgeType.ExceptionHandling => RelationshipTypes.CatchesException,
            _ => RelationshipTypes.Uses
        };
    }

    private static string BuildWhereClause(NodeFilter? filter)
    {
        if (filter == null)
        {
            return "";
        }

        var conditions = new List<string>();

        if (!string.IsNullOrEmpty(filter.Language))
        {
            conditions.Add("n.language = $language");
        }

        if (filter.NodeTypes?.Any() == true)
        {
            conditions.Add("n.nodeType IN $nodeTypes");
        }

        if (!string.IsNullOrEmpty(filter.FilePath))
        {
            conditions.Add("n.filePath CONTAINS $filePath");
        }

        if (!string.IsNullOrEmpty(filter.NamePattern))
        {
            conditions.Add("n.name =~ $namePattern");
        }

        if (!string.IsNullOrEmpty(filter.Scope))
        {
            conditions.Add("n.scope = $scope");
        }

        return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    }

    private static Dictionary<string, object> BuildFilterParameters(NodeFilter? filter, int limit)
    {
        var parameters = new Dictionary<string, object> { ["limit"] = limit };

        if (filter == null)
        {
            return parameters;
        }

        if (!string.IsNullOrEmpty(filter.Language))
        {
            parameters["language"] = filter.Language;
        }

        if (filter.NodeTypes?.Any() == true)
        {
            parameters["nodeTypes"] = filter.NodeTypes.ToList();
        }

        if (!string.IsNullOrEmpty(filter.FilePath))
        {
            parameters["filePath"] = filter.FilePath;
        }

        if (!string.IsNullOrEmpty(filter.NamePattern))
        {
            // Convert wildcard to regex
            parameters["namePattern"] = ".*" + filter.NamePattern.Replace("*", ".*") + ".*";
        }

        if (!string.IsNullOrEmpty(filter.Scope))
        {
            parameters["scope"] = filter.Scope;
        }

        return parameters;
    }

    private static string BuildTypeList(IEnumerable<HyperedgeType> types)
    {
        var typeStrings = types.Select(t => $"'{t}'");
        return "[" + string.Join(", ", typeStrings) + "]";
    }
}