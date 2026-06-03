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

    /// <summary>
    /// Narrows a module-scoped <c>go test</c> command to ONLY the packages a
    /// workstream's diff actually touched, within a single workspace module.
    ///
    /// <para><b>Why.</b> <see cref="ScopeCommandToModule"/> already keeps the
    /// gate from fail-opening onto a sibling <i>module</i>'s pre-existing
    /// breakage. But a module can itself hold dozens of packages — e.g.
    /// <c>services/agent-memory</c> bundles ~19 <c>embedded-postgres</c>
    /// integration packages that are slow (each spins up a real Postgres) and
    /// flaky under the concurrency of a multi-stage spawn (many worktrees
    /// racing to <c>initdb</c>/bind a port at once). Running the whole module's
    /// <c>./...</c> for a 6-file config change drags those unrelated packages
    /// into the per-iter gate, where their flaky/slow failures (exit 1, often
    /// &gt;600s) are blamed on a diff that never touched them — the workstream
    /// can't fix code it didn't write, so it stalls. Scoping the TEST stage to
    /// the changed packages extends the existing module-scoping philosophy one
    /// level down: don't fail-open onto an unrelated <i>package</i>'s breakage.</para>
    ///
    /// <para><b>How.</b> Derives one <c>./&lt;dir&gt;/...</c> target per distinct
    /// directory of a changed <c>.go</c> file that lives under
    /// <paramref name="moduleRel"/> (module-relative), preserving every flag and
    /// dropping the old package spec. The <c>/...</c> suffix still includes the
    /// changed package's sub-packages. Conservative fall-backs return the whole
    /// module (<see cref="ScopeCommandToModule"/>) so coverage is never silently
    /// dropped: no changed-file info; a changed <c>.go</c> file at the module
    /// root (e.g. <c>main.go</c> — a root-package change can affect everything);
    /// a <c>go.mod</c>/<c>go.sum</c>/<c>go.work</c> change (dependency-graph
    /// shift); or no <c>.go</c> file resolvable inside this module. A non-Go
    /// command is returned unchanged.</para>
    ///
    /// <para><b>Tradeoff.</b> Like the module-level scoping, this runs the
    /// changed packages' OWN tests, not the tests of unrelated packages that
    /// import them. That mirrors the established gate behaviour and the
    /// evaluator still reviews the full diff; the per-iter gate's job is to
    /// catch regressions in the changed code's own tests. Pure — exposed for
    /// unit testing without touching the filesystem.</para>
    ///
    /// <para><b>Module ownership (<paramref name="allModules"/>).</b> A file is
    /// claimed by this module ONLY if its longest-matching workspace module is
    /// <paramref name="moduleRel"/>. Without this, the root <c>.</c> module
    /// (whose empty prefix matches everything) would emit targets pointing INTO
    /// a nested module — e.g. <c>go test ./services/agent-memory/...</c> — which
    /// in <c>go.work</c> workspace mode actually EXECUTES the nested module's
    /// packages (re-dragging the flaky embedded-PG suite back in under the root
    /// module) and was observed to re-stall a workstream with a broad diff
    /// (<c>[module .]</c> exit 1). Passing every workspace module lets each
    /// module claim only its own files; nested-module files are left to that
    /// module's own scoped command.</para>
    /// </summary>
    public static string ScopeTestCommandToPackages(
        string command,
        string moduleRel,
        IReadOnlyList<string> allModules,
        IReadOnlyList<string>? changedFiles)
    {
        if (!IsGoCommand(command)) return command;

        // No diff info → safest is the whole module (caller already picked it).
        if (changedFiles is null || changedFiles.Count == 0) return ScopeCommandToModule(command);

        var modNorm = moduleRel == "." || string.IsNullOrEmpty(moduleRel)
            ? string.Empty
            : moduleRel.Replace('\\', '/').TrimEnd('/') + "/";

        // Longest module prefix first — used to decide which module OWNS a
        // changed file (the most specific match), mirroring SelectImpactedModules.
        var ranked = (allModules ?? Array.Empty<string>())
            .Select(m => (mod: m, norm: m == "." || string.IsNullOrEmpty(m) ? string.Empty : m.Replace('\\', '/').TrimEnd('/') + "/"))
            .OrderByDescending(x => x.norm.Length)
            .ToList();

        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in changedFiles)
        {
            var nf = f.Replace('\\', '/').TrimStart('/');

            // Attribute the file to its longest-prefix module; skip if another
            // (deeper) module owns it — that module's own command covers it.
            if (ranked.Count > 0)
            {
                var owner = ranked.FirstOrDefault(r => r.norm.Length == 0 || nf.StartsWith(r.norm, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(owner.mod ?? ".", moduleRel, StringComparison.OrdinalIgnoreCase)) continue;
            }

            string relToMod;
            if (modNorm.Length == 0)
            {
                relToMod = nf;
            }
            else
            {
                if (!nf.StartsWith(modNorm, StringComparison.OrdinalIgnoreCase)) continue;
                relToMod = nf[modNorm.Length..];
            }
            if (relToMod.Length == 0) continue;

            var slash = relToMod.LastIndexOf('/');
            var name = slash < 0 ? relToMod : relToMod[(slash + 1)..];

            // A module manifest change can shift the whole dependency graph —
            // test the whole module rather than guess which packages moved.
            if (name is "go.mod" or "go.sum" or "go.work") return ScopeCommandToModule(command);

            // Only .go files identify a Go package directory; non-Go changes
            // (docs, testdata, configs) don't widen the scope on their own.
            if (!name.EndsWith(".go", StringComparison.OrdinalIgnoreCase)) continue;

            // A .go file at the module root is a root-package change (e.g.
            // main.go) that can affect everything → whole module.
            if (slash < 0) return ScopeCommandToModule(command);

            var dir = relToMod[..slash];
            var target = "./" + dir + "/...";
            if (seen.Add(target)) targets.Add(target);
        }

        // No .go package resolvable inside this module (e.g. only sibling-module
        // or non-Go files touched) → fall back to the module's ./... .
        if (targets.Count == 0) return ScopeCommandToModule(command);

        var tokens = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length + targets.Count);
        foreach (var tok in tokens)
        {
            if (LooksLikePackageSpec(tok)) continue;
            kept.Add(tok);
        }
        kept.AddRange(targets);
        return string.Join(' ', kept);
    }
}
