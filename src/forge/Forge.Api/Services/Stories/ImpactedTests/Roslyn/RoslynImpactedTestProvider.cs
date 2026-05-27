// -----------------------------------------------------------------------
// <copyright file="RoslynImpactedTestProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Roslyn;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

/// <summary>
/// Phase 59 Stage C — Roslyn-in-process impacted-test provider for
/// C# workspaces. Loads the workspace's <c>.sln</c> (preferred) or
/// first <c>.csproj</c> via <c>MSBuildWorkspace</c>, then for each
/// changed <c>.cs</c> file walks every top-level method / class
/// declaration, calls
/// <see cref="SymbolFinder.FindReferencesAsync(ISymbol, Solution, CancellationToken)"/>,
/// filters reference locations to enclosing methods carrying
/// <c>[Fact]</c> / <c>[Theory]</c> / <c>[TestMethod]</c> /
/// <c>[Test]</c> / <c>[TestCase]</c>, and groups the matched test
/// methods by owning project.
///
/// <para>The output is either per-project narrowing
/// (<c>dotnet test {proj1} {proj2}</c>) or per-test narrowing
/// (<c>dotnet test {proj} --filter "FullyQualifiedName~Foo.Bar"</c>)
/// — the latter wins when the matched test set is < 80% of the
/// project's total tests, because at that point the
/// per-test invocation overhead is worth the extra
/// narrowing.</para>
///
/// <para>Workspace loads are <b>slow</b> (typical 5–30s on the FRP
/// `update` repo). The provider caches the loaded Solution per
/// `(workspacePath, headSha)` pair so consecutive gate runs for the
/// same iter reuse the same parse + symbol tables. Cache evicts on
/// HEAD SHA change because any commit could have moved code
/// around.</para>
/// </summary>
public sealed class RoslynImpactedTestProvider : IImpactedTestProvider
{
    private static readonly ConcurrentDictionary<string, CachedSolution> SolutionCache =
        new(StringComparer.OrdinalIgnoreCase);

    public string Name => "roslyn";

