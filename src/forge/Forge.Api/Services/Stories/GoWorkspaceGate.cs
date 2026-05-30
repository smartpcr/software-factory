// -----------------------------------------------------------------------
// <copyright file="GoWorkspaceGate.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Multi-module (<c>go.work</c>) awareness for the build/test gate.
///
/// <para><b>Why this exists.</b> Forge's default Go gate command is
/// <c>go test ./...</c> / <c>go build ./...</c>, run with the worktree
/// root as the working directory (<see cref="StoryBuildRunner.RunBuildAsync"/>).
/// But <c>./...</c> does NOT cross module boundaries even in workspace
/// mode (see <see href="https://go.dev/ref/mod#workspaces"/>). So in a
/// repo whose real code lives under sibling modules pinned by a root
/// <c>go.work</c> — e.g. <c>use ( . ./services/clean-code ./services/agent-memory )</c>
/// — <c>go test ./...</c> from the root finds ZERO packages: the gate
/// exits "no packages to test" and none of the workstream's actual tests
/// run. (An engineer agent was observed working around this by committing
/// a <c>tools/forge_gate_proxy</c> shim — a root-module <c>TestMain</c>
/// that shells out to each service module — plus copies of e2e tests into
/// the legacy root module. That "fake module" plumbing is exactly what
/// this gate-side fix obviates.)</para>
///
/// <para><b>What it does.</b> Parses the <c>use (...)</c> directives of a
/// repo's root <c>go.work</c> into module directories, picks the
/// module(s) actually touched by a workstream's changed files, and
/// rewrites a root-scoped <c>go ...</c> command to run inside each module
/// scoped to that module's <c>./...</c>. The caller then runs the command
/// once per module and aggregates — see <see cref="StoryBuildRunner.RunGoWorkspaceAsync"/>.</para>
///
/// <para><b>Why scope tests to impacted modules?</b> Running <c>go test
/// ./...</c> in EVERY workspace module would fail-open onto a sibling
/// module's pre-existing breakage (e.g. agent-memory's fingerprint
/// goldens were generated on an older Go toolchain and emit different
/// bytes today) and block unrelated work. The build stage is safe to run
/// across all modules (a clean compile is module-independent); the test
/// stage scopes to the modules the diff actually touched.</para>
/// </summary>
public static class GoWorkspaceGate
{
    /// <summary>True when <paramref name="command"/>'s program token is
    /// <c>go</c> (the only ecosystem with the cross-module <c>./...</c>
    /// quirk). Whitespace / null → false.</summary>
    public static bool IsGoCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var first = command.TrimStart().Split(' ', '\t').FirstOrDefault();
        return string.Equals(first, "go", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads <c>{workspace}/go.work</c> and returns the <c>use</c>-block
    /// module directories as forward-slash, workspace-relative paths
    /// (the root module is <c>"."</c>). Only directories that exist AND
    /// contain a <c>go.mod</c> are returned, so a stale <c>use</c> entry
    /// can't break the gate. Returns <c>null</c> when there is no
    /// <c>go.work</c> (the single-module / non-Go case — caller keeps its
    /// existing root-cwd behaviour).
    /// </summary>
    public static IReadOnlyList<string>? TryReadWorkspaceModules(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var goWork = Path.Combine(workspace, "go.work");
        if (!File.Exists(goWork)) return null;

        string text;
        try { text = File.ReadAllText(goWork); }
        catch { return null; }

        var rels = ParseUseDirectives(text);
        var kept = new List<string>();
        foreach (var rel in rels)
        {
            // Resolve against the workspace root; keep only real modules.
            string abs;
            try { abs = Path.GetFullPath(Path.Combine(workspace, rel)); }
            catch { continue; }
            if (Directory.Exists(abs) && File.Exists(Path.Combine(abs, "go.mod")))
            {
                kept.Add(rel);
            }
        }
        return kept.Count > 0 ? kept : null;
    }

    /// <summary>
    /// Parses the <c>use</c> directives out of <c>go.work</c> text.
    /// Handles both the block form (<c>use ( ... )</c>, one path per
    /// line) and the single-line form (<c>use ./foo</c>), strips <c>//</c>
    /// line comments, normalises separators to forward-slash, and
    /// canonicalises the root entry to <c>"."</c>. Pure — exposed for
    /// unit testing without touching the filesystem.
    /// </summary>
    public static IReadOnlyList<string> ParseUseDirectives(string goWorkText)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(goWorkText)) return result;

        var inBlock = false;
        foreach (var raw in goWorkText.Split('\n'))
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (inBlock)
            {
                if (line.StartsWith(")", StringComparison.Ordinal)) { inBlock = false; continue; }
                AddNormalised(result, line);
                continue;
            }

