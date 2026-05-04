namespace CodeIntelligence.GraphMemory.Schema;

/// <summary>
/// A node in the code knowledge graph representing a code entity.
/// 
/// <para><b>Canonical ID format</b> (by kind):</para>
/// <list type="bullet">
///   <item><c>repo:{repo-name}</c></item>
///   <item><c>module:{repo}:{dotted.namespace}</c></item>
///   <item><c>file:{repo}:{forward/slash/path}</c></item>
///   <item><c>class:{repo}:{qualified.name}</c></item>
///   <item><c>function:{repo}:{qualified.name}:{signature-hash}</c></item>
///   <item><c>variable:{repo}:{qualified.owner}:{name}</c></item>
/// </list>
/// </summary>
public sealed record GraphNode
{
    /// <summary>
    /// Stable, deterministic identifier following the canonical ID scheme.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The kind of code entity this node represents.</summary>
    public required NodeKind Kind { get; init; }

    /// <summary>Short display name (e.g. class name, function name).</summary>
    public required string Name { get; init; }

    /// <summary>Fully qualified name including namespace / module path.</summary>
    public string? QualifiedName { get; init; }

    /// <summary>Optional source location for this symbol.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>Language of the source code (e.g. "csharp", "typescript").</summary>
    public string? Language { get; init; }

    /// <summary>Extensible property bag for producer-specific metadata.</summary>
    public PropertyBag Properties { get; init; } = new();

    /// <summary>When this node was first observed.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this node was last refreshed or mutated.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
