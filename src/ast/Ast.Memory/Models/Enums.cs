// -----------------------------------------------------------------------
// <copyright file="Enums.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Kind of expertise entry authored by an agent.
/// </summary>
public enum ExpertiseKind
{
    /// <summary>A recurring design or implementation pattern worth reusing.</summary>
    Pattern,

    /// <summary>A known hazard, footgun, or fragile assumption.</summary>
    Pitfall,

    /// <summary>A team or codebase convention / style preference.</summary>
    Preference,

    /// <summary>A general insight or observation about code behavior.</summary>
    Insight
}

/// <summary>
/// Retrieval mode for memory queries.
/// </summary>
public enum RetrievalMode
{
    StructuralDrill,
    CallTrace,
    SemanticSearch,
    ExpertiseRecall,
    Hybrid
}

/// <summary>
/// Backend for the memory graph store.
/// </summary>
public enum MemoryBackend
{
    Sqlite,
    Neo4j
}

/// <summary>
/// Status of a file change detected by the file system watcher.
/// Explicit status avoids inferring deleted/renamed from File.Exists.
/// </summary>
public enum FileChangeStatus
{
    Modified,
    Created,
    Deleted,
    Renamed
}

/// <summary>
/// A file change entry with explicit status, used by the debounced batch processor.
/// </summary>
public record FileChangeEntry(string Path, FileChangeStatus Status);
