// -----------------------------------------------------------------------
// <copyright file="WorkItemSyncUpsertTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 12.11 — invariants of the sync upsert path. Drives
/// <see cref="WorkItemSyncService"/> with a stub provider so the merge
/// behaviour is observable without HTTP cassettes.
/// </summary>
public class WorkItemSyncUpsertTests
{
    [Fact]
    public async Task First_Sync_Inserts_New_Row()
    {
        using var factory = new ForgeWebApplicationFactory();
        var sync = MakeService(factory, new[]
        {
            MakeWorkItem("WI-1", providerRef: 1, title: "from remote", state: "active"),
        });

        var repo = await GetRepoAsync(factory, "atlas-platform");
        var result = await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        Assert.Equal(1, result.Pulled);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await db.WorkItems.FirstAsync(w => w.Provider == "stub" && w.ProviderRef == 1);
        Assert.Equal("from remote", row.Title);
        Assert.Equal("active", row.State);
        Assert.Equal("atlas-platform", row.RepoId);
    }

    [Fact]
    public async Task Repeat_Sync_With_Same_Revision_Skips()
    {
        using var factory = new ForgeWebApplicationFactory();
        var item = MakeWorkItem("WI-2", providerRef: 2, title: "v1", state: "active", revision: "rev-1");
        var sync = MakeService(factory, new[] { item });

        var repo = await GetRepoAsync(factory, "atlas-platform");

        // First call inserts.
        var first = await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(1, first.Inserted);

        // Second call with the same revision skips.
        var second = await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task Update_Preserves_Local_FeatureRef_When_Remote_Empty()
    {
        using var factory = new ForgeWebApplicationFactory();

        // Pre-seed a row that already has FeatureRef set locally.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-3",
                RepoId = "atlas-platform",
                Provider = "stub",
                ProviderRef = 3,
                Track = "dev",
                Type = "feature",
                State = "active",
                Title = "v1",
                FeatureRef = "Auth", // local-only metadata the remote never carries
                TagsJson = "[]",
                RemoteRevision = "rev-old",
            });
            await db.SaveChangesAsync();
        }

        // Remote arrives with a different revision but no FeatureRef.
        var sync = MakeService(factory, new[]
        {
            MakeWorkItem("WI-3", providerRef: 3, title: "v2", state: "resolved", revision: "rev-new"),
        });

        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await verifyDb.WorkItems.FirstAsync(w => w.Provider == "stub" && w.ProviderRef == 3);
        Assert.Equal("v2", row.Title);
        Assert.Equal("resolved", row.State);
        // CRITICAL invariant: local FeatureRef survived the upsert.
        Assert.Equal("Auth", row.FeatureRef);
        Assert.Equal("rev-new", row.RemoteRevision);
    }

    [Fact]
    public async Task Update_Preserves_Local_Assignee_When_Remote_Null()
    {
        using var factory = new ForgeWebApplicationFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "WI-4",
                RepoId = "atlas-platform",
                Provider = "stub",
                ProviderRef = 4,
                Track = "dev",
                Type = "task",
                State = "active",
                Title = "v1",
                AssignedToJson = """{"kind":"agent","id":"E4","displayName":"Forge","initials":"E4"}""",
                TagsJson = "[]",
                RemoteRevision = "rev-old",
            });
            await db.SaveChangesAsync();
        }

        var sync = MakeService(factory, new[]
        {
            // Remote has no assignee on this update — must NOT clobber the local agent.
            MakeWorkItem("WI-4", providerRef: 4, title: "v2", state: "active", revision: "rev-new", assignedTo: null),
        });

        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await verifyDb.WorkItems.FirstAsync(w => w.Provider == "stub" && w.ProviderRef == 4);
        Assert.NotNull(row.AssignedToJson);
        Assert.Contains("Forge", row.AssignedToJson!);
    }

    [Fact]
    public async Task Update_Preserves_Local_Parent_When_Remote_Empty()
    {
        using var factory = new ForgeWebApplicationFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.WorkItems.AddRange(
                new WorkItemRow { Id = "EP-X", Track = "dev", Type = "epic", State = "active", Title = "Epic X", TagsJson = "[]" },
                new WorkItemRow
                {
                    Id = "WI-5", RepoId = "atlas-platform", Provider = "stub", ProviderRef = 5,
                    Track = "dev", Type = "feature", State = "active", Title = "v1",
                    ParentId = "EP-X",
                    TagsJson = "[]", RemoteRevision = "rev-old",
                });
            await db.SaveChangesAsync();
        }

        var sync = MakeService(factory, new[]
        {
            // Remote arrives with no ParentId → keep the local re-parent.
            MakeWorkItem("WI-5", providerRef: 5, title: "v2", state: "active", revision: "rev-new", parentId: null),
        });

        var repo = await GetRepoAsync(factory, "atlas-platform");
        await sync.SyncAsync(repo, new WorkItemSyncFilters(), ignoreThrottle: true, default);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await verifyDb.WorkItems.FirstAsync(w => w.Provider == "stub" && w.ProviderRef == 5);
        Assert.Equal("EP-X", row.ParentId);
    }

    // --- helpers ---------------------------------------------------------

    private static WorkItem MakeWorkItem(
        string id,
        int providerRef,
        string title,
        string state,
        string? revision = "rev-1",
        string track = "dev",
        string type = "feature",
        WorkItemAssignee? assignedTo = null,
        string? parentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkItem(
            Id: id,
            RepoId: null, // sync service pins to the requested repo
            Provider: "stub",
            ProviderRef: providerRef,
            Track: track,
            Type: type,
            State: state,
            Title: title,
            DescriptionMd: string.Empty,
            ParentId: parentId,
            FeatureRef: null,
            IterationRef: string.Empty,
            AreaPath: string.Empty,
            AssignedTo: assignedTo,
            Points: 0,
            Priority: "med",
            Tags: Array.Empty<string>(),
            CreatedAt: now,
            UpdatedAt: now,
            RemoteRevision: revision);
    }

    private static WorkItemSyncService MakeService(
        ForgeWebApplicationFactory factory,
        IReadOnlyList<WorkItem> remoteItems)
    {
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var hub = factory.Services.GetRequiredService<IHubContext<ForgeHub>>();
        var stubProvider = new StubWorkItemProvider(remoteItems);
        var providers = new IForgeProvider[] { stubProvider };
        return new WorkItemSyncService(providers, scopeFactory, hub, NullLogger<WorkItemSyncService>.Instance);
    }

    private static async Task<RepoRow> GetRepoAsync(ForgeWebApplicationFactory factory, string id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        return repo ?? throw new InvalidOperationException($"seed repo '{id}' not found");
    }

    /// <summary>
    /// Inline provider that returns whatever the test sets up. Reports
    /// <c>Kind = "stub"</c> + <c>IsConfigured = true</c> so the sync service
    /// picks it up. The seeded atlas-platform repo's Kind is "ado", so we
    /// also override <see cref="RepoRow.Kind"/> via a fixture wrapper inside
    /// each test if we need a different match — easier path: stub claims to
    /// be every kind by accepting whatever Kind property the seeded repo has.
    /// </summary>
    private sealed class StubWorkItemProvider : IForgeProvider
    {
        private readonly IReadOnlyList<WorkItem> items;
        public StubWorkItemProvider(IReadOnlyList<WorkItem> items) { this.items = items; }

        // Match any repo kind so the seeded "atlas-platform / ado" repo dispatches here.
        public string Kind => "ado";
        public bool IsConfigured => true;

        public Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(WorkItemSyncFilters _, CancellationToken __ = default)
            => Task.FromResult(this.items);

        public Task<WorkItem?> GetWorkItemForSyncAsync(string providerRef, CancellationToken ct = default)
            => Task.FromResult<WorkItem?>(this.items.FirstOrDefault(i => i.ProviderRef?.ToString() == providerRef));

        // The rest of the surface is inert for these tests.
        public Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());
        public Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default) => Task.FromResult<Card?>(null);
        public Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Pr>>(Array.Empty<Pr>());
        public Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default) => Task.FromResult<Pr?>(null);
        public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderHealth(this.Kind, true, "stub", null, 0, null, null));
        public Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) => Task.FromResult<BuildStatus?>(null);
        public Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscoverableRepo>>(Array.Empty<DiscoverableRepo>());
    }
}
