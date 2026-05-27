// -----------------------------------------------------------------------
// <copyright file="ImpactedTestResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 59 Stage A — pure helper that narrows a <c>dotnet test</c>
/// command to the test projects impacted by the worktree's changes
/// vs. the workstream's base ref. The motivation: an 8-min whole-
/// solution test run on a Stage that touched one file
/// (operator screenshot 2026-05-19 on
/// <c>stage-1.1-base-classes-and-port-allocation</c>) is wasted wall-
/// clock and triggers gate timeouts. By rewriting
/// <c>dotnet test</c> → <c>dotnet test {proj}</c> when exactly one test
/// project is impacted, the gate avoids unnecessary whole-suite runs for
/// common single-project changes. When multiple test projects are impacted,
/// the resolver keeps the original command because <c>dotnet test</c>
/// accepts only one positional project/solution target.
///
/// <para><b>No-op contracts</b> — the resolver returns the original
/// command unchanged (with <see cref="ImpactedTestResolverResult.Rewritten"/>=false)
/// in any of these cases:</para>
/// <list type="bullet">
///   <item>The command isn't a <c>dotnet test</c> invocation.</item>
///   <item>The command already names an explicit project (<c>*.csproj</c> /
///         <c>*.sln</c> / <c>*.slnf</c>) or carries <c>--filter</c> /
///         <c>--list-tests</c> (operator override wins).</item>
///   <item><paramref name="impactedOnly"/> is false (global / per-repo
///         off-switch).</item>
///   <item>No test projects exist in the workspace at all
///         (degenerate — nothing to narrow against).</item>
/// </list>
///
/// <para><b>Whole-suite fallback</b> — when the impacted test-project
/// set is ≥ <see cref="DefaultWholeSuiteThresholdRatio"/> (80%) of all
/// test projects under the workspace, the rewrite overhead isn't
/// worth it; the resolver returns the original command with
/// <see cref="ImpactedTestResolverResult.WholeSuiteFallback"/>=true and
/// <see cref="ImpactedTestResolverResult.ImpactedProjects"/> still
/// populated so the transcript can show what would have been chosen.
/// </para>
///
/// <para><b>Empty impacted set</b> — no changed files touched any
/// project (e.g. only docs / .gitignore changes). The resolver returns
/// <see cref="ImpactedTestResolverResult.Skipped"/>=true so the caller
/// emits <c>task.test-gate.skipped { reason = "no impacted test
/// projects" }</c> instead of running a green no-op.</para>
///
/// <para><b>Global-impact files</b> — changes to <c>*.sln</c>,
/// <c>Directory.Build.props</c>, <c>Directory.Build.targets</c>,
/// <c>Directory.Packages.props</c>, <c>global.json</c>, or
/// <c>nuget.config</c> escape the impacted-project narrowing because
/// they affect every project; the resolver falls back to the whole
/// suite when any of these is in the changed set.</para>
/// </summary>
public static class ImpactedTestResolver
{
    /// <summary>Ratio threshold at which "narrow to N out of M" collapses to a whole-suite run.</summary>
    public const double DefaultWholeSuiteThresholdRatio = 0.8;

    /// <summary>
    /// Filename suffixes that mark a csproj as a test project. Covers
    /// both plural (<c>Tests</c>) and singular (<c>Test</c>) variants
    /// because in-the-wild repos use both — Forge's own test projects
    /// follow the plural xUnit convention; the <c>update</c> /
    /// <c>FRP</c> repo (operator-pointed 2026-05-19) uses singular
    /// (<c>*.UnitTest.csproj</c>, <c>*.IntegrationTest.csproj</c>) plus
    /// non-Test-named runners like <c>CloudTest.csproj</c> /
    /// <c>Bvt.csproj</c> that are caught by
    /// <see cref="IsTestProjectByContent"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> TestProjectSuffixes = new[]
    {
        // Plural (xUnit / MSTest / NUnit canonical).
        ".Tests.csproj",
        ".UnitTests.csproj",
        ".IntegrationTests.csproj",
        ".FunctionalTests.csproj",
        ".E2ETests.csproj",
        ".SystemTests.csproj",
        // Singular variants used by Microsoft-internal repo conventions
        // (FRP, AzureStack, etc.).
        ".UnitTest.csproj",
        ".IntegrationTest.csproj",
        ".FunctionalTest.csproj",
        ".E2ETest.csproj",
        ".SystemTest.csproj",
    };

