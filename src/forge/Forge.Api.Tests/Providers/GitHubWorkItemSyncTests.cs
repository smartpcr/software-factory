// -----------------------------------------------------------------------
// <copyright file="GitHubWorkItemSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Net.Http;
using System.Text;
using Forge.Api.Auth;
using Forge.Api.Providers;
using Forge.Api.Providers.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Phase 12.4 — GitHub sync (query string + body parent parse).</summary>
public class GitHubWorkItemSyncTests
{
    private static (GitHubProvider Provider, StubHttpMessageHandler Stub) NewProvider()
    {
        var stub = new StubHttpMessageHandler();
        var factory = new SingleNamedHttpClientFactory(GitHubProvider.ClientName, stub);
        var opts = Options.Create(new GitHubOptions { Repo = "ms/forge-runtime", Pat = "server-pat" });
        return (new GitHubProvider(factory, opts, Array.Empty<IAuthService>(), NullLogger<GitHubProvider>.Instance), stub);
    }

    [Fact]
    public void BuildSyncQueryString_Includes_Defaults()
    {
        var qs = GitHubProvider.BuildSyncQueryString(new WorkItemSyncFilters());
        Assert.Contains("state=all", qs);
        Assert.Contains("per_page=100", qs);
    }

    [Fact]
    public void BuildSyncQueryString_Adds_AssignedTo_Labels_Since()
    {
        var from = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var qs = GitHubProvider.BuildSyncQueryString(new WorkItemSyncFilters(
            AssignedTo: "octocat",
            Types: new[] { "bug", "feature" },
            UpdatedFrom: from));
        Assert.Contains("assignee=octocat", qs);
        Assert.Contains("labels=bug,feature", qs);
        Assert.Contains("since=", qs);
    }

    [Fact]
    public void IssueToWorkItem_Maps_State_Reason_Per_Q12_3()
    {
        // Open + not_planned → proposed.
        var openProposed = System.Text.Json.JsonDocument.Parse("""
        { "number": 1, "title": "x", "state": "open", "state_reason": "not_planned",
          "labels": [], "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" }
        """).RootElement;
        var w1 = GitHubMapper.IssueToWorkItem(openProposed, "ms/r", "ms/r");
        Assert.Equal("proposed", w1.State);

        // Closed + completed → resolved.
        var closedDone = System.Text.Json.JsonDocument.Parse("""
        { "number": 2, "title": "x", "state": "closed", "state_reason": "completed",
          "labels": [], "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" }
        """).RootElement;
        var w2 = GitHubMapper.IssueToWorkItem(closedDone, "ms/r", "ms/r");
        Assert.Equal("resolved", w2.State);

        // Closed + not_planned → closed.
        var closedDropped = System.Text.Json.JsonDocument.Parse("""
        { "number": 3, "title": "x", "state": "closed", "state_reason": "not_planned",
          "labels": [], "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" }
        """).RootElement;
        var w3 = GitHubMapper.IssueToWorkItem(closedDropped, "ms/r", "ms/r");
        Assert.Equal("closed", w3.State);
    }

    [Fact]
    public void ParseParentFromBody_Reads_Parent_Hash()
    {
        Assert.Equal("WI-GH-42", GitHubMapper.ParseParentFromBody("Parent: #42\nbody"));
        Assert.Equal("WI-GH-7",  GitHubMapper.ParseParentFromBody("- description\nparent: 7"));
        Assert.Null(GitHubMapper.ParseParentFromBody("no parent here"));
    }

    [Fact]
    public void IssueToWorkItem_Derives_Track_And_Type_From_Labels()
    {
        var phase = System.Text.Json.JsonDocument.Parse("""
        { "number": 100, "title": "Editorial revamp", "state": "open",
          "labels": [{"name": "phase"}],
          "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" }
        """).RootElement;
        var w = GitHubMapper.IssueToWorkItem(phase, "ms/r", "ms/r");
        Assert.Equal("design", w.Track);
        Assert.Equal("phase", w.Type);
    }

    [Fact]
    public async Task ListWorkItemsForSync_Skips_Pull_Requests_And_Maps_Parent()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Get &&
                   req.RequestUri!.AbsolutePath.Contains("/repos/ms/forge-runtime/issues"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                [
                  {
                    "number": 421,
                    "title": "Observability v2",
                    "state": "open",
                    "labels": [{"name": "epic"}],
                    "body": "",
                    "created_at": "2026-04-12T09:00:00Z",
                    "updated_at": "2026-04-26T13:00:00Z"
                  },
                  {
                    "number": 820,
                    "title": "OTel propagation",
                    "state": "open",
                    "labels": [{"name": "task"}],
                    "body": "Parent: #421\nMore detail here.",
                    "created_at": "2026-04-12T09:00:00Z",
                    "updated_at": "2026-04-26T13:00:00Z"
                  },
                  {
                    "number": 999,
                    "title": "Some PR — should be skipped",
                    "state": "open",
                    "labels": [],
                    "pull_request": { "url": "https://api.github.com/..." },
                    "body": "",
                    "created_at": "2026-04-12T09:00:00Z",
                    "updated_at": "2026-04-26T13:00:00Z"
                  }
                ]
                """, Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);

        Assert.Equal(2, items.Count);
        var epic = items.First(i => i.Type == "epic");
        var task = items.First(i => i.Type == "task");
        Assert.Equal("WI-GH-421", epic.Id);
        Assert.Equal("WI-GH-820", task.Id);
        Assert.Equal("WI-GH-421", task.ParentId);
    }
}
