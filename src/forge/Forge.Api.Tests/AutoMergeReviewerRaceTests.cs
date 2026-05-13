// -----------------------------------------------------------------------
// <copyright file="AutoMergeReviewerRaceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 57b — regression tests for the AutoMergeWatcher / DriveReviewAsync
/// reviewer race. Observed 2026-05-13 on failover-cluster:XRAFT PR #9:
/// AutoMergeWatcher's 15 s tick beat the orchestrator's slower reviewer
/// dispatch, merging the PR before the reviewer agent had a chance to
/// post comments. The 3 review comments then landed on the merged PR
/// with no fix path (workstream had advanced to "complete" and the
/// worktree was already removed).
///
/// Fix A: AutoMergeWatcher refuses to merge a workstream-scoped PR whose
/// Review verdict is null. Non-workstream PRs (operator-created with
/// AutoMerge=true) keep the permissive null-verdict semantics.
///
/// Fix B (belt-and-suspenders): DriveReviewAsync stamps Review.verdict =
/// "pending" on first sight of an unreviewed workstream PR, so the existing
/// "if (pr.Review?.Verdict is "changes" or "pending") continue;" guard in
/// AutoMergeWatcher catches the race even before Fix A's workstream check.
/// </summary>
public class AutoMergeReviewerRaceTests
{
    private const string WorkstreamPrId = "PR smartpcr/forge-amrr#1";
    private const string OperatorPrId = "PR smartpcr/forge-amrr#2";
    private const string WorkstreamId = "ws-amrr-test";

    [Fact]
    public async Task Workstream_Pr_With_Null_Review_Is_Not_Merged_By_Watcher()
    {
        await using var factory = new RecordingProviderFactory();
        using (factory.CreateClient()) { /* trigger boot + migrations */ }

        await SeedWorkstreamWithPrAsync(factory, WorkstreamPrId, reviewVerdict: null);

        using var scope = factory.Services.CreateScope();
        var watcher = scope.ServiceProvider.GetRequiredService<AutoMergeWatcher>();
        await watcher.RunOnceAsync(default);

        Assert.False(factory.Provider.MergeCalled,
            "Workstream PR with null Review verdict must NOT be merged by AutoMergeWatcher — " +
            "the orchestrator's reviewer hasn't run yet.");

        // PR row stays unmerged.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var pr = await db.Prs.AsNoTracking().FirstAsync(p => p.Id == WorkstreamPrId);
        Assert.NotEqual("merged", pr.Col);
    }

    [Fact]
    public async Task Workstream_Pr_With_Pending_Review_Is_Not_Merged_By_Watcher()
    {
        // Fix B path — DriveReviewAsync stamps "pending"; the existing
        // (pre-Phase-57b) guard already skips "pending". This test pins
        // that contract so a future refactor of either side preserves it.
        await using var factory = new RecordingProviderFactory();
        using (factory.CreateClient()) { }

        await SeedWorkstreamWithPrAsync(factory, WorkstreamPrId, reviewVerdict: "pending");

        using var scope = factory.Services.CreateScope();
        var watcher = scope.ServiceProvider.GetRequiredService<AutoMergeWatcher>();
        await watcher.RunOnceAsync(default);

        Assert.False(factory.Provider.MergeCalled,
            "Workstream PR with Review.verdict='pending' must NOT be merged.");
    }

    [Fact]
    public async Task Workstream_Pr_With_Approved_Review_Is_Merged_By_Watcher()
    {
        // Happy-path: once the reviewer agent has stamped 'approved' and
        // there are no active comments, the watcher should still merge.
        // Pins the fix doesn't regress the legitimate merge path.
        await using var factory = new RecordingProviderFactory();
        using (factory.CreateClient()) { }

        await SeedWorkstreamWithPrAsync(factory, WorkstreamPrId, reviewVerdict: "approved");

        using var scope = factory.Services.CreateScope();
        var watcher = scope.ServiceProvider.GetRequiredService<AutoMergeWatcher>();
        await watcher.RunOnceAsync(default);

        Assert.True(factory.Provider.MergeCalled,
            "Workstream PR with Review.verdict='approved' and no active comments must merge.");
        Assert.Equal(WorkstreamPrId, factory.Provider.LastMergedPrId);
    }

