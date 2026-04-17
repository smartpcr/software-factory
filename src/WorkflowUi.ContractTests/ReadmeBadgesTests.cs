// -----------------------------------------------------------------------
// <copyright file="ReadmeBadgesTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Meta-tests validating readme.md status badges.
/// </summary>
[Trait("Category", "ReadmeBadges")]
public class ReadmeBadgesTests
{
    private readonly string readme;

    public ReadmeBadgesTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Workflow.sln")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
        this.readme = File.ReadAllText(Path.Combine(repoRoot, "readme.md"));
    }

    [Fact]
    public void Readme_ContainsCiBadge()
    {
        Assert.Contains("actions/workflows/ci-workflow-ui.yml/badge.svg", this.readme);
    }

    [Fact]
    public void Readme_ContainsCoverageBadge()
    {
        Assert.Contains("codecov.io/gh/smartpcr/workflow", this.readme);
    }

    [Fact]
    public void Readme_ContainsContractDriftBadge()
    {
        Assert.Contains("job=contract-drift", this.readme);
    }

    [Fact]
    public void Readme_ContainsPlaywrightBadge()
    {
        Assert.Contains("job=playwright", this.readme);
    }

    [Fact]
    public void AllBadges_TargetMainBranch()
    {
        var badgeUrls = Regex.Matches(this.readme, @"badge\.svg\?[^)]+");
        Assert.True(badgeUrls.Count > 0, "No badge URLs found");
        foreach (Match m in badgeUrls)
        {
            Assert.Contains("branch=main", m.Value);
        }
    }

    [Fact]
    public void NoBadge_ReferencesMaster()
    {
        Assert.DoesNotContain("branch=master", this.readme);
    }

    [Fact]
    public void BadgesSection_LinksToPlan()
    {
        Assert.Contains("docs/workflow-ui-plan.md", this.readme);
    }

    [Fact]
    public void BadgesSection_LinksToSpecFolder()
    {
        Assert.Contains("docs/workflow-ui/", this.readme);
    }

    [Fact]
    public void NoBadge_ContainsToken()
    {
        Assert.DoesNotContain("token=", this.readme);
    }
}
