using System.Security.Cryptography;
using System.Text;

namespace Ast.Memory.Models;

/// <summary>
/// Agent-learned expertise attached to a graph node.
/// Per architecture.md §4.2.
/// </summary>
public class ExpertiseEntry
{
    /// <summary>
    /// Deterministic ID in the format "expertise:{kind}:{hash8}".
    /// Hash is the first 8 hex characters of SHA-256(Title + Body + TargetNodeId).
    /// </summary>
    public string Id { get; }

    public ExpertiseKind Kind { get; }
    public string Title { get; }
    public string Body { get; }
    public string TargetNodeId { get; }

    private string _scope = "exact";
    /// <summary>
    /// Scope: must be "exact" or "subtree". Default is "exact".
    /// </summary>
    public string Scope
    {
        get => _scope;
        init
        {
            if (value is not ("exact" or "subtree"))
                throw new ArgumentException($"Scope must be \"exact\" or \"subtree\", got \"{value}\".", nameof(Scope));
            _scope = value;
        }
    }

    private double _confidence = 1.0;
    /// <summary>
    /// Confidence score in [0.0, 1.0]. Default is 1.0.
    /// </summary>
    public double Confidence
    {
        get => _confidence;
        init
        {
            if (value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(Confidence), value, "Confidence must be between 0.0 and 1.0.");
            _confidence = value;
        }
    }

    public bool Archived { get; init; }
    public string? SourceAgent { get; init; }
    public IReadOnlyList<Citation> Evidence { get; init; } = Array.Empty<Citation>();
    public float[]? Embedding { get; init; }

    /// <summary>SHA-256 hex of Title + Body (for change detection).</summary>
    public string ContentHash { get; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ExpertiseEntry(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        if (string.IsNullOrEmpty(title))
            throw new ArgumentException("Title cannot be null or empty.", nameof(title));
        if (string.IsNullOrEmpty(body))
            throw new ArgumentException("Body cannot be null or empty.", nameof(body));
        if (string.IsNullOrEmpty(targetNodeId))
            throw new ArgumentException("TargetNodeId cannot be null or empty.", nameof(targetNodeId));

        Kind = kind;
        Title = title;
        Body = body;
        TargetNodeId = targetNodeId;

        var idHash = ComputeHash8(title + body + targetNodeId);
        Id = $"expertise:{kind.ToString().ToLowerInvariant()}:{idHash}";

        ContentHash = ComputeFullHash(title + body);
    }

    private static string ComputeHash8(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string ComputeFullHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
