// -----------------------------------------------------------------------
// <copyright file="BuildArtifactPaths.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.Collections.Generic;

/// <summary>
/// Single source of truth for "this repo-relative path is generated build
/// output and must never be read into a producer's file dict, reported as a
/// PR change, or committed". A from-scratch workstream frequently hasn't
/// authored a <c>.gitignore</c> yet, and the build/test gate
/// (<c>dotnet build</c>, <c>go test</c>, <c>cargo build</c>, …) materialises
/// <c>bin/</c> / <c>obj/</c> / <c>target/</c> / <c>node_modules/</c> trees in
/// the workspace. Without this filter a blanket <c>git add -A</c> sweeps all
/// of it into the PR (observed on smartpcr/code-intelligence#51 — 276 files,
/// mostly <c>tests/bin/Debug/net8.0/*.dll</c> + <c>obj/Debug/net8.0/*</c>).
///
/// <para>Two surfaces:
/// <list type="bullet">
///   <item><see cref="IsBuildArtifact"/> — broad predicate for FILTERING
///         (drop the path from a reported / committed set). Conservative —
///         only directories whose entire purpose is throwaway output.</item>
///   <item><see cref="IndexScrubPathspecs"/> — narrow "never legitimately
///         tracked" subset rendered as git glob pathspecs, for actively
///         removing already-staged artifacts from the index via
///         <c>git rm -r --cached --ignore-unmatch -- …</c>.</item>
/// </list></para>
/// </summary>
public static class BuildArtifactPaths
{
    /// <summary>
    /// Directories whose contents are generated/cached. Path-segment
    /// anchored so e.g. <c>src/target_resolver.rs</c> is NOT matched by the
    /// <c>target/</c> rule. <c>bin/</c> is intentionally NOT blanket-listed
    /// (many repos legitimately track a <c>bin/</c> of scripts); only the
    /// .NET <c>bin/Debug/</c> + <c>bin/Release/</c> output subtrees are.
    /// </summary>
    private static readonly string[] ArtifactDirs =
    [
        "target/",            // Rust
        "node_modules/",      // Node
        "__pycache__/",       // Python bytecode
        ".pytest_cache/",     // Pytest
        ".mypy_cache/",       // mypy
        ".ruff_cache/",       // ruff
        ".tox/",              // tox
        ".venv/",             // Python virtualenv
        "venv/",              // Python virtualenv (alt)
        "obj/",               // .NET intermediate
        "bin/Debug/",         // .NET build output (debug)
        "bin/Release/",       // .NET build output (release)
        ".gradle/",           // Gradle cache
        ".idea/",             // JetBrains
        ".vs/",               // Visual Studio cache
        ".cache/",            // generic
        "dist/",              // common JS bundler output
        ".next/",             // Next.js
        ".nuxt/",             // Nuxt
        ".turbo/",            // Turborepo
        ".parcel-cache/",     // Parcel
        ".forge/",            // Forge iteration scratch (iter-notes, notes/)
    ];

    /// <summary>
    /// The subset of <see cref="ArtifactDirs"/> that is NEVER legitimately
    /// committed, so it is safe to actively <c>git rm --cached</c> from a
    /// staged index. Excludes ambiguous dirs (<c>dist/</c>, <c>.cache/</c>,
    /// IDE dirs) which a repo might intentionally track.
    /// </summary>
    private static readonly string[] NeverCommitDirs =
    [
        "obj/",
        "bin/Debug/",
        "bin/Release/",
        "node_modules/",
        "target/",
        "__pycache__/",
    ];

    /// <summary>
    /// True for a repo-relative path under a generated/cached directory.
    /// The path is normalised to forward slashes internally so callers can
    /// pass either separator.
    /// </summary>
    public static bool IsBuildArtifact(string repoRelativePath)
    {
        if (string.IsNullOrEmpty(repoRelativePath)) return false;
        var p = repoRelativePath.Replace('\\', '/');
        foreach (var dir in ArtifactDirs)
        {
            if (p.StartsWith(dir, StringComparison.Ordinal)) return true;
            if (p.Contains("/" + dir, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// git glob pathspecs matching every <see cref="NeverCommitDirs"/> entry
    /// at any depth, for <c>git rm -r --cached --ignore-unmatch -- …</c>.
    /// The <c>:(glob)</c> magic + leading <c>**/</c> matches the directory at
    /// the repo root AND nested (e.g. <c>tests/bin/Debug/…</c>).
    /// </summary>
    public static IReadOnlyList<string> IndexScrubPathspecs()
    {
        var specs = new List<string>(NeverCommitDirs.Length);
        foreach (var dir in NeverCommitDirs)
        {
            var d = dir.TrimEnd('/');
            specs.Add($":(glob)**/{d}/**");
        }
        return specs;
    }
}
