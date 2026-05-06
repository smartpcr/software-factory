// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Agent-authored knowledge node. Not a HypergraphNode — has its own dedicated table.
/// </summary>
public sealed class ExpertiseEntry
{
    public string Id { get; init; } = string.Empty;
    public ExpertiseKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string TargetNodeId { get; init; } = string.Empty;
    public string Scope { get; init; } = "exact";
    public double Confidence { get; set; } = 1.0;
    public bool Archived { get; set; }
    public string SourceAgent { get; init; } = string.Empty;
    public IReadOnlyList<Citation> Evidence { get; init; } = [];
    public float[]? Embedding { get; set; }
    public string ContentHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Generates a content-addressable ID from the entry's content.
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash8 = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }

    /// <summary>
    /// Generates the content hash from title, body, and target node ID.
    /// </summary>
    public static string ComputeContentHash(string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
