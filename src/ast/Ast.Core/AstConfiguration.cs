// -----------------------------------------------------------------------
// <copyright file="AstConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core;

/// <summary>
/// Configuration options for AST parsing and hypergraph extraction.
/// </summary>
public class AstConfiguration
{
    /// <summary>
    /// Directories to exclude from processing.
    /// </summary>
    public string[] ExcludedDirectories { get; set; } =
    [
        "bin",
        "obj",
        "packages",
        ".git",
        "node_modules",
        "TestResults",
        ".vs",
        ".idea",
        "dist",
        "build"
    ];

    /// <summary>
    /// File patterns for C# files.
    /// </summary>
    public string[] CSharpFilePatterns { get; set; } = ["*.cs"];

    /// <summary>
    /// File patterns for PowerShell files.
    /// </summary>
    public string[] PowerShellFilePatterns { get; set; } = ["*.ps1", "*.psm1", "*.psd1"];

    /// <summary>
    /// Maximum file size in bytes to process. Files larger than this are skipped.
    /// Default: 1MB
    /// </summary>
    public int MaxFileSizeBytes { get; set; } = 1_000_000;

    /// <summary>
    /// Whether to enable data flow analysis (variable def-use tracking).
    /// </summary>
    public bool EnableDataFlowAnalysis { get; set; } = true;

    /// <summary>
    /// Whether to detect cross-language references (e.g., PowerShell calling C# types).
    /// </summary>
    public bool EnableCrossLanguageDetection { get; set; } = true;

    /// <summary>
    /// Whether to include source code in the extracted nodes (increases memory usage).
    /// </summary>
    public bool IncludeSourceCode { get; set; } = true;

    /// <summary>
    /// Whether to extract XML documentation comments (C#) and help comments (PowerShell).
    /// </summary>
    public bool ExtractDocumentation { get; set; } = true;

    /// <summary>
    /// Configuration for the embedding service.
    /// </summary>
    public EmbeddingConfiguration Embedding { get; set; } = new();

    /// <summary>
    /// Configuration for the graph database.
    /// </summary>
    public GraphDbConfiguration GraphDb { get; set; } = new();

    /// <summary>
    /// Configuration for the vector database.
    /// </summary>
    public VectorDbConfiguration VectorDb { get; set; } = new();
}

/// <summary>
/// Configuration for embedding generation.
/// </summary>
public class EmbeddingConfiguration
{
    /// <summary>
    /// The base URL of the embedding API endpoint.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// The embedding model name to use.
    /// </summary>
    public string ModelName { get; set; } = "nomic-embed-text";

    /// <summary>
    /// The dimension of the embedding vectors.
    /// </summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>
    /// Batch size for embedding generation.
    /// </summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Configuration for Neo4j graph database.
/// </summary>
public class GraphDbConfiguration
{
    /// <summary>
    /// The Neo4j connection URI.
    /// </summary>
    public string Uri { get; set; } = "bolt://localhost:7687";

    /// <summary>
    /// The Neo4j username.
    /// </summary>
    public string Username { get; set; } = "neo4j";

    /// <summary>
    /// The Neo4j password.
    /// </summary>
    public string Password { get; set; } = "password";

    /// <summary>
    /// The database name to use.
    /// </summary>
    public string Database { get; set; } = "neo4j";

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    public int MaxConnectionPoolSize { get; set; } = 50;
}

/// <summary>
/// Configuration for Qdrant vector database.
/// </summary>
public class VectorDbConfiguration
{
    /// <summary>
    /// The Qdrant host address.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// The Qdrant gRPC port.
    /// </summary>
    public int Port { get; set; } = 6334;

    /// <summary>
    /// The collection name for code nodes.
    /// </summary>
    public string NodesCollection { get; set; } = "code_nodes";

    /// <summary>
    /// The collection name for hyperedges.
    /// </summary>
    public string EdgesCollection { get; set; } = "code_edges";

    /// <summary>
    /// Whether to use HTTPS for connection.
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// API key for authentication (if required).
    /// </summary>
    public string? ApiKey { get; set; }
}