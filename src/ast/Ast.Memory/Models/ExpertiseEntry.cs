// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Agent-learned expertise attached to a graph node.
/// Per architecture.md §4.2.
/// </summary>
public class ExpertiseEntry
{
    /// <summary>
    /// Deterministic ID in the format "expertise:{kind}:{hash8}".
    /// </summary>
    public string Id { get; }

    public ExpertiseKind Kind { get; }
    public string Title { get; }
    public string Body { get; }
    public string TargetNodeId { get; }

    private string scope = "exact";

    public string Scope
    {
        get => this.scope;
        init
        {
            if (value is not ("exact" or "subtree"))
            {
                throw new ArgumentException($"Scope must be \"exact\" or \"subtree\", got \"{value}\".", nameof(this.Scope));
            }

            this.scope = value;
        }
    }

    private double confidence = 1.0;

    public double Confidence
    {
        get => this.confidence;
        init
        {
            if (value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(this.Confidence), value, "Confidence must be between 0.0 and 1.0.");
            }

            this.confidence = value;
        }
    }

    public bool Archived { get; init; }
    public string SourceAgent { get; }
    public IReadOnlyList<Citation> Evidence { get; init; } = Array.Empty<Citation>();
    public float[]? Embedding { get; init; }

    /// <summary>SHA-256 hex of Title + Body + TargetNodeId (per architecture.md §4.2, A-18).</summary>
    public string ContentHash { get; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public ExpertiseEntry(ExpertiseKind kind, string title, string body, string targetNodeId, string sourceAgent)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(body);
        ArgumentException.ThrowIfNullOrEmpty(targetNodeId);
        ArgumentException.ThrowIfNullOrEmpty(sourceAgent);

        this.Kind = kind;
        this.Title = title;
        this.Body = body;
        this.TargetNodeId = targetNodeId;
        this.SourceAgent = sourceAgent;

        var idHash = ComputeHash8(title + body + targetNodeId);
        this.Id = $"expertise:{kind.ToString().ToLowerInvariant()}:{idHash}";
        this.ContentHash = ComputeFullHash(title + body + targetNodeId);
    }

    /// <summary>
    /// Creates a copy with updated confidence and timestamp.
    /// </summary>
    public ExpertiseEntry WithConfidence(double newConfidence)
    {
        return new ExpertiseEntry(this.Kind, this.Title, this.Body, this.TargetNodeId, this.SourceAgent)
        {
            Confidence = Math.Clamp(newConfidence, 0.0, 1.0),
            Archived = this.Archived,
            Evidence = this.Evidence,
            Embedding = this.Embedding,
            Scope = this.Scope,
            CreatedAt = this.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
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