// -----------------------------------------------------------------------
// <copyright file="MemoryBackend.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Storage backend selection for the memory graph and vector stores.
/// </summary>
public enum MemoryBackend
{
    /// <summary>
    /// SQLite for graph store, SqliteVectorStore for vector store (local / single-user mode).
    /// Uses brute-force cosine similarity in managed code.
    /// Both graph and vector stores share the same SQLite database file specified by
    /// <see cref="MemoryOptions.SqlitePath"/> (architecture.md §3.2).
    /// </summary>
    Sqlite,

    /// <summary>
    /// Neo4j for graph store, Qdrant for vector store (team / multi-user mode).
    /// </summary>
    Neo4j
}