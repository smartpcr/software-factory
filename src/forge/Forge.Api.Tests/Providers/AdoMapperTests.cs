// -----------------------------------------------------------------------
// <copyright file="AdoMapperTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Text.Json;
using Forge.Api.Providers.Ado;
using Xunit;

/// <summary>Phase 5.5 — verifies the ADO work-item field mapping.</summary>
public class AdoMapperTests
{
    [Theory]
    [InlineData("New", "backlog")]
    [InlineData("Active", "build")]
    [InlineData("Code Review", "review")]
    [InlineData("Closed", "done")]
    [InlineData("Removed", "done")]
    [InlineData("Mystery", "backlog")]
    public void MapState_Maps_Common_Vsts_States(string state, string expected)
    {
        Assert.Equal(expected, AdoMapper.MapState(state));
    }

    [Theory]
    [InlineData("contoso\\Backend\\Auth", "Auth")]
    [InlineData("contoso/Frontend/Web", "Web")]
    [InlineData("contoso", "contoso")]
    [InlineData(null, "fallback")]
    [InlineData("", "fallback")]
    public void MapEpic_Returns_Last_Segment(string? area, string expected)
    {
        Assert.Equal(expected, AdoMapper.MapEpic(area, "fallback"));
    }

    [Fact]
    public void ToCard_Maps_All_Required_Fields()
    {
        const string json = """
        {
          "id": 234,
          "fields": {
            "System.Title": "OIDC token rotation",
            "System.WorkItemType": "User Story",
            "System.State": "Active",
            "System.Tags": "auth; security",
            "System.AreaPath": "Atlas\\Auth",
            "System.AssignedTo": { "displayName": "Xiaodong Li", "uniqueName": "xli@example.com" },
            "Microsoft.VSTS.Scheduling.StoryPoints": 5.0,
            "Microsoft.VSTS.Common.Priority": "1"
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var card = AdoMapper.ToCard(doc.RootElement, "Atlas", "Atlas");

        Assert.Equal("#234", card.Id);
        Assert.Equal("Auth", card.Epic);
        Assert.Equal("build", card.Col);
        Assert.Equal("story", card.Type);
        Assert.Equal("OIDC token rotation", card.Title);
        Assert.Equal(new[] { "auth", "security" }, card.Tags);
        Assert.Equal(5, card.Pts);
        Assert.Equal("high", card.Priority);
        Assert.NotNull(card.Agent);
        Assert.Equal("XL", card.Agent!.Id);
        Assert.Equal("human", card.Agent.Role);
        Assert.Equal("Xiaodong Li", card.Agent.Name);
    }

    [Fact]
    public void ToPr_Maps_Source_And_Target_And_Conflict()
    {
        const string json = """
        {
          "pullRequestId": 41,
          "title": "Add IdP failover",
          "sourceRefName": "refs/heads/ws/idp-failover",
          "targetRefName": "refs/heads/main",
          "status": "active",
          "mergeStatus": "conflicts",
          "createdBy": { "displayName": "Anvil" }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var pr = AdoMapper.ToPr(doc.RootElement, "Atlas");

        Assert.Equal("PR #41", pr.Id);
        Assert.Equal("ws/idp-failover", pr.Branch);
        Assert.Equal("main", pr.Base);
        Assert.Equal("review", pr.Col);
        Assert.True(pr.Conflicts);
        Assert.Equal("Anvil", pr.Author.Name);
    }
}
