// -----------------------------------------------------------------------
// <copyright file="SpawnDeleteQuiescenceTests.cs" company="Microsoft Corp.">
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
/// Regression: a spawn in phase <c>Deleting</c> must be treated as quiescent
/// by the orchestrator the SAME way <c>Paused</c> already is, so that an
/// in-flight DELETE /api/stories/{storyId}/spawn can wipe its rows without a
/// concurrent scheduler tick fighting it.
///
/// <para>
/// <b>The race the operator hit.</b> <c>DeleteSpawn</c> flips
/// <c>Phase=Deleting</c> then does seconds of slow cleanup (closing PRs
/// upstream, <c>git push origin --delete</c> per branch, cascade-deleting
/// every workstream worktree). It does NOT hold the orchestrator's per-spawn
/// drive lock. Meanwhile <see cref="SpawnDependencyScheduler"/> ticks every
/// 15 s, and its enumeration historically excluded only <c>Paused</c> — so a
/// <c>Deleting</c> spawn was still driven. <c>RunOneStepAsync</c> re-creates
/// the story <see cref="WorkItemRow"/> whenever it's missing (and the
/// deliverable wipe un-passes the Phase-48 plan-doc gate, regressing the phase
/// to Plan and re-claiming plan-doc agents). Net effect: deletes 500'd on
/// SQLITE_BUSY and "tasks rescheduled mid-delete", which is why the operator
/// had to Pause first then Delete.
/// </para>
///
/// <para>
/// <b>The fix.</b> (1) the scheduler now excludes <c>Deleting</c> alongside
/// <c>Paused</c> when enumerating spawns, and (2) <c>RunOneStepAsync</c>
/// early-returns on <c>Deleting</c> BEFORE the story-WorkItem resurrection —
/// covering a tick that was already in flight (or orphaned past the 60 s
/// timeout) when the phase flipped. This test pins behaviour (2), the one that
/// produced the visible "tasks rescheduled" symptom.
/// </para>
/// </summary>
[Collection("Phase28")]
public class SpawnDeleteQuiescenceTests
{
    [Fact]
    public async Task DriveAsync_For_Deleting_Spawn_Does_Not_Resurrect_Story_WorkItem()
    {
        // No-colon storyId so the AsyncLocal slug fallback routes to the
        // unscoped ProductDbContext — same path DriveDoneOneShotForLocalProvider
        // and Stage52DoneGate use. The guard under test is independent of the
        // Phase 73 DB-split routing.
        const string storyId = "WI-delete-quiescence";
        const string spawnId = "spawn-" + storyId;

        using var f = ForgeWebApplicationFactory.WithOrchestratorEnabled();
        using (f.CreateClient())
        {
            // Boot the host so EnsureCreated / migrations run.
        }

        // Seed a Phase=Deleting spawn and deliberately do NOT pre-create a
        // WorkItemRow — this is exactly the window DeleteSpawn opens after its
        // cascade has removed the story WI but before it deletes the spawn row
        // itself. A non-quiescent RunOneStepAsync would re-create the WI here.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = "Delete quiescence regression",
                StoryDescription = "regression seed",
                Phase = StorySpawnPhases.Deleting,
                RepoId = ForgeSeed.DefaultRepoId,
                BaseBranch = "main",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StoryPoints = 1,
            });
            await db.SaveChangesAsync();

            // Sanity: no story WorkItem exists going in.
            Assert.False(await db.WorkItems.AnyAsync(w => w.Id == storyId));
        }

        // Drive the Deleting spawn. Must be a no-op: the early-return at the
        // top of RunOneStepAsync fires before the resurrection block.
        using (var scope = f.Services.CreateScope())
        {
            var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
            await orch.DriveAsync(storyId);
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            // The key invariant: the story WorkItem was NOT resurrected, so a
            // delete in flight can finish without racing a re-created parent
            // (and its downstream plan-doc / queue / claim re-derivation).
            Assert.False(
                await db.WorkItems.AnyAsync(w => w.Id == storyId),
                "Driving a Deleting-phase spawn must not resurrect the story WorkItem.");

            // The phase must remain Deleting — the orchestrator advanced
            // nothing.
            var spawn = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.Id == spawnId);
            Assert.Equal(StorySpawnPhases.Deleting, spawn.Phase);
        }
    }
}
