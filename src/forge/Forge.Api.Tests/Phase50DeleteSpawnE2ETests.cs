// -----------------------------------------------------------------------
// <copyright file="Phase50DeleteSpawnE2ETests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 50 — END-TO-END coverage for <c>DELETE /api/stories/{storyId}/spawn</c>.
/// Companion to <see cref="Phase50StorySpawnDeleteTests"/>; that suite
/// pins the DB-row contract using <see cref="InMemoryStoryGitOps"/> and a
/// fully mocked <see cref="IForgeProvider"/>. This suite proves the parts
/// the in-memory mocks let pass silently:
/// <list type="bullet">
///   <item>real on-disk worktree directories under
///         <c>{cloneRoot}/.worktree/</c> are gone after delete
///         (per-workstream + plan + qa);</item>
///   <item>local branches (<c>git branch --list</c>) for plan / qa / each
///         workstream are gone;</item>
///   <item>remote branches (<c>git ls-remote --heads origin</c>) for plan
///         / qa / each workstream <i>and</i> the non-main base branch are
///         gone — proving <c>git push origin --delete</c> actually ran
///         and not just that the endpoint set
///         <c>baseBranchDeleted=true</c>;</item>
///   <item>on-disk <c>storyworkspaces/{spawnId}/</c> and
///         <c>attachments/{spawnId}/</c> directories are gone;</item>
///   <item>per-workstream deliverables, transcripts, queue rows, agent
///         claims AND the plan-doc synthetic ids
///         (<c>plan-doc:{spawn}:{file}</c>) are gone — these are the
///         "iterations" the user observed surviving prior deletes.</item>
/// </list>
///
/// <para>
/// Each test sets up its own bare remote + working clone at <c>%TEMP%</c>,
/// materialises real git worktrees for plan / qa / each workstream
/// (<c>git worktree add</c>), pushes branches to origin, then calls the
/// DELETE endpoint and asserts the post-state. The factory swaps
/// <see cref="InMemoryStoryGitOps"/> for the production
/// <see cref="RealStoryGitOps"/> so <c>storyGitOps.RemoveWorktreeAsync</c>
/// shells out to <c>git</c> for real. <see cref="IForgeProvider"/> is
/// stubbed with a <see cref="RecordingProvider"/> so the test can assert
/// the "open PRs closed upstream / merged PRs not poked" contract without
/// reaching a real provider.
/// </para>
/// </summary>
[Collection("Phase50E2E")]
public sealed class Phase50DeleteSpawnE2ETests : IAsyncLifetime
{
    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";
    private const string BaseBranch = "release/2026.x";

    private readonly string remoteDir = Path.Combine(Path.GetTempPath(), $"forge-delete-e2e-remote-{Guid.NewGuid():N}");
    private readonly string cloneDir = Path.Combine(Path.GetTempPath(), $"forge-delete-e2e-clone-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        if (!GitAvailable()) return;

        // Bare remote so push --delete actually has an effect we can observe.
        Directory.CreateDirectory(this.remoteDir);
        await Git(this.remoteDir, "init", "--bare", "-b", "main");

        // Seed clone: main with an initial commit, then a non-main base branch
        // (release/2026.x) branched off main and also pushed to origin. The
        // non-main base branch is the only thing that lets the test prove
        // `deleteBaseBranch=true` actually deletes a remote ref (deleting
        // origin/main is special-cased away).
        var seedDir = Path.Combine(Path.GetTempPath(), $"forge-delete-e2e-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(seedDir);
        await Git(seedDir, "init", "-b", "main");
        await Git(seedDir, "config", "user.email", "test@forge.local");
        await Git(seedDir, "config", "user.name", "Forge Test");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "README.md"), "seed\n");
        await Git(seedDir, "add", "README.md");
        await Git(seedDir, "commit", "-m", "seed");
        await Git(seedDir, "remote", "add", "origin", this.remoteDir);
        await Git(seedDir, "push", "-u", "origin", "main");

