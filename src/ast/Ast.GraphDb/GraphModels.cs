// -----------------------------------------------------------------------
// <copyright file="GraphModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb;

using Ast.Core;

/// <summary>
/// Filter criteria for querying nodes.
/// </summary>
public class NodeFilter
{
    /// <summary>
    /// Gets or sets the language to filter by.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the node types to filter by.
    /// </summary>
    public IEnumerable<string>? NodeTypes { get; set; }

    /// <summary>
    /// Gets or sets the file path pattern to filter by.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets or sets the name pattern to filter by (supports wildcards).
    /// </summary>
    public string? NamePattern { get; set; }

    /// <summary>
    /// Gets or sets the scope to filter by.
    /// </summary>
    public string? Scope { get; set; }
}

/// <summary>
/// Represents a path in the graph.
/// </summary>
public class GraphPath
{
    /// <summary>
    /// Gets or sets the nodes in the path (in order).
    /// </summary>
    public List<HypergraphNode> Nodes { get; set; } = [];

    /// <summary>
    /// Gets or sets the edges connecting the nodes.
    /// </summary>
    public List<Hyperedge> Edges { get; set; } = [];

    /// <summary>
    /// Gets the length of the path (number of edges).
    /// </summary>
    public int Length => this.Edges.Count;

    /// <summary>
    /// Gets whether this path crosses language boundaries.
    /// </summary>
    public bool IsCrossLanguage => this.Nodes.Select(n => n.Language).Distinct().Count() > 1;
}

/// <summary>
/// A node with its relationship depth from a source.
/// </summary>
public class NodeWithDepth
{
    /// <summary>
    /// Gets or sets the node.
    /// </summary>
    public required HypergraphNode Node { get; set; }

    /// <summary>
    /// Gets or sets the depth (number of hops from source).
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Gets or sets the relationship type that connects this node.
    /// </summary>
    public HyperedgeType RelationshipType { get; set; }

    /// <summary>
    /// Gets or sets the edge ID that connects this node.
    /// </summary>
    public string? EdgeId { get; set; }
}

/// <summary>
/// Result of impact analysis.
/// </summary>
public class ImpactAnalysis
{
    /// <summary>
    /// Gets or sets the analyzed node.
    /// </summary>
    public required HypergraphNode SourceNode { get; set; }

    /// <summary>
    /// Gets or sets directly affected nodes (depth 1).
    /// </summary>
    public List<NodeWithDepth> DirectlyAffected { get; set; } = [];

    /// <summary>
    /// Gets or sets indirectly affected nodes (depth > 1).
    /// </summary>
    public List<NodeWithDepth> IndirectlyAffected { get; set; } = [];

    /// <summary>
    /// Gets the total number of affected nodes.
    /// </summary>
    public int TotalAffected => this.DirectlyAffected.Count + this.IndirectlyAffected.Count;

    /// <summary>
    /// Gets or sets affected files.
    /// </summary>
    public HashSet<string> AffectedFiles { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the change crosses language boundaries.
    /// </summary>
    public bool CrossLanguageImpact { get; set; }

    /// <summary>
    /// Gets or sets a breakdown of affected nodes by type.
    /// </summary>
    public Dictionary<string, int> AffectedByType { get; set; } = [];
}

/// <summary>
/// Graph statistics.
/// </summary>
public class GraphStatistics
{
    /// <summary>
    /// Gets or sets the total number of nodes.
    /// </summary>
    public long TotalNodes { get; set; }

    /// <summary>
    /// Gets or sets the total number of edges.
    /// </summary>
    public long TotalEdges { get; set; }

    /// <summary>
    /// Gets or sets nodes by language.
    /// </summary>
    public Dictionary<string, long> NodesByLanguage { get; set; } = [];

    /// <summary>
    /// Gets or sets nodes by type.
    /// </summary>
    public Dictionary<string, long> NodesByType { get; set; } = [];

    /// <summary>
    /// Gets or sets edges by type.
    /// </summary>
    public Dictionary<string, long> EdgesByType { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of cross-language edges.
    /// </summary>
    public long CrossLanguageEdges { get; set; }

    /// <summary>
    /// Gets or sets the number of unique files.
    /// </summary>
    public long UniqueFiles { get; set; }
}

/// <summary>
/// Configuration options for the graph database.
/// </summary>
public class GraphStoreOptions
{
    /// <summary>
    /// Gets or sets the Neo4j connection URI.
    /// </summary>
    public string Uri { get; set; } = "bolt://localhost:7687";

    /// <summary>
    /// Gets or sets the Neo4j username.
    /// </summary>
    public string Username { get; set; } = "neo4j";

    /// <summary>
    /// Gets or sets the Neo4j password.
    /// </summary>
    public string Password { get; set; } = "password";

    /// <summary>
    /// Gets or sets the database name.
    /// </summary>
    public string Database { get; set; } = "neo4j";

    /// <summary>
    /// Gets or sets the maximum connection pool size.
    /// </summary>
    public int MaxConnectionPoolSize { get; set; } = 50;

    /// <summary>
    /// Gets or sets the connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the query timeout in seconds.
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets whether to encrypt connections.
    /// </summary>
    public bool Encrypted { get; set; } = false;

    /// <summary>
    /// Gets or sets the batch size for bulk operations.
    /// </summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Neo4j node labels used in the graph.
/// </summary>
public static class NodeLabels
{
    public const string CodeNode = "CodeNode";
    public const string Class = "Class";
    public const string Interface = "Interface";
    public const string Method = "Method";
    public const string Function = "Function";
    public const string Property = "Property";
    public const string Variable = "Variable";
    public const string File = "File";
}

/// <summary>
/// Neo4j relationship types used in the graph.
/// </summary>
public static class RelationshipTypes
{
    public const string Calls = "CALLS";
    public const string Implements = "IMPLEMENTS";
    public const string Extends = "EXTENDS";
    public const string Uses = "USES";
    public const string Contains = "CONTAINS";
    public const string DependsOn = "DEPENDS_ON";
    public const string Imports = "IMPORTS";
    public const string DefinesIn = "DEFINES_IN";
    public const string FlowsTo = "FLOWS_TO";
    public const string CatchesException = "CATCHES_EXCEPTION";
}