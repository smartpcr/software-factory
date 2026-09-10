// -----------------------------------------------------------------------
// <copyright file="StoryOperatorActionService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Services.Git;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Forge.Api.Endpoints.StoriesEndpoints;

/// <summary>Shared, authenticated spawn and artifact operations. No HTTP context or synthetic headers.</summary>
public sealed class StoryOperatorActionService(IServiceProvider services, ForgeActionContext actions)
{
    public async Task<IResult> ResumeArtifactAsync(string storyId, string key, CancellationToken ct)
    {
        await actions.RequireAsync("artifact.retry", new(StoryId: storyId, ArtifactKey: key.ToLowerInvariant()), ct);
        key = key.ToLowerInvariant();
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var strategy = services.GetRequiredService<IStoryArtifactStrategy>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var story = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == storyId);
        if (story is null) return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);

        // Phase 68 — Resume on a merged plan-doc is rejected with 409 (mirrors
        // RestartArtifact's guard at line ~3060). Re-firing the gen-eval loop
        // after the doc PR merged rebuilds the worktree from base, then runs
        // the loop against a phantom workspace — the evaluator scores 0
        // because the doc file is "absent from the requested worktree".
        // This is the exact pattern observed 2026-05-15 on
        // code-intelligence:AGENT-MEMORY e2e-scenarios.md (iter-2 row 5079,
        // score 0, $9.81 burned, "draft cannot be reviewed" notes). Mirroring
        // the Restart guard prevents both operator-initiated Resume clicks
        // AND boot-recovery resume sweeps from triggering the phantom run.
        if (PlanDocStateHelpers.IsSequentialMode(spawn)
            && PlanDocStateHelpers.PlanDocFileName.ContainsKey(key.ToLowerInvariant()))
        {
            var entry = PlanDocStateHelpers.GetEntry(spawn, key.ToLowerInvariant());
            if (!string.IsNullOrEmpty(entry.PrId))
            {
                var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.PrId);
                if (pr is not null && string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    // Defensive: if a stale resume-pending marker exists from
                    // a pre-Phase-68 boot, clear it so StoryResumeRecoveryService
                    // doesn't keep re-firing the phantom path on every restart.
                    if (RemoveResumePending(spawn, key.ToLowerInvariant()))
                    {
                        spawn.UpdatedAt = DateTimeOffset.UtcNow;
                        await control.SaveChangesAsync(); await db.SaveChangesAsync();
                    }
                    return Results.Problem(
                        title: "Plan doc PR already merged",
                        detail: $"doc '{key}' PR {entry.PrId} is already merged; resume not supported (would run gen-eval against a phantom worktree). Use ACCEPT to flip the latest deliverable verdict to pass, or delete + re-create the spawn.",
                        statusCode: 409);
                }
            }
        }

        // Phase 41 — persist the pending intent before the task starts so a
        // crash between here and ResumeSingleArtifactAsync's first persisted
        // event still leaves a recovery marker.
        AddResumePending(spawn, key);
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(() => RunResumeWithCleanupAsync(scopeFactory, strategy, spawn.Id, storyId, key, CancellationToken.None));

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "artifact-resumed", storyId, key });

        return Results.Ok(new { storyId, key, status = "resuming" });
    }

    public async Task<IResult> RetryArtifactAsync(string storyId, string key, CancellationToken ct)
    {
        await actions.RequireAsync("artifact.retry", new(StoryId: storyId, ArtifactKey: key.ToLowerInvariant()), ct);
        key = key.ToLowerInvariant();
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var strategy = services.GetRequiredService<IStoryArtifactStrategy>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var story = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == storyId);
        if (story is null) return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);

        var docKey = key.ToLowerInvariant();
        if (!Forge.Api.Services.Stories.PlanDocStateHelpers.PlanDocFileName.TryGetValue(
                docKey, out var fileName))
        {
            return Results.Problem(
                type: "retry.unknown-key",
                title: $"Unknown plan-doc key '{key}'.",
                detail: $"Expected one of: {string.Join(", ", Forge.Api.Services.Stories.PlanDocStateHelpers.PlanDocFileName.Keys)}.",
                statusCode: 400);
        }

        var docWorkItemId = $"plan-doc:{spawn.Id}:{fileName}";

        var deliverables = await db.Deliverables
            .Where(d => d.WorkItemId == docWorkItemId)
            .ToListAsync();
        var lastDel = deliverables
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Iter)
            .FirstOrDefault();
        if (lastDel is null)
        {
            return Results.Problem(
                type: "retry.no-deliverables",
                title: $"Nothing to retry -- doc '{key}' has no prior iterations.",
                detail: "Use Resume / Restart to dispatch fresh.",
                statusCode: 400);
        }

        // (1) Demote verdict. The blocker that prevents Resume from
        // doing useful work is DeliverableLoop's terminal-replay check
        // (DeliverableLoop.cs:316-345): when verdict is "stalled"/"sloppy"
        // AND iter >= maxIter, the loop returns immediately. We need
        // BOTH (a) verdict NOT in {pass, reject, stalled-budget} AND
        // (b) iter < maxIter for seeding to fire. Demote to "iterate"
        // covers (a); the cap bump below covers (b).
        var priorVerdict = lastDel.Verdict;
        var demoted = false;
        if (lastDel.Verdict is "pass" or "stalled" or "sloppy")
        {
            lastDel.Verdict = "iterate";
            var note = $"OPERATOR RETRY (plan-doc {key}.md) -- verdict demoted from '{priorVerdict}' to 'iterate'; iter cap bumped +5 so DeliverableLoop seeding continues at iter+1 instead of terminal-replaying. Doc, prior chain, transcript, and cost ledger preserved.";
            lastDel.Notes = string.IsNullOrEmpty(lastDel.Notes) ? note : lastDel.Notes + "\n\n" + note;
            demoted = true;
        }

        // (2) Bump the iter cap via TaskQueueRow.MaxIterOverride. The
        // DeliverableLoop reads this on every invocation
        // (DeliverableLoop.cs:260-276) -- it overrides the points-derived
        // cap. We aim for max(currentOverride, lastIter+5) so a previous
        // bump isn't accidentally lowered.
        var existingOverride = await db.TaskQueue
            .Where(q => q.WorkItemId == docWorkItemId)
            .OrderByDescending(q => q.Id)
            .FirstOrDefaultAsync();
        var pointsDerived = ScoringConstants.MaxIterationsForPoints(
            Math.Max(1, story.Points));
        var currentCap = existingOverride?.MaxIterOverride ?? pointsDerived;
        var newCap = Math.Max(currentCap, lastDel.Iter + 5);

        if (existingOverride is not null && existingOverride.State != "completed")
        {
            existingOverride.MaxIterOverride = newCap;
            existingOverride.LastUpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.TaskQueue.Add(new TaskQueueRow
            {
                WorkItemId = docWorkItemId,
                TargetKind = "team",
                TargetId = "Architect",
                State = TaskQueueStates.Queued,
                MaxIterOverride = newCap,
                EnqueuedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        // (3) Reset the doc work item's ExecutionStatus so the UI
        // badge and the orchestrator's claim sweep see a clean row.
        var docWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == docWorkItemId);
        if (docWi is not null)
        {
            docWi.ExecutionStatus = ExecutionStatuses.Queued;
            docWi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Clear the spawn's last-error banner so the operator
        // sees the retry kick off cleanly (same as RetrySpawn).
        spawn.LastError = null;
        spawn.LastErrorAt = null;

        // (5) Mark the doc as pending-resume BEFORE the background task
        // starts so a crash mid-Retry is recovered by StoryResumeRecoveryService
        // (matches Phase 41's restart-safe contract on ResumeArtifact).
        AddResumePending(spawn, key);
        spawn.UpdatedAt = DateTimeOffset.UtcNow;

        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // (6) Fire the same background drive as Resume -- DeliverableLoop
        // now sees verdict="iterate" + new maxIter cap, so seeding takes
        // over and the loop continues from iter+1.
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(() => RunResumeWithCleanupAsync(
            scopeFactory, strategy, spawn.Id, storyId, key, CancellationToken.None));

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "artifact-retried", storyId, key });

        return Results.Ok(new
        {
            storyId,
            key,
            priorVerdict,
            newVerdict = lastDel.Verdict,
            demoted,
            priorIter = lastDel.Iter,
            priorScore = lastDel.Score,
            priorMaxIter = currentCap,
            newMaxIter = newCap,
            status = "retrying",
        });
    }

    public async Task<IResult> CancelArtifactAsync(string storyId, string key, CancellationToken ct)
    {
        await actions.RequireAsync("artifact.cancel", new(StoryId: storyId, ArtifactKey: key.ToLowerInvariant()), ct);
        key = key.ToLowerInvariant();
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var runner = services.GetRequiredService<IAgentRunner>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";

        // (1) Kill in-flight invocations first so no events land mid-cleanup.
        var invocationsCancelled = 0;
        try
        {
            invocationsCancelled = await runner.CancelByTaskAsync(workItemId, ct);
        }
        catch
        {
            // Don't let runner failures prevent DB cleanup.
        }

        // (2) Cancel any queued/active TaskQueue rows (plan-doc loops mostly
        // run inline in the orchestrator, but defensive cleanup is cheap).
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == workItemId && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (3) Reset WorkItem ExecutionStatus.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);
        if (wi is not null && wi.ExecutionStatus is "live" or "queued")
        {
            wi.ExecutionStatus = "idle";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Flip last deliverable from iterate → stalled so the doc UI
        // shows the loop as halted rather than mid-iteration.
        var lastDel = (await db.Deliverables
            .Where(d => d.WorkItemId == workItemId)
            .ToListAsync())
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (lastDel is not null && lastDel.Verdict == "iterate")
        {
            lastDel.Verdict = "stalled";
        }

        // (5) Drop the resume-pending marker so a process restart doesn't
        // re-fire the loop the operator just cancelled.
        if (RemoveResumePending(spawn, key))
        {
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "artifact-cancelled", storyId, key });

        return Results.Ok(new
        {
            storyId,
            key,
            status = "cancelled",
            queueRowsCancelled = queueRows.Count,
            invocationsCancelled,
        });
    }

    public async Task<IResult> AcceptArtifactAsync(string storyId, string key, CancellationToken ct)
    {
        await actions.RequireAsync("artifact.accept", new(StoryId: storyId, ArtifactKey: key.ToLowerInvariant()), ct);
        key = key.ToLowerInvariant();
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var runner = services.GetRequiredService<IAgentRunner>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var git = services.GetRequiredService<IStoryGitOps>();
        var provider = services.GetRequiredService<IStoryProviderOps>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";

        // Must have at least one deliverable to accept.
        var lastDel = (await db.Deliverables
            .Where(d => d.WorkItemId == workItemId)
            .ToListAsync())
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault();
        if (lastDel is null)
            return Results.Problem(title: "No iterations to accept", statusCode: 400);

        var alreadyPassed = string.Equals(lastDel.Verdict, "pass", StringComparison.OrdinalIgnoreCase);
        var previousVerdict = lastDel.Verdict;
        var previousScore = lastDel.Score;

        // (1) Override verdict to pass (no-op when already passed — the
        // caller is re-triggering to get the PR merge that was missed).
        if (!alreadyPassed)
            lastDel.Verdict = "pass";

        // (2) Kill in-flight invocations so the loop doesn't keep running.
        var invocationsCancelled = 0;
        try
        {
            invocationsCancelled = await runner.CancelByTaskAsync(workItemId, ct);
        }
        catch { /* best effort */ }

        // (3) Cancel any queued/active TaskQueue rows.
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == workItemId && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Reset WorkItem ExecutionStatus.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);
        if (wi is not null && wi.ExecutionStatus is "live" or "queued")
        {
            wi.ExecutionStatus = "idle";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (5) Drop the resume-pending marker.
        if (RemoveResumePending(spawn, key))
        {
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "artifact-accepted", storyId, key });

        // (6) Fire-and-forget: push the plan branch and merge to main via
        // a PR. Per-iteration commits are local-only (CommitPathInWorktree
        // doesn't push), so we push the branch first, then open/merge a PR.
        // PushBranchAsync stages any uncommitted changes and pushes without
        // calling EnsureWorktreeAsync (which can reset the local branch).
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        var capturedStoryId = storyId;
        var capturedSpawnId = spawn.Id;
        var capturedRepoId = spawn.RepoId;
        // Phase 53 — resolve the per-doc branch + PR (sequential mode) or
        // fall back to the legacy single-PR branch (legacy mode). The
        // accept-artifact flow then merges THIS doc's branch into the
        // spawn's base, not a global plan branch.
        var capturedDocKey = key.ToLowerInvariant();
        var capturedIsSequential = PlanDocStateHelpers.IsSequentialMode(spawn)
            && PlanDocStateHelpers.PlanDocFileName.ContainsKey(capturedDocKey);
        var capturedPlanBranch = capturedIsSequential
            ? PlanDocStateHelpers.FindDocBranch(spawn, capturedDocKey)
            : (spawn.PlanBranch ?? $"story/{StoryPaths.Safe(spawn.StoryId)}/plan");
        var capturedPlanPrId = capturedIsSequential
            ? PlanDocStateHelpers.GetEntry(spawn, capturedDocKey).PrId
            : spawn.PlanPrId;
        var capturedStoryTitle = spawn.StoryTitle ?? storyId;
        var capturedBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        _ = Task.Run(async () =>
        {
            var logger = loggerFactory?.CreateLogger("forge.story.accept");
            try
            {
                using var scope = scopeFactory.CreateScope();
                var scopeControl = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
                var scopeDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var scopeHub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();

                RepoRow? repo = null;
                if (!string.IsNullOrEmpty(capturedRepoId))
                {
                    // Phase 80.B — detached Task.Run has no request/AsyncLocal
                    // product scope, so route via RepoLookup (registry → owning
                    // product's DB) rather than the control-fallback context.
                    var scopeRepoLookup = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.RepoLookup>();
                    repo = await scopeRepoLookup.GetByIdAsync(capturedRepoId);
                }

                // Push the plan branch — stages any uncommitted changes
                // from a cancelled agent and pushes all local-only
                // per-iteration commits to origin.
                await git.PushBranchAsync(repo, capturedPlanBranch, CancellationToken.None);

                // Phase 45 (revision after operator feedback) — every ACCEPT
                // results in a PR that targets the spawn's CURRENT base
                // branch. Three cases:
                //   1. No PR recorded → open a new one.
                //   2. Existing PR merged → open a fresh "[plan-update]" PR.
                //   3. Existing PR open but its `Base` differs from the
                //      current spawn base → close it locally and open a new
                //      one against the current base. (Operators changed the
                //      base branch mid-flight; the stale PR would never
                //      merge to the right place.)
                //   4. Existing PR open AND already targets the current base
                //      → reuse it (idempotent across multiple per-doc accepts).
                string? prId = capturedPlanPrId;
                var openNewPr = true;
                PrRow? existingPr = null;
                if (!string.IsNullOrEmpty(prId))
                {
                    existingPr = await scopeDb.Prs.FirstOrDefaultAsync(p => p.Id == prId);
                }
                if (existingPr is null)
                {
                    openNewPr = true;
                }
                else if (string.Equals(existingPr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    openNewPr = true;
                }
                else if (!string.Equals(existingPr.Base ?? "main", capturedBase, StringComparison.Ordinal))
                {
                    // Stale base — close locally and open a new PR. The
                    // upstream PR (if any) is left for the operator to close
                    // since IForgeProvider has no edit-PR endpoint at v0.1.
                    logger?.LogInformation(
                        "Closing stale plan PR {PrId} (base '{OldBase}' ≠ current spawn base '{NewBase}') and opening a fresh one.",
                        existingPr.Id, existingPr.Base, capturedBase);
                    existingPr.Col = "closed";
                    existingPr.IsActive = false;
                    existingPr.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
                    await scopeDb.SaveChangesAsync();
                    if (scopeHub is not null)
                    {
                        await scopeHub.Clients.All.SendAsync("pr.changed",
                            new { kind = "superseded", id = existingPr.Id, reason = "base-branch-change" });
                    }
                    openNewPr = true;
                }
                else
                {
                    openNewPr = false;
                }

                if (openNewPr)
                {
                    prId = await provider.OpenPrAsync(new StoryPrRequest(
                        Branch: capturedPlanBranch,
                        BaseBranch: capturedBase,
                        Title: $"[plan-update] {capturedStoryTitle}",
                        Body: $"Accepted plan artifact `{fileName}` for story `{StoryPaths.Safe(capturedStoryId)}` (score {previousScore}, operator override).",
                        Repo: repo,
                        WorkItemId: capturedStoryId,
                        Workstream: null,
                        Epic: capturedStoryId), CancellationToken.None);

                    var spawnRow = await scopeDb.StorySpawns.FirstOrDefaultAsync(s => s.Id == capturedSpawnId);
                    if (spawnRow is not null)
                    {
                        // Phase 53 — for sequential-mode spawns, persist the
                        // new PR id under the right doc key in the JSON
                        // column. For legacy spawns, fall back to PlanPrId.
                        if (capturedIsSequential)
                        {
                            var states = PlanDocStateHelpers.GetPlanDocStates(spawnRow).ToList();
                            var idx = states.FindIndex(e =>
                                string.Equals(e.Key, capturedDocKey, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                            {
                                states[idx] = states[idx] with { PrId = prId };
                            }
                            else
                            {
                                states.Add(new PlanDocPrEntry(capturedDocKey, capturedPlanBranch, prId));
                            }
                            PlanDocStateHelpers.SetPlanDocStates(spawnRow, states);
                        }
                        else
                        {
                            spawnRow.PlanPrId = prId;
                        }
                        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
                        await scopeDb.SaveChangesAsync();
                    }
                    if (scopeHub is not null)
                    {
                        await scopeHub.Clients.All.SendAsync("story-spawn.changed",
                            new { kind = "plan-pr-opened", storyId = capturedStoryId, prId, baseBranch = capturedBase });
                    }
                }

                // Auto-approve + merge.
                try { await provider.ApprovePrAsync(prId!, "rev-default", CancellationToken.None); }
                catch { /* best effort — may already be approved */ }

                await provider.MergePrAsync(prId!, CancellationToken.None);
                await git.MergeAsync(repo, capturedPlanBranch, capturedBase, CancellationToken.None);

                if (scopeHub is not null)
                {
                    await scopeHub.Clients.All.SendAsync("story-spawn.changed",
                        new { kind = "artifact-merged", storyId = capturedStoryId, key });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Phase 45 — surface the failure via spawn.LastError so the
                // operator can see WHY the accept didn't land. Previously
                // swallowed silently, which produced the "I clicked accept
                // and nothing happened" symptom the operator hit on
                // 2026-05-05 with feature/memory-pressure.
                logger?.LogError(ex,
                    "Accept fire-and-forget failed for story {StoryId} key={Key}; persisting LastError.",
                    capturedStoryId, key);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var scopeDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var scopeHub = scope.ServiceProvider.GetService<IHubContext<ForgeHub>>();
                    var spawnRow = await scopeDb.StorySpawns.FirstOrDefaultAsync(s => s.Id == capturedSpawnId);
                    if (spawnRow is not null)
                    {
                        spawnRow.LastError = $"accept {key} failed: {ex.Message}";
                        spawnRow.LastErrorAt = DateTimeOffset.UtcNow;
                        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
                        await scopeDb.SaveChangesAsync();
                        if (scopeHub is not null)
                        {
                            await scopeHub.Clients.All.SendAsync("story-spawn.changed",
                                new { kind = "spawn-error", storyId = capturedStoryId, message = spawnRow.LastError });
                        }
                    }
                }
                catch { /* best-effort — already in the failure path */ }
            }
        });

        return Results.Ok(new
        {
            storyId,
            key,
            status = "accepted",
            previousVerdict,
            previousScore,
            queueRowsCancelled = queueRows.Count,
            invocationsCancelled,
        });
    }

    public async Task<IResult> RestartArtifactAsync(string storyId, string key, CancellationToken ct)
    {
        await actions.RequireAsync("artifact.restart", new(StoryId: storyId, ArtifactKey: key.ToLowerInvariant()), ct);
        key = key.ToLowerInvariant();
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var runner = services.GetRequiredService<IAgentRunner>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var paths = services.GetRequiredService<IForgeDbPaths>();
        var strategy = services.GetRequiredService<IStoryArtifactStrategy>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var story = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == storyId);
        if (story is null) return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";

        // Phase 53 — reject restart of an already-merged plan-doc PR.
        // Re-running a merged upstream doc would invalidate every
        // downstream doc generated against it; unwinding downstream state
        // is a v2 feature. Operator recovery path = delete-spawn +
        // re-create.
        if (PlanDocStateHelpers.IsSequentialMode(spawn)
            && PlanDocStateHelpers.PlanDocFileName.ContainsKey(key.ToLowerInvariant()))
        {
            var entry = PlanDocStateHelpers.GetEntry(spawn, key.ToLowerInvariant());
            if (!string.IsNullOrEmpty(entry.PrId))
            {
                var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.PrId);
                if (pr is not null && string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(
                        title: "Plan doc PR already merged",
                        detail: $"doc '{key}' PR {entry.PrId} is already merged; restart not supported. Delete + re-create the spawn instead.",
                        statusCode: 409);
                }
            }
        }

        // (1) Kill in-flight invocations first so the cleanup below isn't
        // racing with new events.
        try { await runner.CancelByTaskAsync(workItemId, ct); }
        catch { /* best effort — DB cleanup is the source of truth */ }

        // (2) Delete deliverables + transcript events for this artifact.
        var deliverables = await db.Deliverables.Where(d => d.WorkItemId == workItemId).ToListAsync();
        db.Deliverables.RemoveRange(deliverables);
        var events = await db.TaskTranscriptEvents.Where(e => e.WorkItemId == workItemId).ToListAsync();
        db.TaskTranscriptEvents.RemoveRange(events);

        // (2b) Delete open questions for this artifact. Mirrors
        // POST /api/workstreams/{id}/restart (WorkstreamsEndpoints.cs §3).
        // The deeper EnsureFreshDocWorkspaceAsync helper also purges
        // questions, but it only runs *after* EnsureWorktreeAsync inside
        // ResumeSingleArtifactAsync — when that step fails (e.g. git fetch
        // auth failure on the spawn's BaseBranch) the cleanup is skipped
        // and the operator's stale `Q n` badge never clears. Doing it here
        // synchronously guarantees Restart = clean slate regardless of
        // whether the downstream resume succeeds.
        var questions = await db.DeliverableQuestions.Where(q => q.WorkItemId == workItemId).ToListAsync();
        db.DeliverableQuestions.RemoveRange(questions);

        // (2c) Purge agent_usage rows for this synthetic workItemId so the
        // cost card resets to $0 on Restart. TaskId == workItemId is
        // deterministic (`plan-doc:{spawn.Id}:{file}`), identical across
        // restarts, so old gen/eval ledger rows would otherwise leak onto
        // the freshly-running iteration's cost rollup. Mirrors:
        //   - DeleteSpawn synthetic sweep (StoriesEndpoints.cs ~1074)
        //   - PurgeResidualSpawnDataAsync (StoriesEndpoints.cs ~451)
        //   - Workstream cancel/restart (WorkstreamsEndpoints.cs ~458, 702)
        // Without this step, the operator saw $589.85 carried over from
        // the prior architecture.md run instead of starting fresh.
        var usage = await control.AgentUsage.Where(u => u.TaskId == workItemId).ToListAsync();
        control.AgentUsage.RemoveRange(usage);

        // (3) Cancel any queued/active TaskQueue rows.
        var queueRows = await db.TaskQueue
            .Where(q => q.WorkItemId == workItemId && q.State != "completed" && q.State != "cancelled")
            .ToListAsync();
        foreach (var q in queueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = DateTimeOffset.UtcNow;
            q.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        // (4) Reset WorkItem ExecutionStatus.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);
        if (wi is not null)
        {
            wi.ExecutionStatus = "idle";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // (5) Persist a resume-pending marker before kicking off the
        // background task so a process kill mid-restart self-heals via the
        // boot-time recovery sweep.
        AddResumePending(spawn, key);
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // (6) Wipe the architect workspace on disk. Matches the path
        // convention used by CrewStoryArtifactStrategy.PrepareWorkspace
        // (`storyworkspaces/{spawnSlug}/plan-{key}`); checks both dash and
        // underscore slug variants for back-compat.
        var stateDir = paths.Folder;
        var phaseTag = $"plan-{key}";
        foreach (var slug in new[] { spawn.Id, spawn.Id.Replace('-', '_'), spawn.Id.Replace('_', '-') })
        {
            var wsDir = System.IO.Path.Combine(stateDir, "storyworkspaces", slug, phaseTag);
            if (System.IO.Directory.Exists(wsDir))
            {
                try { System.IO.Directory.Delete(wsDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "artifact-restarted", storyId, key });

        // (7) Fire-and-forget: re-enter the gen↔eval loop. With no prior
        // deliverables ResumeSingleArtifactAsync starts at iter 1.
        // Phase 50 follow-up — pass `restartFromScratch: true` so the
        // strategy wipes the on-disk plan doc + any stale per-doc DB rows
        // (deliverables/transcripts/queue) BEFORE the architect's first
        // iteration. The endpoint above already cleaned the DB rows, but
        // it cannot reliably delete the worktree doc file (the worktree
        // path is owned by the strategy via EnsureWorktreeAsync; even if
        // the endpoint deleted it, the subsequent `git checkout {planBranch}`
        // would restore it from the branch tip). The strategy deletes the
        // file AFTER EnsureWorktreeAsync runs, which is the only window
        // where the deletion sticks until iter 1 writes the regenerated
        // content. Without this flag, Restart was silently degrading to
        // an incremental edit because `forceResume=true` bypassed the
        // Phase 50 cleanup block.
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(() => RunResumeWithCleanupAsync(
            scopeFactory, strategy, spawn.Id, storyId, key,
            CancellationToken.None, restartFromScratch: true));

        return Results.Ok(new
        {
            storyId,
            key,
            status = "restarted",
            deliverablesCleaned = deliverables.Count,
            eventsCleaned = events.Count,
            questionsCleaned = questions.Count,
            agentUsageCleaned = usage.Count,
            queueRowsCancelled = queueRows.Count,
        });
    }

    public async Task<IResult> PauseSpawnAsync(string storyId, CancellationToken ct)
    {
        await actions.RequireAsync("spawn.pause", new(StoryId: storyId), ct);
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var runner = services.GetRequiredService<IAgentRunner>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null)
        {
            return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);
        }

        if (spawn.Phase is StorySpawnPhases.Paused or StorySpawnPhases.Done or StorySpawnPhases.Deleting)
        {
            return Results.Problem(
                title: $"Cannot pause spawn in phase '{spawn.Phase}'",
                detail: spawn.Phase == StorySpawnPhases.Paused
                    ? "Spawn is already paused."
                    : $"Spawn phase '{spawn.Phase}' is terminal — pause is not applicable.",
                statusCode: 409);
        }

        var logger = loggerFactory.CreateLogger("forge.story-spawn.pause");

        // 1. Save current phase and flip to "paused" early — same pattern as
        //    "deleting" — so the orchestrator's RunOneStepAsync no-ops even if
        //    a concurrent tick is in-flight.
        spawn.PausedFromPhase = spawn.Phase;
        spawn.Phase = StorySpawnPhases.Paused;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Brief drain so any in-flight tick finishes its current SaveChanges
        // before we contend for the write lock (mirrors DeleteSpawn pattern).
        try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
        catch (OperationCanceledException) { return Results.StatusCode(499); }

        // 2. Build the full set of task IDs to cancel: workstream IDs +
        //    spawn.Id + plan-doc synthetic IDs.
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);

        var planDocFiles = new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" };
        var allTaskIds = new HashSet<string>(StringComparer.Ordinal) { spawn.Id };
        foreach (var fn in planDocFiles) allTaskIds.Add($"plan-doc:{spawn.Id}:{fn}");
        foreach (var w in workstreams) allTaskIds.Add(w.Id);

        // 3. Cancel in-flight runner invocations + open queue rows + claims.
        var invocationsCancelled = 0;
        foreach (var wid in allTaskIds)
        {
            try { invocationsCancelled += await runner.CancelByTaskAsync(wid, ct); }
            catch { /* best-effort */ }
        }

        var nowOff = DateTimeOffset.UtcNow;
        var openQueueRows = await db.TaskQueue
            .Where(q => allTaskIds.Contains(q.WorkItemId)
                && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ct);
        foreach (var q in openQueueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = nowOff;
            q.LastUpdatedAt = nowOff;
        }

        var openClaims = await control.AgentClaims
            .Where(c => allTaskIds.Contains(c.WorkItemId) && c.ReleasedAt == null)
            .ToListAsync(ct);
        foreach (var c in openClaims) c.ReleasedAt = nowOff;

        // 4. Pause non-terminal workstreams — preserve prior status for resume.
        var workstreamsPaused = 0;
        foreach (var w in workstreams)
        {
            if (w.Status is "cancelled" or "complete" or "merging" or WorkstreamStatuses.Historic or "paused")
            {
                continue;
            }

            w.PausedFromStatus = w.Status;
            w.Status = "paused";
            workstreamsPaused++;
        }

        // 5. Set work_item ExecutionStatus to idle for all non-terminal items
        //    under this spawn so the orchestrator doesn't re-claim them.
        var wiIds = allTaskIds.ToList();
        var workItems = await db.WorkItems
            .Where(wi => wiIds.Contains(wi.Id)
                && wi.ExecutionStatus != ExecutionStatuses.Cancelled
                && wi.ExecutionStatus != ExecutionStatuses.Completed)
            .ToListAsync(ct);
        foreach (var wi in workItems)
        {
            wi.ExecutionStatus = ExecutionStatuses.Idle;
            wi.UpdatedAt = nowOff;
        }

        await control.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Spawn {SpawnId} paused from phase '{FromPhase}'. "
            + "Workstreams paused: {WsPaused}, invocations cancelled: {InvCancelled}, "
            + "queue rows cancelled: {QueueCancelled}, claims released: {ClaimsReleased}.",
            spawn.Id, spawn.PausedFromPhase, workstreamsPaused,
            invocationsCancelled, openQueueRows.Count, openClaims.Count);

        await hub.Clients.All.SendAsync(
            "story-spawn.changed",
            new { kind = "paused", spawn = ToContract(spawn) },
            ct);

        return Results.Ok(new
        {
            id = spawn.Id,
            status = "paused",
            pausedFromPhase = spawn.PausedFromPhase,
            workstreamsPaused,
            invocationsCancelled,
            queueRowsCancelled = openQueueRows.Count,
            claimsReleased = openClaims.Count,
        });
    }

    public async Task<IResult> ResumeSpawnAsync(string storyId, CancellationToken ct)
    {
        await actions.RequireAsync("spawn.resume", new(StoryId: storyId), ct);
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var hub = services.GetRequiredService<IHubContext<ForgeHub>>();
        var orchestrator = services.GetRequiredService<StorySpawnOrchestrator>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null)
        {
            return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);
        }

        if (spawn.Phase != StorySpawnPhases.Paused)
        {
            return Results.Problem(
                title: $"Spawn is not paused (phase='{spawn.Phase}')",
                statusCode: 409);
        }

        var fromPhase = spawn.PausedFromPhase;
        if (string.IsNullOrEmpty(fromPhase) || StorySpawnPhases.IndexOf(fromPhase) < 0)
        {
            // Defensive fallback — should never happen, but don't strand the
            // spawn if PausedFromPhase got corrupted. Default to "implement"
            // (the most common pre-pause phase).
            fromPhase = StorySpawnPhases.Implement;
        }

        var logger = loggerFactory.CreateLogger("forge.story-spawn.resume");

        // 1. Restore phase.
        spawn.Phase = fromPhase;
        spawn.PausedFromPhase = null;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;

        // 2. Restore paused workstream statuses.
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id && w.Status == "paused")
            .ToListAsync(ct);

        var workstreamsResumed = 0;
        foreach (var w in workstreams)
        {
            w.Status = string.IsNullOrEmpty(w.PausedFromStatus) ? "active" : w.PausedFromStatus;
            w.PausedFromStatus = null;
            workstreamsResumed++;
        }

        // 3. Ensure work items are claimable again (idle, not cancelled/completed).
        var wsIds = workstreams.Select(w => w.Id).ToList();
        if (wsIds.Count > 0)
        {
            var workItems = await db.WorkItems
                .Where(wi => wsIds.Contains(wi.Id)
                    && wi.ExecutionStatus != ExecutionStatuses.Cancelled
                    && wi.ExecutionStatus != ExecutionStatuses.Completed)
                .ToListAsync(ct);
            foreach (var wi in workItems)
            {
                wi.ExecutionStatus = ExecutionStatuses.Idle;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Spawn {SpawnId} resumed to phase '{Phase}'. Workstreams resumed: {WsResumed}.",
            spawn.Id, fromPhase, workstreamsResumed);

        await hub.Clients.All.SendAsync(
            "story-spawn.changed",
            new { kind = "resumed", spawn = ToContract(spawn) },
            ct);

        // 4. Kick the orchestrator so it picks up work immediately instead
        //    of waiting for the next 15 s scheduler tick.
        _ = Task.Run(async () =>
        {
            try { await orchestrator.DriveAsync(storyId, CancellationToken.None); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Post-resume DriveAsync failed for spawn {SpawnId}; scheduler will retry.", spawn.Id);
            }
        });

        return Results.Ok(new
        {
            id = spawn.Id,
            status = "resumed",
            phase = fromPhase,
            workstreamsResumed,
        });
    }
}