            if (line.StartsWith("use", StringComparison.Ordinal))
            {
                var rest = line[3..].Trim();
                if (rest.StartsWith("(", StringComparison.Ordinal))
                {
                    inBlock = true;
                    rest = rest[1..].Trim();
                    // `use ( ./a` rare inline-open form: capture the tail.
                    if (rest.Length > 0 && !rest.StartsWith(")", StringComparison.Ordinal))
                    {
                        AddNormalised(result, rest);
                    }
                }
                else if (rest.Length > 0)
                {
                    AddNormalised(result, rest);
                }
            }
        }
        return result;
    }

    private static void AddNormalised(List<string> into, string token)
    {
        var t = token.Trim().Trim('"').Replace('\\', '/').Trim();
        if (t.Length == 0) return;
        // Canonicalise the root + strip a leading "./" so prefix matching
        // and Path.Combine behave predictably.
        if (t is "." or "./" or "") { if (!into.Contains(".")) into.Add("."); return; }
        if (t.StartsWith("./", StringComparison.Ordinal)) t = t[2..];
        t = t.TrimEnd('/');
        if (t.Length == 0) t = ".";
        if (!into.Contains(t)) into.Add(t);
    }

    /// <summary>
    /// Picks the workspace modules touched by <paramref name="changedFiles"/>
    /// (workspace-relative, any separator). Each changed file is attributed
    /// to the module with the LONGEST matching path prefix, so a file under
    /// <c>services/clean-code/…</c> maps to <c>services/clean-code</c> and
    /// not the catch-all root <c>"."</c> module. When the changed-file set
    /// is empty / unknown, every module is returned (safe default: test the
    /// whole workspace rather than silently nothing). Result preserves the
    /// <paramref name="modules"/> order.
    /// </summary>
    public static IReadOnlyList<string> SelectImpactedModules(
        IReadOnlyList<string> modules,
        IReadOnlyList<string>? changedFiles)
    {
        if (modules.Count == 0) return modules;
        if (changedFiles is null || changedFiles.Count == 0) return modules;

        // Longest module path first so the most specific wins the match.
        var ranked = modules
            .Select(m => (mod: m, norm: m == "." ? string.Empty : m.Replace('\\', '/').TrimEnd('/') + "/"))
            .OrderByDescending(x => x.norm.Length)
            .ToList();

        var hit = new HashSet<string>();
        foreach (var f in changedFiles)
        {
            var nf = f.Replace('\\', '/').TrimStart('/');
            foreach (var r in ranked)
            {
                if (r.norm.Length == 0)
                {
                    // Root module matches a file that has no deeper module
                    // owner — only reached after the specific modules miss.
                    hit.Add(r.mod);
                    break;
                }
                if (nf.StartsWith(r.norm, StringComparison.OrdinalIgnoreCase))
                {
                    hit.Add(r.mod);
                    break;
                }
            }
        }

        if (hit.Count == 0) return modules;
        return modules.Where(hit.Contains).ToList();
    }

    /// <summary>
    /// Rewrites a root-scoped <c>go</c> command so it targets the current
    /// module's <c>./...</c>: drops any package-path argument (e.g.
    /// <c>./...</c>, <c>./services/clean-code/internal/...</c>,
    /// <c>foo/...</c>, <c>all</c>) while preserving the program, every flag,
    /// and the <c>-run '&lt;regex&gt;'</c> filter, then appends <c>./...</c>.
    /// So <c>go test -count=1 ./services/clean-code/... -run '^TestX$'</c>
    /// becomes <c>go test -count=1 ./... -run '^TestX$'</c>, runnable from
    /// inside each module directory. A non-Go command is returned unchanged.
    /// </summary>
    public static string ScopeCommandToModule(string command)
    {
        if (!IsGoCommand(command)) return command;
        var tokens = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length + 1);
        foreach (var tok in tokens)
        {
            if (LooksLikePackageSpec(tok)) continue;
            kept.Add(tok);
        }
        kept.Add("./...");
        return string.Join(' ', kept);
    }

    private static bool LooksLikePackageSpec(string token)
    {
        if (token.Length == 0) return false;
        if (token == "all") return true;
        if (token == "." || token == "./...") return true;
        if (token.StartsWith("./", StringComparison.Ordinal)) return true;
        if (token.StartsWith(".\\", StringComparison.Ordinal)) return true;
        if (token.EndsWith("/...", StringComparison.Ordinal)) return true;
        if (token.EndsWith("\\...", StringComparison.Ordinal)) return true;
        return false;
    }
}
