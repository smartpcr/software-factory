// -----------------------------------------------------------------------
// <copyright file="CanonicalId.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Extraction;

using System;

/// <summary>
/// Computes canonical node IDs per architecture §4.2.
/// Symbols: "/{relativePath}::{qualifiedName}" — Files: "/{relativePath}".
/// </summary>
public static class CanonicalId
{
    public static string ForSymbol(string relativePath, string qualifiedName)
    {
        var normalized = NormalizePath(relativePath);
        return $"/{normalized}::{qualifiedName}";
    }

    public static string ForFile(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return $"/{normalized}";
    }

    public static string ForPackage(string projectRelativePath)
    {
        var normalized = NormalizePath(projectRelativePath);
        var dir = normalized;
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0 && normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            dir = normalized[..lastSlash];
        }

        return $"/{dir}";
    }

    public static string RepoRoot => "/";

    public static string ForExternal(string packageName, string qualifiedName)
        => $"ext:{packageName}:{qualifiedName}";

    public static string? ExtractQualifiedName(string canonicalId)
    {
        var idx = canonicalId.IndexOf("::", StringComparison.Ordinal);
        return idx >= 0 ? canonicalId[(idx + 2)..] : null;
    }

    public static string? ExtractFilePath(string canonicalId)
    {
        if (!canonicalId.StartsWith('/'))
        {
            return null;
        }

        var withoutSlash = canonicalId[1..];
        var idx = withoutSlash.IndexOf("::", StringComparison.Ordinal);
        return idx >= 0 ? withoutSlash[..idx] : withoutSlash;
    }

    public static bool IsInRepo(string canonicalId) => canonicalId.StartsWith('/');

    public static bool IsExternal(string canonicalId) =>
        canonicalId.StartsWith("ext:", StringComparison.Ordinal);

    /// <summary>
    /// Normalizes a file path: backslashes→forward slashes, strips leading / and ./, trims trailing /.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}