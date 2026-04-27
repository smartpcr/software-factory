// -----------------------------------------------------------------------
// <copyright file="AdoWorkItemSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Net.Http;
using System.Text;
using Forge.Api.Auth;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Phase 12.4 — ADO sync surface (WIQL builder + relations parse).</summary>
public class AdoWorkItemSyncTests
{
    private static (AdoProvider Provider, StubHttpMessageHandler Stub) NewProvider()
    {
        var stub = new StubHttpMessageHandler();
        var factory = new SingleNamedHttpClientFactory(AdoProvider.ClientName, stub);
        var opts = Options.Create(new AdoOptions
        {
            OrgUrl = "https://dev.azure.test/contoso",
            Project = "Atlas",
            Repository = "atlas-platform",
            Pat = "pat",
        });
        return (new AdoProvider(factory, opts, NullLogger<AdoProvider>.Instance, Array.Empty<IAuthService>()), stub);
    }

    [Fact]
    public void BuildSyncWiql_Includes_Project_Always()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters());
        Assert.Contains("[System.TeamProject] = 'Atlas'", wiql);
        Assert.Contains("Order By [System.ChangedDate] Desc", wiql);
    }

    [Fact]
    public void BuildSyncWiql_Adds_AreaPath_Under()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters(AreaPath: @"Atlas\Platform\Auth"));
        Assert.Contains(@"[System.AreaPath] UNDER 'Atlas\Platform\Auth'", wiql);
    }

    [Fact]
    public void BuildSyncWiql_Maps_Forge_Types_To_Ado_Names()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters(
            Types: new[] { "task", "bug", "feature" }));
        Assert.Contains("[System.WorkItemType] IN", wiql);
        Assert.Contains("'Task'", wiql);
        Assert.Contains("'Bug'", wiql);
        Assert.Contains("'Feature'", wiql);
    }

    [Fact]
    public void BuildSyncWiql_Adds_Date_Range()
    {
        var from = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 4, 30, 23, 59, 59, TimeSpan.Zero);
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters(UpdatedFrom: from, UpdatedTo: to));
        Assert.Contains("[System.ChangedDate] >= '2026-04-01T00:00:00Z'", wiql);
        Assert.Contains("[System.ChangedDate] <= '2026-04-30T23:59:59Z'", wiql);
    }

    [Fact]
    public void BuildSyncWiql_Escapes_Single_Quotes_In_Filters()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atl'as", new WorkItemSyncFilters(AreaPath: "with'quote"));
        Assert.Contains("[System.TeamProject] = 'Atl''as'", wiql);
        Assert.Contains("UNDER 'with''quote'", wiql);
    }

    [Fact]
    public async Task ListWorkItemsForSync_Maps_Wiql_Hydrate_With_Parent()
    {
        var (provider, stub) = NewProvider();

        // 1) WIQL POST returns two ids.
        stub.When(
            req => req.Method == HttpMethod.Post &&
                   req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"workItems\":[{\"id\":24},{\"id\":234}]}",
                    Encoding.UTF8, "application/json"),
            });

        // 2) Hydrate batch returns one parent (Epic 24) and one child (Task 234).
        stub.When(
            req => req.Method == HttpMethod.Get &&
                   req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/workitems"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "value": [
                    {
                      "id": 24,
                      "fields": {
                        "System.WorkItemType": "Epic",
                        "System.State": "Active",
                        "System.Title": "Multi-tenant authentication",
                        "System.AreaPath": "Atlas\\Platform\\Auth",
                        "System.IterationPath": "Atlas\\Sprint 14",
                        "System.Rev": 5,
                        "System.CreatedDate": "2026-04-12T09:00:00Z",
                        "System.ChangedDate": "2026-04-26T13:00:00Z"
                      }
                    },
                    {
                      "id": 234,
                      "fields": {
                        "System.WorkItemType": "Task",
                        "System.State": "Active",
                        "System.Title": "Apply jitter to refetch",
                        "System.AreaPath": "Atlas\\Platform\\Auth",
                        "System.IterationPath": "Atlas\\Sprint 14",
                        "System.Rev": 2,
                        "Microsoft.VSTS.Scheduling.StoryPoints": 2.0
                      },
                      "relations": [
                        { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.test/_apis/wit/workItems/24" }
                      ]
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);

        Assert.Equal(2, items.Count);
        var epic = items.First(i => i.Type == "epic");
        var task = items.First(i => i.Type == "task");
        Assert.Equal("WI-24", epic.Id);
        Assert.Equal("dev", epic.Track);
        Assert.Equal("active", epic.State);
        Assert.Null(epic.ParentId);
        Assert.Equal("WI-234", task.Id);
        Assert.Equal("WI-24", task.ParentId);
        Assert.Equal(2, task.Points);
        Assert.Equal("5", epic.RemoteRevision);
    }
}
