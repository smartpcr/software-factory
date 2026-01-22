// -----------------------------------------------------------------------
// <copyright file="VectorModels.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb;

/// <summary>
/// Represents a point in the vector database.
/// </summary>
public class VectorPoint
{
    /// <summary>
    /// Gets or sets the unique identifier for this point.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the vector embedding.
    /// </summary>
    public required float[] Vector { get; set; }

    /// <summary>
    /// Gets or sets the payload (metadata) associated with this point.
    /// </summary>
    public Dictionary<string, object> Payload { get; set; } = new();
}

/// <summary>
/// Represents a search result from the vector database.
/// </summary>
public class VectorSearchResult
{
    /// <summary>
    /// Gets or sets the point ID.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the similarity score (higher is more similar).
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    /// Gets or sets the vector (if requested).
    /// </summary>
    public float[]? Vector { get; set; }

    /// <summary>
    /// Gets or sets the payload (metadata).
    /// </summary>
    public Dictionary<string, object> Payload { get; set; } = new();
}

/// <summary>
/// Filter conditions for vector search.
/// </summary>
public class VectorFilter
{
    /// <summary>
    /// Gets or sets the must conditions (AND).
    /// </summary>
    public List<FilterCondition> Must { get; set; } = new();

    /// <summary>
    /// Gets or sets the should conditions (OR).
    /// </summary>
    public List<FilterCondition> Should { get; set; } = new();

    /// <summary>
    /// Gets or sets the must not conditions (NOT).
    /// </summary>
    public List<FilterCondition> MustNot { get; set; } = new();
}

/// <summary>
/// A single filter condition.
/// </summary>
public class FilterCondition
{
    /// <summary>
    /// Gets or sets the field name to filter on.
    /// </summary>
    public required string Field { get; set; }

    /// <summary>
    /// Gets or sets the filter operator.
    /// </summary>
    public FilterOperator Operator { get; set; } = FilterOperator.Equals;

    /// <summary>
    /// Gets or sets the value to compare against.
    /// </summary>
    public required object Value { get; set; }
}

/// <summary>
/// Filter operators.
/// </summary>
public enum FilterOperator
{
    /// <summary>Exact match.</summary>
    Equals,

    /// <summary>Not equal.</summary>
    NotEquals,

    /// <summary>Greater than.</summary>
    GreaterThan,

    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual,

    /// <summary>Less than.</summary>
    LessThan,

    /// <summary>Less than or equal.</summary>
    LessThanOrEqual,

    /// <summary>Value is in list.</summary>
    In,

    /// <summary>Value is not in list.</summary>
    NotIn,

    /// <summary>String contains.</summary>
    Contains
}

/// <summary>
/// Configuration options for the vector database.
/// </summary>
public class VectorStoreOptions
{
    /// <summary>
    /// Gets or sets the Qdrant host address.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the Qdrant HTTP port.
    /// </summary>
    public int HttpPort { get; set; } = 6333;

    /// <summary>
    /// Gets or sets the Qdrant gRPC port.
    /// </summary>
    public int GrpcPort { get; set; } = 6334;

    /// <summary>
    /// Gets or sets whether to use gRPC (true) or HTTP (false).
    /// </summary>
    public bool UseGrpc { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to use HTTPS/TLS.
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// Gets or sets the API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the default collection name for code nodes.
    /// </summary>
    public string NodesCollection { get; set; } = "code_nodes";

    /// <summary>
    /// Gets or sets the default collection name for hyperedges.
    /// </summary>
    public string EdgesCollection { get; set; } = "code_edges";

    /// <summary>
    /// Gets or sets the default vector dimensions.
    /// </summary>
    public int DefaultVectorSize { get; set; } = 768;

    /// <summary>
    /// Gets or sets the distance metric to use.
    /// </summary>
    public DistanceMetric Distance { get; set; } = DistanceMetric.Cosine;

    /// <summary>
    /// Gets or sets the request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Distance metrics for similarity calculation.
/// </summary>
public enum DistanceMetric
{
    /// <summary>Cosine similarity.</summary>
    Cosine,

    /// <summary>Euclidean distance.</summary>
    Euclidean,

    /// <summary>Dot product.</summary>
    DotProduct
}

/// <summary>
/// Represents collection information.
/// </summary>
public class CollectionInfo
{
    /// <summary>
    /// Gets or sets the collection name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the vector size.
    /// </summary>
    public int VectorSize { get; set; }

    /// <summary>
    /// Gets or sets the point count.
    /// </summary>
    public long PointCount { get; set; }

    /// <summary>
    /// Gets or sets the distance metric.
    /// </summary>
    public DistanceMetric Distance { get; set; }
}
