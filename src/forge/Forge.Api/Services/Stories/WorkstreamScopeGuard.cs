// -----------------------------------------------------------------------
// <copyright file="WorkstreamScopeGuard.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    /// <summary>When the best deliverable score so far is at or above this, the
    /// off-task guard is SUPPRESSED — the evaluator endorsing the work is ground
    /// truth that the agent is on the RIGHT files, so a declared scope it
    /// contradicts is a bad/stale target list, not the agent off-task.
    /// (BEYOND-AST 5.2: scored 79→92→88 editing the real behavioral/summary files
    /// while the declared scope was a bogus `src/CodeIntelligence` path — the
    /// guard wrongly stalled it.)</summary>
    public const int OffTaskHealthyScoreSuppress = 75;

    /// <summary>Sentinel scope key emitted when a declared target is a ROOT-LEVEL
    /// file (<c>Makefile</c>, <c>go.mod</c>, <c>main.go</c>). Admits root-level
    /// changed files in <see cref="IsInScope"/>. Without it, a bootstrap-style
    /// workstream whose deliverables live at the repo root arms the guard from its
    /// few subdirectory targets and then reads 100% of its own declared work as
    /// out-of-scope (release RELEASE-PROVIDER stage 1.1, 2026-07-15: scope derived
    /// to <c>(internal, examples)</c> while the stage's unchecked steps were
    /// go.sum / Makefile / .goreleaser.yml / CI at root — stalled on-task work).</summary>
    public const string RepoRootScopeKey = ".";

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
    /// A root-level target (no directory) contributes <see cref="RepoRootScopeKey"/>
    /// so root-level changed files stay in scope. Returns an empty set when no
    /// target survives — callers MUST treat empty as "guard disabled" (fail-open:
    /// never trips).
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
                keys.Add(RepoRootScopeKey); // root-level target (Makefile, go.mod)
                continue;
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
    /// scope key (path-segment containment, slash-normalized). A root-level path
    /// (no directory) is in scope when the scope carries
    /// <see cref="RepoRootScopeKey"/> — i.e. the workstream declared at least one
    /// root-level target of its own.</summary>
    public static bool IsInScope(string changedPath, IReadOnlySet<string> scopeKeys)
    {
        if (scopeKeys.Count == 0)
        {
            return true; // fail-open
        }

        var trimmed = changedPath.Replace('\\', '/').Trim('/');
        if (!trimmed.Contains('/'))
        {
            return scopeKeys.Contains(RepoRootScopeKey);
        }

        var norm = "/" + trimmed + "/";
        foreach (var key in scopeKeys)
        {
            if (key == RepoRootScopeKey)
            {
                continue; // sentinel — only admits root-level paths above
            }

            if (norm.Contains("/" + key + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalize an edited-file path captured from the tool-call stream before it
    /// enters the off-task tally: agents pass ABSOLUTE worktree paths as often as
    /// repo-relative ones (<c>C:\…\.worktree\stage-1.1\Makefile</c> vs
    /// <c>Makefile</c>), which (a) double-counts one file as two distinct paths
    /// toward <see cref="OffTaskOutOfScopeEditThreshold"/> and (b) hides that a
    /// file is root-level, defeating <see cref="RepoRootScopeKey"/>. Strips the
    /// <paramref name="workspace"/> prefix when the path is rooted under it;
    /// returns the slash-normalized path otherwise.
    /// </summary>
    public static string NormalizeEditedPath(string path, string? workspace)
    {
        var p = path.Replace('\\', '/').Trim();
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var ws = workspace.Replace('\\', '/').TrimEnd('/') + "/";
            if (p.StartsWith(ws, StringComparison.OrdinalIgnoreCase))
            {
                p = p[ws.Length..];
            }
        }

        return p.Trim('/');
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

    /// <summary>Distinct out-of-scope EDITED files (from the agent's mutating tool
    /// calls, not just the committed diff) that trip the off-task guard — even
    /// when the agent reverts the edits before commit so the committed diff looks
    /// on-task. Catches backend thrash on a frontend task (code-intelligence
    /// 3D-RENDER 3.2: 40+ mutating calls to go.mod / schema.sql / serve_module.go
    /// while the committed diff was mostly web/).</summary>
    public const int OffTaskOutOfScopeEditThreshold = 5;

    // Tool-name substrings that MUTATE files (vs read/search). Case-insensitive.
    private static readonly string[] MutatingToolHints =
    {
        "edit", "create", "write", "str_replace", "apply_patch", "insert",
        "replace", "patch", "new_file", "multi_edit", "multiedit",
    };

    // Argument keys that carry the edited file path across backends.
    private static readonly string[] PathKeys =
    {
        "path", "filePath", "filepath", "file", "fileName", "filename",
        "target_file", "targetFile",
    };

    /// <summary>True when <paramref name="toolName"/> denotes a file-mutating tool
    /// (edit/create/write/…) rather than a read/search. Reads never count toward
    /// off-task — an agent may legitimately READ backend files for context.</summary>
    public static bool IsMutatingTool(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return false;
        }

        var n = toolName.ToLowerInvariant();
        foreach (var h in MutatingToolHints)
        {
            if (n.Contains(h, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extract the edited file path from a mutating tool call's payload, or null
    /// when the tool is a read/search or no path arg is present. Best-effort JSON
    /// parse over the known path-arg keys.
    /// </summary>
    public static string? TryExtractEditedPath(string? toolName, string? toolPayloadJson)
    {
        if (!IsMutatingTool(toolName) || string.IsNullOrWhiteSpace(toolPayloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in PathKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    var p = v.GetString();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        return p;
                    }
                }
            }
        }
        catch
        {
            // Malformed / non-JSON payload — ignore (best-effort).
        }

        return null;
    }

    /// <summary>Count of DISTINCT edited paths that fall outside the declared
    /// scope. Returns 0 when scope is unknown (fail-open).</summary>
    public static int CountOutOfScope(IEnumerable<string> editedPaths, IReadOnlySet<string> scopeKeys)
    {
        if (scopeKeys.Count == 0)
        {
            return 0;
        }

        return editedPaths.Count(p => !IsInScope(p, scopeKeys));
    }
}
