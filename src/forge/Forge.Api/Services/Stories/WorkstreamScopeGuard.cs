// -----------------------------------------------------------------------
// <copyright file="WorkstreamScopeGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Pure helpers backing two budget guards on the story workstream gen/eval loop
/// (<see cref="CrewStoryArtifactStrategy"/>):
/// <list type="number">
///   <item><description><b>Off-task wandering (#2)</b> — detect when a generator
///   agent has abandoned its declared file scope and is churning unrelated files
///   (e.g. a <c>web/src</c> frontend stage editing <c>internal/**</c> Go files to
///   chase an out-of-scope repo-wide gate). Conservative + fail-open: only the
///   files the task text explicitly names define the scope; if it names none, the
///   guard is disabled.</description></item>
///   <item><description><b>Inherited gate failure (#3)</b> — decide whether a
///   failing gate is OWNED by this workstream's diff or INHERITED from the base
///   branch. If none of the failing test files appear in the workstream's changed
///   files, the failure is inherited — a Restart will re-burn budget on a gate the
///   workstream cannot fix.</description></item>
/// </list>
/// All methods are deterministic and side-effect-free so they are unit-testable
/// without a live runner (mirrors <c>SloppyDetector</c>).
/// </summary>
public static class WorkstreamScopeGuard
{
    /// <summary>Off-task trips only when this fraction (or more) of the iter's
    /// changed files fall outside the declared scope.</summary>
    public const double OffTaskRatioThreshold = 0.8;

    /// <summary>Don't evaluate off-task on tiny incidental diffs — require at
    /// least this many changed files so a single stray edit can't trip it.</summary>
    public const int OffTaskMinChangedFiles = 3;

    /// <summary>Consecutive off-task iterations before the loop aborts.</summary>
    public const int OffTaskThreshold = 2;

    /// <summary>
    /// Compute the set of declared scope KEYS from the workstream's DECLARED
    /// TARGET file paths (the <c>targets</c> list the loop already derives via
    /// <c>ExtractTargetPaths</c> + the architect-targets fallback). Each key is a
    /// target file's PARENT-AREA directory — the target's directory minus its last
    /// segment — so siblings of the target's own folder stay in scope
    /// (e.g. <c>web/src/api/types.ts</c> → key <c>web/src</c>, which still admits
    /// <c>web/src/components/*</c>) while a foreign module (<c>internal/**</c>,
    /// <c>cmd/**</c>) does not. Keys are matched by path-segment containment later,
    /// so they tolerate a target written module-relative (<c>web/src/api/…</c>)
    /// while git reports repo-relative (<c>services/agent-memory/web/src/api/…</c>).
    /// Returns an empty set when no target names a file in a directory — callers
    /// MUST treat empty as "guard disabled" (fail-open: never trips).
    /// </summary>
    public static IReadOnlySet<string> ScopeKeysFromTargets(IEnumerable<string>? targetPaths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (targetPaths is null)
        {
            return keys;
        }

        foreach (var raw in targetPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var p = raw.Replace('\\', '/').Trim().Trim('/');
            var lastSlash = p.LastIndexOf('/');
            if (lastSlash < 0)
            {
                continue; // root-level target (Makefile, go.mod) — no area key
            }

            var dir = p[..lastSlash];
            var segs = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0)
            {
                continue;
            }

            // Parent area = directory minus its last segment (admits siblings).
            // For a single-segment dir, use it as-is.
            var keySegs = segs.Length >= 2 ? segs[..^1] : segs;
            var key = string.Join('/', keySegs);
            if (key.Length > 0)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>True when <paramref name="changedPath"/> lies within any declared
    /// scope key (path-segment containment, slash-normalized).</summary>
    public static bool IsInScope(string changedPath, IReadOnlySet<string> scopeKeys)
    {
        if (scopeKeys.Count == 0)
        {
            return true; // fail-open
        }

        var norm = "/" + changedPath.Replace('\\', '/').Trim('/') + "/";
        foreach (var key in scopeKeys)
        {
            if (norm.Contains("/" + key + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Off-task verdict for one iteration's changed files. Returns false (on-task)
    /// when the guard is disabled (no declared scope), the diff is too small, or
    /// enough changes land in scope. <paramref name="offRatio"/> is reported for
    /// the emitted telemetry event.
    /// </summary>
    public static bool IsOffTask(
        IReadOnlyList<string> changedFiles,
        IReadOnlySet<string> scopeKeys,
        out double offRatio,
        out int offCount)
    {
        offRatio = 0;
        offCount = 0;
        if (scopeKeys.Count == 0 || changedFiles.Count < OffTaskMinChangedFiles)
        {
            return false;
        }

        offCount = changedFiles.Count(f => !IsInScope(f, scopeKeys));
        offRatio = (double)offCount / changedFiles.Count;
        return offRatio >= OffTaskRatioThreshold;
    }

    /// <summary>
    /// Extract candidate failing-test file basenames from a gate error summary
    /// (e.g. <c>probe_harness_gate_test.go:175</c>, <c>suite.go:640</c>,
    /// <c>TestFoo_test.go</c>). Best-effort — returns whatever source-file tokens
    /// the gate happened to print.
    /// </summary>
    public static IReadOnlySet<string> ExtractFailingFiles(string? errorSummary)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(errorSummary))
        {
            return files;
        }

        // Matches file.ext optionally followed by :line, across go/cs/ts/rs/py.
        foreach (Match m in Regex.Matches(errorSummary, @"[\w.\-/]+\.(?:go|cs|ts|tsx|rs|py)(?=[:\s)]|$)"))
        {
            var token = m.Value.Replace('\\', '/');
            var name = token[(token.LastIndexOf('/') + 1)..];
            if (name.Length > 0)
            {
                files.Add(name);
            }
        }

        return files;
    }

    /// <summary>
    /// True when a gate failure is INHERITED rather than owned: none of the
    /// failing test files appear (by basename) in the workstream's changed files.
    /// Returns false (treat as owned) when we can't tell — either no failing files
    /// were parsed or the workstream changed nothing — so we never mislabel a real
    /// regression as inherited.
    /// </summary>
    public static bool IsInheritedGateFailure(
        IReadOnlySet<string> failingFiles,
        IReadOnlyList<string> changedFiles,
        out string failingList)
    {
        failingList = string.Join(", ", failingFiles.Take(8));
        if (failingFiles.Count == 0 || changedFiles.Count == 0)
        {
            return false;
        }

        var changedBasenames = new HashSet<string>(
            changedFiles.Select(f =>
            {
                var n = f.Replace('\\', '/');
                return n[(n.LastIndexOf('/') + 1)..];
            }),
            StringComparer.OrdinalIgnoreCase);

        // Inherited iff NOT a single failing test file was touched by this ws.
        return !failingFiles.Any(changedBasenames.Contains);
    }
}