        // Branch base off main, add a file so the tip differs, push.
        await Git(seedDir, "checkout", "-b", BaseBranch);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "base.txt"), "base branch tip\n");
        await Git(seedDir, "add", "base.txt");
        await Git(seedDir, "commit", "-m", "base branch tip");
        await Git(seedDir, "push", "-u", "origin", BaseBranch);

        try { Directory.Delete(seedDir, recursive: true); } catch { /* best-effort */ }

        // Working clone (the "cloneRoot" the orchestrator drives). Configure
        // the worktree base path under .worktree/ via convention; the helpers
        // create the directory on first worktree-add.
        await Git(Path.GetTempPath(), "clone", this.remoteDir, this.cloneDir);
        await Git(this.cloneDir, "config", "user.email", "test@forge.local");
        await Git(this.cloneDir, "config", "user.name", "Forge Test");
        // Need both main and base fetched locally so worktree-add can branch
        // off them without a follow-up fetch.
        await Git(this.cloneDir, "fetch", "origin", BaseBranch);
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        try { Directory.Delete(this.remoteDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(this.cloneDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // ===================================================================
    // Test A — every iteration row and work item row is removed.
    // ===================================================================

    /// <summary>
    /// One DELETE call wipes EVERY iteration (DeliverableRow), transcript,
    /// queue row, agent claim, work item, and card row for the spawn —
    /// including the synthetic plan-doc ids
    /// (<c>plan-doc:{spawnId}:{file}</c>) that the per-workstream cascade
    /// does NOT touch. Maps to user requirement #1: "clear all iterations,
    /// all work items".
    /// </summary>
    [Fact]
    public async Task Delete_clears_all_iterations_and_work_items()
    {
        if (!GitAvailable()) return;

        using var f = new E2EFactory(this.cloneDir);
        var client = NewWriterClient(f);

        var storyId = "WI-99001";
        var seed = await this.SeedSpawnAsync(f, storyId);

        var resp = await client.DeleteAsync($"/api/stories/{storyId}/spawn?deleteBaseBranch=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Spawn row + every child workstream row gone.
        Assert.False(await db.StorySpawns.AnyAsync(s => s.Id == seed.SpawnId));
        Assert.False(await db.Workstreams.AnyAsync(w => w.StorySpawnId == seed.SpawnId));
        foreach (var wsId in seed.WorkstreamIds)
        {
            Assert.False(await db.Workstreams.AnyAsync(w => w.Id == wsId), $"Workstream {wsId} survived.");
        }

        // Every WorkItemId we touched (per-ws + plan-doc synthetic +
        // spawn-id) is now free of execution surface rows.
        foreach (var wid in seed.AllWorkItemIds)
        {
            Assert.False(
                await db.Deliverables.AnyAsync(d => d.WorkItemId == wid),
                $"Deliverable rows for {wid} survived — iterations not cleared.");
            Assert.False(
                await db.TaskTranscriptEvents.AnyAsync(t => t.WorkItemId == wid),
                $"Transcript rows for {wid} survived.");
            Assert.False(
                await db.TaskQueue.AnyAsync(q => q.WorkItemId == wid),
                $"Queue rows for {wid} survived.");

            // The DeleteSpawn endpoint contract: AgentClaim rows for the
            // workstream id get the row removed by the per-workstream
            // cascade, while synthetic-id claims have ReleasedAt set.
            // Either way the claim is "no longer active" (released != null
            // OR the row is gone).
            var claims = await db.AgentClaims.Where(c => c.WorkItemId == wid).ToListAsync();
            foreach (var c in claims)
            {
                Assert.NotNull(c.ReleasedAt);
            }

            Assert.False(
                await db.WorkItems.AnyAsync(w => w.Id == wid),
                $"WorkItem {wid} survived.");
            Assert.False(
                await db.Cards.AnyAsync(c => c.Id == wid),
                $"Card {wid} survived.");
        }
    }

    // ===================================================================
    // Test B — worktree folders (plan, qa, each workstream) are removed
    // from disk, local branches deleted, and the on-disk
    // storyworkspaces/attachments dirs gone.
    // ===================================================================

    /// <summary>
    /// Maps to user requirement #2: "clear all worktree folders". Asserts
    /// the on-disk paths the orchestrator materialised (plan, qa, each
    /// workstream) no longer exist after DELETE, that <c>git worktree list</c>
    /// only shows the main checkout, and that the storyworkspaces /
    /// attachments dirs (which carry per-iteration agent state) are gone.
    /// </summary>
    [Fact]
    public async Task Delete_clears_all_worktree_folders()
    {
        if (!GitAvailable()) return;

        using var f = new E2EFactory(this.cloneDir);
        var client = NewWriterClient(f);

        var storyId = "WI-99002";
        var seed = await this.SeedSpawnAsync(f, storyId);

        // Sanity: all worktree dirs we materialised exist BEFORE delete so
        // the post-state assertion is meaningful.
        foreach (var path in seed.AllWorktreePaths)
        {
            Assert.True(Directory.Exists(path), $"Pre-delete invariant failed: {path} should exist.");
        }
        Assert.True(Directory.Exists(seed.StoryWorkspacesDir), "Pre-delete: storyworkspaces dir should exist.");
        Assert.True(Directory.Exists(seed.AttachmentsDir), "Pre-delete: attachments dir should exist.");

        var resp = await client.DeleteAsync($"/api/stories/{storyId}/spawn?deleteBaseBranch=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 1) Each materialised worktree directory is gone.
        foreach (var path in seed.AllWorktreePaths)
        {
            Assert.False(Directory.Exists(path), $"Worktree dir {path} survived delete.");
        }

        // 2) `git worktree list` shows only the main clone — no orphan
        //    registrations from the plan / qa / workstream worktrees we
        //    removed (those would block `git worktree add` on a re-spawn).
        var (wlCode, wlOut, _) = await CaptureGit(this.cloneDir, "worktree", "list", "--porcelain");
        Assert.Equal(0, wlCode);
        var worktreeLines = wlOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Each registered worktree begins with a `worktree {path}` line.
        var worktreePaths = worktreeLines
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => l.Substring("worktree ".Length).Trim())
            .ToList();
        Assert.Single(worktreePaths);
        Assert.Equal(NormalisePath(this.cloneDir), NormalisePath(worktreePaths[0]));

        // 3) Local branches for plan / qa / each workstream are gone.
        var (brCode, brOut, _) = await CaptureGit(this.cloneDir, "branch", "--list");
        Assert.Equal(0, brCode);
        var localBranches = brOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimStart('*', ' ').Trim())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var branch in seed.AllBranches)
        {
            Assert.DoesNotContain(branch, localBranches);
        }

        // 4) storyworkspaces/{spawnId}/ and attachments/{spawnId}/ wiped.
        Assert.False(Directory.Exists(seed.StoryWorkspacesDir),
            $"storyworkspaces dir {seed.StoryWorkspacesDir} survived delete.");
        Assert.False(Directory.Exists(seed.AttachmentsDir),
            $"attachments dir {seed.AttachmentsDir} survived delete.");
    }

    // ===================================================================
    // Test C — every PR + the non-main base branch are deleted on the
    // remote, the response payload reports baseBranchDeleted=true, and
    // RecordingProvider observed close calls for the pending PRs only.
    // ===================================================================

    /// <summary>
    /// Maps to user requirement #3: "clear/delete remote base branch and
    /// PRs". Asserts <c>git ls-remote --heads</c> against the bare remote
    /// no longer reports the spawn's branches or the non-main base branch,
    /// every PrRow in the DB is gone, and the test's
    /// <see cref="RecordingProvider"/> received close calls for the open
    /// PRs (merged PRs are deliberately NOT poked upstream).
    /// </summary>
    [Fact]
    public async Task Delete_clears_remote_base_branch_and_PRs()
    {
        if (!GitAvailable()) return;

        using var f = new E2EFactory(this.cloneDir);
        var client = NewWriterClient(f);

        var storyId = "WI-99003";
        var seed = await this.SeedSpawnAsync(f, storyId);

        // Pre-delete invariants: each branch is on origin.
        foreach (var branch in seed.AllBranches.Append(BaseBranch))
        {
            var (code, output, _) = await CaptureGit(this.cloneDir, "ls-remote", "--heads", "origin", branch);
            Assert.Equal(0, code);
            Assert.False(string.IsNullOrWhiteSpace(output),
                $"Pre-delete invariant: branch {branch} should exist on origin.");
        }

        var resp = await client.DeleteAsync($"/api/stories/{storyId}/spawn?deleteBaseBranch=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("baseBranchDeleted").GetBoolean(),
            $"baseBranchDeleted should be true. response: {body}");
        // remoteDeleteFailures must be present and empty against a bare
        // remote with no protection rules.
        var failures = body.GetProperty("remoteDeleteFailures");
        Assert.Equal(JsonValueKind.Array, failures.ValueKind);
        Assert.Equal(0, failures.GetArrayLength());

        // 1) No spawn branch survives on origin.
        foreach (var branch in seed.AllBranches)
        {
            var (code, output, _) = await CaptureGit(this.cloneDir, "ls-remote", "--heads", "origin", branch);
            Assert.Equal(0, code);
            Assert.True(string.IsNullOrWhiteSpace(output),
                $"Branch {branch} survives on origin: {output.Trim()}");
        }

        // 2) Base branch survives on origin? It must NOT — we passed
        //    deleteBaseBranch=true and no other spawn shares it.
        var (baseCode, baseOut, _) = await CaptureGit(this.cloneDir, "ls-remote", "--heads", "origin", BaseBranch);
        Assert.Equal(0, baseCode);
        Assert.True(string.IsNullOrWhiteSpace(baseOut),
            $"Base branch {BaseBranch} survives on origin: {baseOut.Trim()}");

        // 3) Every PR row removed from the DB regardless of merged/open state.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        foreach (var prId in seed.PrIds)
        {
            Assert.False(await db.Prs.AnyAsync(p => p.Id == prId), $"PR {prId} survived in DB.");
        }

        // 4) Provider received close calls for OPEN PRs only.
        var openPrIds = seed.OpenPrIds;
        foreach (var prId in openPrIds)
        {
            Assert.Contains(prId, f.Provider.ClosedPrIds);
        }
        foreach (var prId in seed.MergedPrIds)
        {
            Assert.DoesNotContain(prId, f.Provider.ClosedPrIds);
        }
    }

    // ===================================================================
    // Test D — combined happy-path: one DELETE wipes A + B + C.
    // ===================================================================

    /// <summary>
    /// One DELETE call satisfies all three user requirements simultaneously.
    /// Stricter than the per-requirement tests because it asserts the
    /// combined post-state on one fixture — proves no piece of the cleanup
    /// silently leaks rows that another piece happened to also wipe.
    /// </summary>
    [Fact]
    public async Task Delete_happy_path_wipes_db_disk_and_remote_in_one_call()
    {
        if (!GitAvailable()) return;

        using var f = new E2EFactory(this.cloneDir);
        var client = NewWriterClient(f);

        var storyId = "WI-99004";
        var seed = await this.SeedSpawnAsync(f, storyId);

        var resp = await client.DeleteAsync($"/api/stories/{storyId}/spawn?deleteBaseBranch=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // DB
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.False(await db.StorySpawns.AnyAsync(s => s.Id == seed.SpawnId));
        foreach (var wid in seed.AllWorkItemIds)
        {
            Assert.False(await db.Deliverables.AnyAsync(d => d.WorkItemId == wid));
            Assert.False(await db.TaskTranscriptEvents.AnyAsync(t => t.WorkItemId == wid));
            Assert.False(await db.WorkItems.AnyAsync(w => w.Id == wid));
        }
        foreach (var prId in seed.PrIds)
        {
            Assert.False(await db.Prs.AnyAsync(p => p.Id == prId));
            Assert.False(await db.PrComments.AnyAsync(c => c.PrId == prId));
        }

        // Disk
        foreach (var path in seed.AllWorktreePaths)
        {
            Assert.False(Directory.Exists(path));
        }
        Assert.False(Directory.Exists(seed.StoryWorkspacesDir));
        Assert.False(Directory.Exists(seed.AttachmentsDir));

        // Remote
        foreach (var branch in seed.AllBranches.Append(BaseBranch))
        {
            var (_, output, _) = await CaptureGit(this.cloneDir, "ls-remote", "--heads", "origin", branch);
            Assert.True(string.IsNullOrWhiteSpace(output),
                $"Branch {branch} survives on origin after happy-path delete.");
        }
    }

    // ===================================================================
    // Test E — shared base branch: refusal preserves both spawns.
    // ===================================================================

    /// <summary>
    /// When two spawns share a non-main base branch, asking for
    /// <c>deleteBaseBranch=true</c> must refuse before any destructive
    /// work runs. Mirrors the in-memory test in
    /// <see cref="Phase50StorySpawnDeleteTests"/>, but here against real
    /// git so we also prove no branch is actually deleted on origin during
    /// the refused call.
    /// </summary>
    [Fact]
    public async Task Delete_with_shared_base_branch_refuses_and_preserves_remote()
    {
        if (!GitAvailable()) return;

        using var f = new E2EFactory(this.cloneDir);
        var client = NewWriterClient(f);

        // Seed two spawns sharing BaseBranch (the non-main branch the
        // fixture pushed in InitializeAsync).
        var storyA = "WI-99010";
        var storyB = "WI-99011";
        var seedA = await this.SeedSpawnAsync(f, storyA);
        var seedB = await this.SeedSpawnAsync(f, storyB);

        var resp = await client.DeleteAsync($"/api/stories/{storyA}/spawn?deleteBaseBranch=true");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // Both spawn rows survive — the refusal must precede every
        // destructive step.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.True(await db.StorySpawns.AnyAsync(s => s.Id == seedA.SpawnId));
        Assert.True(await db.StorySpawns.AnyAsync(s => s.Id == seedB.SpawnId));

        // Base branch is intact on origin.
        var (baseCode, baseOut, _) = await CaptureGit(this.cloneDir, "ls-remote", "--heads", "origin", BaseBranch);
        Assert.Equal(0, baseCode);
        Assert.False(string.IsNullOrWhiteSpace(baseOut),
            "Base branch must survive a refused delete (refusal precedes destructive work).");

        // No PR row was removed from either spawn.
        foreach (var prId in seedA.PrIds.Concat(seedB.PrIds))
        {
            Assert.True(await db.Prs.AnyAsync(p => p.Id == prId),
                $"PR {prId} should survive a refused delete.");
        }
    }

    // ===================================================================
    // Seed helper — materialises real git state + DB state + on-disk dirs.
    // ===================================================================

    private async Task<SeedResult> SeedSpawnAsync(E2EFactory f, string storyId)
    {
        var safeStoryId = StoryPaths.Safe(storyId);
        var spawnId = $"spawn-{safeStoryId}";
        // RepoRow is keyed per-fixture (not per-storyId) so multiple
        // spawns in the same fixture share the row — the
        // `repos.RemoteUrl` UNIQUE index would otherwise reject the
        // second insert in the shared-base-branch refusal test.
        var repoId = $"repo-e2e-{Path.GetFileName(this.remoteDir)}";
        var planBranch = $"story/{storyId}/plan";
        var qaBranch = $"story/{storyId}/qa";
        var ws1Branch = $"ws/{storyId}/stage-1-foo";
        var ws2Branch = $"ws/{storyId}/stage-2-bar";

        var workstreamBranches = new[] { ws1Branch, ws2Branch };
        var allBranches = new[] { planBranch, qaBranch, ws1Branch, ws2Branch };

        // 1) Materialise a worktree per branch off the non-main base.
        //    Git tracks the (branch -> path) mapping in `worktree list`,
        //    and RealStoryGitOps.RemoveWorktreeAsync queries that directly
        //    to discover the path. The exact directory name doesn't matter
        //    here — the production cleanup is path-agnostic.
        var worktreePaths = new Dictionary<string, string>();
        foreach (var branch in allBranches)
        {
            var sanitised = SanitiseBranchForWorktree(branch);
            var wtPath = Path.Combine(this.cloneDir, ".worktree", sanitised);
            Directory.CreateDirectory(Path.GetDirectoryName(wtPath)!);

            // Create branch from base on origin, then add a worktree
            // tracking it. Push a per-branch file so origin's ref is
            // distinguishable from base.
            await Git(this.cloneDir, "worktree", "add", "-b", branch, wtPath, $"origin/{BaseBranch}");
            await File.WriteAllTextAsync(Path.Combine(wtPath, $"{Path.GetFileName(wtPath)}.txt"), $"seed for {branch}\n");
            await Git(wtPath, "add", "-A");
            await Git(wtPath, "commit", "-m", $"seed {branch}");
            await Git(wtPath, "push", "-u", "origin", branch);

            worktreePaths[branch] = wtPath;
        }

        // 2) Seed DB rows.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        if (!await db.Products.AnyAsync(p => p.Id == "test-product"))
        {
            db.Products.Add(new ProductRow
            {
                Id = "test-product",
                Name = "test-product",
                Slug = "test-product",
                Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        if (!await db.Repos.AnyAsync(r => r.Id == repoId))
        {
            db.Repos.Add(new RepoRow
            {
                Id = repoId,
                ProductId = "test-product",
                Kind = "github",
                // RemoteUrl is unique cross-product; use the per-test bare
                // remote dir as the URL so two parallel tests don't collide
                // on the SQLite uniqueness constraint.
                RemoteUrl = this.remoteDir,
                DisplayName = $"e2e-{storyId}",
                CloneRoot = this.cloneDir,
                DefaultBranch = "main",
            });
        }

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
            StoryDescription = "E2E delete test",
            RepoId = repoId,
            Phase = StorySpawnPhases.Implement,
            BaseBranch = BaseBranch,
            PlanBranch = planBranch,
            PlanPrId = $"pr-plan-{storyId}",
            QaBranch = qaBranch,
            QaPrId = $"pr-qa-{storyId}",
            DecompositionApproved = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Plan PR: merged so DELETE removes the row locally but does NOT
        // call provider.ClosePullRequestAsync (the merged-PR-not-poked
        // contract).
        db.Prs.Add(new PrRow
        {
            Id = $"pr-plan-{storyId}",
            RepoId = repoId,
            Col = "merged",
            Branch = planBranch,
            Base = BaseBranch,
            Title = $"Plan for {storyId}",
            Provider = "github",
            WebUrl = $"https://example.com/test/repo/pull/0",
        });

        // QA PR: open so DELETE calls ClosePullRequestAsync.
        db.Prs.Add(new PrRow
        {
            Id = $"pr-qa-{storyId}",
            RepoId = repoId,
            Col = "review",
            Branch = qaBranch,
            Base = BaseBranch,
            Title = $"QA for {storyId}",
            Provider = "github",
            WebUrl = $"https://example.com/test/repo/pull/99",
        });

        // Plan-doc synthetic rows — the per-workstream cascade does NOT
        // touch these ids. They use the `plan-doc:{spawnId}:{filename}`
        // convention emitted by CrewStoryArtifactStrategy.
        var planDocFiles = new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" };
        var syntheticIds = new List<string> { spawnId };
        foreach (var fn in planDocFiles)
        {
            syntheticIds.Add($"plan-doc:{spawnId}:{fn}");
        }

        foreach (var sid in syntheticIds)
        {
            // Three iterations per synthetic id — covers the "iterations
            // surface for days" complaint in the user-observed bug.
            for (var iter = 1; iter <= 3; iter++)
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = sid,
                    CrewId = "crew-arch",
                    Iter = iter,
                    Score = 80 + iter,
                    Verdict = iter == 3 ? "pass" : "iterate",
                    Notes = $"iter {iter} for {sid}",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
            {
                WorkItemId = sid,
                Kind = "task.token",
                PayloadJson = $"{{\"text\":\"event for {sid}\"}}",
                At = DateTimeOffset.UtcNow,
            });
            db.AgentClaims.Add(new AgentClaimRow
            {
                AgentId = "agent-arch",
                WorkItemId = sid,
                Role = "generator",
                ClaimedAt = DateTimeOffset.UtcNow,
                ReleasedAt = null,
            });
        }

        // Per-workstream rows.
        var workstreamIds = new List<string>();
        var prIds = new List<string> { $"pr-plan-{storyId}", $"pr-qa-{storyId}" };
        var openPrIds = new List<string> { $"pr-qa-{storyId}" };
        var mergedPrIds = new List<string> { $"pr-plan-{storyId}" };

        var i = 0;
        foreach (var branch in workstreamBranches)
        {
            i++;
            var wsId = $"ws-{storyId}-stage-{i}";
            workstreamIds.Add(wsId);
            var implPrId = $"pr-ws-{storyId}-{i}";
            prIds.Add(implPrId);

            // Mix merged + open: ws-1 open ("review"), ws-2 merged.
            var col = i == 1 ? "review" : "merged";
            if (i == 1) openPrIds.Add(implPrId);
            else mergedPrIds.Add(implPrId);

            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                StorySpawnId = spawnId,
                RepoId = repoId,
                Name = $"Stage {i}",
                Branch = branch,
                Base = BaseBranch,
                Epic = storyId,
                Status = "active",
                Summary = $"Stage {i} work",
                PrsJson = $"[\"{implPrId}\"]",
            });

            db.Prs.Add(new PrRow
            {
                Id = implPrId,
                RepoId = repoId,
                Col = col,
                Branch = branch,
                Base = BaseBranch,
                Title = $"Stage {i}",
                Provider = "github",
                WebUrl = $"https://example.com/test/repo/pull/{i}",
                WorkItemId = wsId,
            });

            db.PrComments.Add(new PrCommentRow
            {
                PrId = implPrId,
                AuthorId = "reviewer",
                AuthorKind = "human",
                Body = $"PR {implPrId} comment",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            db.WorkItems.Add(new WorkItemRow
            {
                Id = wsId,
                Title = $"Stage {i}",
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            db.Cards.Add(new CardRow
            {
                Id = wsId,
                Title = $"Stage {i}",
                Epic = "delete-spawn-e2e",
                Col = "implementation",
                Type = "task",
            });

            for (var iter = 1; iter <= 3; iter++)
            {
                db.Deliverables.Add(new DeliverableRow
                {
                    WorkItemId = wsId,
                    CrewId = "crew-eng",
                    Iter = iter,
                    Score = 70 + iter * 5,
                    Verdict = iter == 3 ? "pass" : "iterate",
                    Notes = $"iter {iter} for {wsId}",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
            {
                WorkItemId = wsId,
                Kind = "task.token",
                PayloadJson = $"{{\"text\":\"event for {wsId}\"}}",
                At = DateTimeOffset.UtcNow,
            });
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = wsId,
                TargetKind = "crew",
                TargetId = "crew-eng",
                State = TaskQueueStates.PickedUp,
                EnqueuedAt = DateTimeOffset.UtcNow,
                PickedUpAt = DateTimeOffset.UtcNow,
            });
            db.AgentClaims.Add(new AgentClaimRow
            {
                AgentId = $"agent-{i}",
                WorkItemId = wsId,
                Role = "generator",
                ClaimedAt = DateTimeOffset.UtcNow,
                ReleasedAt = null,
            });
        }

        await db.SaveChangesAsync();

        // 3) On-disk artifacts under {stateDir}/storyworkspaces/{spawnId}/
        //    and {stateDir}/attachments/{spawnId}/ — both are wiped by the
        //    DELETE endpoint's step 8. The stateDir is the parent of the
        //    factory's temp DB path (matches the production rule:
        //    `Path.GetDirectoryName(config["Forge:Db:Path"])`).
        var stateDir = f.StateDir;
        var storyWorkspacesDir = Path.Combine(stateDir, "storyworkspaces", spawnId);
        var attachmentsDir = Path.Combine(stateDir, "attachments", spawnId);

        Directory.CreateDirectory(storyWorkspacesDir);
        await File.WriteAllTextAsync(Path.Combine(storyWorkspacesDir, "sentinel.txt"), "spawn workspace sentinel\n");
        foreach (var wsId in workstreamIds)
        {
            var implDir = Path.Combine(storyWorkspacesDir, $"impl-{wsId}");
            Directory.CreateDirectory(implDir);
            await File.WriteAllTextAsync(Path.Combine(implDir, "agent.log"), $"sentinel for {wsId}\n");
        }
        Directory.CreateDirectory(attachmentsDir);
        await File.WriteAllTextAsync(Path.Combine(attachmentsDir, "spec.png"), "fake binary");

        return new SeedResult(
            spawnId,
            workstreamIds.ToArray(),
            syntheticIds.ToArray(),
            prIds.ToArray(),
            openPrIds.ToArray(),
            mergedPrIds.ToArray(),
            allBranches,
            allBranches.Select(b => worktreePaths[b]).ToArray(),
            storyWorkspacesDir,
            attachmentsDir);
    }

    private readonly record struct SeedResult(
        string SpawnId,
        string[] WorkstreamIds,
        string[] SyntheticIds,
        string[] PrIds,
        string[] OpenPrIds,
        string[] MergedPrIds,
        string[] AllBranches,
        string[] AllWorktreePaths,
        string StoryWorkspacesDir,
        string AttachmentsDir)
    {
        public IEnumerable<string> AllWorkItemIds =>
            this.WorkstreamIds.Concat(this.SyntheticIds);
    }

    // ===================================================================
    // Test infrastructure — factory + helpers.
    // ===================================================================

    private static HttpClient NewWriterClient(E2EFactory f)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);
        return client;
    }

    /// <summary>
    /// Test factory that swaps the in-memory <see cref="IStoryGitOps"/>
    /// back to <see cref="RealStoryGitOps"/> so DELETE shells out to real
    /// <c>git</c>, stubs <see cref="IForgeProvider"/> with a recorder, and
    /// points <c>Forge:Db:Path</c> at the per-test state dir so the
    /// endpoint's on-disk dir wipes target our seeded paths.
    /// </summary>
    private sealed class E2EFactory : ForgeWebApplicationFactory
    {
        private readonly string cloneRoot;

        public RecordingProvider Provider { get; } = new();

        /// <summary>
        /// Parent directory of the per-test temp DB. The DELETE endpoint
        /// resolves <c>storyworkspaces/{spawnId}</c> and
        /// <c>attachments/{spawnId}</c> under this directory, so seed
        /// helpers materialise sentinels here before calling DELETE.
        /// </summary>
        public string StateDir => Path.GetDirectoryName(this.DbPath)!;

        public E2EFactory(string cloneRoot)
        {
            this.cloneRoot = cloneRoot;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStoryGitOps>();
                services.AddSingleton<IStoryGitOps>(_ => new RealStoryGitOps());

                services.RemoveAll<IForgeProvider>();
                services.AddSingleton<IForgeProvider>(_ => this.Provider);
            });
        }
    }

    /// <summary>
    /// Mirror of the recorder in <see cref="Phase50StorySpawnDeleteTests"/>
    /// — captures every <c>ClosePullRequestAsync</c> call so tests can
    /// assert the "open PRs closed upstream, merged PRs not poked" contract.
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

        // Unused surface — return empty / NotImplemented so DI resolves.
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
    /// Replace every non-[A-Za-z0-9-_.] character with <c>-</c> and trim
    /// edges, producing a filesystem-safe directory name from a branch.
    /// </summary>
    private static string SanitiseBranchForWorktree(string branch)
    {
        var sb = new System.Text.StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }

    private static string NormalisePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    // ── Real git driver ─────────────────────────────────────────────────

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit(2_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task Git(string cwd, params string[] args)
    {
        var (code, _, stderr) = await CaptureGit(cwd, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in '{cwd}': {stderr}");
        }
    }

    private static async Task<(int Code, string StdOut, string StdErr)> CaptureGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}
