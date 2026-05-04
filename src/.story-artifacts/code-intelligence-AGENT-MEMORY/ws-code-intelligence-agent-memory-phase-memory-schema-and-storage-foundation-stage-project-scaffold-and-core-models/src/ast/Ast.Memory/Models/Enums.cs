namespace Ast.Memory.Models;

/// <summary>
/// Classifies the kind of expertise an agent has learned.
/// Per architecture.md §2.1.
/// </summary>
public enum ExpertiseKind
{
    Pattern,
    Pitfall,
    Preference,
    Insight
}

/// <summary>
/// Determines how memory is retrieved from the graph.
/// Per architecture.md §5.3.
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
/// Backend storage engine for memory persistence.
/// Per architecture.md §2.1.
/// </summary>
public enum MemoryBackend
{
    Sqlite,
    Neo4j
}
