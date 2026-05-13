// -----------------------------------------------------------------------
// <copyright file="Phase59ReviewFixSkipMergedPrTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 59 (2026-05-13) — regressions for the "stuck review-fix loop on
/// merged PR with no worktree" bug. Pre-fix, when a workstream's PR went
/// terminal (merged/closed) AND its per-stage worktree was wiped by
/// Phase 49 follow-up's <see cref="IStoryGitOps.RemoveWorktreeAsync"/>,
/// any reviewer comment still in <c>State == "active"</c> burned ~$0.10
/// per orchestrator tick on a doomed engineer invocation:
/// <list type="number">
///   <item><see cref="IStoryArtifactStrategy.ProduceReviewFixAsync"/>
///     succeeded (engineer agent wrote reply.md to scratch dir).</item>
///   <item><see cref="IStoryGitOps.CommitFilesAsync"/> threw — branch
///     worktree had been wiped from disk.</item>
///   <item>The catch in <c>RunReviewFixLoopAsync</c> swallowed the throw
///     and <see cref="IStoryProviderOps.ResolvePrCommentAsync"/> never
///     ran, so the comment stayed <c>active</c> and the next tick
///     repeated the same failing call indefinitely.</item>
/// </list>
/// <para>Phase 59 installs a three-layer guard:</para>
/// <list type="number">
///   <item><b>Orchestrator gate</b> — <c>DriveReviewAsync</c> probes
///     <c>pr.Col</c> and <see cref="IStoryGitOps.WorktreeExistsAsync"/>
///     BEFORE invoking <c>RunReviewFixLoopAsync</c>; when either trips,
///     auto-resolves all active comments with a system reply.</item>
///   <item><b>Loop defense</b> — <c>RunReviewFixLoopAsync</c> re-probes
///     so admin endpoints / future direct callers also get the
///     protection.</item>
///   <item><b>Strategy defense</b> — <c>ProduceReviewFixAsync</c>
///     short-circuits to <see cref="ReviewFix"/> reply-only without
///     spinning up a workspace or invoking the engineer agent.</item>
/// </list>
/// <para>System-resolved comments are tagged with
/// <c>ResolvedByUserId = "system:post-merge-cleanup"</c> so tests can
/// distinguish "Phase 59 gate fired" from "engineer fix loop resolved
/// it" (engineer would set <c>ResolvedByUserId</c> to the agent id).</para>
/// </summary>
[Collection("Phase28")]
public class Phase59ReviewFixSkipMergedPrTests
{
    private const string SystemResolverId = "system:post-merge-cleanup";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    private static (ForgeWebApplicationFactory factory, HttpClient client) NewFactory()
    {
        var factory = new ForgeWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);
        return (factory, client);
    }

    private static async Task SeedStoryAsync(ForgeWebApplicationFactory factory, string id, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (await db.WorkItems.AnyAsync(w => w.Id == id)) return;
        db.WorkItems.Add(new WorkItemRow
        {
            Id = id,
            Provider = "ado",
            Track = "dev",
            Type = "feature",
            State = "active",
            Title = title,
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> SpawnAsync(HttpClient c, string storyId)
        => await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn", new { repoId = (string?)null }, JsonOpts);

    private static async Task SetPlanPrAsync(ForgeWebApplicationFactory f, string storyId, string prId, string col)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var s = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        s.PlanPrId = prId;
        s.PlanBranch = $"story/{storyId}/plan";
        s.PlanDocPrsJson = null;
        if (await db.Prs.AnyAsync(p => p.Id == prId))
        {
            (await db.Prs.FirstAsync(p => p.Id == prId)).Col = col;
        }
        else
        {
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = col,
                Branch = $"story/{storyId}/plan",
                Base = "main",
                Title = $"Plan for story {storyId}",
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedAllPlanDocsPassedAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var spawn = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        var docs = new (string FileName, int Iter)[]
        {
            ("tech-spec.md", 3),
            ("architecture.md", 4),
            ("implementation-plan.md", 5),
            ("e2e-scenarios.md", 4),
        };
        foreach (var (fileName, iter) in docs)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = $"plan-doc:{spawn.Id}:{fileName}",
                CrewId = "crew-arch",
                GeneratorAgentId = "G",
                EvaluatorAgentId = "E",
                Iter = iter,
                Score = 92,
                DimsJson = "{}",
                Verdict = "pass",
                Notes = string.Empty,
                GeneratorSummary = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Drive the spawn through plan → decompose → implement → review with a
    /// single workstream pre-seeded into <paramref name="initialStatus"/>
    /// (default "review"), a PR row in <paramref name="prCol"/>, and an
    /// active reviewer comment on that PR. Mirrors
    /// <c>Phase55ActiveCommentsGuardTests.SeedReviewWorkstreamAsync</c>.
    /// </summary>
    private static async Task<(string wsId, string prId, long commentId)> SeedReviewWorkstreamAsync(
        ForgeWebApplicationFactory f, string storyId, string initialStatus, string prCol)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        await SpawnAsync(c, storyId);
        await SetPlanPrAsync(f, storyId, $"pr-plan-{storyId}", "merged");
        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
        await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn/decompose",
            new { workstreams = new[] { new { title = "ws1" } } }, JsonOpts);
        await c.PostAsync($"/api/stories/{storyId}/spawn/decompose/approve", null);
        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

        await SeedAllPlanDocsPassedAsync(f, storyId);

        var safeId = storyId.ToLowerInvariant();
        var wsId = $"ws-{safeId}-ws1";
        var prId = $"pr-ws1-{storyId}";

        long commentId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var ws = await db.Workstreams.FirstAsync(w => w.Id == wsId);
            ws.Status = initialStatus;
            ws.PrsJson = JsonSerializer.Serialize(new[] { prId });
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = prCol,
                IsActive = !string.Equals(prCol, "merged", StringComparison.Ordinal)
                    && !string.Equals(prCol, "closed", StringComparison.Ordinal),
                Branch = ws.Branch,
                Base = "main",
                Title = $"[impl] {ws.Name}",
                Workstream = wsId,
            });

            var row = new PrCommentRow
            {
                PrId = prId,
                ProviderCommentId = $"upstream-{Guid.NewGuid():N}",
                ThreadId = $"thread-{Guid.NewGuid():N}",
                AuthorId = "human-reviewer",
                AuthorKind = "user",
                Body = "Needs another pass.",
                FilePath = "src/Example.cs",
                Line = 42,
                State = "active",
                RequiresAttention = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.PrComments.Add(row);
            await db.SaveChangesAsync();
            commentId = row.Id;
        }

        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));

        return (wsId, prId, commentId);
    }

    /// <summary>
    /// Wipe the workstream's in-memory worktree so
    /// <see cref="IStoryGitOps.WorktreeExistsAsync"/> returns false for
    /// the branch on the next tick. Models the Phase 49 follow-up's
    /// post-merge cleanup that the orchestrator runs after a workstream
    /// PR merges.
    /// </summary>
    private static async Task WipeWorktreeAsync(ForgeWebApplicationFactory f, string wsId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
        var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == wsId);
        await git.RemoveWorktreeAsync(repo: null, branch: ws.Branch);
    }

    // -----------------------------------------------------------------------
    // 1) DriveReviewAsync gate — merged PR
    // -----------------------------------------------------------------------

    /// <summary>
    /// Merged PR + active comment → orchestrator auto-resolves the comment
    /// with a system reply and does NOT invoke the engineer. The "system"
    /// signature is the <c>ResolvedByUserId == "system:post-merge-cleanup"</c>
    /// stamp, which the engineer fix loop would not produce.
    /// </summary>
    [Fact]
    public async Task DriveReview_Resolves_With_System_When_Pr_Is_Merged()
    {
        var (f, _) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-59001", "phase-59-merged-pr");
            var (wsId, prId, commentId) = await SeedReviewWorkstreamAsync(f, "WI-59001",
                initialStatus: "review", prCol: "merged");
            await WipeWorktreeAsync(f, wsId);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-59001");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var c2 = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
                Assert.Equal("resolved", c2.State);
                Assert.Equal(SystemResolverId, c2.ResolvedByUserId);

                // The system reply comment is also persisted on the same PR
                // thread so the operator sees an explicit response.
                var replies = await db.PrComments.AsNoTracking()
                    .Where(pc => pc.PrId == prId && pc.ParentCommentId == commentId)
                    .ToListAsync();
                Assert.Single(replies);
                Assert.Equal(SystemResolverId, replies[0].AuthorId);
                Assert.Contains("merged", replies[0].Body, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Closed PR + active comment → same auto-resolve flow as merged.
    /// Closed PRs share the "no further fix possible" semantics — the
    /// upstream branch is gone or rejected; no commit can land.
    /// </summary>
    [Fact]
    public async Task DriveReview_Resolves_With_System_When_Pr_Is_Closed()
    {
        var (f, _) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-59002", "phase-59-closed-pr");
            var (wsId, _, commentId) = await SeedReviewWorkstreamAsync(f, "WI-59002",
                initialStatus: "review", prCol: "closed");
            await WipeWorktreeAsync(f, wsId);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-59002");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var c2 = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
                Assert.Equal("resolved", c2.State);
                Assert.Equal(SystemResolverId, c2.ResolvedByUserId);
            }
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Worktree missing alone (PR still open) → gate fires on the worktree
    /// check and auto-resolves the comment. Models the rare case where
    /// the worktree directory was wiped by an out-of-band cleanup (manual
    /// <c>git worktree remove</c>, disk corruption) while the PR row was
    /// still open. Better to system-resolve than burn tokens looping.
    /// </summary>
    [Fact]
    public async Task DriveReview_Resolves_With_System_When_Worktree_Is_Missing()
    {
        var (f, _) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-59003", "phase-59-missing-worktree");
            var (wsId, _, commentId) = await SeedReviewWorkstreamAsync(f, "WI-59003",
                initialStatus: "review", prCol: "open");
            await WipeWorktreeAsync(f, wsId);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-59003");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var c2 = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
                Assert.Equal("resolved", c2.State);
                Assert.Equal(SystemResolverId, c2.ResolvedByUserId);
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 2) ProduceReviewFixAsync — deepest defense
    // -----------------------------------------------------------------------

    /// <summary>
    /// Direct call to <see cref="CrewStoryArtifactStrategy.ProduceReviewFixAsync"/>
    /// with a workstream whose PR row is merged must short-circuit BEFORE
    /// invoking the engineer agent. Tests the deepest defense layer:
    /// even if the orchestrator's gate is somehow bypassed (admin
    /// endpoint, future direct caller, tests), the strategy itself
    /// refuses to burn LLM tokens on a sealed PR.
    /// </summary>
    [Fact]
    public async Task ProduceReviewFixAsync_Returns_Reply_Only_When_Pr_Is_Merged()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            const string prId = "pr-phase59-direct-merged";
            const string wsId = "ws-phase59-direct-merged";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.Prs.Add(new PrRow
                {
                    Id = prId,
                    Col = "merged",
                    IsActive = false,
                    Branch = "story/X/ws1",
                    Base = "main",
                    Title = "x",
                });
                await db.SaveChangesAsync();
            }

            var spawn = new StorySpawnRow { Id = "spawn-stub", StoryId = "WI-59-direct", StoryPoints = 5 };
            var workstream = new WorkstreamRow
            {
                Id = wsId,
                StorySpawnId = spawn.Id,
                Name = "ws1",
                Status = "review",
                Branch = "story/X/ws1",
                PrsJson = JsonSerializer.Serialize(new[] { prId }),
                AgentsJson = "[]",
            };
            var comment = new ReviewCommentContext(
                FilePath: "src/Example.cs", Line: 42,
                Body: "needs another pass", AuthoringAgentId: "human-reviewer");

            var strategy = ActivatorUtilities.CreateInstance<CrewStoryArtifactStrategy>(f.Services);
            var result = await strategy.ProduceReviewFixAsync(spawn, workstream, comment);

            // Reply-only: no file, no content, only a system message.
            Assert.True(result.IsReplyOnly, "expected reply-only ReviewFix for merged PR");
            Assert.Null(result.FilePath);
            Assert.Null(result.NewContent);
            Assert.Contains("System:", result.ReplyBody, StringComparison.Ordinal);
            Assert.Contains("merged", result.ReplyBody, StringComparison.OrdinalIgnoreCase);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Direct call with a closed-PR workstream → same reply-only result
    /// path as merged. Closed and merged share the terminal contract.
    /// </summary>
    [Fact]
    public async Task ProduceReviewFixAsync_Returns_Reply_Only_When_Pr_Is_Closed()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            const string prId = "pr-phase59-direct-closed";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.Prs.Add(new PrRow
                {
                    Id = prId,
                    Col = "closed",
                    IsActive = false,
                    Branch = "story/X/ws1",
                    Base = "main",
                    Title = "x",
                });
                await db.SaveChangesAsync();
            }

            var spawn = new StorySpawnRow { Id = "spawn-stub", StoryId = "WI-59-direct-c", StoryPoints = 5 };
            var workstream = new WorkstreamRow
            {
                Id = "ws-phase59-direct-closed",
                StorySpawnId = spawn.Id,
                Name = "ws1",
                Status = "review",
                Branch = "story/X/ws1",
                PrsJson = JsonSerializer.Serialize(new[] { prId }),
                AgentsJson = "[]",
            };
            var comment = new ReviewCommentContext(
                FilePath: "src/Example.cs", Line: 42,
                Body: "needs another pass", AuthoringAgentId: "human-reviewer");

            var strategy = ActivatorUtilities.CreateInstance<CrewStoryArtifactStrategy>(f.Services);
            var result = await strategy.ProduceReviewFixAsync(spawn, workstream, comment);

            Assert.True(result.IsReplyOnly);
            Assert.Contains("closed", result.ReplyBody, StringComparison.OrdinalIgnoreCase);
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 3) Happy path — open PR with live worktree still runs the engineer
    // -----------------------------------------------------------------------

    /// <summary>
    /// Regression guard — when the PR is open and the worktree is live,
    /// the engineer fix loop must still run normally. Pins that Phase 59
    /// didn't break the happy path; the engineer-resolved comment has
    /// <see cref="PrCommentRow.ResolvedByUserId"/> set to the engineer
    /// agent id, NOT <c>"system:post-merge-cleanup"</c>.
    /// </summary>
    [Fact]
    public async Task DriveReview_Engineer_Resolves_Comments_When_Pr_Open_And_Worktree_Live()
    {
        var (f, _) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-59010", "phase-59-happy-path");
            var (wsId, _, commentId) = await SeedReviewWorkstreamAsync(f, "WI-59010",
                initialStatus: "review", prCol: "open");

            // Ensure the in-memory worktree is registered for the branch so
            // WorktreeExistsAsync returns true (the gate must NOT fire).
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == wsId);
                await git.EnsureBranchAsync(repo: null, branch: ws.Branch, baseBranch: "main");
                var exists = await git.WorktreeExistsAsync(repo: null, branch: ws.Branch);
                Assert.True(exists, "in-memory worktree should be registered for happy-path test");
            }

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-59010");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var c2 = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
                Assert.Equal("resolved", c2.State);
                // Engineer resolved, not the Phase 59 system gate.
                Assert.NotEqual(SystemResolverId, c2.ResolvedByUserId);
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 4) WorktreeExistsAsync — in-memory probe contract
    // -----------------------------------------------------------------------

    /// <summary>
    /// Direct unit-level test of <see cref="IStoryGitOps.WorktreeExistsAsync"/>
    /// on <see cref="InMemoryStoryGitOps"/>: tracks the
    /// EnsureBranch → RemoveWorktree → re-probe cycle so tests modelling
    /// the post-merge cleanup sequence can rely on the probe contract.
    /// </summary>
    [Fact]
    public async Task WorktreeExistsAsync_Tracks_Ensure_Then_Remove_Cycle()
    {
        var git = new InMemoryStoryGitOps();
        const string branch = "story/X/ws1";

        // Initially no branch → no worktree.
        Assert.False(await git.WorktreeExistsAsync(repo: null, branch: branch));

        // EnsureBranch creates the branch tree → worktree now "exists".
        await git.EnsureBranchAsync(repo: null, branch: branch, baseBranch: "main");
        Assert.True(await git.WorktreeExistsAsync(repo: null, branch: branch));

        // RemoveWorktree records the wipe → probe returns false even
        // though the branch tree row is still in the dictionary.
        await git.RemoveWorktreeAsync(repo: null, branch: branch);
        Assert.False(await git.WorktreeExistsAsync(repo: null, branch: branch));
    }
}
