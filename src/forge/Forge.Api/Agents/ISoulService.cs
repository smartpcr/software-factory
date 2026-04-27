// -----------------------------------------------------------------------
// <copyright file="ISoulService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

/// <summary>
/// Phase 11.3 — soul.md filesystem source-of-truth. Default location is
/// <c>%LOCALAPPDATA%\Forge\agents\{agentId}\soul.md</c>; the
/// <see cref="Forge.Api.Persistence.AgentRow.SoulPath"/> override moves the
/// file (e.g. into a versioned soul/ folder inside a product repo). Reading
/// returns the raw markdown body plus parsed YAML front-matter capabilities.
/// </summary>
public interface ISoulService
{
    /// <summary>Resolve the on-disk path for an agent's soul.md.</summary>
    string ResolvePath(string agentId, string? overridePath);

    /// <summary>Read soul.md for an agent. Returns null if the file does not exist yet.</summary>
    Task<SoulDocument?> ReadAsync(string agentId, string? overridePath, CancellationToken ct);

    /// <summary>Write soul.md for an agent, creating directories as needed.</summary>
    Task WriteAsync(string agentId, string? overridePath, string contents, CancellationToken ct);
}

/// <summary>
/// Parsed soul.md. <see cref="Capabilities"/> is the YAML front-matter
/// <c>tools</c> list (plus any other simple lists); <see cref="Body"/> is
/// the markdown body that becomes the system prompt.
/// </summary>
public sealed record SoulDocument(
    string Path,
    string Raw,
    string Body,
    IReadOnlyList<string> Capabilities);
