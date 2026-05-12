// -----------------------------------------------------------------------
// <copyright file="Phase50RestartFromScratchTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 50 follow-up — operator-initiated Restart of a plan-doc artifact
/// MUST regenerate the doc from scratch, not edit the previous run's
/// content incrementally. Two regression vectors are pinned here:
///
/// <list type="number">
///   <item><b>Endpoint plumbing</b> — POST /api/stories/{id}/spawn/artifacts/{key}/restart
///         must fire ResumeSingleArtifactAsync with restartFromScratch=true.
///         Before this fix, the endpoint called the default overload
///         (restartFromScratch=false), so the strategy never entered the
///         from-scratch cleanup path even though deliverables had been
///         wiped.</item>
///   <item><b>Strategy cleanup</b> — CrewStoryArtifactStrategy.EnsureFreshDocWorkspaceAsync
///         must delete the stale on-disk plan doc AND purge every DB row
///         keyed by docWorkItemId (deliverables, questions, transcripts,
///         queue, queue history). Before this fix the cleanup block in
///         ProducePlanFileAsync sat inside `if (!forceResume)` — and
///         Restart always set forceResume=true via ResumeSingleArtifactAsync —
///         so the block never ran on the operator's Restart click.</item>
/// </list>
///
/// User-reported symptom (2026-05-11, qq:TELEGRAM-MESSENGER-S
/// architecture.md): orchestrator halted after 56 iterations with
/// verdict=stalled-no-convergence; operator clicked RESTART; the
/// Document tab kept showing the 1,139-line stalled doc; the new
/// "iter 1" picked up at the old content and edited incrementally
/// instead of regenerating.
/// </summary>
public class Phase50RestartFromScratchTests
{
    // ════════════════════════════════════════════════════════════════════
    //  Endpoint plumbing
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Endpoint path — POST /api/stories/{id}/spawn/artifacts/{key}/restart
    /// must propagate restartFromScratch=true all the way through
    /// RunResumeWithCleanupAsync to ResumeSingleArtifactAsync. The
    /// deterministic strategy records the flag in
    /// <see cref="DeterministicStoryArtifactStrategy.ResumedWithRestartFromScratch"/>
    /// so we can verify end-to-end plumbing without spinning up a real LLM.
    /// </summary>
    [Fact]
    public async Task RestartArtifact_Endpoint_PropagatesRestartFromScratchTrue()
    {
        DeterministicStoryArtifactStrategy.Reset();
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        const string storyId = "WI-50001";
        const string spawnId = "spawn-WI-50001";
        await SeedStoryAndSpawnAsync(f, storyId, spawnId);

        var resp = await client.PostAsJsonAsync(
            $"/api/stories/{Uri.EscapeDataString(storyId)}/spawn/artifacts/architecture/restart",
            new { });
        Assert.True(resp.IsSuccessStatusCode,
            $"Restart endpoint returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

        // Wait briefly for the fire-and-forget Task.Run to invoke the strategy.
        var bucket = $"{spawnId}:architecture";
        await WaitForAsync(TimeSpan.FromSeconds(3), () =>
            DeterministicStoryArtifactStrategy.Resumed.ContainsKey(bucket));

        Assert.True(DeterministicStoryArtifactStrategy.Resumed.ContainsKey(bucket),
            "Restart endpoint never invoked ResumeSingleArtifactAsync.");
        Assert.True(DeterministicStoryArtifactStrategy.ResumedWithRestartFromScratch.TryGetValue(bucket, out var flagged) && flagged,
            "Restart endpoint invoked ResumeSingleArtifactAsync but did NOT pass restartFromScratch=true. " +
            "The endpoint should call RunResumeWithCleanupAsync(..., restartFromScratch: true).");
    }

    /// <summary>
    /// Sibling-check — POST /api/stories/{id}/spawn/artifacts/{key}/resume
    /// (the ordinary Resume button on a stalled doc) must NOT propagate
    /// restartFromScratch=true; that flag is reserved for the Restart
    /// path. If Resume also set the flag, mid-iter resumption would
    /// destructively delete partial progress instead of picking up where
    /// the prior iter left off.
    /// </summary>
    [Fact]
    public async Task ResumeArtifact_Endpoint_DoesNotSetRestartFromScratch()
    {
        DeterministicStoryArtifactStrategy.Reset();
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        const string storyId = "WI-50002";
        const string spawnId = "spawn-WI-50002";
        await SeedStoryAndSpawnAsync(f, storyId, spawnId);

        var resp = await client.PostAsJsonAsync(
            $"/api/stories/{Uri.EscapeDataString(storyId)}/spawn/artifacts/architecture/resume",
            new { });
        Assert.True(resp.IsSuccessStatusCode,
            $"Resume endpoint returned {(int)resp.StatusCode}.");

        var bucket = $"{spawnId}:architecture";
        await WaitForAsync(TimeSpan.FromSeconds(3), () =>
            DeterministicStoryArtifactStrategy.Resumed.ContainsKey(bucket));

        Assert.True(DeterministicStoryArtifactStrategy.Resumed.ContainsKey(bucket),
            "Resume endpoint never invoked ResumeSingleArtifactAsync.");
        Assert.False(DeterministicStoryArtifactStrategy.ResumedWithRestartFromScratch.ContainsKey(bucket),
            "Resume endpoint incorrectly set restartFromScratch=true; that flag is reserved for the Restart path.");
    }

    /// <summary>
    /// Boot-recovery path — when a previous process died with a Restart
    /// pending key still in ResumePendingJson, the recovery sweep MUST
    /// also fire (the user's Restart click should self-heal across the
    /// process kill). The recovery service doesn't persist the restart
    /// intent today; it re-fires with restartFromScratch=false (the
    /// default). The strategy's `existingDeliverables.Count == 0`
    /// fallback then kicks in (deliverables were wiped by the Restart
    /// endpoint before the kill) and the cleanup still runs. This test
    /// just confirms boot recovery calls through; the count-based
    /// fallback is covered by EnsureFreshDocWorkspace_FreshSpawnPath
    /// below.
    /// </summary>
    [Fact]
    public async Task BootRecovery_ReFiresWithDefaultFlag_NotRestartFromScratch()
    {
        DeterministicStoryArtifactStrategy.Reset();
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        const string storyId = "WI-50003";
        const string spawnId = "spawn-WI-50003";
        await SeedStoryAndSpawnAsync(f, storyId, spawnId);

        // Simulate the previous process dying mid-Restart: pre-seed the
        // pending marker and pretend deliverables were already wiped.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var row = await db.StorySpawns.FirstAsync(s => s.Id == spawnId);
            row.ResumePendingJson = "[\"architecture\"]";
            await db.SaveChangesAsync();
        }

        var recovery = f.Services.GetRequiredService<StoryResumeRecoveryService>();
        await recovery.SweepAsync(awaitCompletion: true, CancellationToken.None);

        var bucket = $"{spawnId}:architecture";
        Assert.True(DeterministicStoryArtifactStrategy.Resumed.ContainsKey(bucket),
            "Boot recovery did not re-fire the architecture Resume.");
        Assert.False(DeterministicStoryArtifactStrategy.ResumedWithRestartFromScratch.ContainsKey(bucket),
            "Boot recovery should re-fire with the default flag (restartFromScratch=false). " +
            "The strategy's count-based fallback handles the Restart-after-kill case.");
    }

