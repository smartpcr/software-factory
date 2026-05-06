// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Agent-authored knowledge node attached to a code element.
/// </summary>
public class ExpertiseEntry
{
    public string Id { get; set; } = string.Empty;

    public ExpertiseKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string TargetNodeId { get; set; } = string.Empty;

    public string Scope { get; set; } = "exact";

    public double Confidence { get; set; } = 1.0;

    public bool Archived { get; set; }

    public string SourceAgent { get; set; } = string.Empty;

    public IReadOnlyList<Citation> Evidence { get; set; } = Array.Empty<Citation>();

    public float[]? Embedding { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Generates the canonical ID for this expertise entry.
    /// </summary>
    public static string GenerateId(ExpertiseKind kind, string title, string body, string targetNodeId)
    {
        var input = title + body + targetNodeId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash8 = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"expertise:{kind.ToString().ToLowerInvariant()}:{hash8}";
    }
}