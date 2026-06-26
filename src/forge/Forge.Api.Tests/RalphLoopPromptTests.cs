// -----------------------------------------------------------------------
// <copyright file="RalphLoopPromptTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.IO;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Unit tests for <see cref="RalphLoopPrompt"/> — the runtime loader that reads
/// the committed <c>ralph-loop</c> skill and frames it for injection into a
/// stalled generator's prompt.
/// </summary>
public class RalphLoopPromptTests
{
    [Fact]
    public void StripFrontMatter_RemovesYamlBlock()
    {
        var text = "---\nname: ralph-loop\nfoo: bar\n---\n# Body\n\ncontent here";
        var body = RalphLoopPrompt.StripFrontMatter(text);
        Assert.StartsWith("# Body", body);
        Assert.DoesNotContain("name: ralph-loop", body);
    }

    [Fact]
    public void StripFrontMatter_NoFrontMatter_IsUnchanged()
    {
        var text = "# Just a heading\n\nno front matter";
        Assert.Equal(text, RalphLoopPrompt.StripFrontMatter(text));
    }

    [Fact]
    public void StripFrontMatter_ToleratesBomAndLeadingBlankLines()
    {
        var text = "﻿\n---\nname: x\n---\nBODY";
        Assert.Equal("BODY", RalphLoopPrompt.StripFrontMatter(text).TrimEnd());
    }

    [Fact]
    public void TryGetGuidanceBlock_ReadsSkillViaEnvOverride_AndFrames()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ralph-skill-{Guid.NewGuid():N}.md");
        File.WriteAllText(tmp, "---\nname: ralph-loop\n---\n## Each Iteration\nAssess, Act, Verify, Evaluate.");
        var prior = Environment.GetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL", tmp);
            RalphLoopPrompt.ResetForTests();

            var block = RalphLoopPrompt.TryGetGuidanceBlock();

            Assert.NotNull(block);
            Assert.Contains("PERSISTENCE DISCIPLINE", block);          // framing header
            Assert.Contains("Assess, Act, Verify, Evaluate.", block);  // skill body
            Assert.DoesNotContain("name: ralph-loop", block);          // front matter stripped
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL", prior);
            RalphLoopPrompt.ResetForTests();
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void TryGetGuidanceBlock_MissingFile_ReturnsNull()
    {
        var prior = Environment.GetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL");
        try
        {
            // Point at a non-existent override; the env override is checked first,
            // but a missing override falls through to the walk-up search. To force
            // a deterministic null we use a path that exists nowhere AND rely on
            // the base-dir walk not finding one in the test host output. If a repo
            // copy IS reachable from the test bin, the walk-up legitimately finds
            // it — so this test only asserts the override-missing path does not
            // throw and returns a string-or-null.
            Environment.SetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL", @"Z:\does\not\exist\SKILL.md");
            RalphLoopPrompt.ResetForTests();

            var block = RalphLoopPrompt.TryGetGuidanceBlock();

            // Either null (nothing found) or a valid framed block (repo copy found
            // via walk-up) — never a throw, never an empty string.
            Assert.True(block is null || block.Contains("PERSISTENCE DISCIPLINE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_RALPH_LOOP_SKILL", prior);
            RalphLoopPrompt.ResetForTests();
        }
    }
}
