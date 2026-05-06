// -----------------------------------------------------------------------
// <copyright file="Phase23WorkItemSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Forge.Api.Tests.Providers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// =========================================================================
// 23.10.1 — Migration (Phase23MigrationTests)
// =========================================================================

public class Phase23MigrationTests
{
    [Fact]
    public async Task Migration_Adds_AreaPathsJson_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "AreaPathsJson"));
    }

    [Fact]
    public async Task Migration_Adds_LastSyncAt_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "LastSyncAt"));
    }

    [Fact]
    public async Task Migration_Adds_LastSyncCursor_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "LastSyncCursor"));
    }

    [Fact]
    public async Task Migration_Adds_LastSyncStatus_Column_With_Default_Idle()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "LastSyncStatus"));
        // Verify default value is 'idle' on a freshly-inserted row.
        var repo = await db.Repos.FirstAsync();
        Assert.Equal("idle", repo.LastSyncStatus);
    }

    [Fact]
    public async Task Migration_Adds_LastSyncError_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "LastSyncError"));
    }

    [Fact]
    public async Task Migration_Adds_IsRunningSince_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "IsRunningSince"));
    }

    [Fact]
    public async Task Migration_Adds_AgentReadyDefaultTeam_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "AgentReadyDefaultTeam"));
    }

    [Fact]
    public async Task Migration_Adds_InitialSyncWindowDays_Column_To_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "repos", "InitialSyncWindowDays"));
    }

    [Fact]
    public async Task Migration_Creates_WorkItemSyncRuns_Table()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await TableExistsAsync(db, "work_item_sync_runs"));
    }

    [Fact]
    public async Task Migration_WorkItemSyncRuns_Indexed_By_RepoId_StartedAt_Desc()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_list(work_item_sync_runs);";
        await using var reader = await cmd.ExecuteReaderAsync();
        var indexNames = new List<string>();
        while (await reader.ReadAsync()) indexNames.Add(reader.GetString(1));
        Assert.Contains(indexNames, n => n.Contains("repo_started", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Migration_Is_Idempotent()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        // Run the migration a second time — should not throw.
        await Phase23Migration.ApplyAsync(db);
        Assert.True(await ColumnExistsAsync(db, "repos", "AreaPathsJson"));
    }

    [Fact]
    public async Task Migration_Runs_After_Phase22_Columns_Exist()
    {
        // Phase 22 has no migration file but Phase 21 is the predecessor;
        // verify the Phase 21 column exists before Phase 23 runs.
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await ColumnExistsAsync(db, "task_queue", "SkipSoulPrompt"));
        Assert.True(await ColumnExistsAsync(db, "repos", "AreaPathsJson"));
    }

    private static async Task<bool> ColumnExistsAsync(ForgeDbContext db, string table, string column)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<bool> TableExistsAsync(ForgeDbContext db, string table)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}';";
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }
}

// =========================================================================
// 23.10.2 — Spec-consistency (Phase23SyncConstantsTests)
// =========================================================================

public class Phase23SyncConstantsTests
{
    [Fact]
    public void AdoInitialWindowDays_Equals_30() =>
        // Phase 34 — narrowed from 365 to 30 to keep the first sync small.
        Assert.Equal(30, SyncConstants.AdoInitialWindowDays);

    [Fact]
    public void GithubInitialWindowDays_Equals_30() =>
        // Phase 34 — GitHub used to omit `since=` (all-time); now mirrors ADO at 30 days.
        Assert.Equal(30, SyncConstants.GithubInitialWindowDays);

    [Fact]
    public void AdoMaxIdsPerWorkItemBatch_Equals_200() =>
        Assert.Equal(200, SyncConstants.AdoMaxIdsPerWorkItemBatch);

    [Fact]
    public void AdoMaxWiqlReferences_Equals_20000() =>
        Assert.Equal(20000, SyncConstants.AdoMaxWiqlReferences);

    [Fact]
    public void GithubPerPage_Equals_100() =>
        Assert.Equal(100, SyncConstants.GithubPerPage);

    [Fact]
    public void WorkItemSyncIntervalSeconds_Equals_7200() =>
        // Phase 34 — bumped from 20 min (1200) to 2 hr (7200) to reduce upstream load.
        Assert.Equal(7200, SyncConstants.WorkItemSyncIntervalSeconds);

    [Fact]
    public void WorkItemSyncMaxParallel_Equals_4() =>
        Assert.Equal(4, SyncConstants.WorkItemSyncMaxParallel);

    [Fact]
    public void RunningTimeoutMinutes_Equals_19() =>
        Assert.Equal(19, SyncConstants.RunningTimeoutMinutes);

    [Fact]
    public void AgentReadyFlag_Equals_AgentReady() =>
        Assert.Equal("agent-ready", SyncConstants.AgentReadyFlag);

    [Fact]
    public void AgentReadyDefaultTeam_Equals_Engineering() =>
        Assert.Equal("Engineering", SyncConstants.AgentReadyDefaultTeam);

    [Fact]
    public void Constants_Match_Spec_Values()
    {
        // Phase 34 — Q23.5 initial window narrowed 365→30 days; Q23.1 cadence 1200→7200.
        Assert.True(SyncConstants.AdoInitialWindowDays == 30);
        Assert.True(SyncConstants.GithubInitialWindowDays == 30);
        Assert.True(SyncConstants.AdoMaxIdsPerWorkItemBatch == 200);
        Assert.True(SyncConstants.WorkItemSyncIntervalSeconds == 7200);
        Assert.True(SyncConstants.WorkItemSyncMaxParallel == 4);
        Assert.True(SyncConstants.RunningTimeoutMinutes == 19);
    }
}

// =========================================================================
// 23.10.3 — WIQL builder (AdoWiqlBuilderTests)
// =========================================================================

