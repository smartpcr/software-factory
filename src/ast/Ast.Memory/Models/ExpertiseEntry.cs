// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// An agent-authored knowledge entry attached to a code node.
/// Per architecture.md §4.2.
/// </summary>
public class ExpertiseEntry
{
    public required string Id { get; init; }
    public required ExpertiseKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string TargetNodeId { get; init; }
    public string Scope { get; init; } = "exact";
    public double Confidence { get; set; } = 1.0;
    public bool Archived { get; set; }
    public string? SourceAgent { get; init; }
    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    public float[]? Embedding { get; set; }
    public string ContentHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Generates a deterministic ID from kind, title, body, and target node.
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash8 = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }
}