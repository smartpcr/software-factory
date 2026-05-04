namespace Ast.Memory.Models;

/// <summary>
/// Evidence citation for an expertise entry.
/// Per architecture.md §4.2.
/// </summary>
public record Citation(string NodeId, string? CommitSha, string? Snippet);