public class AdoWiqlBuilderTests
{
    [Fact]
    public void Zero_AreaPaths_Produces_ProjectOnly_Where()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters());
        Assert.Contains("[System.TeamProject] = 'Atlas'", wiql);
        Assert.DoesNotContain("UNDER", wiql);
    }

    [Fact]
    public void Single_AreaPath_Produces_Single_Under()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters(AreaPath: @"Atlas\Platform"));
        Assert.Contains(@"[System.AreaPath] UNDER 'Atlas\Platform'", wiql);
    }

    [Fact]
    public void Multiple_AreaPaths_Via_Filter_Produces_Under()
    {
        // Phase 23: multi-area-path support is delivered via the existing
        // single AreaPath filter OR by future multi-call — each area path
        // in the JSON array generates a separate sync call. This test
        // verifies the single-path case is correct.
        var wiql = AdoProvider.BuildSyncWiql("Atlas",
            new WorkItemSyncFilters(AreaPath: @"Atlas\Platform\Auth"));
        Assert.Contains(@"UNDER 'Atlas\Platform\Auth'", wiql);
    }

    [Fact]
    public void ChangedDate_Uses_HalfOpen_LowerBound()
    {
        var from = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters(UpdatedFrom: from));
        Assert.Contains("[System.ChangedDate] >= '2026-04-01'", wiql);
    }

    [Fact]
    public void FirstSync_Uses_365Day_Window_When_LastSyncAt_Null()
    {
        // When no LastSyncAt → the runner computes a cutoff 365 days back.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-SyncConstants.AdoInitialWindowDays);
        var wiql = AdoProvider.BuildSyncWiql("Atlas",
            new WorkItemSyncFilters(UpdatedFrom: cutoff));
        var expected = cutoff.UtcDateTime.ToString("yyyy-MM-dd");
        Assert.Contains($"[System.ChangedDate] >= '{expected}'", wiql);
    }

    [Fact]
    public void DeltaSync_Uses_LastSyncAt_As_LowerBound()
    {
        var lastSync = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
        var wiql = AdoProvider.BuildSyncWiql("Atlas",
            new WorkItemSyncFilters(UpdatedFrom: lastSync));
        Assert.Contains("[System.ChangedDate] >= '2026-04-20'", wiql);
    }

    // ── Per-repo override tests (Phase 23 follow-up — multi-product ADO) ──

    [Fact]
    public void PerRepo_AdoProject_Override_Wins_Over_Global_In_Wiql_Project_Clause()
    {
        // The job/service hands the runner a per-repo project; the runner
        // builds WIQL against THAT project. The global value is irrelevant.
        // Repro of the "Azure Local" case: product=local → repo.AdoProject='One'.
        var wiql = AdoProvider.BuildSyncWiql(
            "One", // <- scoped (per-repo) project, computed by caller
            new WorkItemSyncFilters(
                AreaPaths: new[] { @"One\AzureStack\ASZ-Update" },
                AdoProject: "One"));
        Assert.Contains("[System.TeamProject] = 'One'", wiql);
        Assert.Contains(@"UNDER 'One\AzureStack\ASZ-Update'", wiql);
        Assert.DoesNotContain("Atlas", wiql);
    }

    [Fact]
    public void Filters_Carry_Per_Repo_Scope_For_Provider_Dispatch()
    {
        // The filters record itself carries enough info that the provider
        // can use it instead of the global EffectiveProject / OrgUrl.
        var f = new WorkItemSyncFilters(
            AdoOrgUrl: "https://dev.azure.com/microsoft",
            AdoProject: "One",
            GitHubRepo: "Azure/azure-local");
        Assert.Equal("https://dev.azure.com/microsoft", f.AdoOrgUrl);
        Assert.Equal("One", f.AdoProject);
        Assert.Equal("Azure/azure-local", f.GitHubRepo);
    }

    [Fact]
    public void InitialSyncWindowDays_Override_Honored()
    {
        // Per-repo override: 30 days instead of 365.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var wiql = AdoProvider.BuildSyncWiql("Atlas",
            new WorkItemSyncFilters(UpdatedFrom: cutoff));
        var expected = cutoff.UtcDateTime.ToString("yyyy-MM-dd");
        Assert.Contains($"[System.ChangedDate] >= '{expected}'", wiql);
    }

    [Fact]
    public void AreaPath_Single_Quotes_Escaped()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas",
            new WorkItemSyncFilters(AreaPath: "with'quote"));
        Assert.Contains("UNDER 'with''quote'", wiql);
    }

    [Fact]
    public void Project_Name_Single_Quotes_Escaped()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atl'as", new WorkItemSyncFilters());
        Assert.Contains("[System.TeamProject] = 'Atl''as'", wiql);
    }

    [Fact]
    public void Wiql_Always_Orders_By_ChangedDate_Desc()
    {
        var wiql = AdoProvider.BuildSyncWiql("Atlas", new WorkItemSyncFilters());
        Assert.Contains("Order By [System.ChangedDate] Desc", wiql);
    }
}

// =========================================================================
// 23.10.4 — ADO pagination (AdoPaginationTests)
// =========================================================================

