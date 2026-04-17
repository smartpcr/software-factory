// -----------------------------------------------------------------------
// <copyright file="VersionInfo.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Version;

using System;
using System.Reflection;

/// <summary>
/// Reads version metadata from assembly attributes at runtime.
/// </summary>
public static class VersionInfo
{
    private static readonly Lazy<VersionInfoDto> Cached = new(ReadFromAssembly);

    /// <summary>
    /// Gets the version information for the running assembly.
    /// </summary>
    public static VersionInfoDto Current => Cached.Value;

    private static VersionInfoDto ReadFromAssembly()
    {
        var assembly = typeof(VersionInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var commitSha = ExtractCommitSha(informational);
        var buildUtc = DateTime.UtcNow.ToString("o");

        return new VersionInfoDto(
            Version: informational.Split('+')[0],
            CommitSha: commitSha,
            BuildUtc: buildUtc);
    }

    private static string ExtractCommitSha(string informationalVersion)
    {
        // Nerdbank.GitVersioning appends +<sha> to InformationalVersion
        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex >= 0 && plusIndex + 1 < informationalVersion.Length)
        {
            var sha = informationalVersion[(plusIndex + 1)..];
            // Take first 7 chars for short SHA
            return sha.Length > 7 ? sha[..7] : sha;
        }

        return Environment.GetEnvironmentVariable("GITHUB_SHA")?[..7] ?? "local";
    }
}

/// <summary>
/// Version metadata DTO returned by the /api/version endpoint.
/// </summary>
public record VersionInfoDto(string Version, string CommitSha, string BuildUtc);