    /// <summary>
    /// Directory names that imply a csproj inside is a test project —
    /// matches the FRP repo's <c>**/UnitTest/Foo.UnitTest.csproj</c>
    /// convention. The csproj's PARENT directory must equal one of
    /// these (case-insensitive); deeper ancestors don't count (a
    /// <c>tests/Foo/src/Foo.csproj</c> nested layout is intentionally
    /// not classified as a test project — operators sometimes ship a
    /// helper library next to their tests).
    /// </summary>
    private static readonly IReadOnlyCollection<string> TestProjectDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UnitTest", "UnitTests",
        "IntegrationTest", "IntegrationTests",
        "FunctionalTest", "FunctionalTests",
        "E2ETest", "E2ETests",
        "SystemTest", "SystemTests",
        "Test", "Tests",
    };

    /// <summary>
    /// Content-based test-project signal — every modern .NET test
    /// project references <c>Microsoft.NET.Test.Sdk</c> (xUnit, MSTest,
    /// NUnit all flow through it). Also matches the canonical MSBuild
    /// flag <c>&lt;IsTestProject&gt;true&lt;/IsTestProject&gt;</c>
    /// which the SDK sets implicitly when the test sdk is referenced
    /// but operators can also set explicitly. Catches projects with
    /// non-conventional names (e.g. <c>CloudTest.csproj</c>,
    /// <c>Bvt.csproj</c> on the update repo) that no filename or
    /// directory heuristic would catch.
    /// </summary>
    private static readonly Regex TestSdkReferenceRegex = new(
        @"<PackageReference\s+Include\s*=\s*""Microsoft\.NET\.Test\.Sdk""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IsTestProjectFlagRegex = new(
        @"<IsTestProject\s*>\s*true\s*</IsTestProject\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Files that, when changed, force a whole-suite test run because
    /// they affect every project's compilation in the workspace.
    /// Match is by case-insensitive basename — operator config in any
    /// directory counts.
    ///
    /// <para>Conservative on purpose. <c>global.json</c> + <c>nuget.config</c>
    /// were originally in this list but caused operator pain on
    /// 2026-05-19 against the FRP `update` repo: the agent created a
    /// new <c>global.json</c> for an iter, the resolver fell back to
    /// the 31-test-project whole suite + hit the 8-min timeout. The
    /// actual failure was 1 test out of 632 in a single project.
    /// <c>global.json</c> pins the SDK version, <c>nuget.config</c>
    /// picks the package registry — neither flows into the per-test
    /// call graph, so a change to either doesn't change which tests
    /// are impacted. The <c>Directory.Build.*</c> +
    /// <c>Directory.Packages.props</c> trio stays because they
    /// inject MSBuild properties / package versions into every
    /// project's compilation and can genuinely change test
    /// behavior.</para>
    /// </summary>
    private static readonly IReadOnlyCollection<string> GlobalImpactFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
    };

    private static readonly Regex ProjectReferenceRegex = new(
        @"<ProjectReference\s+Include\s*=\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Resolve the impacted-test rewrite for a <c>dotnet test</c>
    /// command against a worktree with a known set of changed files.
    /// Pure — does not invoke git, does not touch the network. Test
    /// projects + reference graph are discovered by enumerating
    /// <c>*.csproj</c> under <paramref name="workspace"/>.
    /// </summary>
    /// <param name="workspace">Absolute path to the worktree root. Must exist on disk.</param>
    /// <param name="changedFiles">Worktree-relative paths of changed files
    /// (forward or back slashes both accepted).</param>
    /// <param name="originalCommand">The repo's resolved test command
    /// (e.g. <c>"dotnet test"</c>, <c>"dotnet test --logger trx"</c>).</param>
    /// <param name="impactedOnly">When false, the resolver no-ops and
    /// returns the original command. Wire from
    /// <c>RepoRow.TestImpactedOnly ?? config["Forge:Test:ImpactedOnly"]</c>.</param>
    /// <param name="wholeSuiteThresholdRatio">Override the 80% whole-
    /// suite fallback ratio. Defaults to <see cref="DefaultWholeSuiteThresholdRatio"/>.</param>
    public static ImpactedTestResolverResult Resolve(
        string workspace,
        IReadOnlyList<string> changedFiles,
        string originalCommand,
        bool impactedOnly = true,
        double wholeSuiteThresholdRatio = DefaultWholeSuiteThresholdRatio)
    {
        if (string.IsNullOrWhiteSpace(originalCommand))
        {
            return ImpactedTestResolverResult.NoOp(originalCommand ?? string.Empty, "empty test command");
        }
        if (!impactedOnly)
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "impacted-only disabled");
        }
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "workspace path does not exist");
        }

        var tokens = ParseCommandLine(originalCommand);
        if (tokens.Length == 0)
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "command did not parse to any tokens");
        }
        if (!IsDotnetTestCommand(tokens))
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "not a `dotnet test` command");
        }
        if (HasOperatorOverrideArgs(tokens, workspace, out var overrideReason, out var traversalProjIndex))
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, overrideReason);
        }

        // Enumerate all csproj files once, classify into test vs. prod,
        // and build the reverse ProjectReference graph.
        var allCsprojs = EnumerateCsprojFiles(workspace);
        if (allCsprojs.Count == 0)
        {
            return ImpactedTestResolverResult.NoOp(originalCommand, "no .csproj files under workspace");
        }

        // Single IO pass — read each csproj's text once, classify it
        // as test / prod via filename + directory + content heuristics,
        // and extract its <ProjectReference> edges into the reverse
        // graph.
        var (allTestProjects, referenceGraph) = ClassifyAndBuildGraph(allCsprojs);
        if (allTestProjects.Count == 0)
        {
            // No test projects to narrow against — let the original
            // command run (it'll either no-op or fail naturally).
            return ImpactedTestResolverResult.NoOp(originalCommand, "no test projects discovered under workspace");
        }

        // Normalise + classify the changed files. A change to a global-
        // impact file (Directory.Build.props, *.sln, global.json …)
        // means every project's compilation may differ — fall back to
        // the whole suite.
        var (changedAbs, hitGlobalImpact, globalImpactFile) = NormaliseChangedFiles(workspace, changedFiles);
        if (hitGlobalImpact)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: allTestProjects.Count,
                WholeSuiteFallback: true,
                Skipped: false,
                Reason: $"global-impact file changed ({globalImpactFile}); running whole suite");
        }
        if (changedAbs.Count == 0)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: allTestProjects.Count,
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: "no impacted test projects");
        }

        // Walk each changed file up to its owning csproj, then expand
        // through the reverse-reference graph. A change directly inside
        // a test project also counts (we add the test project itself).
        var testProjectSet = new HashSet<string>(allTestProjects, StringComparer.OrdinalIgnoreCase);
        var impacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prodImpacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedAbs)
        {
            var owning = FindOwningCsproj(file, allCsprojs);
            if (owning is null)
            {
                // File outside any project directory (e.g. README, docs).
                // Ignore — no project impact.
                continue;
            }
            if (testProjectSet.Contains(owning))
            {
                impacted.Add(owning);
            }
            else
            {
                prodImpacted.Add(owning);
            }
        }

        // Closure: walk the reverse-reference graph. A test project
        // that references any impacted prod project (transitively)
        // joins the set.
        ExpandTransitively(prodImpacted, referenceGraph, impacted, allTestProjects);

        if (impacted.Count == 0)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: allTestProjects.Count,
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: "no impacted test projects");
        }

        // 80% whole-suite fallback — if we'd be running most of the
        // suite anyway, the per-project loop overhead + lost
        // parallelism aren't worth it.
        var threshold = (int)Math.Ceiling(allTestProjects.Count * wholeSuiteThresholdRatio);
        if (impacted.Count >= threshold)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: SortRelative(workspace, impacted),
                AllTestProjectCount: allTestProjects.Count,
                WholeSuiteFallback: true,
                Skipped: false,
                Reason: $"impacted ({impacted.Count}) >= {threshold} of {allTestProjects.Count}; running whole suite");
        }

        var sortedRelative = SortRelative(workspace, impacted);
        if (sortedRelative.Count > 1)
        {
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: sortedRelative,
                AllTestProjectCount: allTestProjects.Count,
                WholeSuiteFallback: true,
                Skipped: false,
                Reason: $"matched {impacted.Count} of {allTestProjects.Count} test projects, but dotnet test accepts only one project target; running original command");
        }

        var rewrittenCommand = RewriteCommand(tokens, sortedRelative, traversalProjIndex);
        return new ImpactedTestResolverResult(
            Command: rewrittenCommand,
            Rewritten: true,
            ImpactedProjects: sortedRelative,
            AllTestProjectCount: allTestProjects.Count,
            WholeSuiteFallback: false,
            Skipped: false,
            Reason: $"matched {impacted.Count} of {allTestProjects.Count} test projects");
    }

    /// <summary>
    /// Discover the set of changed files in <paramref name="workspace"/>
    /// vs. <paramref name="baseRef"/> by invoking <c>git</c>. Combines
    /// committed deltas (<c>git diff --name-only {baseRef}</c>) with
    /// untracked files (<c>git ls-files --others --exclude-standard</c>)
    /// so the impacted set reflects the engineer agent's WIP, not just
    /// what's been committed so far.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DiscoverChangedFilesAsync(
        string workspace,
        string baseRef,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Array.Empty<string>();
        }
        if (string.IsNullOrWhiteSpace(baseRef))
        {
            return Array.Empty<string>();
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Committed + uncommitted tracked changes vs. base — the two-
        // dot form gives "what would land in a merge of HEAD into base"
        // which matches operator intent ("what did THIS WORKSTREAM
        // change?") even after a rebase / merge from base.
        var diffOut = await RunGitCaptureAsync(
            workspace, ct, "diff", "--name-only", "-z", baseRef);
        AddNonEmptyTokens(paths, diffOut, '\0');

        // Uncommitted unstaged + staged WIP vs. HEAD — the engineer
        // agent's in-progress edits.
        var diffHeadOut = await RunGitCaptureAsync(
            workspace, ct, "diff", "--name-only", "-z", "HEAD");
        AddNonEmptyTokens(paths, diffHeadOut, '\0');

        // Untracked files (respects .gitignore).
        var untrackedOut = await RunGitCaptureAsync(
            workspace, ct, "ls-files", "--others", "--exclude-standard", "-z");
        AddNonEmptyTokens(paths, untrackedOut, '\0');

        return paths.ToArray();
    }

    private static readonly Regex CompilationAffectingElementRegex = new(
        @"<\s*(TargetFrameworks?|LangVersion|DefineConstants|Nullable|OutputType|AssemblyName|RootNamespace|ProjectReference|FrameworkReference|RuntimeIdentifiers?|TargetPlatform\w*|Reference)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnalyzerMarkerRegex = new(
        @"BannedApiAnalyzer|BannedSymbols|<\s*AdditionalFiles\b|<\s*WarningsAsErrors\b|RS0030|IncludeAssets\s*=\s*""[^""]*analyzers|<\s*PackageReference[^>]*(Analyzer|BannedApi)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StructuralElementRegex = new(
        @"^</?\s*(Project|Choose|When|Otherwise|ItemGroup|PropertyGroup|NoWarn|WarningLevel|IncludeAssets|PrivateAssets)\b|^</",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 2026-05-26 — drop a <c>Directory.Build.props</c> / <c>.targets</c> change
    /// from <paramref name="changedFiles"/> when its diff is provably ANALYZER-ONLY
    /// (build-time only — e.g. wiring a BannedApiAnalyzer + BannedSymbols.txt). Such
    /// a change can only alter analyzer diagnostics, which the BUILD gate (whole-repo
    /// build) already validates; it cannot change runtime test behaviour, so forcing
    /// the entire (often flaky / multi-hour) suite via the global-impact trigger adds
    /// no signal and strands the workstream. Removing it lets the diff's real source
    /// files drive narrowing instead. <c>Directory.Packages.props</c> is NEVER relaxed
    /// (it pins dependency versions = genuinely compilation-affecting). Conservative:
    /// any uncertainty (git error, a removed/modified line, a compilation-affecting or
    /// unrecognized element) keeps the file → whole-suite. Root cause: common-infra
    /// stage-1.3 banned-api-analyzer wiring was forced whole-suite and failed on
    /// unrelated flaky integration tests (WordCount / Powershell / TestJea), 2026-05-26.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FilterCompilationNeutralGlobalFilesAsync(
        string workspace, string baseRef, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
    {
        if (changedFiles.Count == 0
            || string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)
            || string.IsNullOrWhiteSpace(baseRef))
        {
            return changedFiles;
        }

        List<string>? kept = null; // lazily built only when we actually drop something
        for (var i = 0; i < changedFiles.Count; i++)
        {
            var f = changedFiles[i];
            var name = Path.GetFileName(f);
            var relaxCandidate =
                string.Equals(name, "Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase);

            var drop = false;
            if (relaxCandidate)
            {
                try
                {
                    var diff = await RunGitCaptureAsync(workspace, ct, "diff", baseRef, "--", f);
                    if (string.IsNullOrWhiteSpace(diff))
                    {
                        diff = await RunGitCaptureAsync(workspace, ct, "diff", "HEAD", "--", f);
                    }
                    drop = IsAnalyzerOnlyBuildPropsDiff(diff);
                }
                catch (OperationCanceledException) { throw; }
                catch { drop = false; } // safe default: keep → whole-suite
            }

            if (drop)
            {
                kept ??= new List<string>(changedFiles.Take(i));
            }
            else
            {
                kept?.Add(f);
            }
        }
        return kept ?? changedFiles;
    }

    /// <summary>
    /// True only when a unified <c>git diff</c> of a global build file is provably
    /// build-time-only (analyzer / diagnostics wiring). Conservative — returns false
    /// (treat as compilation-affecting → whole-suite) on ANY of: empty diff, a removed
    /// or modified content line, a compilation-affecting element (TargetFramework,
    /// DefineConstants, a non-analyzer PackageReference, …), or an unrecognized element.
    /// Returns true only for a purely-additive diff whose added XML is limited to
    /// analyzer/diagnostic + structural elements (and comments) AND includes at least
    /// one analyzer marker. Internal for unit testing.
    /// </summary>
    internal static bool IsAnalyzerOnlyBuildPropsDiff(string? diffText)
    {
        if (string.IsNullOrWhiteSpace(diffText)) return false;
        var sawAnalyzer = false;
        var inComment = false;
        foreach (var rawLine in diffText!.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("@@")
                || line.StartsWith("diff ") || line.StartsWith("index ")
                || line.StartsWith("new file") || line.StartsWith("deleted file")
                || line.StartsWith("similarity ") || line.StartsWith("rename "))
            {
                continue;
            }

            var sign = line[0];
            if (sign != '+' && sign != '-') continue; // unchanged context line
            var content = line.Substring(1).Trim();
            if (content.Length == 0) continue;

            // A removed/modified content line means existing build config changed —
            // can't prove neutral (the analyzer-wiring case is purely additive).
            if (sign == '-') return false;

            // Added line — track XML comment spans so prose inside <!-- --> is benign.
            if (inComment)
            {
                if (content.Contains("-->")) inComment = false;
                continue;
            }
            if (content.StartsWith("<!--"))
            {
                if (!content.Contains("-->")) inComment = true;
                continue;
            }
            // Pure comment / plain text (no XML tag) is benign.
            if (!content.Contains('<')) continue;

            if (CompilationAffectingElementRegex.IsMatch(content)) return false;
            if (AnalyzerMarkerRegex.IsMatch(content)) { sawAnalyzer = true; continue; }
            if (StructuralElementRegex.IsMatch(content)) continue;
            // Unrecognized XML element in a global build file → conservative.
            return false;
        }
        return sawAnalyzer;
    }

    /// <summary>
    /// Quote-aware command-line splitter. Mirrors
    /// <c>StoryBuildRunner.ParseCommandLine</c> intentionally — both
    /// classes need the same semantics and a duplicated 15-line helper
    /// is cheaper than introducing a shared utility namespace.
    /// </summary>
    internal static string[] ParseCommandLine(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if ((ch == ' ' || ch == '\t') && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    private static bool IsDotnetTestCommand(string[] tokens)
    {
        if (tokens.Length < 2) return false;
        // Tolerate dotnet path with .exe suffix, full paths, or just
        // "dotnet" on PATH. The verb token must be exactly "test".
        var prog = Path.GetFileNameWithoutExtension(tokens[0]);
        return string.Equals(prog, "dotnet", StringComparison.OrdinalIgnoreCase)
               && string.Equals(tokens[1], "test", StringComparison.OrdinalIgnoreCase);
    }

    // 2026-05-25 — Phase 59 Stage A refinement. The previous override
    // policy was too coarse: ANY `--filter` token caused a hard no-op,
    // even when the filter was an orthogonal category exclusion
    // (e.g. `TestCategory!~CTQ&TestCategory!~Env`) that should compose
    // with project narrowing. Similarly, Traversal SDK `*.proj` files
    // (e.g. `build.proj` with `Sdk="Microsoft.Build.Traversal/2.x"`)
    // were preserved verbatim during rewrite, so the rewritten command
    // STILL re-included every project in `dirs.proj`. This classifier
    // now: (a) treats Traversal `.proj` as a REPLACEABLE token (records
    // its index so RewriteCommand can drop just that one), (b) treats
    // pure-exclusion `--filter` values (only `!=`/`!~` conditions) as
    // narrowing-safe so they compose, (c) still hard-overrides on
    // explicit `.csproj` / `.sln*`, `--list-tests`, inclusion / FQN
    // filters, custom non-Traversal `.proj` orchestrators, and the
    // `--filter` + no-Traversal-anchor combination (since plain
    // `dotnet test --filter X` may mean FQN-narrowing across all
    // projects — too ambiguous to second-guess).
    private static bool HasOperatorOverrideArgs(
        string[] tokens,
        string workspace,
        out string reason,
        out int traversalProjIndex)
    {
        traversalProjIndex = -1;
        var hasFilter = false;
        var filterValue = string.Empty;
        string filterReason = string.Empty;

        // tokens[0] = "dotnet", tokens[1] = "test"; scan the rest.
        for (var i = 2; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                reason = "command already names a .csproj";
                return true;
            }
            if (t.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                reason = "command already names a solution file";
                return true;
            }
            if (t.EndsWith(".proj", StringComparison.OrdinalIgnoreCase)
                && !t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && LooksLikePositionalProjectToken(t))
            {
                // *.proj that isn't *.csproj and isn't an option-value
                // (e.g. `-p:File=foo.proj`). If it's a Traversal SDK
                // file, mark it for replacement during rewrite. Custom
                // .proj orchestrators (no Traversal SDK marker) remain
                // a hard override — we can't safely replace them.
                if (IsTraversalProjFile(workspace, t))
                {
                    // First Traversal proj wins; treat any subsequent
                    // .proj as override (operator may have something
                    // specific in mind we don't understand).
                    if (traversalProjIndex >= 0)
                    {
                        reason = "command names multiple .proj targets";
                        return true;
                    }
                    traversalProjIndex = i;
                    continue;
                }
                reason = "command names a custom (non-Traversal) .proj";
                return true;
            }
            if (string.Equals(t, "--filter", StringComparison.OrdinalIgnoreCase))
            {
                hasFilter = true;
                filterReason = "command already carries --filter";
                if (i + 1 < tokens.Length)
                {
                    filterValue = tokens[i + 1];
                }
                continue;
            }
            if (string.Equals(t, "--list-tests", StringComparison.OrdinalIgnoreCase))
            {
                reason = "command is a --list-tests probe";
                return true;
            }
        }

        // --filter narrows tests within a project; project narrowing
        // narrows the project set. Composition is only safe when:
        //   (a) a Traversal .proj anchors operator intent ("across all
        //       projects, filtered") — without an anchor, the operator
        //       may have meant FQN-narrowing across the whole workspace;
        //       narrowing the project set could vacuously pass.
        //   (b) the filter is pure exclusion (every condition uses
        //       `!=` or `!~`) — inclusion (`=` / `~`) targets a
        //       specific value that might live in a non-impacted
        //       project, also a vacuous-pass risk.
        if (hasFilter)
        {
            if (traversalProjIndex < 0)
            {
                reason = filterReason + " without a Traversal .proj anchor";
                return true;
            }
            if (!IsFilterNarrowingSafe(filterValue))
            {
                reason = filterReason + " with non-exclusion conditions (may vacuous-pass if narrowed)";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// True iff <paramref name="token"/> looks like a positional
    /// project-file argument (`build.proj`, `src/build.proj`) rather
    /// than an MSBuild option value (`-p:Prop=foo.proj` or
    /// `/p:Prop=foo.proj`).
    /// </summary>
    private static bool LooksLikePositionalProjectToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token[0] == '-' || token[0] == '/') return false;
        // -p:Foo=bar.proj would have been caught above, but a bare
        // `Foo=bar.proj` token is also clearly an assignment.
        if (token.Contains('=')) return false;
        return true;
    }

    /// <summary>
    /// Resolve <paramref name="tokenPath"/> against
    /// <paramref name="workspace"/>, read the file, and return true if
    /// it imports the <c>Microsoft.Build.Traversal</c> SDK (the
    /// canonical multi-project orchestrator). Returns false on any IO
    /// error, missing file, or absent SDK marker — the resolver's
    /// classifier then treats the token as a hard operator override.
    /// </summary>
    internal static bool IsTraversalProjFile(string workspace, string tokenPath)
    {
        if (string.IsNullOrWhiteSpace(tokenPath)) return false;
        string absolutePath;
        try
        {
            absolutePath = Path.IsPathRooted(tokenPath)
                ? tokenPath
                : Path.GetFullPath(Path.Combine(workspace ?? string.Empty, tokenPath));
        }
        catch
        {
            return false;
        }
        if (!File.Exists(absolutePath)) return false;

        string contents;
        try
        {
            // Cheap read — Traversal proj files are tiny (sub-KB
            // typically). No size guard needed.
            contents = File.ReadAllText(absolutePath);
        }
        catch
        {
            return false;
        }
        return contents.Contains("Microsoft.Build.Traversal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True iff <paramref name="filterValue"/> is composed entirely of
    /// exclusion conditions (`!=` / `!~`) joined by `&` / `|`. Any
    /// inclusion operator (`=` / `~` not preceded by `!`) marks the
    /// filter as potentially FQN-targeted, in which case narrowing the
    /// project set could vacuously pass if the targeted test lives in a
    /// non-impacted project.
    /// </summary>
    internal static bool IsFilterNarrowingSafe(string filterValue)
    {
        if (string.IsNullOrEmpty(filterValue)) return true;
        for (var i = 0; i < filterValue.Length; i++)
        {
            var ch = filterValue[i];
            if (ch != '=' && ch != '~') continue;

            // Walk back past whitespace to find the previous non-space
            // char — vstest accepts `TestCategory != Slow` with spaces.
            var j = i - 1;
            while (j >= 0 && (filterValue[j] == ' ' || filterValue[j] == '\t')) j--;
            if (j < 0 || filterValue[j] != '!')
            {
                return false;
            }
        }
        return true;
    }

    private static List<string> EnumerateCsprojFiles(string workspace)
    {
        // Skip build output + IDE noise to keep the scan fast on large
        // repos. The patterns mirror IsBuildArtifactPath in
        // RealStoryGitOps so the resolver agrees with the snapshotter
        // about what is "real" source.
        var result = new List<string>();
        EnumerateCsprojRecursive(workspace, result);
        return result;
    }

    private static void EnumerateCsprojRecursive(string dir, List<string> sink)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }
        foreach (var f in files) sink.Add(f);

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return;
        }
        foreach (var sub in subDirs)
        {
            var name = Path.GetFileName(sub);
            if (IsSkippableDirectoryName(name)) continue;
            EnumerateCsprojRecursive(sub, sink);
        }
    }

    private static bool IsSkippableDirectoryName(string name)
    {
        // Hidden + well-known build output. Matches IsBuildArtifactPath
        // shape in RealStoryGitOps; kept local to avoid coupling.
        if (string.IsNullOrEmpty(name)) return true;
        if (name[0] == '.') return true;
        return name switch
        {
            "bin" or "obj" or "node_modules" or "target" or
            "dist" or "out" or "build" or "Debug" or "Release" or
            "TestResults" or "coverage" or "packages" or "pkg" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Filename-only test-project check (fast path; no IO). Used for
    /// quick classification when csproj text isn't already in hand.
    /// The richer multi-signal classifier is
    /// <see cref="ClassifyAndBuildGraph"/>, which combines filename +
    /// directory + content into one IO pass.
    /// </summary>
    internal static bool IsTestProject(string csprojPath)
    {
        if (IsTestProjectByFilename(csprojPath)) return true;
        if (IsTestProjectByDirectory(csprojPath)) return true;
        // Content-based detection requires reading the file — skip it
        // here so the helper stays IO-free; callers that have the
        // text in hand use IsTestProjectByContent directly.
        return false;
    }

    private static bool IsTestProjectByFilename(string csprojPath)
    {
        var name = Path.GetFileName(csprojPath);
        foreach (var suffix in TestProjectSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsTestProjectByDirectory(string csprojPath)
    {
        var dir = Path.GetDirectoryName(csprojPath);
        if (string.IsNullOrEmpty(dir)) return false;
        var parentName = Path.GetFileName(dir);
        return !string.IsNullOrEmpty(parentName)
               && TestProjectDirectoryNames.Contains(parentName);
    }

    private static bool IsTestProjectByContent(string csprojText)
    {
        if (string.IsNullOrEmpty(csprojText)) return false;
        return TestSdkReferenceRegex.IsMatch(csprojText)
               || IsTestProjectFlagRegex.IsMatch(csprojText);
    }

    /// <summary>
    /// Single-pass csproj classifier + reference-graph builder. Reads
    /// each csproj's text once and:
    /// <list type="bullet">
    ///   <item>Classifies it as a test project via filename, directory,
    ///         OR content heuristic (any one suffices).</item>
    ///   <item>Extracts every <c>&lt;ProjectReference&gt;</c> edge into
    ///         the reverse graph
    ///         (<c>prodCsproj → set of csprojs that reference it</c>).</item>
    /// </list>
    /// Combining the passes avoids a second open-read-close cycle per
    /// csproj on large repos (the FRP / update repo carries ~250
    /// csprojs; halving IO matters on a cold filesystem cache).
    /// </summary>
    private static (List<string> TestProjects, IReadOnlyDictionary<string, HashSet<string>> ReverseGraph)
        ClassifyAndBuildGraph(IReadOnlyList<string> allCsprojs)
    {
        var testProjects = new List<string>();
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in allCsprojs)
        {
            string text;
            try
            {
                text = File.ReadAllText(csproj);
            }
            catch
            {
                // Unreadable csproj — classify by filename / directory
                // only so we still pick up the obvious cases.
                if (IsTestProject(csproj)) testProjects.Add(csproj);
                continue;
            }

            if (IsTestProjectByFilename(csproj)
                || IsTestProjectByDirectory(csproj)
                || IsTestProjectByContent(text))
            {
                testProjects.Add(csproj);
            }

            var csprojDir = Path.GetDirectoryName(csproj)!;
            foreach (Match m in ProjectReferenceRegex.Matches(text))
            {
                var rel = m.Groups[1].Value
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                string targetAbs;
                try
                {
                    targetAbs = Path.GetFullPath(Path.Combine(csprojDir, rel));
                }
                catch
                {
                    continue;
                }
                if (!map.TryGetValue(targetAbs, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[targetAbs] = set;
                }
                set.Add(csproj);
            }
        }
        return (testProjects, map);
    }

    private static (HashSet<string> AbsolutePaths, bool HitGlobal, string? Hit) NormaliseChangedFiles(
        string workspace,
        IReadOnlyList<string> changedFiles)
    {
        var abs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaceFull = Path.GetFullPath(workspace);
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = Path.GetFileName(raw);

            // Global-impact files short-circuit the whole resolver.
            if (GlobalImpactFileNames.Contains(name))
            {
                return (abs, true, name);
            }
            // *.sln / *.slnf / global.json variants treated as global impact too.
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

    private static string? FindOwningCsproj(string absoluteFilePath, IReadOnlyList<string> allCsprojs)
    {
        // Pick the csproj whose directory is the longest prefix of the
        // file path. Linear scan is fine — N is small (single-digit
        // hundreds in the worst Forge repo today).
        string? best = null;
        var bestLen = 0;
        foreach (var csproj in allCsprojs)
        {
            var dir = Path.GetDirectoryName(csproj);
            if (string.IsNullOrEmpty(dir)) continue;
            // Append the separator so a/b/Foo doesn't match a/b/FooBar.
            var dirWithSep = dir.EndsWith(Path.DirectorySeparatorChar)
                ? dir
                : dir + Path.DirectorySeparatorChar;
            if (absoluteFilePath.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase)
                && dirWithSep.Length > bestLen)
            {
                best = csproj;
                bestLen = dirWithSep.Length;
            }
        }
        return best;
    }

    private static void ExpandTransitively(
        HashSet<string> prodImpacted,
        IReadOnlyDictionary<string, HashSet<string>> reverseGraph,
        HashSet<string> impactedTests,
        IReadOnlyList<string> allTestProjects)
    {
        // Standard BFS — start with prodImpacted, expand into any
        // csproj that references it (test OR prod). Tests we collect;
        // intermediate prods we keep walking from so a test that
        // depends transitively on a deeply-buried changed prod still
        // joins the set.
        var allTestSet = new HashSet<string>(allTestProjects, StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<string>(prodImpacted);
        var visited = new HashSet<string>(prodImpacted, StringComparer.OrdinalIgnoreCase);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (!reverseGraph.TryGetValue(current, out var dependents)) continue;
            foreach (var dep in dependents)
            {
                if (!visited.Add(dep)) continue;
                if (allTestSet.Contains(dep))
                {
                    impactedTests.Add(dep);
                }
                else
                {
                    // Prod project that references the changed prod —
                    // its consumers may include tests; keep walking.
                    frontier.Enqueue(dep);
                }
            }
        }
    }

    private static IReadOnlyList<string> SortRelative(string workspace, IEnumerable<string> absolutePaths)
    {
        var workspaceFull = Path.GetFullPath(workspace);
        var result = new List<string>();
        foreach (var abs in absolutePaths)
        {
            string rel;
            try
            {
                rel = Path.GetRelativePath(workspaceFull, abs);
            }
            catch
            {
                rel = abs;
            }
            // Normalise to forward slashes so the rewritten command is
            // identical on Windows and Linux (cosmetic — dotnet test
            // accepts either, but the transcript shows it verbatim).
            result.Add(rel.Replace(Path.DirectorySeparatorChar, '/'));
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string RewriteCommand(
        string[] tokens,
        IReadOnlyList<string> impactedRelative,
        int traversalProjIndex)
    {
        // Insert the project paths right after `dotnet test`. Quote any
        // path with whitespace defensively, even though normalised
        // forward-slash paths rarely contain spaces under the worktree
        // (forge_v2 will deal with truly weird paths if they show up).
        var sb = new StringBuilder();
        sb.Append(tokens[0]);    // dotnet
        sb.Append(' ');
        sb.Append(tokens[1]);    // test
        foreach (var proj in impactedRelative)
        {
            sb.Append(' ');
            sb.Append(NeedsQuoting(proj) ? $"\"{proj}\"" : proj);
        }
        for (var i = 2; i < tokens.Length; i++)
        {
            // 2026-05-25 — drop the Traversal SDK `.proj` token (e.g.
            // `build.proj`) identified by the classifier. We're
            // replacing "run all projects via Traversal" with the
            // narrowed impacted set; preserving the Traversal target
            // would re-include every project and defeat the rewrite.
            if (i == traversalProjIndex) continue;

            sb.Append(' ');
            sb.Append(NeedsQuoting(tokens[i]) ? $"\"{tokens[i]}\"" : tokens[i]);
        }
        return sb.ToString();
    }

    private static bool NeedsQuoting(string s)
    {
        // 2026-05-25 — extended beyond whitespace to cover cmd.exe shell
        // metacharacters. The session-script execution path
        // (`StoryBuildRunner.WriteSessionScript` → `cmd /c <script.cmd>`)
        // dumps the rewritten command verbatim into a .cmd file; in cmd,
        // `&` chains commands and `|` pipes them, so an unquoted
        // `--filter TestCategory!~CTQ&TestCategory!~Env` would split
        // into two and corrupt the gate. Quoting on emit keeps the
        // command round-trip-safe across BOTH execution paths (direct
        // ProcessStartInfo with re-parse, and the session-script path).
        foreach (var ch in s)
        {
            if (ch == ' ' || ch == '\t') return true;
            if (ch == '&' || ch == '|' || ch == ';') return true;
            if (ch == '<' || ch == '>') return true;
            if (ch == '(' || ch == ')') return true;
            if (ch == '^' || ch == '%') return true;
            if (ch == '"' || ch == '`') return true;
        }
        return false;
    }

    private static void AddNonEmptyTokens(HashSet<string> sink, string output, char separator)
    {
        if (string.IsNullOrEmpty(output)) return;
        foreach (var raw in output.Split(separator))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            sink.Add(raw.Trim());
        }
    }

    private static async Task<string> RunGitCaptureAsync(
        string workspace, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch
        {
            return string.Empty;
        }
        if (proc is null) return string.Empty;
        using var _ = proc;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        // Drain stderr so the pipe doesn't fill + block — we don't
        // surface git errors here, just whatever stdout managed to
        // produce.
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return string.Empty;
        }

        string stdout = string.Empty;
        try { stdout = await stdoutTask; } catch { }
        try { await stderrTask; } catch { }
        return stdout;
    }
}

/// <summary>
/// Phase 59 — outcome of an <see cref="ImpactedTestResolver.Resolve"/>
/// call. Returned to the caller so the gate chain knows whether to
/// emit a skipped event, run the rewritten command, or fall through
/// to the original whole-suite invocation.
///
/// <para>Phase 59 Stage C — <see cref="Mode"/> identifies which
/// provider in the resolver chain produced this result. Stamped on
/// the gate-event payload so the UI's 📎 chip can show
/// `mode: roslyn` (precise, call-graph based) vs `mode: heuristic`
/// (conservative, filename + content scan) at a glance. Additive
/// default `"heuristic"` keeps every Stage A construction valid
/// while the chain rolls in.</para>
/// </summary>
public sealed record ImpactedTestResolverResult(
    string Command,
    bool Rewritten,
    IReadOnlyList<string> ImpactedProjects,
    int AllTestProjectCount,
    bool WholeSuiteFallback,
    bool Skipped,
    string Reason,
    string Mode = "heuristic")
{
    /// <summary>
    /// Shorthand constructor for a pure no-op (resolver fell through to
    /// the original command without ever computing an impacted set).
    /// </summary>
    public static ImpactedTestResolverResult NoOp(string originalCommand, string reason, string mode = "heuristic") =>
        new(
            Command: originalCommand,
            Rewritten: false,
            ImpactedProjects: Array.Empty<string>(),
            AllTestProjectCount: 0,
            WholeSuiteFallback: false,
            Skipped: false,
            Reason: reason,
            Mode: mode);
}
