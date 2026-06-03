// -----------------------------------------------------------------------
// <copyright file="SoulModelFrontmatterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Xunit;

/// <summary>
/// Forbids any agent soul.md from declaring a <c>model:</c> front-matter key.
/// The runtime model is resolved exclusively from the crew/agent DB settings
/// (ResolveAgentRunConfigAsync); a soul-level <c>model:</c> is silently ignored
/// and only invites misdiagnosis (a stale Sonnet header on a gpt-5.5 evaluator,
/// 2026-06-03). Souls must always wire to the operator-specified crew settings.
/// </summary>
public class SoulModelFrontmatterTests
{
    [Fact]
    public void DeclaresModel_Detects_Frontmatter_Model_Key()
    {
        Assert.True(SoulService.DeclaresModel("---\nname: X\nmodel: claude-opus-4-7\ntools: []\n---\nbody"));
        Assert.True(SoulService.DeclaresModel("---\r\nname: X\r\nMODEL: gpt-5.5\r\n---\r\nbody"));
    }

    [Fact]
    public void DeclaresModel_Ignores_Model_Outside_Frontmatter()
    {
        Assert.False(SoulService.DeclaresModel("---\nname: X\ntools: []\n---\nmodel: discussed in body"));
        Assert.False(SoulService.DeclaresModel("no front matter here\nmodel: x"));
        Assert.False(SoulService.DeclaresModel(string.Empty));
    }

    [Fact]
    public void No_Agent_Soul_Declares_A_Model_Field()
    {
        var agentsDir = Path.Combine(ForgeSeed.FindRepoRoot(), "agents");
        Assert.True(Directory.Exists(agentsDir), $"agents directory not found at {agentsDir}");

        var offenders = new List<string>();
        foreach (var soul in Directory.EnumerateFiles(agentsDir, "soul.md", SearchOption.AllDirectories))
        {
            if (SoulService.DeclaresModel(File.ReadAllText(soul)))
            {
                offenders.Add(Path.GetFileName(Path.GetDirectoryName(soul)) ?? soul);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "soul.md must NOT declare a `model:` field -- the runtime model comes from the "
            + "crew/agent DB settings, never the soul file. Remove `model:` from these agents: "
            + string.Join(", ", offenders.OrderBy(x => x)));
    }
}
