// -----------------------------------------------------------------------
// <copyright file="GitHubMapperTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Text.Json;
using Forge.Api.Providers.GitHub;
using Xunit;

/// <summary>Phase 5.6 — verifies the GitHub issue / PR mapping.</summary>
public class GitHubMapperTests
{
    [Fact]
    public void IssueToCard_Bug_Label_Selects_Bug_Type()
    {
        const string json = """
        {
          "number": 17,
          "title": "Crash on large CSV",
          "state": "open",
          "labels": [{ "name": "bug" }, { "name": "priority/high" }, { "name": "in-review" }],
          "assignee": { "login": "rivet-bot" }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var card = GitHubMapper.IssueToCard(doc.RootElement, "ms/forge");

        Assert.Equal("#17", card.Id);
        Assert.Equal("bug", card.Type);
        Assert.Equal("review", card.Col);
        Assert.Equal("high", card.Priority);
        Assert.NotNull(card.Agent);
        Assert.Equal("RI", card.Agent!.Id);
    }

    [Fact]
    public void IssueToCard_Closed_Issue_Goes_To_Done()
    {
        const string json = """
        { "number": 1, "title": "x", "state": "closed", "labels": [] }
        """;
        using var doc = JsonDocument.Parse(json);
        var card = GitHubMapper.IssueToCard(doc.RootElement, "ms/forge");
        Assert.Equal("done", card.Col);
        Assert.Equal("recently", card.DoneAt);
    }

    [Fact]
    public void PrToPr_Draft_Goes_To_Implementation()
    {
        const string json = """
        {
          "number": 99,
          "title": "WIP refactor",
          "state": "open",
          "draft": true,
          "head": { "ref": "ws/refactor" },
          "base": { "ref": "main" },
          "user": { "login": "forge-eng" },
          "additions": 12, "deletions": 3, "changed_files": 2, "commits": 1,
          "labels": []
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var pr = GitHubMapper.PrToPr(doc.RootElement, "ms/forge");

        Assert.Equal("PR #99", pr.Id);
        Assert.Equal("implementation", pr.Col);
        Assert.Equal("ws/refactor", pr.Branch);
        Assert.Equal("main", pr.Base);
        Assert.Equal(12, pr.Additions);
        Assert.Equal(3, pr.Deletions);
    }

    [Fact]
    public void PrToPr_Merged_Goes_To_Merged()
    {
        const string json = """
        {
          "number": 100, "title": "ship it", "state": "closed",
          "merged_at": "2026-01-01T00:00:00Z",
          "head": { "ref": "ws/x" }, "base": { "ref": "main" },
          "user": { "login": "ci-bot" },
          "additions": 0, "deletions": 0, "changed_files": 0, "commits": 1,
          "labels": []
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var pr = GitHubMapper.PrToPr(doc.RootElement, "ms/forge");
        Assert.Equal("merged", pr.Col);
    }

    [Fact]
    public void PrToPr_Security_Label_Sets_HumanGate()
    {
        const string json = """
        {
          "number": 5, "title": "auth", "state": "open",
          "head": { "ref": "ws/auth" }, "base": { "ref": "main" },
          "user": { "login": "x" },
          "additions": 1, "deletions": 0, "changed_files": 1, "commits": 1,
          "labels": [{ "name": "security" }]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var pr = GitHubMapper.PrToPr(doc.RootElement, "ms/forge");
        Assert.True(pr.HumanGate);
        Assert.NotNull(pr.GateReason);
    }
}