    // ════════════════════════════════════════════════════════════════════
    //  Strategy cleanup helper — direct unit tests on EnsureFreshDocWorkspaceAsync
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Restart path — `restartFromScratch=true` MUST delete the on-disk
    /// doc file even when deliverables.Count > 0 (race-resilient: a
    /// stale in-flight agent could have written a new deliverable
    /// between the endpoint's cancel and the strategy's purge query).
    /// This is the new behavior that fixes the user-reported bug.
    /// </summary>
    [Fact]
    public async Task EnsureFreshDocWorkspace_RestartPath_DeletesFileAndPurgesRows()
    {
        var (f, scope, db, scopeFactory, tempDir, diskPath) = await SetupHelperFixtureAsync("WI-50101");
        try
        {
            const string docWorkItemId = "plan-doc:spawn-WI-50101:architecture.md";

            // Seed: existing file on disk + deliverable + transcript + queue rows.
            await File.WriteAllTextAsync(diskPath, "# STALE CONTENT from 56 stalled iters\n...");
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = docWorkItemId, CrewId = "crew-architect",
                GeneratorAgentId = "agent-gen", EvaluatorAgentId = "agent-eval",
                Iter = 56, Score = 86, Verdict = "stalled", Notes = "race-after-cancel",
                DimsJson = "{}",
            });
            db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
            {
                WorkItemId = docWorkItemId, Kind = "task.token",
                PayloadJson = "{}", At = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            Assert.True(File.Exists(diskPath));
            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == docWorkItemId));
            Assert.Equal(1, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == docWorkItemId));

            await CrewStoryArtifactStrategy.EnsureFreshDocWorkspaceAsync(
                scopeFactory, logger: null, diskPath, docWorkItemId, restartFromScratch: true, CancellationToken.None);

            Assert.False(File.Exists(diskPath),
                "EnsureFreshDocWorkspaceAsync did not delete the stale plan doc — the architect's next iter would inherit it and incrementally edit instead of regenerating.");
            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == docWorkItemId));
            Assert.Equal(0, await db.TaskTranscriptEvents.CountAsync(e => e.WorkItemId == docWorkItemId));
        }
        finally
        {
            scope.Dispose();
            f.Dispose();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Fresh-spawn path — `restartFromScratch=false` with no
    /// deliverables (the historical Phase 50 case (a)/(b): re-spawn
    /// after merged PR, or DeleteSpawn race) must still wipe the
    /// stale on-disk doc. This regression-locks the original Phase 50
    /// behavior so future refactors of the gate don't accidentally
    /// drop it.
    /// </summary>
    [Fact]
    public async Task EnsureFreshDocWorkspace_FreshSpawnPath_DeletesFile()
    {
        var (f, scope, db, scopeFactory, tempDir, diskPath) = await SetupHelperFixtureAsync("WI-50102");
        try
        {
            const string docWorkItemId = "plan-doc:spawn-WI-50102:architecture.md";
            await File.WriteAllTextAsync(diskPath, "# OLD spawn's doc from a merged plan PR\n...");
            Assert.True(File.Exists(diskPath));

            // No deliverables, no transcripts — true fresh-spawn case.
            await CrewStoryArtifactStrategy.EnsureFreshDocWorkspaceAsync(
                scopeFactory, logger: null, diskPath, docWorkItemId, restartFromScratch: false, CancellationToken.None);

            Assert.False(File.Exists(diskPath),
                "Fresh-spawn cleanup must delete the stale plan doc even when restartFromScratch=false.");
        }
        finally
        {
            scope.Dispose();
            f.Dispose();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Missing-file safety — helper must be a no-op (no exception)
    /// when the doc file doesn't exist on disk. Covers iter-1 of a
    /// brand-new spawn where EnsureWorktreeAsync just created the
    /// worktree and the doc has never been written.
    /// </summary>
    [Fact]
    public async Task EnsureFreshDocWorkspace_NoFile_DoesNotThrow()
    {
        var (f, scope, db, scopeFactory, tempDir, diskPath) = await SetupHelperFixtureAsync("WI-50103");
        try
        {
            const string docWorkItemId = "plan-doc:spawn-WI-50103:architecture.md";
            Assert.False(File.Exists(diskPath));

            await CrewStoryArtifactStrategy.EnsureFreshDocWorkspaceAsync(
                scopeFactory, logger: null, diskPath, docWorkItemId, restartFromScratch: true, CancellationToken.None);

            // No exception, no DB rows changed. Just succeeds quietly.
            Assert.False(File.Exists(diskPath));
        }
        finally
        {
            scope.Dispose();
            f.Dispose();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// DB-purge scope — cleanup must NOT delete rows keyed by a
    /// DIFFERENT workItemId. Two spawns running in parallel must not
    /// interfere with each other's restart.
    /// </summary>
    [Fact]
    public async Task EnsureFreshDocWorkspace_DoesNotTouchUnrelatedRows()
    {
        var (f, scope, db, scopeFactory, tempDir, diskPath) = await SetupHelperFixtureAsync("WI-50104");
        try
        {
            const string targetDocId = "plan-doc:spawn-WI-50104:architecture.md";
            const string siblingDocId = "plan-doc:spawn-WI-50104:tech-spec.md";

            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = targetDocId, CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
                Iter = 1, Score = 80, Verdict = "iterate", Notes = "target", DimsJson = "{}",
            });
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = siblingDocId, CrewId = "c", GeneratorAgentId = "g", EvaluatorAgentId = "e",
                Iter = 1, Score = 90, Verdict = "iterate", Notes = "sibling", DimsJson = "{}",
            });
            await db.SaveChangesAsync();

            await CrewStoryArtifactStrategy.EnsureFreshDocWorkspaceAsync(
                scopeFactory, logger: null, diskPath, targetDocId, restartFromScratch: true, CancellationToken.None);

            Assert.Equal(0, await db.Deliverables.CountAsync(d => d.WorkItemId == targetDocId));
            Assert.Equal(1, await db.Deliverables.CountAsync(d => d.WorkItemId == siblingDocId));
        }
        finally
        {
            scope.Dispose();
            f.Dispose();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    private static async Task SeedStoryAndSpawnAsync(
        ForgeWebApplicationFactory f, string storyId, string spawnId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (!await db.WorkItems.AnyAsync(w => w.Id == storyId))
        {
            db.WorkItems.Add(new WorkItemRow
            {
                Id = storyId, Type = "feature", State = "active", Title = $"story {storyId}",
                Provider = "local", Track = "dev", Points = 5,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!await db.StorySpawns.AnyAsync(s => s.Id == spawnId))
        {
            db.StorySpawns.Add(new StorySpawnRow
            {
                Id = spawnId,
                StoryId = storyId,
                StoryTitle = $"story {storyId}",
                StoryDescription = "test",
                StoryPoints = 5,
                Phase = StorySpawnPhases.Implement,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Spin up a factory with a fresh per-test DB + temp dir for the
    /// helper unit tests. Returns the factory, an open scope, the DbContext,
    /// the scope factory (needed by EnsureFreshDocWorkspaceAsync), the temp
    /// dir to clean up, and a sample diskPath for the plan doc.
    /// </summary>
    private static async Task<(
        ForgeWebApplicationFactory factory,
        IServiceScope scope,
        ForgeDbContext db,
        IServiceScopeFactory scopeFactory,
        string tempDir,
        string diskPath)>
        SetupHelperFixtureAsync(string suffix)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var scopeFactory = f.Services.GetRequiredService<IServiceScopeFactory>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"forge-test-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var diskPath = Path.Combine(tempDir, "architecture.md");
        await Task.CompletedTask;
        return (f, scope, db, scopeFactory, tempDir, diskPath);
    }

    private static async Task WaitForAsync(TimeSpan timeout, Func<bool> predicate)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }
}