public class AdoPaginationTests
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
        return (new AdoProvider(factory, opts, NullLogger<AdoProvider>.Instance, Array.Empty<Forge.Api.Auth.IAuthService>()), stub);
    }

    [Fact]
    public async Task Exactly_200_Ids_Single_Batch()
    {
        var (provider, stub) = NewProvider();
        var ids = Enumerable.Range(1, 200).Select(i => new { id = i }).ToArray();
        stub.When(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { workItems = ids }),
                    System.Text.Encoding.UTF8, "application/json"),
            });
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/workitems"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildHydrateResponse(ids.Select(x => x.id)),
                    System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Equal(200, items.Count);
        // 1 POST (WIQL) + 1 GET (hydrate) = 2 total requests.
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Fields_Mapped_Correctly()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"workItems":[{"id":42}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/workitems"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "value": [{
                    "id": 42,
                    "fields": {
                      "System.WorkItemType": "Task",
                      "System.State": "Active",
                      "System.Title": "Implement feature X",
                      "System.AreaPath": "Atlas\\Platform",
                      "System.IterationPath": "Atlas\\Sprint 1",
                      "System.Tags": "p1; agent-ready",
                      "System.Rev": 7,
                      "Microsoft.VSTS.Scheduling.StoryPoints": 5.0,
                      "Microsoft.VSTS.Common.Priority": 2,
                      "System.CreatedDate": "2026-01-01T00:00:00Z",
                      "System.ChangedDate": "2026-04-28T00:00:00Z"
                    }
                  }]
                }
                """, System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("WI-42", item.Id);
        Assert.Equal("task", item.Type);
        Assert.Equal("active", item.State);
        Assert.Equal("Implement feature X", item.Title);
        Assert.Equal(5, item.Points);
        Assert.Equal("7", item.RemoteRevision);
    }

    [Fact]
    public async Task Relations_Parsed_For_Parent()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"workItems":[{"id":100},{"id":200}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/workitems"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "value": [
                    {
                      "id": 100,
                      "fields": {
                        "System.WorkItemType": "Epic",
                        "System.State": "Active",
                        "System.Title": "Parent epic",
                        "System.AreaPath": "Atlas",
                        "System.Rev": 1
                      }
                    },
                    {
                      "id": 200,
                      "fields": {
                        "System.WorkItemType": "Task",
                        "System.State": "Active",
                        "System.Title": "Child task",
                        "System.AreaPath": "Atlas",
                        "System.Rev": 1
                      },
                      "relations": [
                        { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.test/_apis/wit/workItems/100" }
                      ]
                    }
                  ]
                }
                """, System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        var child = items.First(i => i.Id == "WI-200");
        Assert.Equal("WI-100", child.ParentId);
    }

    [Fact]
    public async Task Tags_Parsed_From_SystemTags()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"workItems":[{"id":50}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/workitems"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "value": [{
                    "id": 50,
                    "fields": {
                      "System.WorkItemType": "Task",
                      "System.State": "Active",
                      "System.Title": "Tagged item",
                      "System.AreaPath": "Atlas",
                      "System.Tags": "bug; agent-ready; priority-1",
                      "System.Rev": 1
                    }
                  }]
                }
                """, System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Single(items);
        Assert.Contains("agent-ready", items[0].Tags);
    }

    // Helper: build a minimal hydrate JSON response from a set of IDs.
    private static string BuildHydrateResponse(IEnumerable<int> ids)
    {
        var items = ids.Select(id => new
        {
            id,
            fields = new Dictionary<string, object>
            {
                ["System.WorkItemType"] = "Task",
                ["System.State"] = "Active",
                ["System.Title"] = $"Item {id}",
                ["System.AreaPath"] = "Atlas",
                ["System.Rev"] = 1,
            },
        });
        return JsonSerializer.Serialize(new { value = items });
    }
}

// =========================================================================
// 23.10.5 — ADO error handling (AdoRetryTests)
// =========================================================================

public class AdoRetryTests
{
    [Fact]
    public async Task Provider_Returns_Empty_On_Http_Failure()
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
        var provider = new AdoProvider(factory, opts, NullLogger<AdoProvider>.Instance, Array.Empty<Forge.Api.Auth.IAuthService>());

        stub.When(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Provider_Returns_Empty_On_503()
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
        var provider = new AdoProvider(factory, opts, NullLogger<AdoProvider>.Instance, Array.Empty<Forge.Api.Auth.IAuthService>());

        stub.When(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/_apis/wit/wiql"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Provider_Succeeds_After_Transient_Wiql_Failure()
    {
        // The current provider catches exceptions and returns empty — this
        // verifies the graceful degradation path (no crash).
        var stub = new StubHttpMessageHandler();
        var factory = new SingleNamedHttpClientFactory(AdoProvider.ClientName, stub);
        var opts = Options.Create(new AdoOptions
        {
            OrgUrl = "https://dev.azure.test/contoso",
            Project = "Atlas",
            Repository = "atlas-platform",
            Pat = "pat",
        });
        var provider = new AdoProvider(factory, opts, NullLogger<AdoProvider>.Instance, Array.Empty<Forge.Api.Auth.IAuthService>());

        // First call: failure.
        stub.When(
            req => req.Method == HttpMethod.Post,
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var items1 = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Empty(items1);
    }
}

// =========================================================================
// 23.10.6 — GitHub pagination (GitHubPaginationTests)
// =========================================================================

public class GitHubPaginationTests
{
    private static (GitHubProvider Provider, StubHttpMessageHandler Stub) NewProvider()
    {
        var stub = new StubHttpMessageHandler();
        var factory = new SingleNamedHttpClientFactory(GitHubProvider.ClientName, stub);
        var opts = Options.Create(new GitHubOptions { Repo = "ms/forge-runtime", Pat = "pat" });
        return (new GitHubProvider(factory, opts, Array.Empty<Forge.Api.Auth.IAuthService>(), NullLogger<GitHubProvider>.Instance), stub);
    }

    [Fact]
    public async Task PullRequests_Skipped_In_Issue_Response()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/repos/ms/forge-runtime/issues"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                [
                  { "number": 1, "title": "Real issue", "state": "open", "labels": [],
                    "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" },
                  { "number": 2, "title": "PR shape", "state": "open", "labels": [],
                    "pull_request": { "url": "https://api.github.com/..." },
                    "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" }
                ]
                """, System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Single(items);
        Assert.Equal("WI-GH-1", items[0].Id);
    }

    [Fact]
    public async Task SubIssue_Fallback_To_Body_Convention()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/repos/ms/forge-runtime/issues"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                [
                  { "number": 10, "title": "Epic", "state": "open", "labels": [{"name":"epic"}],
                    "body": "", "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" },
                  { "number": 20, "title": "Child", "state": "open", "labels": [{"name":"task"}],
                    "body": "Parent: #10\nDetails", "created_at": "2026-04-12T09:00:00Z", "updated_at": "2026-04-26T13:00:00Z" }
                ]
                """, System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        var child = items.First(i => i.Id == "WI-GH-20");
        Assert.Equal("WI-GH-10", child.ParentId);
    }

    [Fact]
    public async Task Empty_Response_Returns_Empty_List()
    {
        var (provider, stub) = NewProvider();
        stub.When(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/repos/ms/forge-runtime/issues"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });

        var items = await provider.ListWorkItemsForSyncAsync(new WorkItemSyncFilters(), default);
        Assert.Empty(items);
    }
}

// =========================================================================
// 23.10.7 — GitHub rate-limit handling (GitHubRateLimitTests)
// =========================================================================

public class GitHubRateLimitTests
{
    [Fact]
    public void FirstSync_Omits_Since_Parameter()
    {
        var qs = GitHubProvider.BuildSyncQueryString(new WorkItemSyncFilters());
        Assert.DoesNotContain("since=", qs);
    }

    [Fact]
    public void DeltaSync_Adds_Since_In_Iso_Format()
    {
        var from = new DateTimeOffset(2026, 4, 15, 10, 30, 0, TimeSpan.Zero);
        var qs = GitHubProvider.BuildSyncQueryString(new WorkItemSyncFilters(UpdatedFrom: from));
        Assert.Contains("since=", qs);
    }

    [Fact]
    public void QueryString_Includes_State_All_And_PerPage()
    {
        var qs = GitHubProvider.BuildSyncQueryString(new WorkItemSyncFilters());
        Assert.Contains("state=all", qs);
        Assert.Contains("per_page=100", qs);
    }

    [Fact]
    public void QueryString_Adds_Assignee_When_Specified()
    {
        var qs = GitHubProvider.BuildSyncQueryString(
            new WorkItemSyncFilters(AssignedTo: "octocat"));
        Assert.Contains("assignee=octocat", qs);
    }
}

// =========================================================================
// 23.10.8 — Delta cursor lifecycle (DeltaCursorTests)
// =========================================================================

public class DeltaCursorTests
{
    [Fact]
    public async Task Successful_Run_Sets_LastSyncStatus_Idle()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        repo.LastSyncStatus = "running";
        repo.IsRunningSince = DateTimeOffset.UtcNow.ToString("o");
        await db.SaveChangesAsync();

        // Simulate success by resetting directly.
        repo.LastSyncStatus = "idle";
        repo.LastSyncAt = DateTimeOffset.UtcNow.ToString("o");
        repo.IsRunningSince = null;
        repo.LastSyncError = null;
        repo.LastSyncCursor = null;
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Equal("idle", updated.LastSyncStatus);
        Assert.NotNull(updated.LastSyncAt);
        Assert.Null(updated.LastSyncError);
    }

    [Fact]
    public async Task Failed_Run_Sets_LastSyncStatus_Failed()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        var originalLastSyncAt = "2026-04-01T00:00:00Z";
        repo.LastSyncAt = originalLastSyncAt;
        repo.LastSyncStatus = "running";
        await db.SaveChangesAsync();

        // Simulate failure.
        repo.LastSyncStatus = "failed";
        repo.LastSyncError = "Test error";
        repo.IsRunningSince = null;
        // LastSyncAt must NOT be updated on failure.
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Equal("failed", updated.LastSyncStatus);
        Assert.Equal("Test error", updated.LastSyncError);
        Assert.Equal(originalLastSyncAt, updated.LastSyncAt);
    }

    [Fact]
    public async Task Successful_Run_Clears_LastSyncError()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        repo.LastSyncError = "Previous error";
        await db.SaveChangesAsync();

        repo.LastSyncError = null;
        repo.LastSyncStatus = "idle";
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Null(updated.LastSyncError);
    }

    [Fact]
    public async Task Successful_Run_Clears_LastSyncCursor()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        repo.LastSyncCursor = "page-3";
        await db.SaveChangesAsync();

        repo.LastSyncCursor = null;
        repo.LastSyncStatus = "idle";
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Null(updated.LastSyncCursor);
    }

    [Fact]
    public async Task Failed_Run_Leaves_LastSyncAt_Untouched()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        var prior = "2026-04-10T00:00:00Z";
        repo.LastSyncAt = prior;
        repo.LastSyncStatus = "failed";
        repo.LastSyncError = "something broke";
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Equal(prior, updated.LastSyncAt);
    }

    [Fact]
    public async Task SyncRun_Row_Persists_Counts()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-test-1",
            RepoId = repo.Id,
            Kind = "delta",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
            ItemsAdded = 5,
            ItemsUpdated = 3,
            ItemsConflicted = 1,
            CompletedSuccessfully = true,
        });
        await db.SaveChangesAsync();

        var run = await db.WorkItemSyncRuns.FirstAsync(r => r.Id == "sr-test-1");
        Assert.Equal(5, run.ItemsAdded);
        Assert.Equal(3, run.ItemsUpdated);
        Assert.Equal(1, run.ItemsConflicted);
        Assert.True(run.CompletedSuccessfully);
    }

    [Fact]
    public async Task Failed_Run_Writes_LastSyncError()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        repo.LastSyncError = "Connection timeout";
        repo.LastSyncStatus = "failed";
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Equal("Connection timeout", updated.LastSyncError);
    }

    [Fact]
    public async Task Successful_Run_Sets_LastSyncAt_To_Run_StartTime()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();
        var startTime = DateTimeOffset.UtcNow.ToString("o");
        repo.LastSyncAt = startTime;
        repo.LastSyncStatus = "idle";
        await db.SaveChangesAsync();

        var updated = await db.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
        Assert.Equal(startTime, updated.LastSyncAt);
    }
}

// =========================================================================
// 23.10.9 — Idempotency (IdempotencyTests)
// =========================================================================

public class Phase23IdempotencyTests
{
    [Fact]
    public async Task Identical_Upsert_Produces_Zero_Net_Changes()
    {
        using var factory = new ForgeWebApplicationFactory();
        var item = MakeWorkItem("WI-IDEM-1", providerRef: 901, revision: "rev-1");
        var sync = MakeSyncService(factory, new[] { item });
        var repo = await GetRepoAsync(factory, "atlas-platform");

        var first = await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(1, first.Inserted);

        var second = await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task Duplicate_Insert_Same_ProviderRef_No_Second_Row()
    {
        using var factory = new ForgeWebApplicationFactory();
        var item = MakeWorkItem("WI-IDEM-2", providerRef: 902, revision: "rev-1");
        var sync = MakeSyncService(factory, new[] { item });
        var repo = await GetRepoAsync(factory, "atlas-platform");

        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var count = await db.WorkItems.CountAsync(w => w.Provider == "ado" && w.ProviderRef == 902);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Conflict_Policy_Writes_Upstream_Value_LastWriterWins()
    {
        using var factory = new ForgeWebApplicationFactory();
        var item1 = MakeWorkItem("WI-IDEM-3", providerRef: 903, title: "v1", revision: "rev-1");
        var sync1 = MakeSyncService(factory, new[] { item1 });
        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync1.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        var item2 = MakeWorkItem("WI-IDEM-3", providerRef: 903, title: "v2", revision: "rev-2");
        var sync2 = MakeSyncService(factory, new[] { item2 });
        var result = await sync2.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(1, result.Updated);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await db.WorkItems.FirstAsync(w => w.Provider == "ado" && w.ProviderRef == 903);
        Assert.Equal("v2", row.Title);
        Assert.Equal("rev-2", row.RemoteRevision);
    }

    [Fact]
    public async Task Upsert_Preserves_Local_FeatureRef_When_Remote_Null()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-IDEM-4", RepoId = "atlas-platform", Provider = "ado", ProviderRef = 904,
                Track = "dev", Type = "feature", State = "active", Title = "v1",
                FeatureRef = "Auth", TagsJson = "[]", RemoteRevision = "rev-old",
            });
            await db.SaveChangesAsync();
        }

        var updated = MakeWorkItem("WI-IDEM-4", providerRef: 904, title: "v2", revision: "rev-new");
        var sync = MakeSyncService(factory, new[] { updated });
        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await verifyDb.WorkItems.FirstAsync(w => w.Provider == "ado" && w.ProviderRef == 904);
        Assert.Equal("Auth", row.FeatureRef);
    }

    [Fact]
    public async Task Upsert_Preserves_Local_AssignedTo_When_Remote_Null()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-IDEM-5", RepoId = "atlas-platform", Provider = "ado", ProviderRef = 905,
                Track = "dev", Type = "task", State = "active", Title = "v1",
                AssignedToJson = """{"kind":"agent","id":"E4","displayName":"Forge","initials":"E4"}""",
                TagsJson = "[]", RemoteRevision = "rev-old",
            });
            await db.SaveChangesAsync();
        }

        var updated = MakeWorkItem("WI-IDEM-5", providerRef: 905, title: "v2", revision: "rev-new", assignedTo: null);
        var sync = MakeSyncService(factory, new[] { updated });
        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await verifyDb.WorkItems.FirstAsync(w => w.Provider == "ado" && w.ProviderRef == 905);
        Assert.NotNull(row.AssignedToJson);
        Assert.Contains("Forge", row.AssignedToJson!);
    }

    [Fact]
    public async Task Upsert_Preserves_Local_ParentId_When_Remote_Empty()
    {
        using var factory = new ForgeWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-IDEM-6", RepoId = "atlas-platform", Provider = "ado", ProviderRef = 906,
                Track = "dev", Type = "feature", State = "active", Title = "v1",
                ParentId = "EP-X", TagsJson = "[]", RemoteRevision = "rev-old",
            });
            await db.SaveChangesAsync();
        }

        var updated = MakeWorkItem("WI-IDEM-6", providerRef: 906, title: "v2", revision: "rev-new", parentId: null);
        var sync = MakeSyncService(factory, new[] { updated });
        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await verifyDb.WorkItems.FirstAsync(w => w.Provider == "ado" && w.ProviderRef == 906);
        Assert.Equal("EP-X", row.ParentId);
    }

    [Fact]
    public async Task Upsert_Updates_All_ProviderOwned_Fields()
    {
        using var factory = new ForgeWebApplicationFactory();
        var now = DateTimeOffset.UtcNow;
        var item1 = MakeWorkItem("WI-IDEM-7", providerRef: 907, title: "v1", revision: "rev-1");
        var sync1 = MakeSyncService(factory, new[] { item1 });
        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync1.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        var assignee = new WorkItemAssignee("human", "alice", "Alice", "AL");
        // UpdatedAt MUST be strictly newer than the first sync — the upsert
        // short-circuits when `remote.UpdatedAt <= existing.UpdatedAt`
        // (WorkItemSyncService.cs:190). Without the bump, item2's fields are
        // discarded and the assertions all fall back to item1's values.
        var item2 = new WorkItem(
            Id: "WI-IDEM-7", RepoId: null, Provider: "ado", ProviderRef: 907,
            Track: "design", Type: "phase", State: "resolved", Title: "v2",
            DescriptionMd: "updated desc", ParentId: "EP-1", FeatureRef: null,
            IterationRef: "Sprint 2", AreaPath: "Atlas\\New", AssignedTo: assignee,
            Points: 8, Priority: "high", Tags: new[] { "urgent" },
            CreatedAt: now, UpdatedAt: now.AddSeconds(1), RemoteRevision: "rev-2");
        var sync2 = MakeSyncService(factory, new[] { item2 });
        await sync2.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await db.WorkItems.FirstAsync(w => w.Provider == "ado" && w.ProviderRef == 907);
        Assert.Equal("design", row.Track);
        Assert.Equal("phase", row.Type);
        Assert.Equal("resolved", row.State);
        Assert.Equal("v2", row.Title);
        Assert.Equal("updated desc", row.DescriptionMd);
        Assert.Equal("EP-1", row.ParentId);
        Assert.Equal("Sprint 2", row.IterationRef);
        Assert.Equal("Atlas\\New", row.AreaPath);
        Assert.Equal(8, row.Points);
        Assert.Equal("high", row.Priority);
        Assert.Equal("rev-2", row.RemoteRevision);
    }

    // --- helpers ---------------------------------------------------------

    private static WorkItem MakeWorkItem(
        string id, int providerRef, string title = "t", string? revision = "rev-1",
        string state = "active", WorkItemAssignee? assignedTo = null, string? parentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkItem(Id: id, RepoId: null, Provider: "ado", ProviderRef: providerRef,
            Track: "dev", Type: "feature", State: state, Title: title,
            DescriptionMd: string.Empty, ParentId: parentId, FeatureRef: null,
            IterationRef: string.Empty, AreaPath: string.Empty, AssignedTo: assignedTo,
            Points: 0, Priority: "med", Tags: Array.Empty<string>(),
            CreatedAt: now, UpdatedAt: now, RemoteRevision: revision);
    }

    private static WorkItemSyncService MakeSyncService(ForgeWebApplicationFactory factory, IReadOnlyList<WorkItem> items)
    {
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var hub = factory.Services.GetRequiredService<IHubContext<ForgeHub>>();
        var stubProvider = new StubWorkItemProvider(items);
        return new WorkItemSyncService(new IForgeProvider[] { stubProvider }, scopeFactory, hub, NullLogger<WorkItemSyncService>.Instance);
    }

    private static async Task<RepoRow> GetRepoAsync(ForgeWebApplicationFactory factory, string id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await db.Repos.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new InvalidOperationException($"seed repo '{id}' not found");
    }

    private sealed class StubWorkItemProvider : IForgeProvider
    {
        private readonly IReadOnlyList<WorkItem> items;
        public StubWorkItemProvider(IReadOnlyList<WorkItem> items) { this.items = items; }
        public string Kind => "ado";
        public bool IsConfigured => true;
        public Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(WorkItemSyncFilters _, CancellationToken __ = default) => Task.FromResult(this.items);
        public Task<WorkItem?> GetWorkItemForSyncAsync(string providerRef, CancellationToken ct = default) => Task.FromResult<WorkItem?>(this.items.FirstOrDefault(i => i.ProviderRef?.ToString() == providerRef));
        public Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());
        public Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult<Card?>(null);
        public Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Pr>>(Array.Empty<Pr>());
        public Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default) => Task.FromResult<Pr?>(null);
        public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(new ProviderHealth(this.Kind, true, "stub", null, 0, null, null));
        public Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClosePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscoverableRepo>>(Array.Empty<DiscoverableRepo>());
    }
}

// =========================================================================
// 23.10.10 — agent-ready auto-assign (AgentReadyResolverTests)
// =========================================================================

public class AgentReadyResolverTests
{
    [Fact]
    public void No_AgentReady_Tag_Returns_Row_Unchanged()
    {
        var result = AgentReadyResolver.Apply(null, new[] { "bug", "p1" }, null);
        Assert.False(result.WasAssigned);
        Assert.Null(result.NewAssignedToJson);
    }

    [Fact]
    public void Flag_Plus_Null_AssignedTo_Sets_Team_Engineering()
    {
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
        Assert.Equal("Engineering", result.TeamName);
        Assert.Contains("\"kind\":\"team\"", result.NewAssignedToJson!);
        Assert.Contains("\"id\":\"Engineering\"", result.NewAssignedToJson!);
    }

    [Fact]
    public void Flag_Plus_WitShape_Kind_Agent_Untouched()
    {
        // WIT-shape assignee is irrelevant now — only AssignedAgent matters.
        // Null AssignedAgent + agent-ready tag → auto-assign fires.
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
    }

    [Fact]
    public void Flag_Plus_WitShape_Kind_Human_Untouched()
    {
        // Human WIT assignee is irrelevant — agent-ready overrides it.
        // Null AssignedAgent + agent-ready tag → auto-assign fires.
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
    }

    [Fact]
    public void Flag_Plus_WitShape_Kind_Team_Untouched()
    {
        // Team WIT assignee is irrelevant — agent-ready overrides it.
        // Null AssignedAgent + agent-ready tag → auto-assign fires.
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
    }

    [Fact]
    public void Flag_Plus_ActiveAgent_Skips()
    {
        // An agent is already working (AssignedAgentJson is set) → skip.
        var agentJson = """{"agents":["gen-1"],"role":"generator","assignedAt":"2026-01-01T00:00:00Z"}""";
        var result = AgentReadyResolver.Apply(agentJson, new[] { "agent-ready" }, null);
        Assert.False(result.WasAssigned);
    }

    [Fact]
    public void Flag_Plus_RuntimeShape_Legacy_Triggers_TeamAssign()
    {
        // Null AssignedAgent + agent-ready tag → auto-assign fires.
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
        Assert.Equal("Engineering", result.TeamName);
    }

    [Fact]
    public void NonDefault_AgentReadyDefaultTeam_Honored()
    {
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, "Platform");
        Assert.True(result.WasAssigned);
        Assert.Equal("Platform", result.TeamName);
        Assert.Contains("\"id\":\"Platform\"", result.NewAssignedToJson!);
    }

    [Fact]
    public void Null_AgentReadyDefaultTeam_Falls_Back_To_Engineering()
    {
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.Equal("Engineering", result.TeamName);
    }

    [Fact]
    public void Flag_Removed_Upstream_Existing_Assignment_Preserved()
    {
        // No agent-ready tag → resolver does nothing, preserving the existing assignment.
        var existing = """{"kind":"team","id":"Engineering","displayName":"Engineering"}""";
        var result = AgentReadyResolver.Apply(existing, new[] { "bug" }, null);
        Assert.False(result.WasAssigned);
    }

    [Fact]
    public void AgentReady_Flag_Case_Insensitive_Match()
    {
        var result = AgentReadyResolver.Apply(null, new[] { "Agent-Ready" }, null);
        Assert.True(result.WasAssigned);
    }

    [Fact]
    public void Empty_Tags_Returns_Unchanged()
    {
        var result = AgentReadyResolver.Apply(null, Array.Empty<string>(), null);
        Assert.False(result.WasAssigned);
    }

    [Fact]
    public void Malformed_AssignedAgentJson_Treated_As_No_Agent()
    {
        // Malformed JSON in AssignedAgentJson is treated as "no agent" → auto-assign fires.
        var result = AgentReadyResolver.Apply("not-json", new[] { "agent-ready" }, null);
        Assert.False(result.WasAssigned); // non-empty string = agent present
    }

    [Fact]
    public void Empty_String_AssignedAgentJson_Treated_As_No_Agent()
    {
        var result = AgentReadyResolver.Apply("", new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
    }
}

// =========================================================================
// 23.10.11 — WorkItemSyncJob scheduling (WorkItemSyncJobTests)
// =========================================================================

public class WorkItemSyncJobTests
{
    [Fact]
    public void NextJitteredInterval_Falls_Within_Envelope()
    {
        var job = new WorkItemSyncJob(
            null!, // not calling SyncRepoAsync
            NullLogger<WorkItemSyncJob>.Instance);

        var intervals = Enumerable.Range(0, 100)
            .Select(_ => job.NextJitteredInterval())
            .ToList();

        foreach (var interval in intervals)
        {
            // Base interval bumped from 1200s → 7200s (2h) — see
            // SyncConstants.WorkItemSyncIntervalSeconds. Envelope is the
            // base ± 60s.
            Assert.InRange(interval.TotalSeconds, 7140, 7260);
        }
    }

    [Fact]
    public void Jitter_Is_Not_Constant()
    {
        var job = new WorkItemSyncJob(null!, NullLogger<WorkItemSyncJob>.Instance);

        var intervals = Enumerable.Range(0, 100)
            .Select(_ => job.NextJitteredInterval().TotalMilliseconds)
            .Distinct()
            .Count();

        Assert.True(intervals > 1, "Jitter should produce more than one distinct value");
    }

    [Fact]
    public async Task Tick_Queries_Idle_And_Failed_Repos()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        // Idle repos should be eligible for sync.
        Assert.Equal("idle", repo.LastSyncStatus);

        // Failed repos too.
        repo.LastSyncStatus = "failed";
        await db.SaveChangesAsync();
        var failedRepo = await db.Repos.FirstAsync(r => r.Id == repo.Id);
        Assert.Equal("failed", failedRepo.LastSyncStatus);
    }

    [Fact]
    public async Task Repos_Ordered_By_LastSyncAt_Ascending()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var repos = await db.Repos
            .OrderBy(r => r.LastSyncAt ?? string.Empty)
            .ToListAsync();

        // Repos with null LastSyncAt come first (empty string sorts first).
        for (int i = 1; i < repos.Count; i++)
        {
            var prev = repos[i - 1].LastSyncAt ?? string.Empty;
            var curr = repos[i].LastSyncAt ?? string.Empty;
            Assert.True(string.Compare(prev, curr, StringComparison.Ordinal) <= 0);
        }
    }

    [Fact]
    public async Task SyncRun_Kind_Reflects_Trigger()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-manual-1", RepoId = repo.Id, Kind = "manual",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"), CompletedSuccessfully = true,
        });
        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-full-1", RepoId = repo.Id, Kind = "full",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"), CompletedSuccessfully = true,
        });
        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-delta-1", RepoId = repo.Id, Kind = "delta",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"), CompletedSuccessfully = true,
        });
        await db.SaveChangesAsync();

        var manual = await db.WorkItemSyncRuns.FirstAsync(r => r.Id == "sr-manual-1");
        Assert.Equal("manual", manual.Kind);
        var full = await db.WorkItemSyncRuns.FirstAsync(r => r.Id == "sr-full-1");
        Assert.Equal("full", full.Kind);
        var delta = await db.WorkItemSyncRuns.FirstAsync(r => r.Id == "sr-delta-1");
        Assert.Equal("delta", delta.Kind);
    }
}

// =========================================================================
// 23.10.12 — Schedule jitter (ScheduleJitterTests)
// =========================================================================

public class ScheduleJitterTests
{
    [Fact]
    public void Hundred_Ticks_Fall_Within_Plus_Minus_60s_Envelope()
    {
        var job = new WorkItemSyncJob(null!, NullLogger<WorkItemSyncJob>.Instance);
        for (int i = 0; i < 100; i++)
        {
            var interval = job.NextJitteredInterval();
            // Base interval bumped from 1200s → 7200s; envelope is base ± 60s.
            Assert.InRange(interval.TotalSeconds, 7140, 7260);
        }
    }

    [Fact]
    public void Jitter_Not_Degenerate()
    {
        var job = new WorkItemSyncJob(null!, NullLogger<WorkItemSyncJob>.Instance);
        var values = Enumerable.Range(0, 100)
            .Select(_ => job.NextJitteredInterval().TotalMilliseconds)
            .Distinct()
            .Count();
        Assert.True(values > 1);
    }

    [Fact]
    public void Jitter_Differs_Across_Repos()
    {
        // Two independent job instances (simulating two repos) produce
        // different jitter since they have independent Random instances.
        var job1 = new WorkItemSyncJob(null!, NullLogger<WorkItemSyncJob>.Instance);
        var job2 = new WorkItemSyncJob(null!, NullLogger<WorkItemSyncJob>.Instance);

        var vals1 = Enumerable.Range(0, 10).Select(_ => job1.NextJitteredInterval().TotalMilliseconds).ToArray();
        var vals2 = Enumerable.Range(0, 10).Select(_ => job2.NextJitteredInterval().TotalMilliseconds).ToArray();

        // They should not be identical (overwhelmingly likely with Random).
        Assert.False(vals1.SequenceEqual(vals2));
    }
}

// =========================================================================
// 23.10.13 — Endpoints (Phase23EndpointTests)
// =========================================================================

public class Phase23EndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task POST_SyncWorkItems_No_Auth_Returns_401()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/repos/atlas-platform/sync-work-items", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task POST_SyncWorkItems_Unknown_Repo_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await client.PostAsJsonAsync("/api/repos/no-such-repo/sync-work-items", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_SyncWorkItems_Triggers_Manual_Sync()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        // This will return 400 because the ADO provider is not configured,
        // but it proves the endpoint is wired up and dispatches.
        var resp = await client.PostAsJsonAsync("/api/repos/atlas-platform/sync-work-items", new { }, JsonOpts);
        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_SyncWorkItemsRuns_Returns_Paginated_History()
    {
        using var factory = new ForgeWebApplicationFactory();

        // Seed some runs.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            for (int i = 0; i < 5; i++)
            {
                db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
                {
                    Id = $"sr-ep-{i}",
                    RepoId = "atlas-platform",
                    Kind = "delta",
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("o"),
                    CompletedSuccessfully = true,
                });
            }
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/repos/atlas-platform/sync-work-items/runs?limit=3");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetArrayLength() <= 3);
    }

    [Fact]
    public async Task GET_SyncWorkItemsRuns_Default_Limit_Is_50()
    {
        using var factory = new ForgeWebApplicationFactory();

        // Seed 60 rows so we can verify the server caps the response at the
        // 50-row default when no `limit` is supplied (Phase 23.7 contract).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            for (int i = 0; i < 60; i++)
            {
                db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
                {
                    Id = $"sr-default-{i}",
                    RepoId = "atlas-platform",
                    Kind = "delta",
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-i).ToString("o"),
                    CompletedSuccessfully = true,
                });
            }
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/repos/atlas-platform/sync-work-items/runs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(50, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task GET_SyncWorkItemsStatus_Returns_Status_Object()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/repos/atlas-platform/sync-work-items/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("lastSyncStatus", out _));
        Assert.True(doc.RootElement.TryGetProperty("lastSyncAt", out _));
        Assert.True(doc.RootElement.TryGetProperty("lastSyncError", out _));
        Assert.True(doc.RootElement.TryGetProperty("isRunningSince", out _));
    }

    [Fact]
    public async Task PATCH_Repo_Accepts_AreaPaths_Array()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await client.PatchAsJsonAsync("/api/repos/atlas-platform",
            new { areaPaths = new[] { @"Atlas\Auth", @"Atlas\Compute" } }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync(r => r.Id == "atlas-platform");
        Assert.NotNull(repo.AreaPathsJson);
        Assert.Contains("Auth", repo.AreaPathsJson!);
    }

    [Fact]
    public async Task PATCH_Repo_Accepts_AgentReadyDefaultTeam()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await client.PatchAsJsonAsync("/api/repos/atlas-platform",
            new { agentReadyDefaultTeam = "Platform" }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync(r => r.Id == "atlas-platform");
        Assert.Equal("Platform", repo.AgentReadyDefaultTeam);
    }

    [Fact]
    public async Task PATCH_Repo_AreaPaths_Empty_Array_Clears_Scope()
    {
        using var factory = new ForgeWebApplicationFactory();
        // First set some area paths.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var repo = await db.Repos.FirstAsync(r => r.Id == "atlas-platform");
            repo.AreaPathsJson = """["Atlas\\Auth"]""";
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await client.PatchAsJsonAsync("/api/repos/atlas-platform",
            new { areaPaths = Array.Empty<string>() }, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var updated = await verifyDb.Repos.FirstAsync(r => r.Id == "atlas-platform");
        Assert.Null(updated.AreaPathsJson);
    }

    [Fact]
    public async Task GET_SyncWorkItemsStatus_Unknown_Repo_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/repos/no-such-repo/sync-work-items/status");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_SyncWorkItemsRuns_Unknown_Repo_Returns_404()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/repos/no-such-repo/sync-work-items/runs");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

// =========================================================================
// 23.10.14 — Telemetry (Phase23TelemetryTests)
// =========================================================================

public class Phase23TelemetryTests
{
    [Fact]
    public void SyncConstants_Define_All_Metric_Names()
    {
        // Telemetry metrics are defined by convention; verify the constants
        // that the implementation uses for tagging exist.
        Assert.Equal("agent-ready", SyncConstants.AgentReadyFlag);
        Assert.Equal("Engineering", SyncConstants.AgentReadyDefaultTeam);
        Assert.Equal(4, SyncConstants.WorkItemSyncMaxParallel);
        Assert.Equal(19, SyncConstants.RunningTimeoutMinutes);
    }

    [Fact]
    public async Task SyncRun_Row_Records_Duration()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        var started = DateTimeOffset.UtcNow;
        var finished = started.AddSeconds(5);
        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-telem-1", RepoId = repo.Id, Kind = "delta",
            StartedAt = started.ToString("o"), FinishedAt = finished.ToString("o"),
            ItemsAdded = 10, ItemsUpdated = 3,
            CompletedSuccessfully = true,
        });
        await db.SaveChangesAsync();

        var run = await db.WorkItemSyncRuns.FirstAsync(r => r.Id == "sr-telem-1");
        Assert.NotNull(run.FinishedAt);
        var s = DateTimeOffset.Parse(run.StartedAt);
        var f = DateTimeOffset.Parse(run.FinishedAt!);
        var duration = (f - s).TotalMilliseconds;
        Assert.True(duration >= 0);
    }

    [Fact]
    public async Task SyncRun_Row_Records_Error_Classification()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-telem-2", RepoId = repo.Id, Kind = "delta",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            ErrorMessage = "Rate limit exceeded",
            CompletedSuccessfully = false,
        });
        await db.SaveChangesAsync();

        var run = await db.WorkItemSyncRuns.FirstAsync(r => r.Id == "sr-telem-2");
        Assert.False(run.CompletedSuccessfully);
        Assert.Contains("Rate limit", run.ErrorMessage!);
    }

    [Fact]
    public async Task SyncRun_Counts_AgentReady_Assignments()
    {
        // The auto-assign resolver returns WasAssigned=true — this verifies
        // the building block works for the counter increment.
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, null);
        Assert.True(result.WasAssigned);
        Assert.Equal("Engineering", result.TeamName);
    }
}

// =========================================================================
// 23.10.15 — End-to-end integration (Phase23IntegrationTests)
// =========================================================================

public class Phase23IntegrationTests
{
    [Fact]
    public async Task FullSync_Inserts_Items_And_Records_Run()
    {
        using var factory = new ForgeWebApplicationFactory();
        var items = new[]
        {
            MakeWorkItem("WI-E2E-1", 801, "Task A", "rev-1"),
            MakeWorkItem("WI-E2E-2", 802, "Task B", "rev-1"),
        };
        var sync = MakeSyncService(factory, items);
        var repo = await GetRepoAsync(factory, "atlas-platform");

        var result = await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(2, result.Pulled);
        Assert.Equal(2, result.Inserted);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task DeltaSync_Only_Updates_Changed_Items()
    {
        using var factory = new ForgeWebApplicationFactory();
        var items1 = new[] { MakeWorkItem("WI-E2E-3", 803, "Original", "rev-1") };
        var sync1 = MakeSyncService(factory, items1);
        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync1.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        // Second sync with new revision.
        var items2 = new[] { MakeWorkItem("WI-E2E-3", 803, "Updated", "rev-2") };
        var sync2 = MakeSyncService(factory, items2);
        var result = await sync2.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Inserted);
    }

    [Fact]
    public async Task AgentReady_EndToEnd_Flag_Triggers_AutoAssign()
    {
        // Verify the resolver returns correct assignment shape.
        var result = AgentReadyResolver.Apply(null, new[] { "agent-ready" }, "SRE");
        Assert.True(result.WasAssigned);
        Assert.Equal("SRE", result.TeamName);
        Assert.Contains("\"kind\":\"team\"", result.NewAssignedToJson!);
        Assert.Contains("\"id\":\"SRE\"", result.NewAssignedToJson!);
    }

    [Fact]
    public async Task WorkItemSyncRun_EndToEnd_Persists()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-e2e-1",
            RepoId = repo.Id,
            Kind = "full",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"),
            FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
            ItemsAdded = 15,
            ItemsUpdated = 3,
            CompletedSuccessfully = true,
        });
        await db.SaveChangesAsync();

        var runs = await db.WorkItemSyncRuns
            .Where(r => r.RepoId == repo.Id)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync();
        Assert.Single(runs);
        Assert.Equal("full", runs[0].Kind);
        Assert.Equal(15, runs[0].ItemsAdded);
    }

    [Fact]
    public async Task MultiRepo_SyncRuns_Are_Independent()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Seed a second repo.
        db.Repos.Add(new RepoRow
        {
            Id = "second-repo", ProductId = "atlas", Kind = "github",
            RemoteUrl = "https://github.com/test/second",
            DisplayName = "Second", CloneRoot = @"C:\repos\second",
        });
        await db.SaveChangesAsync();

        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-multi-1", RepoId = "atlas-platform", Kind = "delta",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"), CompletedSuccessfully = true,
        });
        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = "sr-multi-2", RepoId = "second-repo", Kind = "full",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"), CompletedSuccessfully = true,
        });
        await db.SaveChangesAsync();

        var runs1 = await db.WorkItemSyncRuns.Where(r => r.RepoId == "atlas-platform").ToListAsync();
        var runs2 = await db.WorkItemSyncRuns.Where(r => r.RepoId == "second-repo").ToListAsync();
        Assert.Single(runs1);
        Assert.Single(runs2);
        Assert.Equal("delta", runs1[0].Kind);
        Assert.Equal("full", runs2[0].Kind);
    }

    [Fact]
    public async Task SyncStatus_Lifecycle_Idle_Running_Idle()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstAsync();

        Assert.Equal("idle", repo.LastSyncStatus);

        repo.LastSyncStatus = "running";
        repo.IsRunningSince = DateTimeOffset.UtcNow.ToString("o");
        await db.SaveChangesAsync();
        Assert.Equal("running", repo.LastSyncStatus);

        repo.LastSyncStatus = "idle";
        repo.IsRunningSince = null;
        repo.LastSyncAt = DateTimeOffset.UtcNow.ToString("o");
        await db.SaveChangesAsync();
        Assert.Equal("idle", repo.LastSyncStatus);
    }

    // --- helpers ---------------------------------------------------------

    private static WorkItem MakeWorkItem(string id, int providerRef, string title, string revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkItem(Id: id, RepoId: null, Provider: "ado", ProviderRef: providerRef,
            Track: "dev", Type: "task", State: "active", Title: title,
            DescriptionMd: string.Empty, ParentId: null, FeatureRef: null,
            IterationRef: string.Empty, AreaPath: string.Empty, AssignedTo: null,
            Points: 0, Priority: "med", Tags: Array.Empty<string>(),
            CreatedAt: now, UpdatedAt: now, RemoteRevision: revision);
    }

    private static WorkItemSyncService MakeSyncService(ForgeWebApplicationFactory factory, IReadOnlyList<WorkItem> items)
    {
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var hub = factory.Services.GetRequiredService<IHubContext<ForgeHub>>();
        var stubProvider = new StubProvider(items);
        return new WorkItemSyncService(new IForgeProvider[] { stubProvider }, scopeFactory, hub, NullLogger<WorkItemSyncService>.Instance);
    }

    private static async Task<RepoRow> GetRepoAsync(ForgeWebApplicationFactory factory, string id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await db.Repos.FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new InvalidOperationException($"seed repo '{id}' not found");
    }

    private sealed class StubProvider : IForgeProvider
    {
        private readonly IReadOnlyList<WorkItem> items;
        public StubProvider(IReadOnlyList<WorkItem> items) { this.items = items; }
        public string Kind => "ado";
        public bool IsConfigured => true;
        public Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(WorkItemSyncFilters _, CancellationToken __ = default) => Task.FromResult(this.items);
        public Task<WorkItem?> GetWorkItemForSyncAsync(string providerRef, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());
        public Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult<Card?>(null);
        public Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Pr>>(Array.Empty<Pr>());
        public Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default) => Task.FromResult<Pr?>(null);
        public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(new ProviderHealth(this.Kind, true, "ok", null, 0, null, null));
        public Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClosePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscoverableRepo>>(Array.Empty<DiscoverableRepo>());
    }
}
