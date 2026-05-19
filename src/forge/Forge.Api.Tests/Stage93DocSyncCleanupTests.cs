// -----------------------------------------------------------------------
// <copyright file="Stage93DocSyncCleanupTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 9.3 — doc-sync cleanup pins. Two test
/// scenarios from the Stage 9.3 spec:
///
/// <list type="bullet">
///   <item><c>[unit]</c> <c>PlanDocBriefConsistencyTests</c> still
///         passes against the new <c>e2e-scenarios</c> brief — covered
///         transitively by the existing
///         <see cref="PlanDocBriefConsistencyTests"/> file (every Stage
///         test sweep runs it, no change needed here).</item>
///   <item><c>[integration]</c> No grep hits in <c>src/</c> for stale
///         <c>DriveQaAsync.*NEW behaviour</c> markers — covered by
///         <see cref="NoStaleNewBehaviourMarkersInSrc"/> below.</item>
/// </list>
///
/// <para>The cleanup test is intentionally cheap (just walks the
/// `src/` tree and greps source files). Adding it as a test rather
/// than a manual grep means a future contributor who reintroduces a
/// "NEW behaviour" comment during an incremental migration sees the
/// CI red on their PR instead of relying on humans noticing during
/// review.</para>
/// </summary>
public class Stage93DocSyncCleanupTests
{
    // ── 1. No stale `DriveQaAsync.*NEW behaviour` markers in src/ ───────

    [Fact]
    public void NoStaleNewBehaviourMarkersInSrc()
    {
        var repoRoot = LocateRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        Assert.True(Directory.Exists(srcDir), $"Expected `src/` at {srcDir}");

        // Walk every .cs / .ts / .tsx / .md file under src/ — this is
        // the canonical home for production code + co-located docs the
        // cleanup criterion is about. Skip build artefacts (`bin/`,
        // `obj/`, `node_modules/`, `dist/`) so the assertion times in
        // ms not seconds.
        var extensions = new[] { ".cs", ".ts", ".tsx", ".md" };
        var skipped = new[] { "bin", "obj", "node_modules", "dist", ".worktree", ".story-artifacts" };
        var pattern = new Regex(
            @"DriveQaAsync.*NEW\s+behaviour",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var hits = new List<string>();
        foreach (var path in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            if (path.Split(Path.DirectorySeparatorChar).Any(seg => skipped.Contains(seg, StringComparer.OrdinalIgnoreCase))) continue;

            // Skip the test file itself — it carries the literal pattern
            // in a code-as-string comment so a grep would self-trigger.
            if (path.EndsWith("Stage93DocSyncCleanupTests.cs", StringComparison.OrdinalIgnoreCase)) continue;

            string body;
            try { body = File.ReadAllText(path); }
            catch { continue; } // perms / file-lock — skip per CLAUDE.md error-handling rule

            foreach (Match m in pattern.Matches(body))
            {
                hits.Add($"{path} :: {m.Value}");
            }
        }

        Assert.True(hits.Count == 0,
            "Stage 9.3 cleanup criterion: no `DriveQaAsync.*NEW behaviour` markers "
            + "should remain in `src/`. The incremental migration used these as "
            + "scaffolding; the Stage 9.3 doc-sync removed them. Hits:\n  "
            + string.Join("\n  ", hits));
    }

    // ── 2. PlanDocBriefConsistencyTests still covers the e2e-scenarios brief ──
    //
    // The headline Stage 9.3 contract — the e2e-scenarios brief in
    // BuildPlanDocSpecs survives the doc-sync. Covered transitively
    // (every test sweep runs PlanDocBriefConsistencyTests), but the
    // assertion below pins the specific contract that Stage 9.3's
    // doc edits MUST NOT break: the brief still exists for the
    // `e2e-scenarios` key + the spec dictionary still carries all
    // four canonical plan docs.

    [Fact]
    public void PlanDocSpecs_Still_Carries_The_E2eScenarios_Brief_After_DocSync()
    {
        var specs = Forge.Api.Services.Stories.CrewStoryArtifactStrategy
            .BuildPlanDocSpecs("docs/stories/stage93");

        Assert.Equal(4, specs.Count);
        Assert.Contains("e2e-scenarios", specs.Keys);

        // The e2e-scenarios brief must still mandate the §8 setup
        // contract — Stage 6.1 added these requirements + Stage 9.3
        // is the doc-sync gate that ensures they aren't accidentally
        // reverted. Token-level pins so the brief can be rewritten
        // freely as long as the contract stays.
        var brief = specs["e2e-scenarios"].Brief;
        Assert.Contains("Setup", brief);
        Assert.Contains("inline", brief);
        Assert.Contains("compose", brief);
        Assert.Contains("lab-wsfc", brief);
        Assert.Contains("lab-bare-metal", brief);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Walk parent directories until we find one carrying `src/` and
    /// `docs/forge/e2e.md`. Test binaries land at
    /// `src/forge/Forge.Api.Tests/bin/Debug/net8.0/win-x64/...`, so
    /// the walk has to ascend several levels to reach the repo root.
    /// Falls back to <c>AppContext.BaseDirectory</c> if neither marker
    /// is found within 12 levels — keeps the test self-contained even
    /// from a relocated test runner.
    /// </summary>
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var srcMarker = Path.Combine(dir.FullName, "src");
            var docMarker = Path.Combine(dir.FullName, "docs", "forge", "e2e.md");
            if (Directory.Exists(srcMarker) && File.Exists(docMarker)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Stage 9.3 cleanup test couldn't locate repo root (no parent of "
            + $"{AppContext.BaseDirectory} carries both `src/` and `docs/forge/e2e.md`).");
    }
}