    public Task<bool> CanHandleAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Task.FromResult(false);
        }
        // Probe is intentionally cheap — just look for any .sln or
        // .csproj under the workspace root. The actual load happens
        // in ResolveAsync. We DON'T require MSBuildLocator be
        // registered here because Program.cs handles that
        // defensively at boot; if it failed the
        // MSBuildWorkspace.Create() below will throw and the
        // chain falls through to the next provider.
        try
        {
            // Use the same entry resolution as ResolveAsync (which excludes
            // build-output dirs), so we don't claim-handle a repo whose only
            // .csproj live under out\/bin\/obj\.
            return Task.FromResult(FindEntry(workspace) is not null);
        }
        catch
        {
            // IO error → can't handle.
            return Task.FromResult(false);
        }
    }

    public async Task<ImpactedTestResolverResult?> ResolveAsync(
        string workspace,
        IReadOnlyList<string> changedFiles,
        string originalCommand,
        CancellationToken ct)
    {
        // Same gating as the heuristic — only `dotnet test` commands
        // get rewritten; explicit project / filter overrides stay
        // as-is.
        var tokens = ImpactedTestResolver.ParseCommandLine(originalCommand);
        if (tokens.Length < 2
            || !string.Equals(Path.GetFileNameWithoutExtension(tokens[0]), "dotnet", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tokens[1], "test", StringComparison.OrdinalIgnoreCase))
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "not a `dotnet test` command", "roslyn");
        }
        if (HasOperatorOverrideArgs(tokens))
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "command has explicit project / --filter override", "roslyn");
        }
        if (changedFiles.Count == 0)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: 0,
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: "no changed files",
                Mode: "roslyn");
        }

        // Load (or fetch cached) Solution.
        CachedSolution? cached;
        try
        {
            cached = await GetOrLoadSolutionAsync(workspace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Workspace load failed (corrupt .sln, missing SDK,
            // MSBuildLocator not registered). Return null so the
            // chain falls through to the heuristic instead of
            // stranding the gate.
            Debug.WriteLine($"RoslynImpactedTestProvider load failed: {ex.Message}");
            return null;
        }
        if (cached is null) return null;
        var solution = cached.Solution;

        // 2026-05-26 — if the workspace loaded but exposes NO test projects,
        // Roslyn can't narrow OR meaningfully whole-suite-fallback, and
        // returning ANY non-null result here would STOP the chain before the
        // filename-based heuristic (which doesn't need MSBuild) gets a turn.
        // Confirmed failure mode: the FRP `update` repo is .NET Framework
        // (net472). The in-process MSBuildWorkspace runs under the .NET SDK and
        // can't load net472 projects, so `solution.Projects` comes back empty
        // (observed live: a same-repo story resolved with allTestProjectCount=0
        // / solution.Projects.Count()=0) — Roslyn then ran the WHOLE suite and
        // the gate timed out at 25 min. Fall through (null) instead so the
        // heuristic narrows by filename (38 *.UnitTest.csproj on disk → the one
        // impacted project), turning a 25-min timeout into a ~3-min green gate.
        if (CountTestProjects(solution) == 0)
        {
            Debug.WriteLine(
                "RoslynImpactedTestProvider: workspace loaded 0 test projects "
                + "(likely a .NET Framework repo the SDK MSBuildWorkspace can't load); "
                + "deferring to the next provider (heuristic).");
            return null;
        }

        // Normalise + bound changed files to .cs entries inside the
        // workspace. Global-impact files (Directory.Build.props /
        // global.json / *.sln) still trigger the heuristic's
        // whole-suite fallback to be safe.
        var (changedAbs, hitGlobal, hitFile) = NormaliseChangedFiles(workspace, changedFiles);
        if (hitGlobal)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: solution.Projects.Count(),
                WholeSuiteFallback: true,
                Skipped: false,
                Reason: $"global-impact file changed ({hitFile}); running whole suite",
                Mode: "roslyn");
        }

        // For each changed .cs file, find its Document in the
        // Solution + collect every top-level symbol we might query
        // references on. Skip files Roslyn doesn't know about
        // (untracked / outside any project).
        var changedSymbols = new List<ISymbol>();
        var changedDocs = new List<Document>();
        foreach (var abs in changedAbs)
        {
            if (!abs.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var docIds = solution.GetDocumentIdsWithFilePath(abs);
            foreach (var id in docIds)
            {
                var doc = solution.GetDocument(id);
                if (doc is null) continue;
                changedDocs.Add(doc);
                await CollectDeclaredSymbolsAsync(doc, changedSymbols, ct).ConfigureAwait(false);
            }
        }

        if (changedSymbols.Count == 0)
        {
            // No C# source in the diff (or all changes are inside
            // files Roslyn doesn't track). Returning Skipped tells
            // the caller to emit task.test-gate.skipped — same
            // semantic as the heuristic's empty-set path.
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: CountTestProjects(solution),
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: "no C# symbols changed",
                Mode: "roslyn");
        }

        // Walk each symbol's incoming references. For each
        // reference location, find its enclosing
        // MethodDeclarationSyntax + check its attribute list. Any
        // reference inside a changed test file ALSO counts
        // (operator edited the test itself).
        var impactedTestMethods = new HashSet<(string ProjectPath, string FullyQualifiedName)>();
        foreach (var doc in changedDocs)
        {
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root is null) continue;
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!TestAttributeDetector.IsTestMethod(method)) continue;
                var fqn = await GetFqnAsync(doc, method, ct).ConfigureAwait(false);
                var projPath = doc.Project.FilePath ?? string.Empty;
                if (!string.IsNullOrEmpty(fqn) && !string.IsNullOrEmpty(projPath))
                {
                    impactedTestMethods.Add((projPath, fqn));
                }
            }
        }

        foreach (var symbol in changedSymbols)
        {
            IEnumerable<ReferencedSymbol> refs;
            try
            {
                refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }
            foreach (var refSym in refs)
            {
                foreach (var loc in refSym.Locations)
                {
                    var enclosing = await GetEnclosingTestMethodAsync(loc, ct).ConfigureAwait(false);
                    if (enclosing is null) continue;
                    impactedTestMethods.Add(enclosing.Value);
                }
            }
        }

        var totalTestProjectCount = CountTestProjects(solution);
        if (impactedTestMethods.Count == 0)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: totalTestProjectCount,
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: "no impacted test methods",
                Mode: "roslyn");
        }

        // Group matched test methods by owning project.
        var byProject = impactedTestMethods
            .GroupBy(t => t.ProjectPath, t => t.FullyQualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 80% whole-suite fallback (same threshold as heuristic) —
        // if we'd be invoking most of the suite anyway the per-
        // project overhead isn't worth it.
        var threshold = (int)Math.Ceiling(totalTestProjectCount * ImpactedTestResolver.DefaultWholeSuiteThresholdRatio);
        if (byProject.Count >= threshold && totalTestProjectCount > 1)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: SortRelativeProjectPaths(workspace, byProject.Keys),
                AllTestProjectCount: totalTestProjectCount,
                WholeSuiteFallback: true,
                Skipped: false,
                Reason: $"impacted ({byProject.Count}) >= {threshold} of {totalTestProjectCount}; running whole suite",
                Mode: "roslyn");
        }

        // Per-test narrowing when we matched < 80% of any single
        // project's tests; per-project narrowing when we matched
        // most of them. The threshold check is intentionally
        // simple: count of matched FQNs vs total test methods in
        // the project.
        var rewrittenCommand = await BuildRewrittenCommandAsync(
            tokens, byProject, solution, ct).ConfigureAwait(false);

        return new ImpactedTestResolverResult(
            Command: rewrittenCommand,
            Rewritten: true,
            ImpactedProjects: SortRelativeProjectPaths(workspace, byProject.Keys),
            AllTestProjectCount: totalTestProjectCount,
            WholeSuiteFallback: false,
            Skipped: false,
            Reason: $"matched {impactedTestMethods.Count} test method(s) across {byProject.Count} of {totalTestProjectCount} test project(s)",
            Mode: "roslyn");
    }

    // ── Solution loading + caching ───────────────────────────────────────

    private static async Task<CachedSolution?> GetOrLoadSolutionAsync(string workspace, CancellationToken ct)
    {
        var headSha = await ComputeHeadShaAsync(workspace, ct).ConfigureAwait(false);
        var cacheKey = workspace + "|" + headSha;
        if (SolutionCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var entry = FindEntry(workspace);
        if (entry is null) return null;

        // MSBuildWorkspace expects the locator to be registered;
        // Program.cs does this at startup. If registration failed
        // there, MSBuildWorkspace.Create() throws InvalidOperationException
        // which the caller catches.
        var mb = MSBuildWorkspace.Create();
        Solution solution;
        try
        {
            if (entry.Value.IsSolution)
            {
                solution = await mb.OpenSolutionAsync(entry.Value.Paths[0], cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                // sln-less (traversal-based, e.g. build.proj / dirs.proj) repo:
                // load the SOURCE test projects (+ their transitive references)
                // so the reference graph AND test-project count are real.
                // Loading one arbitrary .csproj (the old behaviour) picked a
                // project under out\ on the FRP `update` repo, yielding a
                // 0-test-project graph that defeated narrowing entirely and ran
                // the whole suite.
                foreach (var projPath in entry.Value.Paths)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await mb.OpenProjectAsync(projPath, cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // One project failed to load (unrestored / malformed) —
                        // keep loading the rest so partial narrowing still works.
                    }
                }
                solution = mb.CurrentSolution;
            }
        }
        catch
        {
            mb.Dispose();
            throw;
        }

        var cachedEntry = new CachedSolution(solution, mb);
        SolutionCache[cacheKey] = cachedEntry;
        return cachedEntry;
    }

    /// <summary>
    /// Locate the workspace entry to load. Prefers a real <c>.sln</c> (root,
    /// then anywhere); for sln-less traversal repos (build.proj / dirs.proj)
    /// returns the set of SOURCE test projects to load so the reference graph
    /// and test-project count are complete. Build-output and intermediate
    /// directories (out/bin/obj/packages/TestResults/.git/…) are excluded so we
    /// never load a generated project out of <c>out\</c> (which silently
    /// defeated narrowing on the FRP repos).
    /// </summary>
    private static (bool IsSolution, IReadOnlyList<string> Paths)? FindEntry(string workspace)
    {
        var workspaceFull = Path.GetFullPath(workspace);

        var slns = SafeEnumerate(workspace, "*.sln")
            .Where(p => !IsUnderExcludedDir(p, workspaceFull))
            .ToList();
        var rootSln = slns.FirstOrDefault(p =>
            string.Equals(Path.GetDirectoryName(p), workspaceFull, StringComparison.OrdinalIgnoreCase));
        if (rootSln is not null) return (true, new[] { rootSln });
        if (slns.Count > 0) return (true, new[] { slns[0] });

        var csprojs = SafeEnumerate(workspace, "*.csproj")
            .Where(p => !IsUnderExcludedDir(p, workspaceFull))
            .ToList();
        var testProjs = csprojs.Where(IsTestProjectFile).ToList();
        if (testProjs.Count > 0) return (false, testProjs);
        if (csprojs.Count > 0) return (false, new[] { csprojs[0] });
        return null;
    }

    private static readonly string[] ExcludedDirSegments =
    {
        "out", "bin", "obj", "packages", "TestResults", "node_modules",
        ".git", ".vs", ".nuget", ".worktree",
    };

    private static bool IsUnderExcludedDir(string fullPath, string workspaceFull)
    {
        string rel;
        try { rel = Path.GetRelativePath(workspaceFull, fullPath); }
        catch { return false; }
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (var ex in ExcludedDirSegments)
            {
                if (string.Equals(seg, ex, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static List<string> SafeEnumerate(string workspace, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(workspace, pattern, SearchOption.AllDirectories).ToList();
        }
        catch
        {
            try { return Directory.EnumerateFiles(workspace, pattern, SearchOption.TopDirectoryOnly).ToList(); }
            catch { return new List<string>(); }
        }
    }

    /// <summary>
    /// Cheap pre-load test-project probe (MetadataReferences aren't available
    /// before the workspace loads): the project file references
    /// <c>Microsoft.NET.Test.Sdk</c> or sets <c>&lt;IsTestProject&gt;true</c>.
    /// Mirrors the file-content fallback in <see cref="IsTestProject"/>.
    /// </summary>
    private static bool IsTestProjectFile(string csprojPath)
    {
        try
        {
            var text = File.ReadAllText(csprojPath);
            return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComputeHeadShaAsync(string workspace, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return stdout.Trim();
        }
        catch
        {
            // No git, or workspace isn't a clone → don't cache.
            return string.Empty;
        }
    }

    // ── Reference walking helpers ────────────────────────────────────────

    private static async Task CollectDeclaredSymbolsAsync(
        Document doc, List<ISymbol> sink, CancellationToken ct)
    {
        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null) return;
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    {
                        var sym = model.GetDeclaredSymbol(method, ct);
                        if (sym is not null) sink.Add(sym);
                        break;
                    }
                case ClassDeclarationSyntax cls:
                    {
                        var sym = model.GetDeclaredSymbol(cls, ct);
                        if (sym is not null) sink.Add(sym);
                        break;
                    }
                case PropertyDeclarationSyntax prop:
                    {
                        var sym = model.GetDeclaredSymbol(prop, ct);
                        if (sym is not null) sink.Add(sym);
                        break;
                    }
            }
        }
    }

    private static async Task<(string ProjectPath, string FullyQualifiedName)?> GetEnclosingTestMethodAsync(
        ReferenceLocation loc, CancellationToken ct)
    {
        if (loc.Document is null) return null;
        var root = await loc.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return null;
        var node = root.FindNode(loc.Location.SourceSpan);
        var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null) return null;
        if (!TestAttributeDetector.IsTestMethod(method)) return null;
        var projPath = loc.Document.Project.FilePath;
        if (string.IsNullOrEmpty(projPath)) return null;
        var fqn = await GetFqnAsync(loc.Document, method, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(fqn)) return null;
        return (projPath, fqn);
    }

    private static async Task<string> GetFqnAsync(
        Document doc, MethodDeclarationSyntax method, CancellationToken ct)
    {
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null) return string.Empty;
        var sym = model.GetDeclaredSymbol(method, ct);
        if (sym is null) return string.Empty;
        // `Namespace.Class.Method` shape — matches `dotnet test --filter
        // FullyQualifiedName~...` semantics. Strip the global:: prefix
        // that Roslyn's default SymbolDisplayFormat sometimes inserts.
        var fqn = sym.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return fqn.StartsWith("global::", StringComparison.Ordinal)
            ? fqn["global::".Length..]
            : fqn;
    }

    // ── Test-project + total-count helpers ──────────────────────────────

    private static int CountTestProjects(Solution solution)
    {
        var count = 0;
        foreach (var proj in solution.Projects)
        {
            if (IsTestProject(proj)) count++;
        }
        return count;
    }

    private static bool IsTestProject(Project proj)
    {
        // The Roslyn way: a project is a test project iff it
        // references `Microsoft.NET.Test.Sdk` (every modern .NET
        // test runner carries it). Avoids the heuristic's
        // filename-based brittleness; same signal Roslyn's own
        // SDK detection uses.
        foreach (var meta in proj.MetadataReferences)
        {
            var name = Path.GetFileNameWithoutExtension(meta.Display ?? string.Empty);
            if (string.Equals(name, "Microsoft.TestPlatform.TestFramework", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "xunit.core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "xunit.v3.core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "nunit.framework", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Microsoft.VisualStudio.TestPlatform.TestFramework", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // Fall-back to project-file content scan when MetadataReferences
        // didn't resolve (happens when the workspace loaded but
        // NuGet restore didn't materialize the test SDK on disk).
        if (proj.FilePath is { } path && File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                if (text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // best-effort
            }
        }
        return false;
    }

    // ── Command-rewriting ────────────────────────────────────────────────

    private static async Task<string> BuildRewrittenCommandAsync(
        string[] tokens,
        IReadOnlyDictionary<string, List<string>> impactedByProject,
        Solution solution,
        CancellationToken ct)
    {
        // For a single project: emit `dotnet test {proj} --filter
        // "FullyQualifiedName~Foo.Bar|FullyQualifiedName~Foo.Baz"`
        // when the matched method set is < 80% of the project's
        // total test methods. Falls back to per-project narrowing
        // when matching is dense.
        var sb = new StringBuilder();
        sb.Append(tokens[0]);  // dotnet
        sb.Append(' ');
        sb.Append(tokens[1]);  // test

        if (impactedByProject.Count == 1)
        {
            var kv = impactedByProject.First();
            var totalInProj = await CountTestMethodsInProjectAsync(solution, kv.Key, ct).ConfigureAwait(false);
            var matched = kv.Value.Count;
            sb.Append(' ');
            sb.Append(QuoteIfNeeded(kv.Key));
            if (totalInProj > 0 && matched < (int)Math.Ceiling(totalInProj * 0.8))
            {
                sb.Append(" --filter \"");
                for (var i = 0; i < kv.Value.Count; i++)
                {
                    if (i > 0) sb.Append('|');
                    sb.Append("FullyQualifiedName~");
                    sb.Append(kv.Value[i]);
                }
                sb.Append('"');
            }
        }
        else
        {
            // Multiple projects — list them all. Per-test
            // narrowing across projects gets unwieldy; just
            // run each impacted project in full.
            foreach (var projPath in impactedByProject.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(' ');
                sb.Append(QuoteIfNeeded(projPath));
            }
        }

        // Preserve any trailing flags the operator supplied
        // (--logger trx, --verbosity minimal, etc.) — they live
        // in tokens[2..]. SKIP the positional project / solution /
        // traversal entry (build.proj / dirs.proj / *.csproj / *.sln):
        // Roslyn just REPLACED it with the impacted-project list above,
        // so re-appending it would re-run the whole traversal and defeat
        // the narrowing. Explicit *.csproj / *.sln overrides are already
        // short-circuited by HasOperatorOverrideArgs, but a traversal
        // *.proj is NOT an override (it's the normal sln-less entry point
        // on the FRP `update` repo) and must be dropped here — without
        // this, `dotnet test build.proj --filter "TestCategory!~…"`
        // narrowed to `dotnet test {impacted.csproj} build.proj …` and
        // ran everything anyway.
        for (var i = 2; i < tokens.Length; i++)
        {
            if (IsProjectLikeArg(tokens[i])) continue;
            sb.Append(' ');
            sb.Append(QuoteIfNeeded(tokens[i]));
        }

        return sb.ToString();
    }

    private static async Task<int> CountTestMethodsInProjectAsync(
        Solution solution, string projectPath, CancellationToken ct)
    {
        var proj = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
        if (proj is null) return 0;
        var count = 0;
        foreach (var doc in proj.Documents)
        {
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root is null) continue;
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (TestAttributeDetector.IsTestMethod(method)) count++;
            }
        }
        return count;
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    /// <summary>
    /// True when the token is a positional project / solution / traversal file
    /// (the entry point Roslyn replaces with the impacted-project list). Used to
    /// drop it from the rewritten command's trailing passthrough so narrowing
    /// isn't defeated by re-running the original entry point.
    /// </summary>
    private static bool IsProjectLikeArg(string token) =>
        token.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".proj", StringComparison.OrdinalIgnoreCase);

    private static bool HasOperatorOverrideArgs(string[] tokens)
    {
        for (var i = 2; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "--filter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "--list-tests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static (HashSet<string> AbsolutePaths, bool HitGlobal, string? Hit) NormaliseChangedFiles(
        string workspace, IReadOnlyList<string> changedFiles)
    {
        // Stay in lockstep with ImpactedTestResolver.GlobalImpactFileNames.
        // global.json + nuget.config were removed 2026-05-19 — they
        // affect build resolution but not the per-test call graph.
        var globalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
        };
        var abs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaceFull = Path.GetFullPath(workspace);
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = Path.GetFileName(raw);
            if (globalNames.Contains(name)) return (abs, true, name);
            if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return (abs, true, name);
            }
            var normalised = raw
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            string full;
            try
            {
                full = Path.IsPathRooted(normalised)
                    ? Path.GetFullPath(normalised)
                    : Path.GetFullPath(Path.Combine(workspaceFull, normalised));
            }
            catch
            {
                continue;
            }
            abs.Add(full);
        }
        return (abs, false, null);
    }

    private static IReadOnlyList<string> SortRelativeProjectPaths(string workspace, IEnumerable<string> absolutePaths)
    {
        var workspaceFull = Path.GetFullPath(workspace);
        var result = new List<string>();
        foreach (var abs in absolutePaths)
        {
            string rel;
            try { rel = Path.GetRelativePath(workspaceFull, abs); }
            catch { rel = abs; }
            result.Add(rel.Replace(Path.DirectorySeparatorChar, '/'));
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string QuoteIfNeeded(string s)
    {
        foreach (var ch in s)
        {
            if (ch == ' ' || ch == '\t') return $"\"{s}\"";
        }
        return s;
    }

    /// <summary>Cached MSBuildWorkspace + Solution pair keyed on workspace + HEAD SHA.</summary>
    private sealed class CachedSolution
    {
        public Solution Solution { get; }
        public MSBuildWorkspace Workspace { get; }
        public CachedSolution(Solution solution, MSBuildWorkspace workspace)
        {
            this.Solution = solution;
            this.Workspace = workspace;
        }
    }
}
