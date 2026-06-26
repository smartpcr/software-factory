// -----------------------------------------------------------------------
// <copyright file="RalphLoopPrompt.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System;
using System.IO;
using System.Text;

/// <summary>
/// Loads the <c>ralph-loop</c> skill (<c>.claude/skills/ralph-loop/SKILL.md</c>)
/// at runtime and frames it as an injectable generator-prompt block. Used by the
/// story gen/eval loop to push a STALLED generator (3+ iterations without
/// reaching pass) onto the ralph-loop discipline: assess → act on the single
/// highest-priority blocker → verify → make measurable progress, and if the
/// blocker is genuinely out of scope, SAY SO instead of thrashing.
///
/// <para>The skill file is the single source of truth — it is committed to the
/// repo and (via a csproj <c>Content</c> link) copied next to the binary, so it
/// is read from disk at runtime rather than embedded. Resolution order:
/// (1) <c>FORGE_RALPH_LOOP_SKILL</c> env override, (2) the bundled copy beside the
/// binary, (3) walk up from the base directory to the repo's
/// <c>.claude/skills/ralph-loop/SKILL.md</c>. Missing file ⇒ <c>null</c> (the
/// caller simply skips injection — never fatal). The framed block is computed
/// once and cached.</para>
/// </summary>
public static class RalphLoopPrompt
{
    private const string RelSkillPath = @".claude\skills\ralph-loop\SKILL.md";
    private static readonly object Gate = new();
    private static bool resolved;
    private static string? cachedBlock;

    /// <summary>
    /// The framed ralph-loop guidance block to inject into a stalled generator's
    /// prompt, or <c>null</c> when the skill file can't be located. Cached.
    /// </summary>
    public static string? TryGetGuidanceBlock()
    {
        if (resolved)
        {
            return cachedBlock;
        }

        lock (Gate)
        {
            if (resolved)
            {
                return cachedBlock;
            }

            try
            {
                var path = LocateSkillFile();
                cachedBlock = path is null ? null : Frame(StripFrontMatter(File.ReadAllText(path)));
            }
            catch
            {
                cachedBlock = null;
            }

            resolved = true;
            return cachedBlock;
        }
    }

    /// <summary>Test seam: reset the cache so a test can re-resolve.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            resolved = false;
            cachedBlock = null;
        }
    }

    private static string? LocateSkillFile()
    {
        var overridePath = Environment.GetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        // Bundled beside the binary (csproj Content link), then walk up to the
        // repo source so a dev run (binary under src/forge/Forge.Api/bin/…) still
        // finds the committed skill.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, RelSkillPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        }

        return null;
    }

    /// <summary>Strip a leading YAML front-matter block (<c>---</c> … <c>---</c>).</summary>
    internal static string StripFrontMatter(string text)
    {
        var t = text.TrimStart('﻿', '\r', '\n', ' ', '\t');
        if (!t.StartsWith("---", StringComparison.Ordinal))
        {
            return text;
        }

        // Find the closing fence on its own line after the opening one.
        var idx = t.IndexOf('\n');
        while (idx >= 0)
        {
            var lineStart = idx + 1;
            var lineEnd = t.IndexOf('\n', lineStart);
            var line = (lineEnd < 0 ? t[lineStart..] : t[lineStart..lineEnd]).Trim();
            if (line == "---")
            {
                return lineEnd < 0 ? string.Empty : t[(lineEnd + 1)..].TrimStart('\r', '\n');
            }

            idx = lineEnd;
        }

        return text;
    }

    private static string Frame(string skillBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  PERSISTENCE DISCIPLINE (ralph-loop) — 3+ ITERS, NOT DONE  ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("You have had 3+ iterations and this workstream is still not passing.");
        sb.AppendLine("Apply the loop discipline below. IMPORTANT — Forge runs the OUTER loop and");
        sb.AppendLine("scores each of your turns, so you get ONE turn per iteration: do NOT emit");
        sb.AppendLine("`DONE` or `[Iteration N/20]` markers. Instead, THIS turn:");
        sb.AppendLine("  1. ASSESS what is actually blocking a passing score (read the gate/eval");
        sb.AppendLine("     output above and your prior-iter notes — don't re-explore from scratch).");
        sb.AppendLine("  2. ACT on the single highest-priority blocker.");
        sb.AppendLine("  3. VERIFY it yourself (run the gate command) before responding.");
        sb.AppendLine("  4. If the blocker is genuinely OUTSIDE your scope/control (e.g. an");
        sb.AppendLine("     inherited failure in files you don't own), SAY SO explicitly in your");
        sb.AppendLine("     Iteration Summary rather than thrashing on unrelated files.");
        sb.AppendLine();
        sb.AppendLine(skillBody.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("────────────────────────────────────────────────────────────");
        sb.AppendLine();
        return sb.ToString();
    }
}