    [Fact]
    public async Task NonWorkstream_Pr_With_Null_Review_Still_Merges()
    {
        // Operator-created auto-merge PRs (no workstream association) keep
        // the historical null-verdict-OK semantics. The fix only narrows
        // behavior for spawn-owned workstream PRs.
        await using var factory = new RecordingProviderFactory();
        using (factory.CreateClient()) { }

        await SeedOperatorPrAsync(factory, OperatorPrId, reviewVerdict: null);

        using var scope = factory.Services.CreateScope();
        var watcher = scope.ServiceProvider.GetRequiredService<AutoMergeWatcher>();
        await watcher.RunOnceAsync(default);

        Assert.True(factory.Provider.MergeCalled,
            "Non-workstream PR (no row in workstreams.PrsJson) with null Review and AutoMerge=true must still merge.");
        Assert.Equal(OperatorPrId, factory.Provider.LastMergedPrId);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task SeedWorkstreamWithPrAsync(
        RecordingProviderFactory factory, string prId, string? reviewVerdict)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        db.Prs.Add(BuildAutoMergeReadyPr(prId, reviewVerdict));
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = WorkstreamId,
            Name = "fixture",
            Epic = "EPIC-1",
            Branch = "ws/test",
            Base = "main",
            Status = "review",
            PrsJson = JsonSerializer.Serialize(new[] { prId }),
            AgentsJson = "[]",
            CardsJson = "[]",
            DepsJson = "[]",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedOperatorPrAsync(
        RecordingProviderFactory factory, string prId, string? reviewVerdict)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        db.Prs.Add(BuildAutoMergeReadyPr(prId, reviewVerdict));
        await db.SaveChangesAsync();
    }

    private static PrRow BuildAutoMergeReadyPr(string prId, string? reviewVerdict)
    {
        // Build = passing, HumanGate = false, AutoMerge = true. The only
        // axis under test is Review.verdict.
        var reviewJson = reviewVerdict is null
            ? null
            : JsonSerializer.Serialize(new { verdict = reviewVerdict });
        return new PrRow
        {
            Id = prId,
            Col = "review",
            Epic = "EPIC-1",
            EpicColor = "blue",
            Workstream = "ws/test",
            Base = "main",
            Branch = "ws/test",
            Title = "auto-merge fixture",
            Additions = 1,
            Deletions = 0,
            Files = 1,
            Commits = 1,
            Age = "1m",
            AutoMerge = true,
            HumanGate = false,
            Conflicts = false,
            BuildJson = JsonSerializer.Serialize(new { status = "passing", step = "CI green", percent = 100 }),
            ReviewJson = reviewJson,
            Provider = "github",
            IsActive = true,
        };
    }

    /// <summary>
    /// Custom factory that swaps the real Ado/GitHub providers for a
    /// recording fake. <c>MergePullRequestAsync</c> records the call and
    /// returns a synthetic merged Pr so callers can complete the merge
    /// path without exception.
    /// </summary>
    private sealed class RecordingProviderFactory : ForgeWebApplicationFactory
    {
        public RecordingProvider Provider { get; } = new();

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

    /// <summary>
    /// Implements <see cref="IForgeProvider"/> directly (rather than
    /// inheriting from <see cref="NullForgeProvider"/> with <c>new</c>) —
    /// interface dispatch through <c>IEnumerable&lt;IForgeProvider&gt;</c>
    /// would otherwise hit the base method, not the recording override.
    /// Reports Kind="github" and IsConfigured=true so AutoMergeWatcher's
    /// provider lookup picks us instead of bailing with
    /// reason=no-configured-provider.
    /// </summary>
    private sealed class RecordingProvider : IForgeProvider
    {
        public string Kind => "github";

        public bool IsConfigured => true;

        public bool MergeCalled { get; private set; }

        public string? LastMergedPrId { get; private set; }

        public Task<Pr> MergePullRequestAsync(
            string id, ProviderCallContext ctx, CancellationToken ct = default)
        {
            this.MergeCalled = true;
            this.LastMergedPrId = id;
            return Task.FromResult(new Pr(
                Id: id, Col: "merged", Epic: "EPIC-1", EpicColor: "blue",
                Workstream: "ws/test", Base: "main", Branch: "ws/test",
                Title: "merged", Author: new Agent("", "", ""),
                Additions: 1, Deletions: 0, Files: 1, Commits: 1, Age: "1m",
                Build: null, Review: null,
                Conflicts: false, HumanGate: false, GateReason: null,
                ConflictPaths: null, AutoMerge: true, Blocked: false));
        }

        // ── benign stubs for everything else ─────────────────────────────

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

        public Task ClosePullRequestAsync(string id, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RequestChangesOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CommentOnPullRequestAsync(string id, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReplyToReviewCommentAsync(string prId, string parentProviderCommentId, string body, ProviderCallContext ctx, CancellationToken ct = default) =>
            Task.CompletedTask;

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
}
