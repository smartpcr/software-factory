// -----------------------------------------------------------------------
// <copyright file="DriveDoneOneShotForLocalProviderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Regression: <see cref="StorySpawnOrchestrator"/>'s
/// <c>DriveDoneAsync</c> path must be one-shot for ProductStory-sourced
/// spawns (Provider=&quot;local&quot;) just like it already is for upstream
/// ADO / GitHub work items.
///
/// <para>
/// <b>Before the fix</b> the orchestrator synthesised a
/// <see cref="WorkItemRow"/> with <c>Provider=&quot;local&quot;</c> in
/// <c>RunOneStepAsync</c>, then in <c>DriveDoneAsync</c> dispatched to
/// <c>ProviderStoryProviderOps.TransitionWorkItemAsync</c> which short-circuits
/// for local rows without touching <c>wi.State</c>. The next-tick guard at
/// the top of <c>DriveDoneAsync</c> (<c>if (story.State is &quot;closed&quot;
/// or &quot;resolved&quot;) return false;</c>) therefore never tripped, and
/// <see cref="SpawnDependencyScheduler"/> re-emitted <c>story-closed</c>
/// every 15 s — producing the recurring
/// <c>Story {storyId}: complete ✓</c> toast in the UI.
/// </para>
///
/// <para>
/// <b>After the fix</b> <c>DriveDoneAsync</c> stamps
/// <c>story.State = &quot;closed&quot;</c> + <c>UpdatedAt</c> on the local
/// row before emitting, and the <c>hasUpstream</c> branch is keyed off
/// <c>story.Provider</c> rather than &quot;row exists&quot; so the
/// ProductStoryRow.Status=&quot;done&quot; assignment in the else branch is
/// actually reachable. The first drive transitions both rows; the second
/// drive short-circuits at the guard and writes nothing.
/// </para>
/// </summary>
[Collection("Phase28")]
public class DriveDoneOneShotForLocalProviderTests
{
    [Fact]
    public async Task DriveDoneAsync_For_ProductStory_Sourced_Spawn_Is_One_Shot()
    {
        // No-colon storyId so the AsyncLocal slug fallback routes to the
        // unscoped ProductDbContext (the same path Stage52DoneGateTests
        // uses). The bug is independent of Phase 73 DB-split routing —
        // it depends solely on the synthetic WorkItemRow.Provider="local"
        // discriminator inside DriveDoneAsync.
        const string storyId = "WI-drive-done-oneshot-local";
        const string spawnId = "spawn-" + storyId;

        using var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (f.CreateClient())
        {
            // Boot the host so EnsureCreated / Phase 73 migrations run.
        }

        // Seed a ProductStory + a Phase=Done spawn. Deliberately do NOT
        // pre-create a WorkItemRow — RunOneStepAsync will synthesise one
        // with Provider="local", which is the exact state production hit
        // for `code-intelligence:AGENT-MEMORY` 2026-05-23.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.ProductStories.Add(new ProductStoryRow
            {
                Id = storyId,
                ProductId = ForgeSeed.DefaultProductId,
                RepoId = ForgeSeed.DefaultRepoId,
                Name = "Drive-done one-shot regression",
                Status = "active",
                Description = "regression seed",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Drive-done one-shot regression",
                Phase = StorySpawnPhases.Done,
                RepoId = ForgeSeed.DefaultRepoId,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 1,
            });
            await db.SaveChangesAsync();
        }

        // First drive: RunOneStepAsync synthesises WorkItem(Provider=local,
        // State=active); DriveDoneAsync then closes both the local WI and
        // the ProductStory.
        using (var scope = f.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orch.DriveAsync(storyId);
        }

        DateTimeOffset wiUpdatedAfterFirstDrive;
        DateTimeOffset psUpdatedAtSentinel = default;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == storyId);
            Assert.Equal("local", wi.Provider);                 // synthesised by RunOneStepAsync
            Assert.Equal("closed", wi.State);                   // ← key invariant the bug broke
            wiUpdatedAfterFirstDrive = wi.UpdatedAt;

            var ps = await db.ProductStories.AsNoTracking().FirstAsync(p => p.Id == storyId);
            Assert.Equal("done", ps.Status);                    // else-branch is now reachable
            psUpdatedAtSentinel = ps.CreatedAt;                 // ProductStoryRow has no UpdatedAt; keep sentinel for shape parity

            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
            Assert.Equal(StorySpawnPhases.Done, spawn.Phase);
        }

        // Allow the wall clock to advance so a (buggy) second
        // SaveChangesAsync would produce a strictly-later UpdatedAt.
        await Task.Delay(50);

        // Second drive: must short-circuit at the guard
        // (story.State == "closed") — NO SaveChangesAsync, NO EmitChange.
        using (var scope = f.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orch.DriveAsync(storyId);
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == storyId);
            Assert.Equal("closed", wi.State);
            // Proof of short-circuit: WorkItem.UpdatedAt did not advance, so
            // DriveDoneAsync took the early-return path and never reached
            // the EmitChange("story-closed", ...) call. This is exactly
            // the SpawnDependencyScheduler "one-shot per spawn" contract.
            Assert.Equal(wiUpdatedAfterFirstDrive, wi.UpdatedAt);
        }
    }
}
