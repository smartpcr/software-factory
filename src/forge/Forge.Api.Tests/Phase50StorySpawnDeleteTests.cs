// -----------------------------------------------------------------------
// <copyright file="Phase50StorySpawnDeleteTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 50 — DELETE /api/stories/{storyId}/spawn coverage. Each Fact maps
/// to one of the requirements the operator's "delete and restart" button
/// flow has to satisfy:
/// <list type="bullet">
///   <item>404 when no spawn exists.</item>
///   <item>403 when caller isn't a writer.</item>
///   <item>Spawn with no children: spawn row removed.</item>
///   <item>Spawn with children + open PRs: pending PRs closed upstream
///         (verified via stub provider call recorder), every PR row
///         removed locally regardless of state.</item>
///   <item>Spawn with merged PRs: provider.ClosePullRequestAsync is NOT
///         called for already-merged rows (we don't poke a finalised PR).</item>
///   <item>Plan-doc / spawn-id synthetic rows (deliverables, transcripts,
///         queue, work items) are wiped — rubber-duck blind spot #2.</item>
///   <item>AgentClaimRow rows for any deleted work item are released
///         (ReleasedAt set) — rubber-duck blind spot #3.</item>
///   <item>WorkstreamDepRow edges referencing deleted workstreams are
///         cleaned (covered by CascadeDeleteWorkstreamAsync).</item>
///   <item>Build chain rows tied to removed PRs are wiped.</item>
///   <item>Non-main base branch + deleteBaseBranch=false → spawn deleted,
///         base branch left alone.</item>
///   <item>Non-main base branch + deleteBaseBranch=true + no other
///         spawn shares it → endpoint returns baseBranchDeleted=true.</item>
///   <item>Non-main base branch + deleteBaseBranch=true + ANOTHER
///         spawn shares it → 409, NO destructive side effects (the
///         spawn row + child rows survive).</item>
///   <item>Concurrent DELETE: second call returns 404 because the spawn
///         row is gone after the first.</item>
/// </list>
/// </summary>
[Collection("Phase50")]
public class Phase50StorySpawnDeleteTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    /// <summary>
    /// Stub provider that records every <c>ClosePullRequestAsync</c> call
    /// so tests can assert the abandon-pending-PRs requirement.
    /// </summary>
    private sealed class RecordingProvider : IForgeProvider
    {
        public ConcurrentBag<string> ClosedPrIds { get; } = new();

        public string Kind => "github";

        public bool IsConfigured => true;

        public Task ClosePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default)
        {
            this.ClosedPrIds.Add(id);
            return Task.CompletedTask;
        }

        // The rest are no-ops / NotImplemented for the unused surface.
        public Task<IReadOnlyList<Card>> ListWorkItemsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());
        public Task<Card?> GetWorkItemAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<Card?>(null);
        public Task<IReadOnlyList<Pr>> ListPullRequestsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Pr>>(Array.Empty<Pr>());
        public Task<Pr?> GetPullRequestAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<Pr?>(null);
        public Task<BuildStatus?> GetLatestBuildAsync(string branch, CancellationToken ct = default) =>
            Task.FromResult<BuildStatus?>(null);
        public Task<ProviderHealth> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderHealth(this.Kind, true, "fake", null, 0, null, null));
        public Task<Card> CreateWorkItemAsync(NewWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<Card> UpdateWorkItemAsync(string id, UpdateWorkItemRequest req, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task CommentOnWorkItemAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<Pr> CreatePullRequestAsync(NewPullRequestRequest req, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task ApprovePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ReplyToReviewCommentAsync(string prId, string parentProviderCommentId, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<Pr> MergePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<BuildStatus?> TriggerBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.FromResult<BuildStatus?>(null);
        public Task<BuildStatus?> CancelBuildAsync(string branch, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.FromResult<BuildStatus?>(null);
        public Task<IReadOnlyList<DiscoverableRepo>> ListRepositoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoverableRepo>>(Array.Empty<DiscoverableRepo>());
        public Task<IReadOnlyList<WorkItem>> ListWorkItemsForSyncAsync(WorkItemSyncFilters filters, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());
        public Task<WorkItem?> GetWorkItemForSyncAsync(string providerRef, CancellationToken ct = default) =>
            Task.FromResult<WorkItem?>(null);
    }

    /// <summary>
    /// Test-only factory that swaps the upstream <see cref="IForgeProvider"/>s
    /// for a single <see cref="RecordingProvider"/> exposed via the
    /// instance so individual tests can inspect call counts. Mirrors the
    /// <c>MergeStubFactory</c> pattern used by
    /// <c>PrsEndpointsMergeConflictTests</c>.
    /// </summary>
    private sealed class DeleteSpawnFactory : ForgeWebApplicationFactory
    {
        public RecordingProvider Provider { get; } = new();
        public string DatabasePath => this.DbPath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IForgeProvider>();
                s.AddSingleton<IForgeProvider>(_ => this.Provider);
            });
        }
    }

    private static (DeleteSpawnFactory factory, HttpClient client) NewFactory()
    {
        var factory = new DeleteSpawnFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);
        return (factory, client);
    }

    /// <summary>
    /// Seed a complete spawn fixture: spawn row + N workstream rows + one
    /// PR per workstream + plan PR + one synthetic plan-doc deliverable +
    /// one agent claim. Returns the spawn id and the workstream ids so
    /// tests can assert against them.
    /// </summary>
    private static async Task<(string spawnId, string[] wsIds, string[] prIds)> SeedSpawnAsync(
        DeleteSpawnFactory f,
        string storyId,
        int wsCount = 2,
        string baseBranch = "main",
        string[]? prCols = null)
    {
        prCols ??= Enumerable.Repeat("review", wsCount).ToArray();
        var spawnId = $"spawn-{storyId}";
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        if (!await db.WorkItems.AnyAsync(w => w.Id == storyId))
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId,
                Title = $"Test story {storyId}",
                Provider = "local",
                Track = "dev",
                Type = "feature",
                State = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = $"Test story {storyId}",
            StoryDescription = "Test description",
            Phase = StorySpawnPhases.Implement,
            BaseBranch = baseBranch,
            PlanBranch = $"story/{storyId}/plan",
            PlanPrId = $"pr-plan-{storyId}",
            QaBranch = $"story/{storyId}/qa",
            DecompositionApproved = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Plan PR — already merged so the test can verify merged PRs are
        // removed locally without being closed upstream.
        db.Prs.Add(new PrRow
        {
            Id = $"pr-plan-{storyId}",
            Col = "merged",
            Branch = $"story/{storyId}/plan",
            Base = baseBranch,
            Title = $"Plan for {storyId}",
            Provider = "github",
            WebUrl = $"https://github.com/test/repo/pull/0",
        });

        var wsIds = new string[wsCount];
        var prIds = new List<string> { $"pr-plan-{storyId}" };
        for (var i = 0; i < wsCount; i++)
        {
            var wsId = $"ws-{storyId}-stage-{i + 1}";
            wsIds[i] = wsId;
            var prId = $"pr-ws-{storyId}-{i + 1}";
            prIds.Add(prId);

            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                StorySpawnId = spawnId,
                Name = $"Stage {i + 1}",
                Branch = $"ws/{storyId}/stage-{i + 1}",
                Base = baseBranch,
                Epic = storyId,
                Status = "active",
                Summary = $"Stage {i + 1} work",
                PrsJson = $"[\"{prId}\"]",
            });

            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = prCols[i],
                Branch = $"ws/{storyId}/stage-{i + 1}",
                Base = baseBranch,
                Title = $"Stage {i + 1}",
                Provider = "github",
                WebUrl = $"https://github.com/test/repo/pull/{i + 1}",
                WorkItemId = wsId,
            });

            db.WorkItems.Add(new WorkItemRow
            {
                Id = wsId,
                Title = $"Stage {i + 1}",
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = wsId,
                CrewId = "crew-eng",
                Iter = 1,
                Score = 88,
                Verdict = "iterate",
                Notes = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            db.AgentClaims.Add(new AgentClaimRow
            {
                AgentId = $"agent-{i + 1}",
                WorkItemId = wsId,
                Role = "generator",
                ClaimedAt = DateTimeOffset.UtcNow,
                ReleasedAt = null,
            });
        }

        // Synthetic plan-doc rows (rubber-duck blind spot #2 — these are
        // emitted under id `plan-doc:{spawnId}:architecture.md`, not under
        // any workstream id, so the per-workstream cascade misses them).
        var planDocId = $"plan-doc:{spawnId}:architecture.md";
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = planDocId,
            CrewId = "crew-arch",
            Iter = 3,
            Score = 92,
            Verdict = "pass",
            Notes = "test",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = planDocId,
            Kind = "task.token",
            PayloadJson = "{\"text\":\"test event\"}",
            At = DateTimeOffset.UtcNow,
        });

        // Synthetic spawn-id agent claim (covers the decompose / qa-features
        // path which uses spawn.Id directly as the work-item id).
        db.AgentClaims.Add(new AgentClaimRow
        {
            AgentId = "agent-arch",
            WorkItemId = spawnId,
            Role = "generator",
            ClaimedAt = DateTimeOffset.UtcNow,
            ReleasedAt = null,
        });

        // Phase 50 follow-up (2026-05-12) — cost ledger rows. DeliverableLoop
        // emits one AgentUsageRow per gen/eval invocation with
        // TaskId = workItemId, and the operator-facing doc / workstream
        // cost cards sum these for the doc's deterministic plan-doc id.
        // Without the purge, re-spawning the same story leaks the prior
        // cycle's $$$ totals onto the brand-new doc cards.
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-arch",
            CrewId = "crew-arch",
            TaskId = planDocId,
            Model = "stub",
            InputTokens = 1000,
            OutputTokens = 2000,
            CostUsd = 12.34m,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "ok",
        });
        for (var i = 0; i < wsCount; i++)
        {
            db.AgentUsage.Add(new AgentUsageRow
            {
                AgentId = $"agent-{i + 1}",
                CrewId = "crew-eng",
                TaskId = wsIds[i],
                Model = "stub",
                InputTokens = 500,
                OutputTokens = 1500,
                CostUsd = 7.89m,
                StartedAt = DateTimeOffset.UtcNow,
                Status = "ok",
            });
        }

        await db.SaveChangesAsync();
        return (spawnId, wsIds, prIds.ToArray());
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string storyId, bool? deleteBaseBranch = null)
    {
        var qs = deleteBaseBranch.HasValue ? $"?deleteBaseBranch={deleteBaseBranch.Value.ToString().ToLowerInvariant()}" : string.Empty;
        return await client.DeleteAsync($"/api/stories/{storyId}/spawn{qs}");
    }

    // -----------------------------------------------------------------------
    // 404 / auth
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_returns_404_when_no_spawn_exists()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;

        var resp = await DeleteAsync(client, "story-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_rejects_non_writer()
    {
        var (factory, _) = NewFactory();
        using var _f = factory;
        var anonClient = factory.CreateClient(); // no X-Forge-User → reader

        var storyId = "WI-50001";
        await SeedSpawnAsync(factory, storyId, wsCount: 1);

        var resp = await anonClient.DeleteAsync($"/api/stories/{storyId}/spawn");
        Assert.True(
            resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Expected 401/403, got {(int)resp.StatusCode}");
    }

    // -----------------------------------------------------------------------
    // Happy paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_with_no_workstreams_just_removes_spawn_row()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50010";

        // Seed a bare spawn — no workstreams, no PRs.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = $"spawn-{storyId}",
                StoryId = storyId,
                StoryTitle = "Empty",
                Phase = StorySpawnPhases.Plan,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await DeleteAsync(client, storyId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.False(await verifyDb.StorySpawns.AnyAsync(s => s.StoryId == storyId));
    }

    [Fact]
    public async Task Delete_closes_pending_PRs_upstream_and_skips_merged()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50011";
        // ws[0] open ("review"), ws[1] merged. plan PR is also merged
        // (seeded as merged in helper).
        var (_, _, prIds) = await SeedSpawnAsync(factory, storyId, wsCount: 2,
            prCols: new[] { "review", "merged" });

        var resp = await DeleteAsync(client, storyId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var pendingPrId = prIds.Single(p => p == $"pr-ws-{storyId}-1");
        var mergedPrIds = new[] { $"pr-plan-{storyId}", $"pr-ws-{storyId}-2" };

        // Pending PR was abandoned upstream...
        Assert.Contains(pendingPrId, factory.Provider.ClosedPrIds);
        // ...merged PRs were NOT poked.
        foreach (var id in mergedPrIds)
        {
            Assert.DoesNotContain(id, factory.Provider.ClosedPrIds);
        }

        // ALL PR rows are removed locally (rubber-duck choice: full
        // restart-from-scratch wipes both merged + abandoned for this
        // spawn).
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        foreach (var id in prIds)
        {
            Assert.False(await verifyDb.Prs.AnyAsync(p => p.Id == id),
                $"PR {id} should have been removed from local DB");
        }
    }

    [Fact]
    public async Task Delete_wipes_workstreams_and_their_synthetic_rows()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50012";
        var (spawnId, wsIds, _) = await SeedSpawnAsync(factory, storyId, wsCount: 2);

        var resp = await DeleteAsync(client, storyId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.False(await verifyDb.StorySpawns.AnyAsync(s => s.Id == spawnId));
        foreach (var wsId in wsIds)
        {
            Assert.False(await verifyDb.Workstreams.AnyAsync(w => w.Id == wsId));
            Assert.False(await verifyDb.Deliverables.AnyAsync(d => d.WorkItemId == wsId));
            Assert.False(await verifyDb.WorkItems.AnyAsync(w => w.Id == wsId));
        }

        // Rubber-duck blind spot #2 — plan-doc synthetic rows must also
        // be wiped (the per-workstream cascade misses ids prefixed with
        // `plan-doc:{spawnId}:`).
        var planDocId = $"plan-doc:{spawnId}:architecture.md";
        Assert.False(await verifyDb.Deliverables.AnyAsync(d => d.WorkItemId == planDocId));
        Assert.False(await verifyDb.TaskTranscriptEvents.AnyAsync(e => e.WorkItemId == planDocId));

        // Phase 50 follow-up (2026-05-12) — operator reported re-spawned
        // story showed ~$1300-$2000 per plan-doc card BEFORE any LLM ran.
        // The deterministic spawn id meant new TaskIds collided 1:1 with
        // old `agent_usage` rows. Assert the cost ledger is now purged for
        // BOTH the synthetic plan-doc id and every workstream id (the
        // latter via CascadeDeleteWorkstreamAsync).
        Assert.False(await verifyDb.AgentUsage.AnyAsync(u => u.TaskId == planDocId));
        foreach (var wsId in wsIds)
        {
            Assert.False(await verifyDb.AgentUsage.AnyAsync(u => u.TaskId == wsId));
        }
    }

    [Fact]
    public async Task Delete_releases_active_AgentClaims_for_all_deleted_ids()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50013";
        var (spawnId, wsIds, _) = await SeedSpawnAsync(factory, storyId, wsCount: 2);

        var resp = await DeleteAsync(client, storyId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Every claim that was active under any deleted work item should
        // now have ReleasedAt set OR the row itself should be gone (for
        // synthetic ids the cascade also removes the row).
        var spawnClaim = await verifyDb.AgentClaims
            .FirstOrDefaultAsync(c => c.WorkItemId == spawnId);
        if (spawnClaim is not null)
        {
            Assert.NotNull(spawnClaim.ReleasedAt);
        }

        foreach (var wsId in wsIds)
        {
            var claim = await verifyDb.AgentClaims
                .FirstOrDefaultAsync(c => c.WorkItemId == wsId);
            if (claim is not null)
            {
                Assert.NotNull(claim.ReleasedAt);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Base-branch handling
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_with_non_main_base_and_flag_false_keeps_base()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50020";
        await SeedSpawnAsync(factory, storyId, wsCount: 1, baseBranch: "release/2025.10");

        var resp = await DeleteAsync(client, storyId, deleteBaseBranch: false);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("baseBranchDeleted").GetBoolean());

        // Spawn is gone — verify that even though we kept the base.
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.False(await verifyDb.StorySpawns.AnyAsync(s => s.StoryId == storyId));
    }

    [Fact]
    public async Task Delete_with_non_main_base_and_flag_true_reports_base_deleted()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50021";
        await SeedSpawnAsync(factory, storyId, wsCount: 1, baseBranch: "release/2025.11");

        var resp = await DeleteAsync(client, storyId, deleteBaseBranch: true);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("baseBranchDeleted").GetBoolean());
    }

    [Fact]
    public async Task Delete_with_shared_base_branch_returns_409_no_destructive_side_effects()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;

        // Two spawns share the same non-main base branch.
        var sharedBase = "release/2025.12";
        var storyA = "WI-50030";
        var storyB = "WI-50031";
        var (spawnAId, wsAIds, _) = await SeedSpawnAsync(factory, storyA, wsCount: 1, baseBranch: sharedBase);
        await SeedSpawnAsync(factory, storyB, wsCount: 1, baseBranch: sharedBase);

        var resp = await DeleteAsync(client, storyA, deleteBaseBranch: true);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // Spawn A should still exist (refused before any destructive
        // work — the operator can re-call without the flag).
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await verifyDb.StorySpawns.AnyAsync(s => s.Id == spawnAId));
        foreach (var wsId in wsAIds)
        {
            Assert.True(await verifyDb.Workstreams.AnyAsync(w => w.Id == wsId));
        }

        // Re-call without the flag → spawn deleted, base branch kept.
        var resp2 = await DeleteAsync(client, storyA, deleteBaseBranch: false);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Idempotency
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Second_delete_returns_404_after_first_succeeded()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50040";
        await SeedSpawnAsync(factory, storyId, wsCount: 1);

        var first = await DeleteAsync(client, storyId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await DeleteAsync(client, storyId);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Delete_retries_on_transient_sqlite_write_lock()
    {
        var (factory, client) = NewFactory();
        using var _f = factory;
        var storyId = "WI-50041";
        await SeedSpawnAsync(factory, storyId, wsCount: 1);

        await using var lockConn = new SqliteConnection($"Data Source={factory.DatabasePath};Mode=ReadWrite");
        await lockConn.OpenAsync();
        await using (var beginCmd = lockConn.CreateCommand())
        {
            beginCmd.CommandText = "BEGIN IMMEDIATE;";
            await beginCmd.ExecuteNonQueryAsync();
        }

        // Hold a writer lock long enough for DeleteSpawn's first SaveChanges
        // attempt to collide, then release so the bounded retry can succeed.
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(1200);
            await using var commitCmd = lockConn.CreateCommand();
            commitCmd.CommandText = "COMMIT;";
            await commitCmd.ExecuteNonQueryAsync();
        });

        var resp = await DeleteAsync(client, storyId, deleteBaseBranch: true);
        await releaseTask;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.False(await verifyDb.StorySpawns.AnyAsync(s => s.StoryId == storyId));
    }
}
