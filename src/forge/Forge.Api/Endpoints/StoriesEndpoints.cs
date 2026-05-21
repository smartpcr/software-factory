// -----------------------------------------------------------------------
// <copyright file="StoriesEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

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

/// <summary>
/// forge_implementation.md Phase 28 — story-driven spawn endpoints. The
/// operator selects a story (an ADO Feature / Epic / PBI) and Forge
/// orchestrates the multi-team delivery lifecycle:
/// plan → decompose → implement → review → qa → done. Each phase advance
/// is gated on its preceding artifact PR being merged.
/// </summary>
public static class StoriesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Per-spawn lock pool to serialize concurrent ResumePendingJson cleanup
    // updates. Without this, two parallel RunResumeWithCleanupAsync tasks
    // racing on the same spawn would read the JSON, remove their respective
    // keys, and last-write-wins clobber the other's removal — leaving stale
    // markers behind. The lock is keyed by spawn id and uses a ConcurrentDictionary
    // so we don't proliferate locks for non-resuming spawns.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ResumeCleanupLocks = new();

    private static SemaphoreSlim GetResumeCleanupLock(string spawnId)
        => ResumeCleanupLocks.GetOrAdd(spawnId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Set to <c>false</c> via config <c>Forge:Stories:OrchestratorEnabled</c>
    /// to skip the auto-orchestrator and only run the gate logic. Tests
    /// covering the manual-API contract use this; production stays on.
    /// </summary>
    private static bool OrchestratorEnabled(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetService<IConfiguration>();
        var raw = cfg?["Forge:Stories:OrchestratorEnabled"];
        if (string.IsNullOrEmpty(raw)) return true;
        return bool.TryParse(raw, out var v) ? v : true;
    }

    /// <summary>
    /// When <c>true</c>, POST /spawn awaits the orchestrator drive before
    /// returning so callers can read <c>PlanPrId</c> in the response. Tests
    /// set <c>Forge:Stories:SpawnDriveInline=true</c> in the test factory.
    /// When <c>false</c> (the default), the drive is fire-and-forget — the
    /// response returns 201 immediately and the architect crew runs in the
    /// background. Real-LLM architect runs take many minutes, which would
    /// otherwise freeze the spawn-button on the UI.
    /// <see cref="SpawnDependencyScheduler"/> ticks every 15 s and will
    /// re-drive the spawn if the background task falls over.
    /// </summary>
    private static bool SpawnDriveInline(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetService<IConfiguration>();
        var raw = cfg?["Forge:Stories:SpawnDriveInline"];
        if (string.IsNullOrEmpty(raw)) return false;
        return bool.TryParse(raw, out var v) ? v : false;
    }

    public static WebApplication MapStoriesEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/stories").WithTags("Stories");

        grp.MapPost("/{storyId}/spawn", Spawn).WithName("SpawnStory");
        grp.MapDelete("/{storyId}/spawn", DeleteSpawn).WithName("DeleteStorySpawn");
        grp.MapGet("/spawns", ListSpawns).WithName("ListStorySpawns");
        grp.MapGet("/{storyId}/spawn", GetSpawn).WithName("GetStorySpawn");
        grp.MapPatch("/{storyId}/spawn/phase", AdvancePhase).WithName("AdvanceStoryPhase");
        // Phase 45 — change the operator-chosen base branch on a live spawn.
        // Triggers a rebase + retarget of every still-open PR; merged spawn
        // PRs are replayed onto the new base so downstream phases see the
        // already-checked-in story work.
        grp.MapPatch("/{storyId}/spawn/base-branch", ChangeBaseBranch).WithName("ChangeStoryBaseBranch");
        grp.MapPost("/{storyId}/spawn/retry", RetrySpawn).WithName("RetryStorySpawn");
        grp.MapPost("/{storyId}/spawn/dismiss-error", DismissError).WithName("DismissStorySpawnError");
        grp.MapPost("/{storyId}/spawn/decompose", SubmitDecomposition).WithName("SubmitStoryDecomposition");
        grp.MapPost("/{storyId}/spawn/decompose/approve", ApproveDecomposition).WithName("ApproveStoryDecomposition");
        grp.MapGet("/{storyId}/spawn/artifacts", GetArtifacts).WithName("GetStoryArtifacts");
        grp.MapGet("/{storyId}/spawn/artifacts/{key}", GetArtifactContent).WithName("GetStoryArtifactContent");
        grp.MapGet("/{storyId}/spawn/artifacts/{key}/iterations", GetArtifactIterations).WithName("GetStoryArtifactIterations");
        // Phase 53 follow-up — mirror the workstream-insight surfaces
        // (Insight I.2/I.3/I.4) onto plan-doc tasks so the Conversation
        // tab renders the same KPI strip, sparklines, prompt-view + memory
        // bar for architect docs as for engineer workstreams.
        grp.MapGet("/{storyId}/spawn/artifacts/{key}/iter-kpis", GetArtifactIterKpis).WithName("GetStoryArtifactIterKpis");
        grp.MapGet("/{storyId}/spawn/artifacts/{key}/iter-notes", GetArtifactIterNotes).WithName("GetStoryArtifactIterNotes");
        grp.MapGet("/{storyId}/spawn/artifacts/{key}/memory", GetArtifactMemory).WithName("GetStoryArtifactMemory");
        grp.MapPost("/{storyId}/spawn/artifacts/{key}/resume", ResumeArtifact).WithName("ResumeStoryArtifact");
        grp.MapPost("/{storyId}/spawn/artifacts/{key}/retry", RetryArtifact).WithName("RetryStoryArtifact");
        grp.MapPost("/{storyId}/spawn/artifacts/{key}/cancel", CancelArtifact).WithName("CancelStoryArtifact");
        grp.MapPost("/{storyId}/spawn/artifacts/{key}/accept", AcceptArtifact).WithName("AcceptStoryArtifact");
        grp.MapPost("/{storyId}/spawn/artifacts/{key}/restart", RestartArtifact).WithName("RestartStoryArtifact");
        // Phase 42 — manual force-reconcile of workstreams + workitems against the current
        // implementation-plan.md. The other triggers (per-iter commit, Resume/Restart of the
        // implementation-plan artifact, scheduler tick) cover automatic cases; this one is for
        // operator-driven recovery after a manual edit on the plan branch.
        grp.MapPost("/{storyId}/spawn/reconcile", Reconcile).WithName("ReconcileStorySpawn");
        // Phase 63 — spawn-level pause / resume. Pausing sets phase="paused",
        // kills all in-flight agents across workstreams + plan-doc synthetic
        // tasks, and sets every active workstream to Status="paused". Resume
        // restores the prior phase and workstream statuses.
        grp.MapPost("/{storyId}/spawn/pause", PauseSpawn).WithName("PauseStorySpawn");
        grp.MapPost("/{storyId}/spawn/resume", ResumeSpawn).WithName("ResumeStorySpawn");
        // Phase 50 — operator-pinned decisions that break convergence loops.
        // Pinned text is prepended to every future iter prompt for every
        // plan-doc / workstream under the spawn. Use when the evaluator keeps
        // flagging the same cross-doc contradiction iter after iter.
        grp.MapGet("/{storyId}/spawn/pins", ListDecisionPins).WithName("ListStoryDecisionPins");
        grp.MapPost("/{storyId}/spawn/pins", AddDecisionPin).WithName("AddStoryDecisionPin");
        grp.MapDelete("/{storyId}/spawn/pins/{pinId}", RemoveDecisionPin).WithName("RemoveStoryDecisionPin");
        // Phase 43 — upload reference documents before spawning.
        grp.MapPost("/attachments/upload", UploadAttachments).WithName("UploadStoryAttachments")
            .DisableAntiforgery();
        grp.MapGet("/attachments/{uploadId}/{fileName}", GetAttachmentContent).WithName("GetStoryAttachmentContent");

        return app;
    }

    public sealed record SpawnRequest(string? RepoId, IReadOnlyList<string>? UploadIds = null, string? BaseBranch = null, bool? AutoMerge = null);

    public sealed record ChangeBaseBranchRequest(string BaseBranch);

    public sealed record DecomposeRequest(IReadOnlyList<DecomposeWorkstream> Workstreams);

    public sealed record DecomposeWorkstream(
        string Title,
        string? Description,
        IReadOnlyList<string>? AcceptanceCriteria,
        IReadOnlyList<string>? Deps,
        IReadOnlyList<string>? Deliverables,
        string? Timeline,
        string? EstimatedEffort,
        // docs/forge/e2e.md Stage 1.2 — optional Category for callers that
        // want to submit a qa-e2e workstream directly. Null / omitted means
        // the row defaults to 'impl' (the legacy shape). The endpoint
        // validates against the closed set; an unknown value coerces to
        // 'impl' rather than rejecting, so old test bodies keep working
        // without modification.
        string? Category = null);

    public sealed record AdvancePhaseRequest(string TargetPhase);

    private static async Task<IResult> Spawn(
        HttpContext ctx,
        string storyId,
        [FromBody] SpawnRequest? req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator,
        IForgeDbPaths paths,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(storyId))
        {
            return Results.Problem(title: "storyId is required", statusCode: 400);
        }

        // 28.1 — duplicate spawn for the same story is rejected with 409.
        var existing = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (existing is not null)
        {
            return Results.Problem(
                title: $"Spawn already exists for story {storyId}",
                detail: $"Spawn id {existing.Id} is in phase '{existing.Phase}'.",
                statusCode: 409);
        }

        // Phase 50 defensive purge — runs ONCE at the moment a new spawn is
        // about to be created. spawn.Id is deterministic
        // (`spawn-{safeStoryId}`), so the WorkItemId space
        // (`plan-doc:spawn-{safeStoryId}:*`), worktree slug space
        // (`.worktree/plan-{key}`, `.worktree/qa-{safeStoryId}`,
        // `.worktree/stage-{n.n}-{title}`), and stash dirs
        // (`storyworkspaces/{spawn.Id}/`, `attachments/{spawn.Id}/`) are
        // PREDICTABLE. If a previous
        // DeleteSpawn ran but partially failed (HTTP RequestAborted, FK
        // exception mid-batch, race with an in-flight agent writing a
        // deliverable AFTER step 6 ran), residual rows / files survive
        // under those keys and the next re-spawn inherits them — the
        // pass-verdict guard in ProducePlanFileAsync would return the
        // stale cached doc, the iterations API would surface days-old
        // gen/eval rounds, the worktree would still have the old file
        // (observed 2026-05-11: operator deleted spawn, re-spawned, saw
        // iterations from several days ago). Purging here makes
        // "delete + re-spawn" idempotent regardless of what survived
        // the previous DeleteSpawn.
        //
        // Phase 51 (Fix 2): purge is deferred to the background drive
        // task — it issues `git push origin --delete` against every
        // candidate repo's remote, which against an ADO remote can hang
        // for up to the credential-manager timeout (and BEFORE Phase 51
        // Fix 1 added GIT_TERMINAL_PROMPT=0/GCM_INTERACTIVE=Never, would
        // hang forever waiting for an interactive UI prompt). Running
        // synchronously on the request thread blocked the operator's
        // spawn-button at "Spawning…" for days. The purge still
        // executes BEFORE the orchestrator drive (single Task.Run,
        // awaited sequentially) so plan-doc work items the orchestrator
        // is about to create cannot collide with the stale rows being
        // reaped. Inline mode (tests) keeps the synchronous path so
        // PlanPrId is observable on the 201 response.
        var safeStoryIdForPurge = StoryPaths.Safe(storyId);
        var deterministicSpawnId = $"spawn-{safeStoryIdForPurge}";

        // 28.2 — story must exist. Three sources accepted:
        //   1) ProductStoryRow (locally-authored story; description carries the
        //      architect-agent user prompt and points size the work).
        //   2) WorkItemRow by id (e.g. "WI-12345" mirroring an ADO Feature/Epic).
        //   3) WorkItemRow by numeric ADO provider ref (bare "12345" / "WI-12345").
        string title;
        string description = string.Empty;
        int points = 0;
        string? defaultRepoId = null;
        // Phase 45 — when SpawnRequest.BaseBranch is omitted, fall back to
        // the story's default. Spawn-side override still wins when set.
        string? defaultBaseBranchFromStory = null;
        // Phase 46 — auto-merge default from the story.
        bool defaultAutoMergeFromStory = false;

        var ps = await db.ProductStories.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storyId);
        string? storyAttachmentsJson = null;
        if (ps is not null)
        {
            title = ps.Name;
            description = ps.Description ?? string.Empty;
            points = ps.Points;
            storyAttachmentsJson = ps.AttachmentsJson;
            defaultBaseBranchFromStory = string.IsNullOrEmpty(ps.BaseBranch) ? null : ps.BaseBranch;
            defaultAutoMergeFromStory = ps.AutoMerge;
            // Phase 80.F — the story owns its repo; the spawn inherits it
            // (no per-spawn repo pick). A SpawnRequest.RepoId, if still sent,
            // is honoured below for backward-compat but the UI no longer sends it.
            defaultRepoId = ps.RepoId;
        }
        else
        {
            var wi = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == storyId);
            if (wi is null)
            {
                if (int.TryParse(storyId.Replace("WI-", string.Empty, StringComparison.OrdinalIgnoreCase), out var providerRef))
                {
                    wi = await db.WorkItems.AsNoTracking()
                        .FirstOrDefaultAsync(w => w.ProviderRef == providerRef);
                }
                if (wi is null)
                {
                    return Results.Problem(title: $"Story {storyId} not found", statusCode: 404);
                }
            }
            title = wi.Title;
            description = wi.DescriptionMd ?? string.Empty;
            points = wi.Points;
            defaultRepoId = wi.RepoId;
        }

        var repoId = req?.RepoId ?? defaultRepoId;
        // Phase 80.F — a spawn must bind to a repo (inherited from the story /
        // work item). Refuse rather than create a repo-less spawn the
        // orchestrator can't clone, branch, or open PRs against.
        if (string.IsNullOrEmpty(repoId))
        {
            return Results.Problem(
                title: "repo.required",
                detail: $"Story '{storyId}' has no repo bound; bind a repo to the story before spawning.",
                statusCode: 400);
        }
        var now = DateTimeOffset.UtcNow;
        var safeStoryId = StoryPaths.Safe(storyId);

        // Phase 45 — operator-chosen base branch. Defaults to "main"; non-default
        // values are validated as a single git ref name (no spaces, no leading/
        // trailing slash, no `..`) so a typo can't escape into a `git fetch`
        // command line. Created branched from main on first orchestrator drive.
        // Resolution order: explicit SpawnRequest.BaseBranch > story default > "main".
        var baseBranchInput = req?.BaseBranch?.Trim();
        var baseBranch = !string.IsNullOrEmpty(baseBranchInput)
            ? baseBranchInput!
            : (defaultBaseBranchFromStory ?? "main");
        if (!IsValidBranchName(baseBranch))
        {
            return Results.Problem(
                title: "Invalid base branch name",
                detail: $"'{baseBranch}' is not a valid git branch name.",
                statusCode: 400);
        }

        var spawn = new StorySpawnRow
        {
            Id = $"spawn-{safeStoryId}",
            StoryId = storyId,
            StoryTitle = title,
            StoryDescription = description,
            StoryPoints = points,
            RepoId = repoId,
            Phase = StorySpawnPhases.Plan,
            BaseBranch = baseBranch,
            // Auto-merge inheritance: explicit SpawnRequest override > story
            // default. The flag is honoured for any base (including "main") —
            // the operator is responsible for arming it intentionally.
            AutoMerge = req?.AutoMerge ?? defaultAutoMergeFromStory,
            // Phase 53 — sequential plan-doc state. Initialised to "[]" so
            // PlanDocStateHelpers.IsSequentialMode returns true; the
            // orchestrator's DrivePlanAsync routes to DrivePlanSequentialAsync.
            // PlanBranch is intentionally NOT set (legacy spawns from before
            // Phase 53 shipped will have PlanDocPrsJson=null and PlanBranch
            // set, which routes to DrivePlanLegacyAsync).
            PlanDocPrsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Phase 43 — link pre-uploaded attachments to the spawn.
        // Priority: explicit uploadIds from the spawn request > attachments from the ProductStory.
        if (req?.UploadIds is { Count: > 0 })
        {
            var attachments = ResolveUploadedFiles(req.UploadIds, paths);
            if (attachments.Count > 0)
            {
                spawn.AttachmentsJson = System.Text.Json.JsonSerializer.Serialize(attachments);
            }
        }
        else if (!string.IsNullOrEmpty(storyAttachmentsJson))
        {
            spawn.AttachmentsJson = storyAttachmentsJson;
        }
        db.StorySpawns.Add(spawn);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "created", spawn = ToContract(spawn) });

        // forge_implementation.md Phase 28 — kick the orchestrator so the
        // architect crew can produce the plan artifacts + open the plan PR.
        // Production runs fire-and-forget (Forge:Stories:SpawnDriveInline=false
        // in appsettings.json) because real-LLM architect runs take many
        // minutes; tests keep the inline await so PlanPrId is populated by
        // the time the response is read.
        if (OrchestratorEnabled(ctx))
        {
            if (SpawnDriveInline(ctx))
            {
                // Inline mode (tests): run purge synchronously first so the
                // orchestrator drive sees a clean state, then await the
                // drive itself. PlanPrId is observable on the 201 response.
                await PurgeResidualSpawnDataAsync(
                    control, db, paths, deterministicSpawnId, safeStoryIdForPurge,
                    loggerFactory.CreateLogger("forge.story-spawn.respawn-purge"),
                    ctx.RequestAborted);
                await orchestrator.DriveAsync(storyId, ctx.RequestAborted);
                var refreshed = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
                return Results.Created($"/api/stories/{storyId}/spawn", ToContract(refreshed));
            }

            // Background drive — must NOT use ctx.RequestAborted (the HTTP
            // request ends as soon as we return). StorySpawnOrchestrator is
            // a singleton, so capturing it here is safe across the request
            // boundary. The request-scoped `db` and per-request logger are
            // disposed once we return, so the background task creates its
            // own DI scope. Purge is awaited BEFORE the orchestrator drive
            // so plan-doc work items the orchestrator is about to create
            // cannot collide with the stale rows being reaped. Failures
            // inside DriveAsync are logged inside the orchestrator; the
            // SpawnDependencyScheduler will re-drive on the next 15 s tick.
            var spawnIdForBg = deterministicSpawnId;
            var safeStoryIdForBg = safeStoryIdForPurge;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var bgScope = scopeFactory.CreateScope();
                    var bgControl = bgScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                    var bgDb = bgScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var bgLoggerFactory = bgScope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                    var bgPurgeLogger = bgLoggerFactory.CreateLogger("forge.story-spawn.respawn-purge");
                    try
                    {
                        await PurgeResidualSpawnDataAsync(
                            bgControl, bgDb, paths, spawnIdForBg, safeStoryIdForBg,
                            bgPurgeLogger, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        bgPurgeLogger.LogWarning(ex,
                            "Background purge failed for spawn-id '{SpawnId}'; continuing to drive.",
                            spawnIdForBg);
                    }
                }
                catch { /* swallowed — scope/db creation failed, drive will retry on next tick */ }

                try { await orchestrator.DriveAsync(storyId, CancellationToken.None); }
                catch { /* swallowed — orchestrator already logs */ }
            });
        }
        return Results.Created($"/api/stories/{storyId}/spawn", ToContract(spawn));
    }

    /// <summary>
    /// Phase 50 — defensive cleanup that runs at re-spawn time to purge
    /// any residue from a prior incomplete <see cref="DeleteSpawn"/>.
    /// spawn.Id is deterministic (<c>spawn-{safeStoryId}</c>) so every
    /// downstream key (plan-doc WorkItemIds, storyworkspaces dir,
    /// attachments dir, plan/qa branch worktree paths) is predictable
    /// regardless of whether a prior spawn row exists. Best-effort
    /// throughout — anything that fails logs a warning and continues so
    /// the new spawn isn't blocked by stale state that will get fixed
    /// during normal operation anyway.
    ///
    /// <para>This is the BACKSTOP that makes re-spawn safe even when:
    /// <list type="bullet">
    /// <item>DeleteSpawn's HTTP request was aborted mid-cleanup (browser
    ///   closed, network blip, 500-error after some steps ran).</item>
    /// <item>An in-flight agent committed a new Deliverable AFTER
    ///   DeleteSpawn's step-6 cancel returned but BEFORE the agent's
    ///   own write completed — race that surfaces as "old iterations
    ///   reappear in new spawn".</item>
    /// <item>The operator never ran DeleteSpawn at all (e.g., DB
    ///   imported from a backup with spawn rows already missing).</item>
    /// </list></para>
    /// </summary>
    private static async Task PurgeResidualSpawnDataAsync(
        ControlDbContext control,
        ProductDbContext db,
        IForgeDbPaths paths,
        string deterministicSpawnId,
        string safeStoryId,
        ILogger logger,
        CancellationToken ct)
    {
        var totalRowsPurged = 0;

        // 1. Synthetic plan-doc + spawn-id work-item ids. Matches the
        //    same convention DeleteSpawn uses (StoriesEndpoints:380-387).
        var planDocFiles = new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" };
        var syntheticIds = new HashSet<string>(StringComparer.Ordinal) { deterministicSpawnId };
        foreach (var fn in planDocFiles) syntheticIds.Add($"plan-doc:{deterministicSpawnId}:{fn}");
        var idList = syntheticIds.ToList();

        try
        {
            var deliverables = await db.Deliverables.Where(d => idList.Contains(d.WorkItemId)).ToListAsync(ct);
            db.Deliverables.RemoveRange(deliverables);
            totalRowsPurged += deliverables.Count;

            var deliverableQuestions = await db.DeliverableQuestions.Where(q => idList.Contains(q.WorkItemId)).ToListAsync(ct);
            db.DeliverableQuestions.RemoveRange(deliverableQuestions);
            totalRowsPurged += deliverableQuestions.Count;

            var transcripts = await db.TaskTranscriptEvents.Where(e => idList.Contains(e.WorkItemId)).ToListAsync(ct);
            db.TaskTranscriptEvents.RemoveRange(transcripts);
            totalRowsPurged += transcripts.Count;

            var queueRows = await db.TaskQueue.Where(q => idList.Contains(q.WorkItemId)).ToListAsync(ct);
            var queueIds = queueRows.Select(q => q.Id).ToList();
            if (queueIds.Count > 0)
            {
                var queueHist = await db.TaskQueueHistory.Where(h => queueIds.Contains(h.QueueId)).ToListAsync(ct);
                db.TaskQueueHistory.RemoveRange(queueHist);
                totalRowsPurged += queueHist.Count;
            }
            db.TaskQueue.RemoveRange(queueRows);
            totalRowsPurged += queueRows.Count;

            var agentAssignments = await db.AgentAssignments.Where(a => idList.Contains(a.WorkItemId)).ToListAsync(ct);
            db.AgentAssignments.RemoveRange(agentAssignments);
            totalRowsPurged += agentAssignments.Count;

            // Phase 50 follow-up (2026-05-12) — purge cost ledger rows
            // for the synthetic plan-doc / spawn-id work items. Same
            // reason as the DeleteSpawn-side equivalent: spawn.Id is
            // deterministic, so without this the new spawn inherits the
            // prior spawn's cost rows and the four plan-doc cards
            // surface ~$1300-$2000 totals BEFORE any architect has
            // produced a single iteration. CascadeDeleteWorkstreamAsync
            // (called for orphan workstreams further down) cleans the
            // workstream-keyed rows; this only covers the synthetic
            // plan-doc + spawn ids.
            var agentUsage = await control.AgentUsage.Where(u => idList.Contains(u.TaskId)).ToListAsync(ct);
            control.AgentUsage.RemoveRange(agentUsage);
            totalRowsPurged += agentUsage.Count;

            var workItemLinks = await db.WorkItemLinks.Where(l => idList.Contains(l.FromId) || idList.Contains(l.ToId)).ToListAsync(ct);
            db.WorkItemLinks.RemoveRange(workItemLinks);
            totalRowsPurged += workItemLinks.Count;

            var buildFixPending = await db.BuildFixPending.Where(b => idList.Contains(b.WorkItemId)).ToListAsync(ct);
            db.BuildFixPending.RemoveRange(buildFixPending);
            totalRowsPurged += buildFixPending.Count;

            var workItems = await db.WorkItems.Where(w => idList.Contains(w.Id)).ToListAsync(ct);
            db.WorkItems.RemoveRange(workItems);
            totalRowsPurged += workItems.Count;

            var cards = await db.Cards.Where(c => idList.Contains(c.Id)).ToListAsync(ct);
            db.Cards.RemoveRange(cards);
            totalRowsPurged += cards.Count;

            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Phase 50 respawn purge: DB sweep failed for spawn-id '{SpawnId}'; continuing with disk + git sweeps.",
                deterministicSpawnId);
        }

        // 2. Orphan workstream rows pointing at the deterministic spawn id.
        //    Cascade-delete them so their per-workstream worktrees + DB rows
        //    are reaped via the well-tested helper. This catches the case
        //    where DeleteSpawn's step-5 partial-failed and left workstream
        //    rows around — without this, the new spawn's PlanReconciler
        //    would see "siblings" that don't belong to it.
        try
        {
            var orphanWs = await db.Workstreams.Where(w => w.StorySpawnId == deterministicSpawnId).ToListAsync(ct);
            if (orphanWs.Count > 0)
            {
                logger.LogInformation(
                    "Phase 50 respawn purge: found {N} orphan workstream rows for spawn-id '{SpawnId}'; cascade-deleting.",
                    orphanWs.Count, deterministicSpawnId);
                var git = (Forge.Api.Services.Git.IGitWorktreeService?)null;
                // CascadeDeleteWorkstreamAsync resolves git internally via its
                // own IConfiguration; passing null here forces the per-call
                // best-effort path which is fine since the workstreams may
                // already have been partially cleaned.
                foreach (var ws in orphanWs)
                {
                    try
                    {
                        await WorkstreamsEndpoints.CascadeDeleteWorkstreamAsync(
                            control, db, git!, paths, ws, ct, deleteRemoteBranch: true);
                    }
                    catch (Exception innerEx)
                    {
                        logger.LogWarning(innerEx,
                            "Phase 50 respawn purge: orphan workstream {Id} cascade-delete failed; continuing.",
                            ws.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Phase 50 respawn purge: orphan workstream sweep failed for spawn-id '{SpawnId}'.",
                deterministicSpawnId);
        }

        // 3. On-disk dirs: storyworkspaces/{spawnId}/ and attachments/{spawnId}/
        //    Mirrors DeleteSpawn step 8.
        var stateDir = paths.Folder;
        foreach (var slug in new[] { deterministicSpawnId, deterministicSpawnId.Replace('-', '_') }.Distinct())
        {
            var spawnRoot = Path.Combine(stateDir, "storyworkspaces", slug);
            if (Directory.Exists(spawnRoot))
            {
                try
                {
                    Directory.Delete(spawnRoot, recursive: true);
                    logger.LogInformation("Phase 50 respawn purge: removed orphan storyworkspaces dir {Path}.", spawnRoot);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Phase 50 respawn purge: could not delete storyworkspaces dir {Path}.", spawnRoot);
                }
            }
        }

        var attachmentsDir = Path.Combine(stateDir, "attachments", deterministicSpawnId);
        if (Directory.Exists(attachmentsDir))
        {
            try
            {
                Directory.Delete(attachmentsDir, recursive: true);
                logger.LogInformation("Phase 50 respawn purge: removed orphan attachments dir {Path}.", attachmentsDir);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Phase 50 respawn purge: could not delete attachments dir {Path}.", attachmentsDir);
            }
        }

        // 4. Disk worktree sweep + remote/local branch sweep. Scoped to
        //    branch/worktree patterns derived from the safe story id —
        //    catches plan/qa branches + every workstream branch from
        //    prior decomposition cycles. Runs against EVERY repo
        //    referenced by any ProductStoryRow row sharing this storyId
        //    (typically 1, but a story can be re-pointed at a different
        //    repo on respawn). Pass `cleanedBranches` so the new spawn
        //    code doesn't try to push to the just-deleted branches.
        try
        {
            // Resolve the candidate repo via the story → product → repos
            // path. For locally-authored stories ProductStoryRow doesn't
            // bind a specific repo, so we scan every repo whose product
            // matches. Limited to repos with a non-null CloneRoot.
            var candidateRepos = await db.Repos.AsNoTracking()
                .Where(r => r.CloneRoot != null && r.CloneRoot != string.Empty)
                .ToListAsync(ct);

            foreach (var repo in candidateRepos)
            {
                if (!Directory.Exists(repo.CloneRoot!)) continue;

                // 4a. Worktree dirs whose name contains the safe story id.
                //     Catches `spawn-story-{safeStoryId}-*`,
                //     `spawn-ws-{safeStoryId}-*`,
                //     `stage-*-{safeStoryId}-*`, etc.
                var wtRoot = Path.Combine(repo.CloneRoot!, ".worktree");
                if (Directory.Exists(wtRoot))
                {
                    foreach (var dir in Directory.EnumerateDirectories(wtRoot))
                    {
                        var name = Path.GetFileName(dir);
                        if (!name.Contains(safeStoryId, StringComparison.OrdinalIgnoreCase)) continue;
                        await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot!, "worktree", "remove", "--force", dir);
                        if (Directory.Exists(dir))
                        {
                            try { Directory.Delete(dir, recursive: true); }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex,
                                    "Phase 50 respawn purge: could not raw-delete worktree {Path} for story '{Story}'.",
                                    dir, safeStoryId);
                            }
                        }
                    }
                    await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot!, "worktree", "prune");
                }

                // 4b. Local branches matching story/{X}/* OR ws/{X}/*.
                var (lbCode, lbOut, _) = await WorkstreamsEndpoints.RunGitCaptureAsync(
                    repo.CloneRoot!, "branch", "--list",
                    $"story/{safeStoryId}/*", $"ws/{safeStoryId}/*");
                if (lbCode == 0 && !string.IsNullOrWhiteSpace(lbOut))
                {
                    foreach (var line in lbOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var branch = line.TrimStart('*', ' ', '+', '\t').Trim();
                        if (string.IsNullOrEmpty(branch)) continue;
                        await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot!, "branch", "-D", branch);
                    }
                }

                // 4c. Remote branches matching the same patterns. Best-effort —
                //     auth failures here just mean the orchestrator's
                //     EnsureBranchFreshAsync (Phase 50 first-drive auto-heal)
                //     will force-reset the tip on the next drive.
                var (rbCode, rbOut, _) = await WorkstreamsEndpoints.RunGitCaptureAsync(
                    repo.CloneRoot!, "ls-remote", "--heads", "origin",
                    $"refs/heads/story/{safeStoryId}/*", $"refs/heads/ws/{safeStoryId}/*");
                if (rbCode == 0 && !string.IsNullOrWhiteSpace(rbOut))
                {
                    foreach (var line in rbOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        var branch = parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                            ? parts[1]["refs/heads/".Length..].Trim()
                            : parts[1].Trim();
                        if (string.IsNullOrEmpty(branch)) continue;
                        await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot!, "push", "origin", "--delete", branch);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Phase 50 respawn purge: disk + git sweep failed for story '{Story}'.",
                safeStoryId);
        }

        // 5. Orphan PR rows in DB whose branch matches the storyId pattern.
        //    DeleteSpawn collects PRs by id (off spawn.PlanPrId / QaPrId /
        //    workstream.PrsJson). When those pointers were stale (observed
        //    2026-05-11: spawn.PlanPrId was 'PR #15623333' but the actual
        //    plan-branch PR row was 'PR #15623095' — id never updated when
        //    the PR was re-opened), the actual PR row outlived the spawn.
        try
        {
            var orphanPrs = await db.Prs
                .Where(p => p.Branch.StartsWith($"story/{safeStoryId}/")
                         || p.Branch.StartsWith($"ws/{safeStoryId}/"))
                .ToListAsync(ct);
            if (orphanPrs.Count > 0)
            {
                logger.LogInformation(
                    "Phase 50 respawn purge: removing {N} orphan PR rows whose Branch matches story '{Story}'.",
                    orphanPrs.Count, safeStoryId);
                var prIds = orphanPrs.Select(p => p.Id).ToList();
                var comments = await db.PrComments.Where(c => prIds.Contains(c.PrId)).ToListAsync(ct);
                var commentIds = comments.Select(c => c.Id).ToList();
                if (commentIds.Count > 0)
                {
                    var hist = await db.PrCommentHistory.Where(h => commentIds.Contains(h.CommentId)).ToListAsync(ct);
                    db.PrCommentHistory.RemoveRange(hist);
                    db.PrComments.RemoveRange(comments);
                }
                var builds = await db.Builds.Where(b => prIds.Contains(b.PrId)).ToListAsync(ct);
                if (builds.Count > 0)
                {
                    var buildIds = builds.Select(b => b.Id).ToList();
                    var stages = await db.BuildStages.Where(s => buildIds.Contains(s.BuildId)).ToListAsync(ct);
                    var stageIds = stages.Select(s => s.Id).ToList();
                    if (stageIds.Count > 0)
                    {
                        var stageLogs = await db.BuildStageLogs.Where(l => stageIds.Contains(l.StageId)).ToListAsync(ct);
                        db.BuildStageLogs.RemoveRange(stageLogs);
                        var failurePoints = await db.BuildFailurePoints.Where(fp => stageIds.Contains(fp.StageId)).ToListAsync(ct);
                        db.BuildFailurePoints.RemoveRange(failurePoints);
                    }
                    var artifacts = await db.BuildArtifacts.Where(a => buildIds.Contains(a.BuildId)).ToListAsync(ct);
                    var artifactIds = artifacts.Select(a => a.Id).ToList();
                    if (artifactIds.Count > 0)
                    {
                        var artifactFiles = await db.BuildArtifactFiles.Where(f => artifactIds.Contains(f.ArtifactId)).ToListAsync(ct);
                        db.BuildArtifactFiles.RemoveRange(artifactFiles);
                    }
                    var annotations = await db.BuildAnnotations.Where(a => buildIds.Contains(a.BuildId)).ToListAsync(ct);
                    db.BuildAnnotations.RemoveRange(annotations);
                    db.BuildArtifacts.RemoveRange(artifacts);
                    db.BuildStages.RemoveRange(stages);
                    db.Builds.RemoveRange(builds);
                }
                db.Prs.RemoveRange(orphanPrs);
                await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Phase 50 respawn purge: orphan PR sweep failed for story '{Story}'.",
                safeStoryId);
        }

        if (totalRowsPurged > 0)
        {
            logger.LogInformation(
                "Phase 50 respawn purge complete for spawn-id '{SpawnId}': {N} residual DB rows reaped before new spawn creation.",
                deterministicSpawnId, totalRowsPurged);
        }
    }

    /// <summary>
    /// Phase 50 — DELETE /api/stories/{storyId}/spawn — wipe a story spawn
    /// and every artifact it produced. Used by the operator's "delete and
    /// restart from scratch" button on the swarm story-index. Performs in
    /// order:
    /// <list type="number">
    ///   <item>Snapshot every workItem id under the spawn (children + the
    ///         four plan-doc synthetic ids + the spawn-id used by
    ///         decompose/qa-features) and every PR id (plan PR, QA PR,
    ///         each workstream's <c>PrsJson</c> entries which include
    ///         step-def PRs).</item>
    ///   <item>Cancel in-flight agent invocations, queue rows, and active
    ///         agent claims for that id set so no zombie task survives the
    ///         delete (rubber-duck pass identified plan-doc / claim
    ///         orphans).</item>
    ///   <item>Refuse the base-branch deletion when another live spawn
    ///         shares it (409 — operator must re-call without the flag).</item>
    ///   <item>Close pending PRs upstream (best-effort), then remove every
    ///         PR row + comments + build-chain rows from the local DB
    ///         regardless of merged/abandoned/open state — the user's
    ///         intent is "restart from scratch", not "preserve history".</item>
    ///   <item>Cascade-delete each child workstream via
    ///         <see cref="WorkstreamsEndpoints.CascadeDeleteWorkstreamAsync"/>
    ///         with <c>deleteRemoteBranch=true</c> so the worktree mount,
    ///         local branch, and remote branch are all reaped.</item>
    ///   <item>Cascade-delete the synthetic plan-doc / spawn-id rows
    ///         (deliverables, transcripts, queue, agent assignments, work
    ///         items).</item>
    ///   <item>Remove plan / qa worktrees + branches (local + remote),
    ///         then PR-derived branches (covers step-def branches that
    ///         don't live on the workstream row).</item>
    ///   <item>Wipe on-disk dirs: <c>storyworkspaces/{spawnId}/</c> and
    ///         <c>attachments/{spawnId}/</c>.</item>
    ///   <item>Optionally delete the base branch (local + remote) when the
    ///         operator confirms via <paramref name="deleteBaseBranch"/>
    ///         AND <c>spawn.BaseBranch != "main"</c>.</item>
    ///   <item>Remove the <see cref="StorySpawnRow"/> itself (the
    ///         scheduler tick re-reads spawns each pass and silently
    ///         skips the now-missing row).</item>
    ///   <item>Notify SignalR (<c>story-spawn.changed</c> / kind=deleted)
    ///         so the UI strips the row immediately.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult> DeleteSpawn(
        HttpContext ctx,
        string storyId,
        [FromQuery] bool? deleteBaseBranch,
        ControlDbContext control,
        ProductDbContext db,
        IGitWorktreeService git,
        IForgeDbPaths paths,
        IHubContext<ForgeHub> hub,
        IAgentRunner runner,
        IEnumerable<IForgeProvider> providers,
        ILoggerFactory loggerFactory)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(storyId))
        {
            return Results.Problem(title: "storyId is required", statusCode: 400);
        }

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ctx.RequestAborted);
        if (spawn is null)
        {
            return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);
        }

        var logger = loggerFactory.CreateLogger("forge.story-spawn.delete");

        // 2026-05-13 fix — flip phase to "deleting" before any other write so
        // the orchestrator's RunOneStepAsync switch falls through and stops
        // claiming new agents while the delete is in flight. Without this,
        // the cascade wipe of deliverables un-passes Phase 48's plan-doc
        // gate, which regresses the phase to Plan, which re-claims 4 plan-
        // doc agents (architect / tech-spec / impl-plan / e2e) — observed
        // on code-intelligence:AGENT-MEMORY: 5 active claims appeared
        // within 2 s of the first SaveChanges and held the SQLite write
        // lock long enough for the later cleanup SaveChanges to 500 with
        // "An error occurred while saving the entity changes".
        //
        // Also persist any leftover Phase value into PrevPhase via the
        // LastError column so the operator can correlate post-mortem (no
        // schema change — repurposing the existing free-form field).
        if (!string.Equals(spawn.Phase, StorySpawnPhases.Deleting, StringComparison.Ordinal))
        {
            spawn.LastError =
                $"delete-started: prevPhase={spawn.Phase} at {DateTimeOffset.UtcNow:O}";
            spawn.Phase = StorySpawnPhases.Deleting;
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveChangesWithBusyRetryAsync(
                control, db,
                logger,
                "delete-start-phase",
                ctx.RequestAborted);

            // Brief drain: any tick already in-flight finishes its
            // SaveChanges before we contend for the write lock. 1 s is
            // longer than the orchestrator's longest single write (worst
            // case is a transcript-event append) but short enough that
            // the operator doesn't notice. Skipped on cancellation.
            try { await Task.Delay(TimeSpan.FromSeconds(1), ctx.RequestAborted); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
        }

        var counts = new Dictionary<string, int>();
        // Phase 50 — collect remote-delete failures into a structured list
        // so the response payload can surface them to the operator. The
        // legacy code stored them as `counts["remoteDeleteFailed:{branch}"]`
        // which the UI didn't read, and the response's `baseBranchDeleted`
        // boolean was set off `baseBranchToDelete != null` regardless of
        // whether the push --delete actually succeeded — i.e., the toast
        // claimed "base branch removed" even when origin still carried it.
        // Observed 2026-05-11 on local:HIGH-MEM-CONSUMPTION: re-spawn
        // inherited stale plan-doc commits because the orphan remote
        // branch was never reported as a delete failure.
        var remoteDeleteFailures = new List<string>();
        var alsoDeleteBase = deleteBaseBranch == true;

        // ── 1. Snapshot every relevant id BEFORE we start mutating ──────
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ctx.RequestAborted);
        var workstreamIds = workstreams.Select(w => w.Id).ToList();
        counts["workstreams"] = workstreams.Count;

        // Plan-doc work item ids — see CrewStoryArtifactStrategy:494 where
        // these synthetic ids are minted. Cleaning them here keeps the
        // task-execution surface (deliverables, queue, transcripts) free
        // of orphans pointing at a now-deleted spawn.
        var planDocFiles = new[]
        {
            "architecture.md",
            "tech-spec.md",
            "implementation-plan.md",
            "e2e-scenarios.md",
        };
        var syntheticIds = new HashSet<string>(StringComparer.Ordinal)
        {
            spawn.Id,
        };
        foreach (var fn in planDocFiles)
        {
            syntheticIds.Add($"plan-doc:{spawn.Id}:{fn}");
        }

        // Combined id set used for cancel + cascade cleanup. The order
        // matters: workstream ids go first so the existing
        // CascadeDeleteWorkstreamAsync owns those; the synthetic loop
        // below picks up everything else.
        var allWorkItemIds = new HashSet<string>(workstreamIds, StringComparer.Ordinal);
        foreach (var sid in syntheticIds) allWorkItemIds.Add(sid);

        // PR ids referenced by the spawn — plan PR, QA PR, every
        // workstream's PrsJson list (which on a fully-decomposed spawn
        // includes both impl and step-def PR ids — see
        // StorySpawnOrchestrator stepDefMerged annotations).
        var prIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(spawn.PlanPrId)) prIds.Add(spawn.PlanPrId);
        if (!string.IsNullOrEmpty(spawn.QaPrId)) prIds.Add(spawn.QaPrId);
        // Phase 53 — sequential plan-doc PRs (one per doc on the spawn).
        foreach (var pdoc in PlanDocStateHelpers.GetPlanDocStates(spawn))
        {
            if (!string.IsNullOrEmpty(pdoc.PrId)) prIds.Add(pdoc.PrId!);
        }
        foreach (var w in workstreams)
        {
            foreach (var p in WorkstreamsEndpoints.ParseStringList(w.PrsJson))
            {
                if (!string.IsNullOrEmpty(p)) prIds.Add(p);
            }
        }

        // Load actual PR rows up front so we can collect their branch
        // names for the post-cleanup branch sweep (covers step-def PR
        // branches like ws/{storyId}/{slug}-steps which aren't stored
        // on WorkstreamRow.Branch).
        var prRows = prIds.Count == 0
            ? new List<PrRow>()
            : await db.Prs.Where(p => prIds.Contains(p.Id)).ToListAsync(ctx.RequestAborted);
        counts["prs"] = prRows.Count;

        // ── 2. Base-branch share check (early — refuses BEFORE any
        // destructive work so the operator can re-call safely) ──────────
        var baseBranchToDelete = (string?)null;
        if (alsoDeleteBase
            && !string.IsNullOrEmpty(spawn.BaseBranch)
            && !string.Equals(spawn.BaseBranch, "main", StringComparison.OrdinalIgnoreCase))
        {
            var sharedCount = await db.StorySpawns
                .Where(s => s.Id != spawn.Id && s.BaseBranch == spawn.BaseBranch)
                .CountAsync(ctx.RequestAborted);
            if (sharedCount > 0)
            {
                return Results.Problem(
                    title: "Base branch is shared",
                    detail: $"Base branch '{spawn.BaseBranch}' is also used by {sharedCount} other story spawn(s). Re-call without deleteBaseBranch=true, or delete those spawns first.",
                    statusCode: 409);
            }
            baseBranchToDelete = spawn.BaseBranch;
        }

        // ── 3. Cancel in-flight work — runner, queue, claims ────────────
        var invocationsCancelled = 0;
        foreach (var wid in allWorkItemIds)
        {
            try { invocationsCancelled += await runner.CancelByTaskAsync(wid, ctx.RequestAborted); }
            catch { /* best-effort */ }
        }
        counts["invocationsCancelled"] = invocationsCancelled;

        var openQueueRows = await db.TaskQueue
            .Where(q => allWorkItemIds.Contains(q.WorkItemId)
                && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ctx.RequestAborted);
        var nowOff = DateTimeOffset.UtcNow;
        foreach (var q in openQueueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = nowOff;
            q.LastUpdatedAt = nowOff;
        }
        counts["queueRowsCancelled"] = openQueueRows.Count;

        // Release any active agent claims (Released==null means the agent
        // is still "busy" on this work). Without this, the agent capacity
        // is stranded until the 24 h abandoned-recovery sweep.
        var openClaims = await control.AgentClaims
            .Where(c => allWorkItemIds.Contains(c.WorkItemId) && c.ReleasedAt == null)
            .ToListAsync(ctx.RequestAborted);
        foreach (var c in openClaims) c.ReleasedAt = nowOff;
        counts["agentClaimsReleased"] = openClaims.Count;

        await SaveChangesWithBusyRetryAsync(
            control, db,
            logger,
            "cancel-inflight-work",
            ctx.RequestAborted);

        // ── 4. Close + remove PRs ───────────────────────────────────────
        var prsClosedUpstream = 0;
        var providerList = providers.ToList();
        foreach (var pr in prRows)
        {
            // Close upstream first, but only when the row is still open —
            // calling close on an already-merged PR is wasteful and noisy.
            var isPending = !string.Equals(pr.Col, "merged", StringComparison.Ordinal)
                && !string.Equals(pr.Col, "abandoned", StringComparison.Ordinal);
            if (isPending && !string.IsNullOrEmpty(pr.Provider))
            {
                try
                {
                    var provider = providerList.FirstOrDefault(p =>
                        string.Equals(p.Kind, pr.Provider, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);
                    if (provider is not null)
                    {
                        await provider.ClosePullRequestAsync(pr.Id, ProviderCallContext.Anonymous, ctx.RequestAborted);
                        prsClosedUpstream++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "DeleteSpawn: best-effort upstream close failed for PR {PrId}; continuing local cleanup.",
                        pr.Id);
                }
            }

            // Wipe local PR comment chain (no FK cascade — see
            // ProductDbContext OnModelCreating).
            var comments = await db.PrComments.Where(c => c.PrId == pr.Id).ToListAsync(ctx.RequestAborted);
            var commentIds = comments.Select(c => c.Id).ToList();
            if (commentIds.Count > 0)
            {
                var hist = await db.PrCommentHistory.Where(h => commentIds.Contains(h.CommentId)).ToListAsync(ctx.RequestAborted);
                db.PrCommentHistory.RemoveRange(hist);
                db.PrComments.RemoveRange(comments);
            }

            // Wipe build chain rows referencing this PR. Build artifacts
            // are diagnostic data; once the parent PR is gone they're
            // dangling and serve no purpose.
            var builds = await db.Builds.Where(b => b.PrId == pr.Id).ToListAsync(ctx.RequestAborted);
            if (builds.Count > 0)
            {
                var buildIds = builds.Select(b => b.Id).ToList();
                var stages = await db.BuildStages.Where(s => buildIds.Contains(s.BuildId)).ToListAsync(ctx.RequestAborted);
                var stageIds = stages.Select(s => s.Id).ToList();
                if (stageIds.Count > 0)
                {
                    var stageLogs = await db.BuildStageLogs.Where(l => stageIds.Contains(l.StageId)).ToListAsync(ctx.RequestAborted);
                    db.BuildStageLogs.RemoveRange(stageLogs);
                    var failurePoints = await db.BuildFailurePoints.Where(fp => stageIds.Contains(fp.StageId)).ToListAsync(ctx.RequestAborted);
                    db.BuildFailurePoints.RemoveRange(failurePoints);
                }
                var artifacts = await db.BuildArtifacts.Where(a => buildIds.Contains(a.BuildId)).ToListAsync(ctx.RequestAborted);
                var artifactIds = artifacts.Select(a => a.Id).ToList();
                if (artifactIds.Count > 0)
                {
                    var artifactFiles = await db.BuildArtifactFiles.Where(f => artifactIds.Contains(f.ArtifactId)).ToListAsync(ctx.RequestAborted);
                    db.BuildArtifactFiles.RemoveRange(artifactFiles);
                }
                var annotations = await db.BuildAnnotations.Where(a => buildIds.Contains(a.BuildId)).ToListAsync(ctx.RequestAborted);
                db.BuildAnnotations.RemoveRange(annotations);
                db.BuildArtifacts.RemoveRange(artifacts);
                db.BuildStages.RemoveRange(stages);
                db.Builds.RemoveRange(builds);
            }

            db.Prs.Remove(pr);
        }
        counts["prsClosedUpstream"] = prsClosedUpstream;

        await SaveChangesWithBusyRetryAsync(
            control, db,
            logger,
            "remove-pr-chain",
            ctx.RequestAborted);

        // ── 5. Cascade-delete each child workstream (reuses the
        // existing well-tested helper from WorkstreamsEndpoints with
        // remote-branch deletion enabled) ───────────────────────────────
        var wsCleanupCounts = new Dictionary<string, int>();
        foreach (var ws in workstreams)
        {
            try
            {
                var rep = await WorkstreamsEndpoints.CascadeDeleteWorkstreamAsync(
                    control, db, git, paths, ws, ctx.RequestAborted, deleteRemoteBranch: true);
                if (rep is { } and not null)
                {
                    // Best-effort merge of per-workstream counts into a sum.
                    foreach (var prop in rep.GetType().GetProperties())
                    {
                        // Skip — CascadeDeleteWorkstreamAsync returns an
                        // anonymous object whose props aren't direct ints.
                        // The interesting "counts" sub-dict is what we want;
                        // shape is { id, status, counts: Dictionary<string,int> }.
                        if (prop.Name != "counts") continue;
                        if (prop.GetValue(rep) is IDictionary<string, int> sub)
                        {
                            foreach (var (k, v) in sub)
                            {
                                wsCleanupCounts[k] = wsCleanupCounts.GetValueOrDefault(k) + v;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "DeleteSpawn: cascade delete failed for workstream {WsId}; continuing.", ws.Id);
            }
        }
        foreach (var (k, v) in wsCleanupCounts) counts[$"ws.{k}"] = v;

        // ── 6. Cascade-delete synthetic plan-doc / spawn-id rows ────────
        if (syntheticIds.Count > 0)
        {
            var ids = syntheticIds.ToList();

            var deliverables = await db.Deliverables.Where(d => ids.Contains(d.WorkItemId)).ToListAsync(ctx.RequestAborted);
            db.Deliverables.RemoveRange(deliverables);
            counts["synthetic.deliverables"] = deliverables.Count;

            var deliverableQuestions = await db.DeliverableQuestions.Where(q => ids.Contains(q.WorkItemId)).ToListAsync(ctx.RequestAborted);
            db.DeliverableQuestions.RemoveRange(deliverableQuestions);
            counts["synthetic.deliverableQuestions"] = deliverableQuestions.Count;

            var transcript = await db.TaskTranscriptEvents.Where(e => ids.Contains(e.WorkItemId)).ToListAsync(ctx.RequestAborted);
            db.TaskTranscriptEvents.RemoveRange(transcript);
            counts["synthetic.transcriptEvents"] = transcript.Count;

            var queueRows = await db.TaskQueue.Where(q => ids.Contains(q.WorkItemId)).ToListAsync(ctx.RequestAborted);
            var queueRowIds = queueRows.Select(q => q.Id).ToList();
            if (queueRowIds.Count > 0)
            {
                var queueHistory = await db.TaskQueueHistory.Where(h => queueRowIds.Contains(h.QueueId)).ToListAsync(ctx.RequestAborted);
                db.TaskQueueHistory.RemoveRange(queueHistory);
                counts["synthetic.queueHistory"] = queueHistory.Count;
            }
            db.TaskQueue.RemoveRange(queueRows);
            counts["synthetic.queueRows"] = queueRows.Count;

            var agentAssignments = await db.AgentAssignments.Where(a => ids.Contains(a.WorkItemId)).ToListAsync(ctx.RequestAborted);
            db.AgentAssignments.RemoveRange(agentAssignments);
            counts["synthetic.agentAssignments"] = agentAssignments.Count;

            // Phase 52 follow-up (2026-05-13) — pair-attempt audit rows
            // for synthetic plan-doc / spawn-id work items. Same rationale
            // as the workstream-side cleanup in
            // CascadeDeleteWorkstreamAsync: leaving these orphaned to a
            // dead WorkItemId inflates the involved agents' Phase 14.16
            // SloppyRate after every operator re-spawn cycle.
            var pairAssignments = await db.AgentPairAssignments
                .Where(p => ids.Contains(p.WorkItemId)).ToListAsync(ctx.RequestAborted);
            db.AgentPairAssignments.RemoveRange(pairAssignments);
            counts["synthetic.pairAssignments"] = pairAssignments.Count;

            // Phase 52 follow-up — agent_claims for synthetic ids.
            // DeleteSpawn releases open claims (ReleasedAt = now) at
            // step 3 above but never deletes the rows; this completes
            // the wipe so the agent-capacity sweep doesn't keep
            // re-counting them.
            var agentClaims = await control.AgentClaims
                .Where(c => ids.Contains(c.WorkItemId)).ToListAsync(ctx.RequestAborted);
            control.AgentClaims.RemoveRange(agentClaims);
            counts["synthetic.agentClaims"] = agentClaims.Count;

            // Phase 50 follow-up (2026-05-12) — cost ledger rows for the
            // synthetic plan-doc / spawn-id work items. TaskId on
            // agent_usage equals the work-item id (DeliverableLoop emits
            // each gen/eval invocation with TaskId = workItemId).
            // Without this purge, re-spawning the same story (spawn.Id
            // is deterministic, plan-doc work-item ids are derived from
            // it) inherits the old cost ledger and DOC cards open
            // showing stale dollar totals before any LLM has run on the
            // new spawn (operator-reported: re-spawned story showed
            // ~$1300-$2000 per plan-doc card with PENDING status).
            var agentUsage = await control.AgentUsage.Where(u => ids.Contains(u.TaskId)).ToListAsync(ctx.RequestAborted);
            control.AgentUsage.RemoveRange(agentUsage);
            counts["synthetic.agentUsage"] = agentUsage.Count;

            var workItemLinks = await db.WorkItemLinks.Where(l => ids.Contains(l.FromId) || ids.Contains(l.ToId)).ToListAsync(ctx.RequestAborted);
            db.WorkItemLinks.RemoveRange(workItemLinks);
            counts["synthetic.workItemLinks"] = workItemLinks.Count;

            var buildFixPending = await db.BuildFixPending.Where(b => ids.Contains(b.WorkItemId)).ToListAsync(ctx.RequestAborted);
            db.BuildFixPending.RemoveRange(buildFixPending);
            counts["synthetic.buildFixPending"] = buildFixPending.Count;

            var workItems = await db.WorkItems.Where(w => ids.Contains(w.Id)).ToListAsync(ctx.RequestAborted);
            db.WorkItems.RemoveRange(workItems);
            counts["synthetic.workItems"] = workItems.Count;

            var cards = await db.Cards.Where(c => ids.Contains(c.Id)).ToListAsync(ctx.RequestAborted);
            db.Cards.RemoveRange(cards);
            counts["synthetic.cards"] = cards.Count;
        }

        // Also remove the spawn's STORY work-item if it was synthesized
        // by the orchestrator — story id is the parent, not in
        // syntheticIds. Safe to remove because cascade deletes above
        // already removed every child work-item.
        var storyWi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == spawn.StoryId, ctx.RequestAborted);
        if (storyWi is not null && string.Equals(storyWi.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            db.WorkItems.Remove(storyWi);
            counts["storyWorkItemRemoved"] = 1;
        }

        await SaveChangesWithBusyRetryAsync(
            control, db,
            logger,
            "remove-synthetic-rows",
            ctx.RequestAborted);

        // ── 7. Branch + worktree cleanup for plan / qa / PR-derived
        // branches. Workstream branches were already wiped inside
        // CascadeDeleteWorkstreamAsync above. ────────────────────────────
        var repo = string.IsNullOrEmpty(spawn.RepoId)
            ? null
            : await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ctx.RequestAborted);

        // Track branches we've already cleaned to avoid duplicate work
        // (PR branch may equal plan branch on partial failures, etc.).
        var cleanedBranches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ws in workstreams)
        {
            if (!string.IsNullOrEmpty(ws.Branch)) cleanedBranches.Add(ws.Branch);
        }

        var storyGitOps = ctx.RequestServices.GetService<IStoryGitOps>();

        // Cleanup strategy:
        // (a) IStoryGitOps.RemoveWorktreeAsync — uses `git worktree list`
        //     to find whatever path is registered against the branch, so
        //     this handles operator-customised layouts as well.
        // (b) Local branch ref + remote-branch delete.
        async Task CleanBranchAsync(string? branch)
        {
            if (string.IsNullOrEmpty(branch) || !cleanedBranches.Add(branch)) return;
            if (storyGitOps is not null)
            {
                try { await storyGitOps.RemoveWorktreeAsync(repo, branch, ctx.RequestAborted); }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "DeleteSpawn: RemoveWorktreeAsync failed for {Branch}; continuing.", branch);
                }
            }
            if (repo is not null && !string.IsNullOrEmpty(repo.CloneRoot) && Directory.Exists(repo.CloneRoot))
            {
                await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot, "worktree", "prune");
                await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot, "branch", "-D", branch);
                var pushRc = await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot, "push", "origin", "--delete", branch);
                if (pushRc != 0)
                {
                    // Surface the failure so the operator knows the remote
                    // copy survived (typical causes: branch never pushed,
                    // branch protection rejected the delete, no remote auth).
                    logger.LogWarning(
                        "DeleteSpawn: `git push origin --delete {Branch}` returned exit code {Code}; remote may still hold the branch.",
                        branch, pushRc);
                    counts[$"remoteDeleteFailed:{branch}"] = pushRc;
                    remoteDeleteFailures.Add(branch);
                }
            }
        }

        await CleanBranchAsync(spawn.PlanBranch);
        await CleanBranchAsync(spawn.QaBranch);
        // Phase 53 — clean every sequential plan-doc branch as well.
        foreach (var pdoc in PlanDocStateHelpers.GetPlanDocStates(spawn))
        {
            await CleanBranchAsync(pdoc.Branch);
        }
        foreach (var pr in prRows)
        {
            await CleanBranchAsync(pr.Branch);
        }

        // Clean the base-branch merge worktree. `RealStoryGitOps.MergeAsync`
        // materialises `.worktree/merge-{baseBranch}` when no other worktree
        // is registered on the base. The main worktree (cloneRoot) is
        // intentionally NOT touched — it isn't owned by a single spawn and
        // would wipe the whole repo (observed 2026-05-13 on
        // code-intelligence:AGENT-MEMORY: a prior `RemoveWorktreeAsync(repo,
        // BaseBranch)` call deleted `E:\forge\workflow` including `.git`).
        if (!string.IsNullOrEmpty(spawn.BaseBranch)
            && repo is not null
            && !string.IsNullOrEmpty(repo.CloneRoot)
            && Directory.Exists(repo.CloneRoot))
        {
            var mergeWtPath = Path.Combine(
                repo.CloneRoot, ".worktree",
                $"merge-{spawn.BaseBranch.Replace('/', '-')}");
            if (Directory.Exists(mergeWtPath))
            {
                await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot, "worktree", "remove", "--force", mergeWtPath);
                if (Directory.Exists(mergeWtPath))
                {
                    try { Directory.Delete(mergeWtPath, recursive: true); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "DeleteSpawn: could not raw-delete merge worktree dir {Path}; will be pruned on next claim.",
                            mergeWtPath);
                    }
                }
                await WorkstreamsEndpoints.RunGitQuietAsync(repo.CloneRoot, "worktree", "prune");
            }
        }

        counts["branchesCleaned"] = cleanedBranches.Count;

        // ── 8. On-disk dirs ─────────────────────────────────────────────
        var stateDir = paths.Folder;
        var spawnSlug = spawn.Id;
        foreach (var slug in new[] { spawnSlug, spawnSlug.Replace('-', '_') }.Distinct())
        {
            var spawnRoot = Path.Combine(stateDir, "storyworkspaces", slug);
            if (Directory.Exists(spawnRoot))
            {
                try
                {
                    Directory.Delete(spawnRoot, recursive: true);
                    counts["workspacesRemoved"] = counts.GetValueOrDefault("workspacesRemoved") + 1;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "DeleteSpawn: could not delete workspace dir {Path}; continuing.", spawnRoot);
                }
            }
        }

        var attachmentsDir = Path.Combine(stateDir, "attachments", spawn.Id);
        if (Directory.Exists(attachmentsDir))
        {
            try
            {
                Directory.Delete(attachmentsDir, recursive: true);
                counts["attachmentsRemoved"] = 1;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "DeleteSpawn: could not delete attachments dir {Path}; continuing.", attachmentsDir);
            }
        }

        // ── 9. Optional base-branch REF deletion ────────────────────────
        if (!string.IsNullOrEmpty(baseBranchToDelete) && repo is not null
            && !string.IsNullOrEmpty(repo.CloneRoot) && Directory.Exists(repo.CloneRoot))
        {
            // Phase 51 Fix 6a — base-branch worktree dirs are now cleaned
            // up unconditionally above (section 7's tail). This section
            // is purely the optional ref-delete that runs only when the
            // operator confirms via `deleteBase=true` AND the base isn't
            // "main".
            //
            // `git branch -D` against the currently-checked-out branch
            // fails — the cloneRoot's HEAD is usually `main`, so deleting
            // a non-main base branch is safe. For a base that IS the
            // cloneRoot's HEAD, the local delete is a no-op and the
            // remote push --delete is the meaningful work.
            var localRc = await WorkstreamsEndpoints.RunGitQuietAsync(
                repo.CloneRoot, "branch", "-D", baseBranchToDelete);
            var remoteRc = await WorkstreamsEndpoints.RunGitQuietAsync(
                repo.CloneRoot, "push", "origin", "--delete", baseBranchToDelete);
            counts["baseBranchRemoved"] = (remoteRc == 0) ? 1 : 0;
            if (remoteRc != 0)
            {
                // Most common cause: the base branch is protected on the
                // remote, OR the operator's git auth can't push to it.
                // Surface this in the response so the UI toast can tell
                // the operator the spawn was wiped locally but the remote
                // base branch still exists.
                logger.LogWarning(
                    "DeleteSpawn: `git push origin --delete {Branch}` returned exit code {Code}; remote base branch survived.",
                    baseBranchToDelete, remoteRc);
                counts[$"remoteDeleteFailed:{baseBranchToDelete}"] = remoteRc;
                remoteDeleteFailures.Add(baseBranchToDelete);
            }
            if (localRc != 0)
            {
                logger.LogDebug(
                    "DeleteSpawn: local `git branch -D {Branch}` returned exit code {Code} (typical when branch is HEAD).",
                    baseBranchToDelete, localRc);
            }
        }

        // ── 10. Delete the spawn row itself ─────────────────────────────
        db.StorySpawns.Remove(spawn);
        await SaveChangesWithBusyRetryAsync(
            control, db,
            logger,
            "remove-spawn-row",
            ctx.RequestAborted);
        counts["spawn"] = 1;

        // ── 11. SignalR + result ────────────────────────────────────────
        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "deleted", storyId, counts },
            ctx.RequestAborted);

        return Results.Ok(new
        {
            storyId,
            status = "deleted",
            // Phase 50 — `baseBranchDeleted` now reflects whether the
            // REMOTE delete actually succeeded, not just whether the
            // operator asked for it. Lying here was the source of the
            // "delete looked fine but old doc still picked up" report
            // (operator believed the base branch was gone, then the next
            // spawn for the same storyId inherited the orphan).
            baseBranchDeleted = !string.IsNullOrEmpty(baseBranchToDelete)
                              && !remoteDeleteFailures.Contains(baseBranchToDelete!),
            remoteDeleteFailures,
            counts,
        });
    }

    /// <summary>
    /// SQLite is single-writer; transient writer collisions can throw
    /// SQLITE_BUSY / SQLITE_LOCKED even with WAL + busy_timeout configured.
    /// DeleteSpawn runs several large write batches, so we retry those saves
    /// with bounded exponential backoff instead of returning a 500 on the
    /// first collision.
    /// </summary>
    private static async Task SaveChangesWithBusyRetryAsync(
        ControlDbContext control,
        ProductDbContext db,
        ILogger logger,
        string operation,
        CancellationToken ct)
    {
        const int maxAttempts = 6;
        var delayMs = 200;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && IsSqliteBusy(ex))
            {
                logger.LogWarning(ex,
                    "DeleteSpawn SaveChanges busy (op={Operation}, attempt={Attempt}/{Max}); retrying after {DelayMs}ms.",
                    operation, attempt, maxAttempts, delayMs);
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 2000);
            }
        }
    }

    private static bool IsSqliteBusy(DbUpdateException ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SqliteException sq
                && (sq.SqliteErrorCode == 5
                    || sq.SqliteErrorCode == 6
                    || sq.SqliteExtendedErrorCode == 5
                    || sq.SqliteExtendedErrorCode == 6
                    || sq.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IResult> ListSpawns(HttpContext ctx, ControlDbContext control, ProductDbContext db, [FromQuery] string? phase)
    {
        var scope = await ProductScope.ResolveAsync(ctx, control);
        var q = db.StorySpawns.AsNoTracking().AsQueryable();
        if (!scope.IsAll)
        {
            var ids = scope.RepoIds!;
            q = q.Where(s => s.RepoId != null && ids.Contains(s.RepoId));
        }
        if (!string.IsNullOrEmpty(phase) && !string.Equals(phase, "all", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(s => s.Phase == phase);
        }
        // SQLite can't ORDER BY DateTimeOffset — materialize then sort client-side.
        var rows = await q.ToListAsync();
        rows.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return Results.Ok(rows.Select(ToContract).ToList());
    }

    private static async Task<IResult> GetSpawn(string storyId, ControlDbContext control, ProductDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null)
        {
            return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);
        }

        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .OrderBy(w => w.Sort)
            .ToListAsync();

        var wsIds = workstreams.Select(w => w.Id).ToList();

        var deps = await db.WorkstreamDeps.AsNoTracking()
            .Where(d => wsIds.Contains(d.WorkstreamId))
            .ToListAsync();

        // Phase 49 — DepsJson fallback. Plan-phase spawns (PlanReconciler-
        // populated rows) carry deps as resolved STAGE TITLES inside
        // WorkstreamRow.DepsJson but DON'T populate the WorkstreamDeps
        // FK table — that happens later in DriveDecomposeAsync after the
        // plan PR merges. Without a fallback, the swarm DAG view sees
        // every deps[] array as empty during plan phase and can't draw
        // any arrows. Build a name→id index over this spawn's
        // workstreams and merge resolved DepsJson references into the
        // returned deps[] list (deduped against the FK rows). Excludes
        // cancelled / historic rows from the index so a tombstone with
        // the same name as a live row doesn't get picked up — observed
        // 2026-05-09 on failover-cluster:XRAFT where 7 live ws were
        // resolved against the OLD `phase-server-assembly-and-client-sdk`
        // tombstones instead of their live `…-internal-client` rebirths.
        var nameToId = workstreams
            .Where(w => w.Status != "cancelled" && w.Status != WorkstreamStatuses.Historic)
            .GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var depsByWs = deps
            .GroupBy(d => d.WorkstreamId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(d => d.DependsOnId), StringComparer.Ordinal),
                StringComparer.Ordinal);
        foreach (var w in workstreams)
        {
            if (string.IsNullOrEmpty(w.DepsJson)) continue;
            List<string>? titles;
            try { titles = JsonSerializer.Deserialize<List<string>>(w.DepsJson); }
            catch { continue; }
            if (titles is null || titles.Count == 0) continue;

            if (!depsByWs.TryGetValue(w.Id, out var resolved))
            {
                resolved = new HashSet<string>(StringComparer.Ordinal);
                depsByWs[w.Id] = resolved;
            }
            foreach (var t in titles)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!nameToId.TryGetValue(t.Trim(), out var depId)) continue;
                if (string.Equals(depId, w.Id, StringComparison.Ordinal)) continue;
                resolved.Add(depId);
            }
        }

        // Per-workstream live runtime data: every deliverable in the
        // CrewStoryArtifactStrategy gen/eval loop is keyed on workstream.Id
        // (see CrewStoryArtifactStrategy.ProduceWorkstreamChangesAsync — the
        // workItemId argument is the workstream id). So the deliverables
        // table is the source of truth for "what's the engineer pair doing
        // right now" per workstream — no per-workstream work_items row
        // required.
        var deliverables = await db.Deliverables.AsNoTracking()
            .Where(d => wsIds.Contains(d.WorkItemId))
            .ToListAsync();
        var deliverablesByWs = deliverables
            .GroupBy(d => d.WorkItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.CreatedAt).ToList(), StringComparer.OrdinalIgnoreCase);

        // Cost ledger is keyed on TaskId — the loop writes the workstream id
        // there too. Sum once per ws to avoid per-card queries.
        var costByWs = await control.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId != null && wsIds.Contains(u.TaskId))
            .GroupBy(u => u.TaskId!)
            .Select(g => new { TaskId = g.Key, Cost = g.Sum(x => x.CostUsd) })
            .ToListAsync();
        var costByWsMap = costByWs.ToDictionary(x => x.TaskId, x => x.Cost, StringComparer.OrdinalIgnoreCase);

        // Unanswered question counts per workstream — batch query, dedupe
        // in memory to match the DeliverableLoop's CountUnansweredQuestionsAsync.
        var allQuestions = await db.DeliverableQuestions.AsNoTracking()
            .Where(q => wsIds.Contains(q.WorkItemId))
            .ToListAsync();
        var unansweredByWs = allQuestions
            .GroupBy(q => q.WorkItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase)
                      .Select(eg => eg.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First())
                      .Count(q => string.IsNullOrEmpty(q.Answer)),
                StringComparer.OrdinalIgnoreCase);

        // Detect "loop is re-running right now" so the stalled override
        // below can clear when an iteration is in flight. Two signals,
        // either one keeps the workstream out of stalled:
        //   1. The work-item's ExecutionStatus = "live"/"queued" — what the
        //      UI's LIVE chip in the conversation tab reads. The orchestrator's
        //      impl loop sets this to "live" while running gen↔eval.
        //   2. An open TaskQueueRow exists — covers the queue-driven flows
        //      (review-fix, comment-response, build-fix) that bypass the
        //      direct orchestrator path.
        // Pull the relevant subset and do the live-state check in memory so
        // a stray casing / whitespace difference doesn't make a live work
        // item silently look idle. EF's translated `=` against SQLite is
        // case-sensitive; comparing with OrdinalIgnoreCase here avoids that
        // class of bug.
        var wiExecRows = await db.WorkItems
            .AsNoTracking()
            .Where(wi => wsIds.Contains(wi.Id))
            .Select(wi => new { wi.Id, wi.ExecutionStatus })
            .ToListAsync();
        var liveExecWiIds = wiExecRows
            .Where(r => string.Equals(r.ExecutionStatus, "live", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r.ExecutionStatus, "queued", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Id)
            .ToList();
        var queueRows = await db.TaskQueue
            .AsNoTracking()
            .Where(q => wsIds.Contains(q.WorkItemId))
            .Select(q => new { q.WorkItemId, q.State })
            .ToListAsync();
        var openQueueWsIds = queueRows
            .Where(q => !string.Equals(q.State, "completed", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(q.State, "cancelled", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(q.State, "reassigned", StringComparison.OrdinalIgnoreCase))
            .Select(q => q.WorkItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var liveWsIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in liveExecWiIds) liveWsIds.Add(id);
        foreach (var id in openQueueWsIds) liveWsIds.Add(id);

        // Phase 50 — single PR batch query to drive the story-delivery strip.
        // The strip shows three stages (design / impl / release) on every story
        // row in SwarmIndexView; without this batch the UI would need 1 + N
        // round-trips per row to derive QA-merged + per-workstream step-def
        // PR status (StorySpawnOrchestrator opens a `-steps` PR per workstream
        // after the QA PR merges, line ~1879).
        //
        // The query pulls:
        //   1. The QA PR (spawn.QaPrId, when set) — to derive qaPrMerged.
        //   2. Every PR linked to a workstream in this spawn — to find the
        //      step-def PR per workstream (branch suffix `-steps`) and report
        //      its merge state.
        var prIdsToFetch = new List<string>();
        if (!string.IsNullOrEmpty(spawn.QaPrId)) prIdsToFetch.Add(spawn.QaPrId!);
        var prRowsForSpawn = await db.Prs.AsNoTracking()
            .Where(p => wsIds.Contains(p.Workstream) || prIdsToFetch.Contains(p.Id))
            .Select(p => new { p.Id, p.Workstream, p.Branch, p.Col })
            .ToListAsync();
        var qaPrRow = !string.IsNullOrEmpty(spawn.QaPrId)
            ? prRowsForSpawn.FirstOrDefault(p => string.Equals(p.Id, spawn.QaPrId, StringComparison.Ordinal))
            : null;
        var qaPrMerged = qaPrRow is not null
            && string.Equals(qaPrRow.Col, "merged", StringComparison.OrdinalIgnoreCase);
        // Group step-def PRs by workstream. The branch ends with `-steps`
        // (see StorySpawnOrchestrator.cs:1864 — `ws/{storyId}/{slug}-steps`).
        var stepDefPrByWs = prRowsForSpawn
            .Where(p => !string.IsNullOrEmpty(p.Workstream)
                     && p.Branch is not null
                     && p.Branch.EndsWith("-steps", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Workstream, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var wsByStatus = workstreams.Select(w =>
        {
            var wsAgents = StoriesJsonHelpers.Deserialize(w.AgentsJson);
            var wsDeliverables = deliverablesByWs.TryGetValue(w.Id, out var ds) ? ds : new List<DeliverableRow>();
            var latest = wsDeliverables.FirstOrDefault();
            var wsCost = costByWsMap.TryGetValue(w.Id, out var c) ? c : 0m;
            var wsUnanswered = unansweredByWs.TryGetValue(w.Id, out var uq) ? uq : 0;

            // Override the workstream's persisted Status to "stalled" when
            // its latest deliverable hit the iter cap with verdict="stalled"
            // (DeliverableLoop.cs:400). The persisted Status stays at the
            // last lifecycle state ("active" / "review" / etc.) — without
            // this override the UI shows the workstream as still iterating
            // when the loop has actually given up. Don't override if:
            //   - the workstream already shipped (review/merging/complete/...)
            //   - the operator kicked a new iteration (open queue row exists)
            //     so the loop is currently running — the stalled state is
            //     historical, not current.
            string baseStatus = w.Status;
            var latestVerdictIsStalled = latest is not null
                && latest.Verdict is not null
                && (latest.Verdict.StartsWith("stalled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(latest.Verdict, "reject", StringComparison.OrdinalIgnoreCase));
            var isCurrentlyLive = liveWsIds.Contains(w.Id);
            if (latestVerdictIsStalled
                && !isCurrentlyLive
                && baseStatus is not ("review" or "merging" or "complete" or "cancelled" or "historic"))
            {
                baseStatus = "stalled";
            }

            // 2026-05-15 — reverse override: a workstream sitting at
            // `stalled` whose PR is in `merged` MUST be a stale demotion.
            // Root cause was fixed in SloppyReassignmentReconciler.cs
            // (MarkTerminalFailedAsync no longer demotes complete/merging
            // rows), but defense in depth here so the UI is sane even if
            // another code path slips through. The StalledMergedRecoveryReconciler
            // corrects the DB on its 60s tick; this read-side override
            // keeps the operator's view consistent in the meantime.
            if (string.Equals(baseStatus, WorkstreamStatuses.Stalled, StringComparison.Ordinal))
            {
                var wsPrIds = StoriesJsonHelpers.Deserialize(w.PrsJson);
                if (wsPrIds is not null && wsPrIds.Count > 0)
                {
                    var anyMerged = prRowsForSpawn.Any(p =>
                        wsPrIds.Contains(p.Id)
                        && string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase));
                    if (anyMerged)
                    {
                        baseStatus = "complete";
                    }
                }
            }

            return new
            {
                id = w.Id,
                name = w.Name,
                branch = w.Branch,
                status = ResolveWorkstreamStatus(w.Id, baseStatus, depsByWs, workstreams),
                cards = StoriesJsonHelpers.Deserialize(w.CardsJson),
                prs = StoriesJsonHelpers.Deserialize(w.PrsJson),
                deps = depsByWs.TryGetValue(w.Id, out var resolvedDeps) ? resolvedDeps.ToArray() : Array.Empty<string>(),
                // Phase 49 — surface phase + plan anchors so the swarm DAG view
                // can group stage cards into collapsible phase containers and
                // render the phase / stage labels per workstream without
                // re-parsing the plan client-side. PhaseAnchor is the
                // `phase-{slug}` prefix; PlanAnchor is the full
                // `phase-{slug}/stage-{slug}` ref. Null on legacy rows that
                // predate Phase 47Migration.
                phaseAnchor = w.PhaseAnchor,
                planAnchor = w.PlanAnchor,
                // docs/forge/e2e.md Stage 8.1 — surface the workstream
                // category discriminator ("impl" / "qa-e2e") so the
                // swarm phase-board can stack qa-e2e rows beneath
                // their impl sibling instead of rendering them as
                // standalone phase cards. Added by Phase 79Migration;
                // null on legacy rows defaults to "impl" via the
                // entity property's default.
                category = w.Category,
                // ── live runtime fields ──────────────────────────────────
                agents = wsAgents,
                iter = wsDeliverables.Count,
                latestScore = (int?)(latest?.Score),
                latestVerdict = latest?.Verdict,
                latestGenAgentId = latest?.GeneratorAgentId,
                latestEvalAgentId = latest?.EvaluatorAgentId,
                lastActiveAt = latest?.CreatedAt.ToString("O"),
                costUsd = (double)wsCost,
                commits = w.Commits,
                ahead = w.Ahead,
                behind = w.Behind,
                unansweredQuestionCount = wsUnanswered,
                // Phase 49 — surface the stall reason so the swarm-control
                // tooltip can show why the stage entered triage (build
                // gate, no-progress, non-pass verdict) without the
                // operator having to drill into the Conversation tab.
                stallReason = w.StallReason,
                // Phase 50 — story-delivery strip surfaces the Engineer-crew
                // worktree folder name (the leaf under `{cloneRoot}/.worktree/`)
                // so the tooltip can point the operator at the exact directory
                // their gen↔eval loop is editing. Pure compute over the same
                // sibling list — no disk I/O — so the lookup is free here.
                worktreeFolder = WorkstreamWorktreeSlug.Compute(w, workstreams),
                // Phase 50 — step-def PR fields. `ws.status == "complete"`
                // is set when the IMPL PR merges (StorySpawnOrchestrator.cs
                // DriveReviewAsync, line ~1699) — it does NOT mean the QA
                // step-def PR is merged. The strip's stage-3 rect needs the
                // dedicated PR row to colour the step-def rect correctly.
                stepDefPrId = stepDefPrByWs.TryGetValue(w.Id, out var sdPr) ? sdPr.Id : null,
                stepDefBranch = stepDefPrByWs.TryGetValue(w.Id, out var sdBr) ? sdBr.Branch : null,
                stepDefMerged = stepDefPrByWs.TryGetValue(w.Id, out var sdMr)
                    && string.Equals(sdMr.Col, "merged", StringComparison.OrdinalIgnoreCase),
            };
        }).ToArray();

        return Results.Ok(new
        {
            spawn = ToContract(spawn),
            // Phase 50 — top-level sibling so the strip can colour the QA-PR
            // rect (release stage) without fetching the PR row separately.
            // Sibling rather than inside `spawn` because ToContract is reused
            // by endpoints that don't load PR rows.
            qaPrMerged,
            workstreams = wsByStatus,
        });
    }

    private static string ResolveWorkstreamStatus(
        string wsId,
        string baseStatus,
        IReadOnlyDictionary<string, HashSet<string>> depsByWs,
        IReadOnlyList<WorkstreamRow> all)
    {
        // 28.5 / 28.11 — surface "blocked" when a dependency PR is unmerged.
        //
        // Phase 49 follow-up — only "complete" upstream lets a downstream
        // start (was "merging" or "complete"). Matches the orchestrator's
        // tightened dep-check in StorySpawnOrchestrator.cs so the UI
        // display agrees with what the claim sweep actually does: a
        // downstream waiting on a "merging" upstream renders as blocked
        // (the upstream's lifecycle hasn't fully settled — see notes in
        // StorySpawnOrchestrator's dep-check block).
        if (!depsByWs.TryGetValue(wsId, out var depIds) || depIds.Count == 0) return baseStatus;
        foreach (var dep in depIds)
        {
            var depWs = all.FirstOrDefault(w => w.Id == dep);
            if (depWs is null) continue;
            // Phase 63 — historic and cancelled deps count as satisfied
            // (mirrors the orchestrator's claim-sweep dep check).
            if (depWs.Status is not "complete"
                and not WorkstreamStatuses.Historic
                and not "cancelled")
            {
                return baseStatus == "planning" ? "blocked" : baseStatus;
            }
        }
        return baseStatus;
    }

    private static async Task<IResult> AdvancePhase(
        HttpContext ctx,
        string storyId,
        [FromBody] AdvancePhaseRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (req is null || string.IsNullOrWhiteSpace(req.TargetPhase))
        {
            return Results.Problem(title: "targetPhase is required", statusCode: 400);
        }

        var currentIdx = StorySpawnPhases.IndexOf(spawn.Phase);
        var targetIdx = StorySpawnPhases.IndexOf(req.TargetPhase);
        if (targetIdx < 0)
        {
            return Results.Problem(title: $"Unknown phase '{req.TargetPhase}'", statusCode: 400);
        }

        // Cross-cutting — phase rollback is a non-goal (28 non-goals).
        if (targetIdx <= currentIdx)
        {
            return Results.Problem(
                title: "Phase rollback is not supported",
                detail: $"Cannot move from '{spawn.Phase}' to '{req.TargetPhase}'.",
                statusCode: 422);
        }

        // Only single-step advance unless target is "done" via completion.
        if (targetIdx != currentIdx + 1)
        {
            return Results.Problem(
                title: "Phase advance must be sequential",
                detail: $"Next phase from '{spawn.Phase}' is '{StorySpawnPhases.Order[currentIdx + 1]}', not '{req.TargetPhase}'.",
                statusCode: 422);
        }

        // Gate logic — the Phase 28 "artifact check-in rule".
        var gate = await CheckGateAsync(spawn, req.TargetPhase, control, db);
        if (gate is not null) return gate;

        spawn.Phase = req.TargetPhase;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "phase-advanced", spawn = ToContract(spawn) });
        // Drive the new phase forward — e.g. on entering 'decompose' the
        // architect crew immediately reads the plan and produces the
        // decomposition; on entering 'implement' the engineer crew claims
        // each ready workstream.
        if (OrchestratorEnabled(ctx)) await orchestrator.DriveAsync(storyId, ctx.RequestAborted);
        var refreshed = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
        return Results.Ok(ToContract(refreshed));
    }

    /// <summary>
    /// Phase 45 — change the operator-chosen base branch on a live spawn.
    /// The orchestrator (a) rebases the new base on top of <c>main</c> so
    /// commits added to <c>main</c> after the spawn started are pulled in,
    /// (b) replays every already-merged spawn PR (plan + impl + qa + step-def)
    /// onto the rebased base so downstream phases see the in-progress story
    /// work, and (c) retargets every still-open spawn PR at the new base
    /// branch via the provider.
    /// </summary>
    private static async Task<IResult> ChangeBaseBranch(
        HttpContext ctx,
        string storyId,
        [FromBody] ChangeBaseBranchRequest? req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator,
        IStoryGitOps git,
        IStoryProviderOps provider)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.BaseBranch))
        {
            return Results.Problem(title: "baseBranch is required", statusCode: 400);
        }

        var newBase = req.BaseBranch.Trim();
        if (!IsValidBranchName(newBase))
        {
            return Results.Problem(
                title: "Invalid base branch name",
                detail: $"'{newBase}' is not a valid git branch name.",
                statusCode: 400);
        }

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var oldBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;
        if (string.Equals(oldBase, newBase, StringComparison.Ordinal))
        {
            // No-op — keeps the endpoint idempotent.
            return Results.Ok(ToContract(spawn));
        }

        var result = await ApplySpawnBaseBranchChangeAsync(
            control, db, hub, git, provider, spawn, newBase, ctx.RequestAborted);
        if (result.PrepareError is not null)
        {
            return Results.Problem(
                title: "Failed to prepare new base branch",
                detail: result.PrepareError,
                statusCode: 500);
        }

        return Results.Ok(new
        {
            spawn = ToContract(result.Spawn!),
            replayedBranches = result.ReplayedBranches,
            retargetedPrs = result.RetargetedPrs,
            replayFailures = result.ReplayFailures,
        });
    }

    /// <summary>
    /// Phase 45 — outcome of <see cref="ApplySpawnBaseBranchChangeAsync"/>. When
    /// <see cref="PrepareError"/> is non-null, the new-branch creation /
    /// rebase step failed and no other state was touched. Otherwise the spawn
    /// row was updated, merged branches replayed, and open PRs retargeted —
    /// per-step failures collected in <see cref="ReplayFailures"/>.
    /// </summary>
    internal sealed record SpawnBaseBranchChangeResult(
        StorySpawnRow? Spawn,
        IReadOnlyList<string> ReplayedBranches,
        IReadOnlyList<string> RetargetedPrs,
        IReadOnlyList<string> ReplayFailures,
        string? PrepareError);

    /// <summary>
    /// Phase 45 — apply a base-branch change to a live spawn. Used by both
    /// <c>PATCH /api/stories/{id}/spawn/base-branch</c> (operator-explicit)
    /// AND the story-level <c>PATCH /api/products/{p}/stories/{id}</c> when
    /// the operator edits the story's default base and a spawn already
    /// exists. The two callers want the same behavior — replay merged PRs
    /// onto the new base so the rest of the lifecycle stays consistent
    /// (user requirements #5 + #6).
    /// </summary>
    internal static async Task<SpawnBaseBranchChangeResult> ApplySpawnBaseBranchChangeAsync(
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IStoryGitOps git,
        IStoryProviderOps provider,
        StorySpawnRow spawn,
        string newBase,
        CancellationToken ct)
    {
        var oldBase = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch;

        var repo = !string.IsNullOrEmpty(spawn.RepoId)
            ? await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId, ct)
            : null;

        // (a) Ensure the new base branch exists, branched from main, and is
        // up-to-date with main's tip so it can carry forward both the new
        // upstream commits AND the spawn's already-merged PRs.
        try
        {
            await git.EnsureBranchAsync(repo, newBase, "main", ct);
            await git.RebaseBaseBranchAsync(repo, newBase, "main", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SpawnBaseBranchChangeResult(
                Spawn: null,
                ReplayedBranches: Array.Empty<string>(),
                RetargetedPrs: Array.Empty<string>(),
                ReplayFailures: Array.Empty<string>(),
                PrepareError: ex.Message);
        }

        // (b) Replay every already-merged spawn PR onto the new base so a
        // workstream that depends on a sibling whose PR has already merged
        // doesn't lose the precondition. Best-effort — a real conflict gets
        // surfaced as a LastError on the spawn instead of failing the call.
        var mergedBranches = new List<string>();
        if (!string.IsNullOrEmpty(spawn.PlanPrId))
        {
            var plan = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId, ct);
            if (plan is not null && string.Equals(plan.Col, "merged", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(plan.Branch))
            {
                mergedBranches.Add(plan.Branch);
            }
        }
        // Phase 53 — sequential plan-doc PRs: replay merged ones too.
        foreach (var pdoc in PlanDocStateHelpers.GetPlanDocStates(spawn))
        {
            if (string.IsNullOrEmpty(pdoc.PrId)) continue;
            var pdocPr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pdoc.PrId, ct);
            if (pdocPr is not null && string.Equals(pdocPr.Col, "merged", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(pdocPr.Branch))
            {
                mergedBranches.Add(pdocPr.Branch);
            }
        }
        if (!string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qa = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.QaPrId, ct);
            if (qa is not null && string.Equals(qa.Col, "merged", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(qa.Branch))
            {
                mergedBranches.Add(qa.Branch);
            }
        }

        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ct);
        var allWsPrIds = workstreams
            .SelectMany(w => StoriesJsonHelpers.Deserialize(w.PrsJson))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var wsPrs = allWsPrIds.Count == 0
            ? new List<PrRow>()
            : await db.Prs.AsNoTracking().Where(p => allWsPrIds.Contains(p.Id)).ToListAsync(ct);

        foreach (var pr in wsPrs.Where(p => string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(p.Branch)))
        {
            if (!mergedBranches.Contains(pr.Branch!, StringComparer.Ordinal))
                mergedBranches.Add(pr.Branch!);
        }

        var replayFailures = new List<string>();
        foreach (var b in mergedBranches)
        {
            try
            {
                await git.MergeAsync(repo, b, newBase, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                replayFailures.Add($"{b}: {ex.Message}");
            }
        }

        // (c) Retarget every still-open spawn PR at the new base. Workstreams
        // pinned to the old base are switched too so the next orchestrator
        // tick opens new PRs against the right tip.
        var openPrIds = new List<string>();
        if (!string.IsNullOrEmpty(spawn.PlanPrId))
        {
            var plan = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId, ct);
            if (plan is not null && !string.Equals(plan.Col, "merged", StringComparison.OrdinalIgnoreCase))
                openPrIds.Add(plan.Id);
        }
        // Phase 53 — sequential plan-doc PRs: retarget any still-open ones.
        foreach (var pdoc in PlanDocStateHelpers.GetPlanDocStates(spawn))
        {
            if (string.IsNullOrEmpty(pdoc.PrId)) continue;
            var pdocPr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pdoc.PrId, ct);
            if (pdocPr is not null && !string.Equals(pdocPr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                openPrIds.Add(pdocPr.Id);
        }
        if (!string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qa = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.QaPrId, ct);
            if (qa is not null && !string.Equals(qa.Col, "merged", StringComparison.OrdinalIgnoreCase))
                openPrIds.Add(qa.Id);
        }
        foreach (var pr in wsPrs.Where(p => !string.Equals(p.Col, "merged", StringComparison.OrdinalIgnoreCase)))
        {
            openPrIds.Add(pr.Id);
        }

        foreach (var prId in openPrIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await provider.UpdatePrBaseBranchAsync(prId, newBase, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                replayFailures.Add($"pr {prId} retarget: {ex.Message}");
            }
        }

        // Update each workstream row's Base so subsequent PRs (a workstream
        // with no merged PR yet, or step-def branches opened later) target
        // the new base too.
        var trackedWs = await db.Workstreams.Where(w => w.StorySpawnId == spawn.Id).ToListAsync(ct);
        foreach (var w in trackedWs)
        {
            w.Base = newBase;
        }

        var spawnRow = await db.StorySpawns.FirstAsync(s => s.Id == spawn.Id, ct);
        spawnRow.BaseBranch = newBase;
        spawnRow.UpdatedAt = DateTimeOffset.UtcNow;
        if (replayFailures.Count > 0)
        {
            spawnRow.LastError = $"base-branch change replay incomplete: {string.Join("; ", replayFailures)}";
            spawnRow.LastErrorAt = DateTimeOffset.UtcNow;
        }
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "base-branch-changed", spawn = ToContract(spawnRow), oldBase, newBase }, ct);

        return new SpawnBaseBranchChangeResult(
            Spawn: spawnRow,
            ReplayedBranches: mergedBranches,
            RetargetedPrs: openPrIds.Distinct(StringComparer.Ordinal).ToArray(),
            ReplayFailures: replayFailures,
            PrepareError: null);
    }

    /// <summary>
    /// Cheap subset of git's <c>refname</c> rules — enough to refuse the
    /// obvious typos that would otherwise reach <c>git fetch</c>. Full
    /// validation lives in <c>git check-ref-format</c>; we don't shell out
    /// here because the operator UI is the only caller.
    /// </summary>
    internal static bool IsValidBranchName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 250) return false;
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith(".lock")) return false;
        if (name.Contains("..") || name.Contains("//") || name.Contains("@{")) return false;
        foreach (var c in name)
        {
            if (c <= 0x20 || c == 0x7f) return false;
            if (c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\') return false;
        }
        return true;
    }

    /// <summary>
    /// Phase 37 — operator clicks Retry on the workstream banner: clear the
    /// persisted LastError and immediately re-drive the spawn so the next
    /// orchestrator tick attempts the failing step again. Used after the
    /// operator has resolved the underlying cause (rebased the plan branch,
    /// fixed branch protection, bumped iter cap, etc.).
    /// </summary>
    private static async Task<IResult> RetrySpawn(
        HttpContext ctx,
        string storyId,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        spawn.LastError = null;
        spawn.LastErrorAt = null;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "spawn-retry", spawn = ToContract(spawn) });

        // Fire-and-forget the re-drive so the HTTP response returns
        // immediately — DriveAsync runs the full phase pipeline (architect
        // crew, decomposition, engineer claims, etc.) and can take many
        // minutes. The operator clicking Retry expects fast feedback; the
        // background scheduler picks up unfinished work on its 15s tick
        // anyway. Detach from the request CT so a navigation away doesn't
        // cancel the orchestrator mid-step.
        // forceResume=true tells the architect strategy to bypass the
        // workspace-cache short-circuit so the gen↔eval loop re-iterates
        // (otherwise the retry would replay the stalled artifact unchanged
        // and immediately re-fail with the same verdict).
        if (OrchestratorEnabled(ctx))
        {
            var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("forge.retry");
            _ = Task.Run(async () =>
            {
                try
                {
                    await orchestrator.DriveAsync(storyId, forceResume: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Retry drive failed for story {StoryId}", storyId);
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var scopedDb = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                        var row = await scopedDb.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
                        if (row is not null)
                        {
                            row.LastError = $"retry failed: {ex.Message}";
                            row.LastErrorAt = DateTimeOffset.UtcNow;
                            row.UpdatedAt = DateTimeOffset.UtcNow;
                            await scopedDb.SaveChangesAsync();
                        }
                    }
                    catch
                    {
                        // best-effort error persistence
                    }
                }
            });
        }

        return Results.Ok(ToContract(spawn));
    }

    /// <summary>
    /// Dismiss the spawn's persisted error without re-driving. Clears
    /// <c>LastError</c> / <c>LastErrorAt</c> so the error banner disappears.
    /// Use when the operator has handled the issue manually (e.g. force-pushed,
    /// resolved a merge conflict) and doesn't want an automatic retry.
    /// </summary>
    private static async Task<IResult> DismissError(
        HttpContext ctx,
        string storyId,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        spawn.LastError = null;
        spawn.LastErrorAt = null;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("storySpawn", storyId, "dismissed",
            new { kind = "spawn-error-dismissed", spawn = ToContract(spawn) });

        return Results.Ok(ToContract(spawn));
    }

    /// <summary>
    /// Phase 53 follow-up — Insight I.3 surface for plan-doc tasks. Mirrors
    /// the per-iter KPI rollup that <c>WorkstreamsEndpoints.GetIterKpis</c>
    /// computes for engineer workstreams, keyed on the synthetic
    /// <c>plan-doc:{spawn.Id}:{filename}</c> workItemId so the architect
    /// crew's iterations populate the Conversation tab's KPI strip +
    /// sparklines identically.
    /// </summary>
    private static async Task<IResult> GetArtifactIterKpis(
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        CancellationToken ct)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { workItemId = (string?)null, iters = Array.Empty<object>() });
        }

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";
        var payload = await Forge.Api.Services.ConversationInsightQueries
            .GetIterKpisAsync(control, db, workItemId, ct);
        return Results.Ok(payload);
    }

    /// <summary>
    /// Phase 53 follow-up — Insight I.2 surface for plan-doc tasks. Reads
    /// the architect agent's per-iter working-notes archive under
    /// <c>{cloneRoot}/.worktree/plan-{docKey}/.forge/notes/iter-{N}.md</c>
    /// so the Summary card on a doc task surfaces the same "Working notes"
    /// section that workstreams already render.
    /// </summary>
    private static async Task<IResult> GetArtifactIterNotes(
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        CancellationToken ct)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { workItemId = (string?)null, iters = Array.Empty<object>() });
        }

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";
        var resolved = await WorktreePathResolver.ResolveForPlanDocAsync(control, db, spawn.Id, key, ct);
        var payload = await Forge.Api.Services.ConversationInsightQueries
            .GetIterNotesAsync(workItemId, resolved.WorktreePath, ct);
        return Results.Ok(payload);
    }

    /// <summary>
    /// Phase 53 follow-up — Insight I.4 / Phase G surface for plan-doc
    /// tasks. Returns the same <c>roles</c>-keyed payload that the
    /// workstream memory route returns so the Conversation tab can render
    /// the ContextUsageBar for architect docs identically to engineer
    /// workstreams.
    /// </summary>
    private static async Task<IResult> GetArtifactMemory(
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        Forge.Api.Agents.CopilotSessionPool copilotPool,
        Forge.Api.Services.MemoryCompactor compactor,
        CancellationToken ct)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { workItemId = (string?)null, roles = new Dictionary<string, object?>() });
        }

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";
        var payload = await Forge.Api.Services.ConversationInsightQueries
            .GetMemoryAsync(control, db, copilotPool, compactor, workItemId, ct);
        return Results.Ok(payload);
    }

    /// <summary>
    /// Resume a single plan-doc artifact's gen↔eval loop. Unlike RetrySpawn
    /// (which re-drives the full phase pipeline), this re-runs ONLY the
    /// specified doc. Works even when the spawn has advanced past the plan
    /// phase (e.g. phase=implement) — the doc loop is independent of the
    /// spawn phase state machine.
    ///
    /// Phase 41 — restart-safe. The artifact key is appended to
    /// <see cref="StorySpawnRow.ResumePendingJson"/> BEFORE the fire-and-forget
    /// task starts, and removed in the task's finally block. On boot,
    /// <c>StoryResumeRecoveryService</c> sweeps non-empty entries and re-fires
    /// the loop so a process kill mid-Resume self-heals.
    /// </summary>
    private static async Task<IResult> ResumeArtifact(
        HttpContext ctx,
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IStoryArtifactStrategy strategy)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

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

        var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(() => RunResumeWithCleanupAsync(scopeFactory, strategy, spawn.Id, storyId, key, CancellationToken.None));

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "artifact-resumed", storyId, key });

        return Results.Ok(new { storyId, key, status = "resuming" });
    }

    /// <summary>
    /// Phase 56 -- soft retry for a plan-doc artifact whose gen/eval
    /// loop hit a terminal verdict (sloppy at iter-cap, stalled,
    /// or a recovery-exhausted pass). Unlike <see cref="ResumeArtifact"/>
    /// (which only re-fires the loop -- DeliverableLoop's terminal-replay
    /// at <c>iter >= maxIter</c> immediately returns the same verdict
    /// without doing any work), Retry:
    /// <list type="number">
    ///   <item>Demotes the last deliverable verdict pass/sloppy/stalled
    ///         → <c>iterate</c> so DeliverableLoop seeding picks up at
    ///         iter+1 with the full prior chain.</item>
    ///   <item>Bumps the iter cap by +5 via <c>TaskQueueRow.MaxIterOverride</c>
    ///         so <c>iter < maxIter</c> holds and seeding actually
    ///         triggers (the canonical "operator extended the cap"
    ///         path, see <see cref="TaskQueueRow.MaxIterOverride"/>).</item>
    ///   <item>Resets the work-item's ExecutionStatus to <c>queued</c>
    ///         so the UI badge un-pulses any stuck live state.</item>
    ///   <item>Fires <see cref="RunResumeWithCleanupAsync"/> in the
    ///         background -- exact same code path as Resume, just with
    ///         the demoted verdict + bumped cap baked in.</item>
    /// </list>
    /// PRESERVES: worktree, deliverables, transcript, agent_usage,
    /// costs, plan doc on disk. The architect agent continues
    /// improving the same doc with the prior critique chain.
    /// </summary>
    private static async Task<IResult> RetryArtifact(
        HttpContext ctx,
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IStoryArtifactStrategy strategy)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

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
        var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
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

    /// <summary>
    /// Phase 41 — drive a plan-doc Resume in the background with a finally
    /// block that always clears the pending marker. Shared by the
    /// <c>ResumeArtifact</c> endpoint AND the boot-time recovery sweep so the
    /// cleanup contract is identical.
    ///
    /// <para>Phase 50 follow-up — <paramref name="restartFromScratch"/>
    /// signals that the caller is the operator-clicked Restart path
    /// (not Resume), so <see cref="IStoryArtifactStrategy.ResumeSingleArtifactAsync"/>
    /// MUST wipe the on-disk plan doc + any stale per-doc DB rows before
    /// the architect's first iteration runs. Defaults to <c>false</c> so
    /// the ordinary Resume endpoint and boot-recovery sweep retain
    /// resume-mid-iter semantics (file preserved).</para>
    /// </summary>
    internal static async Task RunResumeWithCleanupAsync(
        IServiceScopeFactory scopeFactory,
        IStoryArtifactStrategy strategy,
        string spawnId,
        string storyId,
        string key,
        CancellationToken ct,
        bool restartFromScratch = false)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var spawn = await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            var story = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == storyId, ct);
            if (spawn is null || story is null) return;

            await strategy.ResumeSingleArtifactAsync(spawn, story, key, restartFromScratch, ct);

            // Phase 42 — when the resumed artifact is implementation-plan.md,
            // re-reconcile workstreams against the (potentially updated) plan
            // so the DB + workstreams.json stay in lockstep.
            if (string.Equals(key, "implementation-plan", StringComparison.OrdinalIgnoreCase))
            {
                var reconciler = scope.ServiceProvider.GetService<Forge.Api.Services.Stories.PlanReconciler>();
                if (reconciler is not null)
                {
                    try { await reconciler.ReconcileAsync(spawnId, ct); }
                    catch (Exception rex) when (rex is not OperationCanceledException)
                    {
                        // Reconcile failure should not surface as a Resume failure;
                        // the scheduler tick will retry shortly.
                    }
                }
            }

            // After a successful resume (doc passed), re-drive the
            // orchestrator so it can commit + open the plan PR when all
            // docs have passed. Resumed docs run outside the orchestrator's
            // original Task.WhenAll, so without this nudge the PR is never
            // opened.
            try
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<Forge.Api.Services.Stories.StorySpawnOrchestrator>();
                await orchestrator.DriveAsync(storyId, ct);
            }
            catch (Exception ox) when (ox is not OperationCanceledException)
            {
                // Best-effort — the scheduler tick will retry.
            }
        }
        catch (Forge.Api.Services.Stories.PlanDocInFlightException)
        {
            // 2026-05-20 — the resume lost the per-doc production lock to an
            // already in-flight run (typically the SpawnDependencyScheduler
            // tick driving the same doc, or a not-yet-drained cancelled run).
            // Transient + expected: the winning run produces the doc, so this
            // is a no-op, NOT a failure. Do NOT record a spawn LastError —
            // that would paint a false "ORCHESTRATOR HALTED" banner over a
            // healthy in-flight run. The finally below still clears the
            // resume-pending marker; the scheduler owns production from here.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using var scope = scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = await db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, ct);
            if (row is not null)
            {
                row.LastError = $"resume {key} failed: {ex.Message}";
                row.LastErrorAt = DateTimeOffset.UtcNow;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            // Serialize concurrent cleanups for the same spawn id to avoid
            // lost-update races where two parallel RunResumeWithCleanupAsync
            // tasks both read ResumePendingJson, remove their respective keys,
            // and last-write-wins clobbers the other's removal. The lock is
            // local to this process; cross-process correctness still relies
            // on the boot-recovery sweep.
            var cleanupLock = GetResumeCleanupLock(spawnId);
            await cleanupLock.WaitAsync(CancellationToken.None);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
                var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var row = await db.StorySpawns.FirstOrDefaultAsync(s => s.Id == spawnId, CancellationToken.None);
                if (row is not null && RemoveResumePending(row, key))
                {
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch
            {
                // best-effort cleanup; the next boot sweep will skip an
                // already-completed key when the loop's last verdict is
                // terminal anyway.
            }
            finally
            {
                cleanupLock.Release();
            }
        }
    }

    /// <summary>
    /// Cancel the in-flight gen↔eval loop for a single plan-doc artifact.
    /// Mirrors POST /api/workstreams/{id}/cancel for coding tasks: kills the
    /// agent invocation(s) keyed by the synthetic <c>plan-doc:{spawn.Id}:{file}</c>
    /// workItemId, cancels any TaskQueue rows, flips the WorkItem ExecutionStatus
    /// out of live/queued, and flips a still-iterating last deliverable to
    /// stalled. Iterations and transcript history are preserved (use Restart
    /// to wipe them).
    /// </summary>
    private static async Task<IResult> CancelArtifact(
        HttpContext ctx,
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";

        // (1) Kill in-flight invocations first so no events land mid-cleanup.
        var invocationsCancelled = 0;
        try
        {
            invocationsCancelled = await runner.CancelByTaskAsync(workItemId, ctx.RequestAborted);
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

    /// <summary>
    /// Manually accept a plan-doc artifact despite its score being below the
    /// passing threshold (90). The operator overrides the evaluator verdict:
    /// the latest deliverable is flipped to <c>pass</c>, any in-flight
    /// invocations and queued rows are cancelled, and the doc transitions to
    /// the <c>awaiting-pr-review</c> / <c>completed</c> lifecycle stage.
    /// After acceptance, the plan branch is pushed and merged to main via
    /// a PR (opening a new one if the original plan PR was already merged).
    /// Iterations and transcript history are preserved.
    /// </summary>
    private static async Task<IResult> AcceptArtifact(
        HttpContext ctx,
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub,
        IStoryGitOps git,
        IStoryProviderOps provider)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

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
            invocationsCancelled = await runner.CancelByTaskAsync(workItemId, ctx.RequestAborted);
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
        var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var loggerFactory = ctx.RequestServices.GetService<ILoggerFactory>();
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

    /// <summary>
    /// Restart a plan-doc artifact's gen↔eval loop from iter 1. Mirrors
    /// POST /api/workstreams/{id}/restart for coding tasks: kills in-flight
    /// invocations, deletes deliverables + transcript events for the
    /// synthetic workItemId, wipes the cached architect workspace on disk,
    /// then fire-and-forgets <see cref="RunResumeWithCleanupAsync"/> which
    /// re-enters <see cref="IStoryArtifactStrategy.ResumeSingleArtifactAsync"/>
    /// — with no prior deliverables it starts fresh at iter 1.
    /// </summary>
    private static async Task<IResult> RestartArtifact(
        HttpContext ctx,
        string storyId,
        string key,
        ControlDbContext control,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub,
        IForgeDbPaths paths,
        IStoryArtifactStrategy strategy)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

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
        try { await runner.CancelByTaskAsync(workItemId, ctx.RequestAborted); }
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
        var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
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

    /// <summary>
    /// Phase 42 — force-reconcile workstreams + workitems against the current
    /// <c>implementation-plan.md</c> on the spawn's plan branch. The other
    /// triggers (per-iter commit, Resume/Restart of the implementation-plan
    /// artifact, scheduler tick) cover automatic cases; this endpoint is for
    /// operator-driven recovery (e.g. after a manual edit on the plan branch
    /// or a stuck reconciler tick).
    /// </summary>
    private static async Task<IResult> Reconcile(
        HttpContext ctx,
        string storyId,
        ControlDbContext control,
        ProductDbContext db,
        Forge.Api.Services.Stories.PlanReconciler reconciler)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var report = await reconciler.ReconcileAsync(spawn.Id, ctx.RequestAborted);
        if (report is null)
        {
            return Results.Problem(
                title: "Reconcile produced no result",
                detail: "The plan file may not yet be on the plan branch (plan phase not started?).",
                statusCode: 409);
        }
        return Results.Ok(report);
    }

    /// <summary>
    /// Phase 41 — append <paramref name="key"/> to the spawn's pending list,
    /// deduping. Returns the new list so callers can persist after.
    /// </summary>
    internal static void AddResumePending(StorySpawnRow spawn, string key)
    {
        var list = ParseResumePending(spawn.ResumePendingJson);
        if (!list.Contains(key, StringComparer.Ordinal)) list.Add(key);
        spawn.ResumePendingJson = list.Count == 0
            ? null
            : JsonSerializer.Serialize(list, JsonOpts);
    }

    /// <summary>
    /// Phase 41 — drop <paramref name="key"/> from the spawn's pending list.
    /// Returns true when a change was made (caller persists), false otherwise.
    /// </summary>
    internal static bool RemoveResumePending(StorySpawnRow spawn, string key)
    {
        var list = ParseResumePending(spawn.ResumePendingJson);
        var removed = list.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
        if (removed == 0) return false;
        spawn.ResumePendingJson = list.Count == 0
            ? null
            : JsonSerializer.Serialize(list, JsonOpts);
        return true;
    }

    internal static List<string> ParseResumePending(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Returns null when the gate is satisfied, or an IResult with the
    /// 422 problem describing the gate violation.
    /// </summary>
    private static async Task<IResult?> CheckGateAsync(StorySpawnRow spawn, string targetPhase, ControlDbContext control, ProductDbContext db)
    {
        switch (targetPhase)
        {
            case StorySpawnPhases.Decompose:
                {
                    // 28.3 — plan PR(s) must be merged.
                    // Phase 53 — sequential-mode spawns require all four
                    // plan-doc PRs to be merged. Legacy spawns require the
                    // single PlanPrId.
                    if (PlanDocStateHelpers.IsSequentialMode(spawn))
                    {
                        var states = PlanDocStateHelpers.GetPlanDocStates(spawn);
                        foreach (var docKey in PlanDocStateHelpers.PlanDocOrder)
                        {
                            var entry = states.FirstOrDefault(e =>
                                string.Equals(e.Key, docKey, StringComparison.OrdinalIgnoreCase));
                            if (entry is null || string.IsNullOrEmpty(entry.PrId))
                            {
                                return Results.Problem(
                                    title: "All plan-doc PRs must be merged before advancing",
                                    detail: $"Plan doc '{docKey}' has no PR yet.",
                                    statusCode: 422);
                            }
                            var pdocPr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.PrId);
                            if (pdocPr is null || !IsPrMerged(pdocPr))
                            {
                                return Results.Problem(
                                    title: "All plan-doc PRs must be merged before advancing",
                                    detail: $"Plan doc '{docKey}' PR {entry.PrId} is not yet merged.",
                                    statusCode: 422);
                            }
                        }
                        return null;
                    }
                    if (string.IsNullOrEmpty(spawn.PlanPrId))
                    {
                        return Results.Problem(
                            title: "Plan PR must be merged before advancing",
                            detail: "No plan PR is recorded on this spawn.",
                            statusCode: 422);
                    }
                    var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId);
                    if (pr is null || !IsPrMerged(pr))
                    {
                        return Results.Problem(
                            title: "Plan PR must be merged before advancing",
                            detail: $"PR {spawn.PlanPrId} is not yet merged.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Implement:
                {
                    // 28.4 — operator must approve the decomposition.
                    if (!spawn.DecompositionApproved)
                    {
                        return Results.Problem(
                            title: "Decomposition not yet approved",
                            detail: "Submit and approve the decomposition before advancing to 'implement'.",
                            statusCode: 422);
                    }
                    if (string.IsNullOrEmpty(spawn.DecompositionJson))
                    {
                        return Results.Problem(
                            title: "Decomposition not yet submitted",
                            detail: "Decomposition must be persisted before advancing to 'implement'.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Review:
                {
                    // 28.5 → 28.6 — every workstream must have an open PR (status='review').
                    var workstreams = await db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == spawn.Id)
                        .ToListAsync();
                    if (workstreams.Count == 0)
                    {
                        return Results.Problem(
                            title: "No workstreams to review",
                            detail: "At least one workstream must exist before advancing to 'review'.",
                            statusCode: 422);
                    }
                    var notReady = workstreams.Where(w => w.Status != "review" && w.Status != "merging" && w.Status != "complete").ToList();
                    if (notReady.Count > 0)
                    {
                        return Results.Problem(
                            title: "All workstream PRs must be open (status='review') before advancing",
                            detail: $"{notReady.Count} workstream(s) not ready: {string.Join(", ", notReady.Select(w => w.Id))}.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Qa:
                {
                    // 28.6 → 28.7 — all workstream PRs approved (status in merging/complete).
                    var workstreams = await db.Workstreams.AsNoTracking()
                        .Where(w => w.StorySpawnId == spawn.Id)
                        .ToListAsync();
                    var notMerged = workstreams.Where(w => w.Status != "merging" && w.Status != "complete").ToList();
                    if (notMerged.Count > 0)
                    {
                        return Results.Problem(
                            title: "All workstream PRs must be merged before advancing to 'qa'",
                            detail: $"{notMerged.Count} workstream(s) not yet merged.",
                            statusCode: 422);
                    }
                    return null;
                }
            case StorySpawnPhases.Done:
                {
                    return await CheckCompletionAsync(spawn, control, db);
                }
        }
        return null;
    }

    /// <summary>
    /// 28.8 — story is complete only when every workstream PR is merged,
    /// the QA feature PR is merged, and all e2e tests pass on main.
    /// </summary>
    private static async Task<IResult?> CheckCompletionAsync(StorySpawnRow spawn, ControlDbContext control, ProductDbContext db)
    {
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync();
        var unmerged = workstreams.Count(w => w.Status != "merging" && w.Status != "complete");
        if (unmerged > 0)
        {
            return Results.Problem(
                title: "Story not complete",
                detail: $"{unmerged}/{workstreams.Count} workstream PR(s) still unmerged.",
                statusCode: 422);
        }
        if (!string.IsNullOrEmpty(spawn.QaPrId))
        {
            var qa = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == spawn.QaPrId);
            if (qa is null || !IsPrMerged(qa))
            {
                return Results.Problem(
                    title: "QA feature PR must be merged",
                    detail: $"QA PR {spawn.QaPrId} is not yet merged.",
                    statusCode: 422);
            }
        }
        // E2E tests pass on main — proxied by the absence of failing builds on
        // the main branch for this repo. If the spawn has no recorded builds,
        // assume tests have not yet run.
        if (spawn.RepoId is not null)
        {
            var failingOnMain = await db.Builds.AsNoTracking()
                .Where(b => b.RepoId == spawn.RepoId && b.Branch == "main"
                    && (b.Result == "failed" || b.Result == "failure" || b.Result == "partiallySucceeded"))
                .CountAsync();
            if (failingOnMain > 0)
            {
                return Results.Problem(
                    title: "E2E tests failing on main",
                    detail: $"{failingOnMain} failing build(s) on main for this repo.",
                    statusCode: 422);
            }
        }
        return null;
    }

    private static bool IsPrMerged(PrRow pr) =>
        string.Equals(pr.Col, "merged", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> SubmitDecomposition(
        HttpContext ctx,
        string storyId,
        [FromBody] DecomposeRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (spawn.Phase != StorySpawnPhases.Decompose)
        {
            return Results.Problem(
                title: "Spawn must be in 'decompose' phase to submit a decomposition",
                detail: $"Current phase: '{spawn.Phase}'.",
                statusCode: 422);
        }

        if (req is null || req.Workstreams is null || req.Workstreams.Count == 0)
        {
            return Results.Problem(
                title: "At least one workstream is required",
                statusCode: 422);
        }

        // Validate metadata — 28.4 each entry must have title (other fields default).
        foreach (var w in req.Workstreams)
        {
            if (string.IsNullOrWhiteSpace(w.Title))
            {
                return Results.Problem(title: "Each workstream needs a title", statusCode: 422);
            }
        }

        // 28.4 — circular dependency detection. Build adjacency by title-slug.
        var titleToSlug = req.Workstreams
            .ToDictionary(w => w.Title, w => SlugFor(w.Title), StringComparer.OrdinalIgnoreCase);
        var slugSet = new HashSet<string>(titleToSlug.Values, StringComparer.OrdinalIgnoreCase);

        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in req.Workstreams)
        {
            var fromSlug = titleToSlug[w.Title];
            adjacency[fromSlug] = new List<string>();
            foreach (var depRaw in w.Deps ?? Array.Empty<string>())
            {
                var dep = depRaw.Trim();
                if (string.IsNullOrEmpty(dep)) continue;
                // Accept either the title or the slug.
                var depSlug = titleToSlug.TryGetValue(dep, out var s) ? s : dep;
                if (!slugSet.Contains(depSlug))
                {
                    return Results.Problem(
                        title: $"Unknown dependency '{depRaw}'",
                        statusCode: 422);
                }
                adjacency[fromSlug].Add(depSlug);
            }
        }
        if (HasCycle(adjacency))
        {
            return Results.Problem(
                title: "Circular dependency detected",
                statusCode: 422);
        }

        // Persist workstreams + dep edges.
        var maxSort = await db.Workstreams.Select(w => (int?)w.Sort).MaxAsync() ?? -1;
        var createdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in req.Workstreams)
        {
            var slug = titleToSlug[w.Title];
            var safeStory = StoryPaths.Safe(spawn.StoryId);
            var wsId = $"ws-{safeStory}-{slug}".ToLowerInvariant();
            createdMap[slug] = wsId;
            var row = new WorkstreamRow
            {
                Id = wsId,
                Sort = ++maxSort,
                RepoId = spawn.RepoId,
                Name = w.Title,
                Branch = $"ws/{safeStory}/{slug}",
                // Phase 45 — manually-submitted workstreams inherit the
                // spawn's chosen base branch so their PRs target the same
                // branch as the orchestrator-created ones.
                Base = string.IsNullOrEmpty(spawn.BaseBranch) ? "main" : spawn.BaseBranch,
                Epic = spawn.StoryId,
                Status = "planning",
                WorktreeJson = "[\"(not yet created)\"]",
                Created = 0,
                LastActive = 0,
                AgentsJson = "[]",
                PrsJson = "[]",
                CardsJson = "[]",
                Commits = 0,
                Ahead = 0,
                Behind = 0,
                Conflicts = 0,
                Summary = w.Description ?? $"Workstream {w.Title} for story {spawn.StoryId}",
                BarStart = 0,
                BarEnd = 5,
                Planned = true,
                StorySpawnId = spawn.Id,
                // docs/forge/e2e.md Stage 1.2 — honour an explicit Category in
                // the request body when it's in the closed set; coerce
                // anything else (null, unknown values) to 'impl' so existing
                // request bodies keep their semantics.
                Category = WorkstreamCategories.IsValid(w.Category) ? w.Category! : WorkstreamCategories.Impl,
            };
            db.Workstreams.Add(row);
        }

        foreach (var w in req.Workstreams)
        {
            var fromId = createdMap[titleToSlug[w.Title]];
            foreach (var depRaw in w.Deps ?? Array.Empty<string>())
            {
                var dep = depRaw.Trim();
                if (string.IsNullOrEmpty(dep)) continue;
                var depSlug = titleToSlug.TryGetValue(dep, out var s) ? s : dep;
                var dependsOnId = createdMap[depSlug];
                db.WorkstreamDeps.Add(new WorkstreamDepRow
                {
                    WorkstreamId = fromId,
                    DependsOnId = dependsOnId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        spawn.DecompositionJson = JsonSerializer.Serialize(req.Workstreams, JsonOpts);
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "decomposed", spawn = ToContract(spawn) });
        return Results.Ok(new
        {
            spawn = ToContract(spawn),
            workstreams = createdMap.Values.ToArray(),
        });
    }

    private static async Task<IResult> ApproveDecomposition(
        HttpContext ctx,
        string storyId,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.IsNullOrEmpty(spawn.DecompositionJson))
        {
            return Results.Problem(
                title: "No decomposition to approve",
                detail: "Submit a decomposition first.",
                statusCode: 422);
        }
        spawn.DecompositionApproved = true;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "decomposition-approved", spawn = ToContract(spawn) });
        if (OrchestratorEnabled(ctx)) await orchestrator.DriveAsync(storyId, ctx.RequestAborted);
        var refreshed = await db.StorySpawns.AsNoTracking().FirstAsync(s => s.StoryId == storyId);
        return Results.Ok(ToContract(refreshed));
    }

    private static async Task<IResult> GetArtifacts(string storyId, ControlDbContext control, ProductDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync();
        var workstreamPrs = workstreams
            .SelectMany(w => StoriesJsonHelpers.Deserialize(w.PrsJson) ?? new List<string>())
            .ToArray();

        var safeStoryId = StoryPaths.Safe(storyId);
        var planArtifacts = new[]
        {
            $"docs/stories/{safeStoryId}/tech-spec.md",
            $"docs/stories/{safeStoryId}/architecture.md",
            $"docs/stories/{safeStoryId}/implementation-plan.md",
            $"docs/stories/{safeStoryId}/e2e-scenarios.md",
        };
        var qaArtifacts = string.IsNullOrEmpty(spawn.QaPrId)
            ? Array.Empty<string>()
            : new[] { $"tests/Features/{safeStoryId}/" };

        return Results.Ok(new
        {
            plan = new
            {
                branch = spawn.PlanBranch,
                pr = spawn.PlanPrId,
                // Phase 53 — per-doc PRs (null when legacy).
                docs = PlanDocStateHelpers.IsSequentialMode(spawn)
                    ? (object)PlanDocStateHelpers.GetPlanDocStates(spawn)
                        .Select(e => new { key = e.Key, branch = e.Branch, prId = e.PrId })
                        .ToArray()
                    : null,
                files = planArtifacts,
            },
            decompose = new
            {
                approved = spawn.DecompositionApproved,
                json = spawn.DecompositionJson,
            },
            implement = new
            {
                prs = workstreamPrs,
                workstreams = workstreams.Select(w => w.Id).ToArray(),
            },
            review = new
            {
                prs = workstreamPrs,
            },
            qa = new
            {
                branch = spawn.QaBranch,
                pr = spawn.QaPrId,
                files = qaArtifacts,
            },
        });
    }

    /// <summary>
    /// Serve the actual markdown body for a single plan artifact. The plan
    /// PR has merged the four documents to <c>docs/stories/{safeStoryId}/</c>
    /// on main; this endpoint reads them straight off the operator's local
    /// working tree (same files git would show on `git checkout main`).
    ///
    /// <para>Valid keys: architecture | tech-spec | implementation-plan |
    /// e2e-scenarios | decomposition. The first four read MD files from disk;
    /// <c>decomposition</c> returns the spawn's stored decomposition JSON
    /// formatted as a markdown-friendly preview.</para>
    /// </summary>
    private static async Task<IResult> GetArtifactContent(string storyId, string key, ControlDbContext control, ProductDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "architecture", "tech-spec", "implementation-plan", "e2e-scenarios", "decomposition",
        };
        if (!allowed.Contains(key))
        {
            return Results.Problem(title: $"Unknown artifact key '{key}'", statusCode: 400);
        }

        // Decomposition has no on-disk MD — surface the stored JSON so the
        // UI can render the workstream breakdown the same way as a doc.
        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new
            {
                key,
                path = (string?)null,
                content = spawn.DecompositionJson ?? string.Empty,
                lastModifiedAt = (string?)null,
                exists = !string.IsNullOrEmpty(spawn.DecompositionJson),
            });
        }

        var safeStoryId = StoryPaths.Safe(storyId);
        var relPath = $"docs/stories/{safeStoryId}/{key.ToLowerInvariant()}.md";

        // Read source priority:
        //   1. Worktree   — `{repo.CloneRoot}/.worktree/{planSlug}/{relPath}`
        //                   The architect's gen↔eval loop reads + writes here every
        //                   iteration. This is the LIVE agent state; show it first.
        //   2. Main       — `{repo.CloneRoot}/{relPath}` after the plan PR merges.
        //   3. Plan branch — `git show story/.../plan:{relPath}` (pushed but not merged).
        // Each source returns a distinct `source` field so the UI banner shows the
        // operator whether they're looking at WIP / shipped / pre-merge content.
        var repo = !string.IsNullOrEmpty(spawn.RepoId)
            ? await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == spawn.RepoId)
            : null;
        var cloneRoot = !string.IsNullOrEmpty(repo?.CloneRoot) && Directory.Exists(repo!.CloneRoot)
            ? repo.CloneRoot
            : ForgeSeed.FindRepoRoot();

        var planBranch = PlanDocStateHelpers.FindDocBranch(spawn, key);
        // Sequential-mode docs live in `.worktree/plan-{key}`; legacy
        // parallel-mode spawns share a single `.worktree/plan` directory.
        var isSequential = PlanDocStateHelpers.IsSequentialMode(spawn)
            && PlanDocStateHelpers.PlanDocFileName.ContainsKey(key);
        var planSlug = isSequential
            ? PlanDocStateHelpers.CanonicalDocWorktreeSlug(spawn.StoryId, key)
            : PlanDocStateHelpers.CanonicalParallelPlanWorktreeSlug();
        var worktreePath = Path.Combine(cloneRoot, ".worktree", planSlug);
        var worktreeFilePath = Path.Combine(worktreePath, relPath.Replace('/', Path.DirectorySeparatorChar));

        // Also check legacy slug (pre-story-scoping) for backward compat.
        string? legacyWorktreeFilePath = null;
        if (isSequential)
        {
            var legacySlug = PlanDocStateHelpers.CanonicalDocWorktreeSlug(key);
            var legacyWt = Path.Combine(cloneRoot, ".worktree", legacySlug);
            legacyWorktreeFilePath = Path.Combine(legacyWt, relPath.Replace('/', Path.DirectorySeparatorChar));
        }

        // (1) Worktree — live agent state. Highest priority.
        var effectiveWtFile = File.Exists(worktreeFilePath)
            ? worktreeFilePath
            : (legacyWorktreeFilePath is not null && File.Exists(legacyWorktreeFilePath) ? legacyWorktreeFilePath : null);
        if (effectiveWtFile is not null)
        {
            var content = await File.ReadAllTextAsync(effectiveWtFile);
            var lastModified = File.GetLastWriteTimeUtc(effectiveWtFile).ToString("O");
            return Results.Ok(new
            {
                key,
                path = relPath,
                content,
                lastModifiedAt = lastModified,
                exists = true,
                source = "worktree",
            });
        }

        // (2) Main — committed and merged.
        var mainPath = Path.Combine(cloneRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(mainPath))
        {
            var content = await File.ReadAllTextAsync(mainPath);
            var lastModified = File.GetLastWriteTimeUtc(mainPath).ToString("O");
            return Results.Ok(new
            {
                key,
                path = relPath,
                content,
                lastModifiedAt = lastModified,
                exists = true,
                source = "main",
            });
        }

        // (3) Plan branch — pushed but not merged. `git show` against the
        // local clone reads the branch tip without mutating the working tree.
        if (!string.IsNullOrEmpty(planBranch))
        {
            var fromBranch = await TryGitShowAsync(cloneRoot, planBranch, relPath);
            if (fromBranch is not null)
            {
                return Results.Ok(new
                {
                    key,
                    path = relPath,
                    content = fromBranch,
                    lastModifiedAt = (string?)null,
                    exists = true,
                    source = $"branch:{planBranch}",
                });
            }
        }

        return Results.Ok(new
        {
            key,
            path = relPath,
            content = string.Empty,
            lastModifiedAt = (string?)null,
            exists = false,
            source = (string?)null,
        });
    }

    /// <summary>
    /// `git show {branch}:{path}` against the operator's local clone. Tries
    /// the local branch first, then <c>origin/{branch}</c> — the orchestrator
    /// commits + pushes to origin without checking the branch out locally,
    /// so the local-only ref usually doesn't exist. Returns null when neither
    /// ref resolves the path (treated as "not produced yet" by callers).
    /// </summary>
    private static async Task<string?> TryGitShowAsync(string repoRoot, string branch, string relPath)
    {
        return await TryGitShowOneAsync(repoRoot, branch, relPath)
            ?? await TryGitShowOneAsync(repoRoot, $"origin/{branch}", relPath);
    }

    private static async Task<string?> TryGitShowOneAsync(string repoRoot, string refSpec, string relPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add($"{refSpec}:{relPath}");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Per-doc iteration history. Each plan file's gen↔eval loop writes one
    /// <see cref="DeliverableRow"/> per iteration keyed on the synthetic
    /// workItemId <c>plan-doc:{spawn.Id}:{filename}</c> (matches the convention
    /// in <see cref="Forge.Api.Services.Stories.CrewStoryArtifactStrategy.ProducePlanFileAsync"/>).
    /// Decomposition has no per-doc iterations — returns empty.
    /// </summary>
    private static async Task<IResult> GetArtifactIterations(string storyId, string key, ControlDbContext control, ProductDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.Equals(key, "decomposition", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new
            {
                key,
                workItemId = (string?)null,
                status = "pending",
                maxIterations = 0,
                budgetSeconds = 0,
                generator = (object?)null,
                evaluator = (object?)null,
                iterations = Array.Empty<object>(),
                totalCostUsd = 0.0,
                pairAttemptCount = 1,
                maxPairAttempts = ScoringConstants.MaxPairAttempts,
                generatorTokensSinceWindow = 0L,
                generatorTokenCap = ScoringConstants.SloppyGeneratorTokenCap,
            });
        }

        var fileName = $"{key.ToLowerInvariant()}.md";
        var workItemId = $"plan-doc:{spawn.Id}:{fileName}";
        // Pull the Phase 52 sloppy meters (PairAttemptCount, BudgetWindowStartUtc)
        // alongside Points so the UI can render attempt n/m and the
        // tokens-used/cap ratio that drives reassignment.
        var workItemRow = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.Points, w.PairAttemptCount, w.BudgetWindowStartUtc })
            .FirstOrDefaultAsync();
        var workItemPoints = workItemRow?.Points;
        var maxIterations = ScoringConstants.MaxIterationsForPoints(workItemPoints ?? 0);
        var budgetSeconds = (int)CrewStoryArtifactStrategy.BudgetForPhase(
            workItemPoints ?? Math.Max(1, spawn.StoryPoints), $"plan:{fileName}").TotalSeconds;

        // SQLite can't ORDER BY DateTimeOffset directly. Order by Iter on the
        // server (cheap index hit) and then re-sort by CreatedAt on the client
        // for the rare same-iter tiebreak case.
        var rows = (await db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .OrderBy(d => d.Iter)
            .ToListAsync())
            .OrderBy(d => d.Iter)
            .ThenBy(d => d.CreatedAt)
            .ToList();

        var totalCost = await control.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId == workItemId)
            .SumAsync(u => (double?)u.CostUsd) ?? 0.0;

        // Phase 52 — windowed generator tokens for the sloppy-cap meter.
        // Computed after agent resolution below; declared up-front so the
        // response shape lines up cleanly.
        long generatorTokensSinceWindow = 0L;

        var iterations = rows.Select(d => new
        {
            id = d.Id,
            iter = d.Iter,
            score = d.Score,
            verdict = d.Verdict,
            generatorAgentId = d.GeneratorAgentId,
            evaluatorAgentId = d.EvaluatorAgentId,
            generatorSummary = d.GeneratorSummary,
            notes = d.Notes,
            genCostUsd = d.GenCostUsd,
            evalCostUsd = d.EvalCostUsd,
            judgeAgentId = d.JudgeAgentId,
            judgeScore = d.JudgeScore,
            judgeNotes = d.JudgeNotes,
            createdAt = d.CreatedAt.ToString("O"),
        }).ToList();

        // Status — derived from multiple signals:
        //   1. ExecutionStatus on the work-item row (authoritative: live/queued)
        //   2. Recent transcript event (fallback for plan-docs without TaskQueue)
        //   3. Deliverable verdict history
        // The old 60s-transcript-gap heuristic caused false "stalled" when the
        // LLM was in a long tool call. ExecutionStatus is set by the queue
        // worker and is the ground-truth for "is the runner running".
        var lastEvent = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(t => t.WorkItemId == workItemId)
            .OrderByDescending(t => t.Id)
            .Select(t => new { t.At })
            .FirstOrDefaultAsync();
        DateTimeOffset? lastEventAt = lastEvent?.At;
        var lastDeliverable = rows.LastOrDefault();

        // Check the authoritative execution status from the work-item row.
        var executionStatus = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => w.ExecutionStatus)
            .FirstOrDefaultAsync();
        var isRunnerLive = executionStatus is "live" or "queued";

        // Transcript recency is a secondary signal — only used when the
        // runner hasn't set execution status (e.g. plan-doc loops that
        // run inline in the orchestrator, not via TaskQueue).
        var isRecentEvent = !isRunnerLive
            && lastEventAt.HasValue
            && DateTimeOffset.UtcNow - lastEventAt.Value < TimeSpan.FromSeconds(90);

        // Count unanswered open questions — blocks pass and should show as
        // stalled so the operator knows to answer before resuming.
        var unansweredQuestions = 0;
        if (!string.IsNullOrEmpty(workItemId))
        {
            // Dedupe by ExternalId — latest row per id wins.
            var allQ = await db.DeliverableQuestions.AsNoTracking()
                .Where(q => q.WorkItemId == workItemId)
                .ToListAsync();
            var byExtId = allQ.GroupBy(q => q.ExternalId)
                .Select(g => g.OrderByDescending(q => q.CreatedAt).First());
            unansweredQuestions = byExtId.Count(q => q.Answer == null);
        }

        // Status priority:
        //   1. completed         — terminal: last verdict pass AND plan PR merged.
        //   2. awaiting-pr-review — terminal: last verdict pass, PR not yet merged.
        //   3. live              — runner reports the task is actively executing.
        //   4. pending           — no deliverables yet.
        //   5. stalled (verdict) — last verdict was reject/stalled/sloppy.
        //   6. iterating         — default for between iterations.
        string status;
        bool planPrMerged = false;
        // Phase 53 — when the spawn is in sequential mode, the relevant
        // "plan PR merged" gate for THIS doc is the doc's own PR, not the
        // legacy spawn.PlanPrId column. The `key` route param maps 1:1 to
        // the doc key.
        var docKeyForStatus = key.ToLowerInvariant();
        if (PlanDocStateHelpers.IsSequentialMode(spawn)
            && PlanDocStateHelpers.PlanDocFileName.ContainsKey(docKeyForStatus))
        {
            var docEntry = PlanDocStateHelpers.GetEntry(spawn, docKeyForStatus);
            if (!string.IsNullOrEmpty(docEntry.PrId))
            {
                var docPr = await db.Prs.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == docEntry.PrId);
                planPrMerged = string.Equals(docPr?.Col, "merged", StringComparison.OrdinalIgnoreCase);
            }
        }
        else if (!string.IsNullOrEmpty(spawn.PlanPrId))
        {
            var planPr = await db.Prs.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == spawn.PlanPrId);
            planPrMerged = string.Equals(planPr?.Col, "merged", StringComparison.OrdinalIgnoreCase);
        }
        bool lastWasPass = lastDeliverable is not null
            && string.Equals(lastDeliverable.Verdict, "pass", StringComparison.OrdinalIgnoreCase);

        if (lastWasPass && planPrMerged) status = "completed";
        else if (lastWasPass) status = "awaiting-pr-review";
        else if (isRunnerLive || isRecentEvent) status = "live";
        else if (lastDeliverable is null) status = "pending";
        else if (lastDeliverable.Verdict != null
            && (lastDeliverable.Verdict.StartsWith("stalled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lastDeliverable.Verdict, "reject", StringComparison.OrdinalIgnoreCase)))
            status = "stalled";
        else status = "iterating";

        // Surface the reason the doc is stalled so the UI can show it in a tooltip.
        string? stallReason = null;
        if (status == "stalled" && lastDeliverable?.Verdict != null)
        {
            stallReason = lastDeliverable.Verdict;
        }

        // Agent info — name + model for the latest gen/eval pair so the doc
        // tile can render avatars with hover tooltips. Two EF passes: agents
        // by id, then their crews for the model fallback.
        object? genInfo = null, evalInfo = null;
        if (lastDeliverable is not null)
        {
            // Resolve the two agent ids individually — clearer than a Contains
            // pass and avoids EF parameter-extraction quirks with array nulls.
            var genAgentId = lastDeliverable.GeneratorAgentId;
            var evalAgentId = lastDeliverable.EvaluatorAgentId;

            // Phase 52 — windowed gen tokens. Mirror the sloppy gate
            // (CheckTokenCapAsync) so the displayed tokens-used/cap ratio
            // matches the actual trigger. Pre-window rows for the current
            // generator are excluded; rows from the previous pair (different
            // AgentId) are excluded too — those got reset on reassignment.
            if (!string.IsNullOrEmpty(genAgentId))
            {
                (_, generatorTokensSinceWindow) = await SloppyDetector.CheckTokenCapAsync(
                    control, db, workItemId, genAgentId, workItemRow?.BudgetWindowStartUtc, default);
            }

            var genAgent = string.IsNullOrEmpty(genAgentId) ? null
                : await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == genAgentId);
            var evalAgent = string.IsNullOrEmpty(evalAgentId) ? null
                : await control.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == evalAgentId);

            // Resolve crew model fallbacks individually. Mirror exactly the
            // runtime resolution chain in
            // CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync so the UI
            // matches what the runner actually dispatches with:
            //   generator: agent.GeneratorModel ?? crew.Model
            //   evaluator: agent.EvaluatorModel ?? crew.EvaluatorModel ?? agent.GeneratorModel ?? crew.Model
            // The previous fallback collapsed evaluator to crew.Model, which
            // made a correctly-configured pair (gen=opus / evl=gpt) look
            // identical in the wizard.
            async Task<(string? Model, string? EvalModel)> CrewModelsAsync(string? crewId)
            {
                if (string.IsNullOrEmpty(crewId)) return (null, null);
                var row = await control.Crews.AsNoTracking()
                    .Where(c => c.Id == crewId)
                    .Select(c => new { c.Model, c.EvaluatorModel })
                    .FirstOrDefaultAsync();
                return (row?.Model, row?.EvaluatorModel);
            }

            if (genAgent is not null)
            {
                var (crewModel, _) = await CrewModelsAsync(genAgent.CrewId);
                genInfo = new
                {
                    agentId = genAgent.Id,
                    name = genAgent.Name,
                    model = !string.IsNullOrEmpty(genAgent.GeneratorModel) ? genAgent.GeneratorModel : crewModel,
                };
            }
            if (evalAgent is not null)
            {
                var (crewModel, crewEvalModel) = await CrewModelsAsync(evalAgent.CrewId);
                var resolved = !string.IsNullOrEmpty(evalAgent.EvaluatorModel) ? evalAgent.EvaluatorModel
                    : !string.IsNullOrEmpty(crewEvalModel) ? crewEvalModel
                    : !string.IsNullOrEmpty(evalAgent.GeneratorModel) ? evalAgent.GeneratorModel
                    : crewModel;
                evalInfo = new
                {
                    agentId = evalAgent.Id,
                    name = evalAgent.Name,
                    model = resolved,
                };
            }
        }

        return Results.Ok(new
        {
            key,
            workItemId,
            status,
            stallReason,
            maxIterations,
            budgetSeconds,
            unansweredQuestions,
            generator = genInfo,
            evaluator = evalInfo,
            iterations,
            totalCostUsd = totalCost,
            pairAttemptCount = workItemRow?.PairAttemptCount ?? 1,
            maxPairAttempts = ScoringConstants.MaxPairAttempts,
            generatorTokensSinceWindow,
            generatorTokenCap = ScoringConstants.SloppyGeneratorTokenCap,
        });
    }

    /// <summary>
    /// Phase 50 — operator-authored decision pin contract. <c>text</c> is
    /// the directive (e.g. "DefaultAction lives in sidecar — STOP
    /// flipping. tech-spec HC-3 is authoritative."). <c>workItemId</c>
    /// scopes the pin to one plan-doc / workstream; null means it
    /// applies to every iter on the spawn.
    /// </summary>
    public sealed record AddDecisionPinRequest(string Text, string? WorkItemId);

    private static async Task<IResult> ListDecisionPins(string storyId, ControlDbContext control, ProductDbContext db)
    {
        var spawn = await db.StorySpawns.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.IsNullOrEmpty(spawn.DecisionPinsJson))
        {
            return Results.Ok(Array.Empty<object>());
        }
        try
        {
            var pins = JsonSerializer.Deserialize<List<JsonElement>>(spawn.DecisionPinsJson, JsonOpts)
                ?? new List<JsonElement>();
            return Results.Ok(pins);
        }
        catch
        {
            // Corrupted JSON — return empty rather than 500 so the UI
            // can recover by re-pinning.
            return Results.Ok(Array.Empty<object>());
        }
    }

    private static async Task<IResult> AddDecisionPin(
        HttpContext ctx,
        string storyId,
        [FromBody] AddDecisionPinRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Text))
        {
            return Results.Problem(title: "text is required", statusCode: 400);
        }

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var actor = ctx.Request.Headers.TryGetValue("X-Forge-User", out var u) ? u.ToString() : "operator";

        // Load existing pins, append new one, persist. The id is a fresh
        // guid so the UI / DELETE endpoint can address it without race
        // conditions on text equality.
        var pins = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(spawn.DecisionPinsJson))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                    spawn.DecisionPinsJson, JsonOpts);
                if (existing is not null) pins = existing;
            }
            catch { /* corrupted — start fresh */ }
        }

        var newPin = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["text"] = req.Text.Trim(),
            ["workItemId"] = string.IsNullOrWhiteSpace(req.WorkItemId) ? null : req.WorkItemId.Trim(),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["createdBy"] = actor,
        };
        pins.Add(newPin);

        spawn.DecisionPinsJson = JsonSerializer.Serialize(pins, JsonOpts);
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("story-spawn.changed",
            new { kind = "decision-pin-added", spawn = ToContract(spawn) });

        return Results.Ok(newPin);
    }

    private static async Task<IResult> RemoveDecisionPin(
        HttpContext ctx,
        string storyId,
        string pinId,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        if (string.IsNullOrEmpty(spawn.DecisionPinsJson))
        {
            return Results.Ok(new { removed = 0 });
        }

        List<Dictionary<string, object?>> pins;
        try
        {
            pins = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                spawn.DecisionPinsJson, JsonOpts) ?? new();
        }
        catch
        {
            // Corrupted JSON — wipe so the operator can re-pin from scratch.
            spawn.DecisionPinsJson = null;
            spawn.UpdatedAt = DateTimeOffset.UtcNow;
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            return Results.Ok(new { removed = 0 });
        }

        var before = pins.Count;
        pins = pins.Where(p =>
            !p.TryGetValue("id", out var v)
            || v is null
            || !string.Equals(v.ToString(), pinId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var removed = before - pins.Count;

        spawn.DecisionPinsJson = pins.Count > 0
            ? JsonSerializer.Serialize(pins, JsonOpts)
            : null;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        if (removed > 0)
        {
            await hub.Clients.All.SendAsync("story-spawn.changed",
                new { kind = "decision-pin-removed", spawn = ToContract(spawn), pinId });
        }

        return Results.Ok(new { removed });
    }

    // ── Phase 63 — Pause / Resume ───────────────────────────────────────

    /// <summary>
    /// POST /api/stories/{storyId}/spawn/pause — hard-pause the entire spawn.
    /// Saves the current phase, flips to <c>paused</c>, kills ALL in-flight
    /// agents (workstream + plan-doc + spawn-level), and sets every
    /// non-terminal workstream to <c>Status="paused"</c> with prior status
    /// preserved for resume.
    /// </summary>
    private static async Task<IResult> PauseSpawn(
        HttpContext ctx,
        string storyId,
        ControlDbContext control,
        ProductDbContext db,
        IAgentRunner runner,
        IHubContext<ForgeHub> hub,
        ILoggerFactory loggerFactory)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ctx.RequestAborted);
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
        await db.SaveChangesAsync(ctx.RequestAborted);

        // Brief drain so any in-flight tick finishes its current SaveChanges
        // before we contend for the write lock (mirrors DeleteSpawn pattern).
        try { await Task.Delay(TimeSpan.FromSeconds(1), ctx.RequestAborted); }
        catch (OperationCanceledException) { return Results.StatusCode(499); }

        // 2. Build the full set of task IDs to cancel: workstream IDs +
        //    spawn.Id + plan-doc synthetic IDs.
        var workstreams = await db.Workstreams
            .Where(w => w.StorySpawnId == spawn.Id)
            .ToListAsync(ctx.RequestAborted);

        var planDocFiles = new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" };
        var allTaskIds = new HashSet<string>(StringComparer.Ordinal) { spawn.Id };
        foreach (var fn in planDocFiles) allTaskIds.Add($"plan-doc:{spawn.Id}:{fn}");
        foreach (var w in workstreams) allTaskIds.Add(w.Id);

        // 3. Cancel in-flight runner invocations + open queue rows + claims.
        var invocationsCancelled = 0;
        foreach (var wid in allTaskIds)
        {
            try { invocationsCancelled += await runner.CancelByTaskAsync(wid, ctx.RequestAborted); }
            catch { /* best-effort */ }
        }

        var nowOff = DateTimeOffset.UtcNow;
        var openQueueRows = await db.TaskQueue
            .Where(q => allTaskIds.Contains(q.WorkItemId)
                && q.State != "completed" && q.State != "cancelled")
            .ToListAsync(ctx.RequestAborted);
        foreach (var q in openQueueRows)
        {
            q.State = "cancelled";
            q.CompletedAt = nowOff;
            q.LastUpdatedAt = nowOff;
        }

        var openClaims = await control.AgentClaims
            .Where(c => allTaskIds.Contains(c.WorkItemId) && c.ReleasedAt == null)
            .ToListAsync(ctx.RequestAborted);
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
            .ToListAsync(ctx.RequestAborted);
        foreach (var wi in workItems)
        {
            wi.ExecutionStatus = ExecutionStatuses.Idle;
            wi.UpdatedAt = nowOff;
        }

        await db.SaveChangesAsync(ctx.RequestAborted);

        logger.LogInformation(
            "Spawn {SpawnId} paused from phase '{FromPhase}'. "
            + "Workstreams paused: {WsPaused}, invocations cancelled: {InvCancelled}, "
            + "queue rows cancelled: {QueueCancelled}, claims released: {ClaimsReleased}.",
            spawn.Id, spawn.PausedFromPhase, workstreamsPaused,
            invocationsCancelled, openQueueRows.Count, openClaims.Count);

        await hub.Clients.All.SendAsync(
            "story-spawn.changed",
            new { kind = "paused", spawn = ToContract(spawn) },
            ctx.RequestAborted);

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

    /// <summary>
    /// POST /api/stories/{storyId}/spawn/resume — resume a paused spawn.
    /// Restores the phase from <see cref="StorySpawnRow.PausedFromPhase"/>
    /// and flips every <c>Status="paused"</c> workstream back to its prior
    /// status.
    /// </summary>
    private static async Task<IResult> ResumeSpawn(
        HttpContext ctx,
        string storyId,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        StorySpawnOrchestrator orchestrator,
        ILoggerFactory loggerFactory)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ctx.RequestAborted);
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
            .ToListAsync(ctx.RequestAborted);

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
                .ToListAsync(ctx.RequestAborted);
            foreach (var wi in workItems)
            {
                wi.ExecutionStatus = ExecutionStatuses.Idle;
                wi.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ctx.RequestAborted);

        logger.LogInformation(
            "Spawn {SpawnId} resumed to phase '{Phase}'. Workstreams resumed: {WsResumed}.",
            spawn.Id, fromPhase, workstreamsResumed);

        await hub.Clients.All.SendAsync(
            "story-spawn.changed",
            new { kind = "resumed", spawn = ToContract(spawn) },
            ctx.RequestAborted);

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

    private static object ToContract(StorySpawnRow row) => new
    {
        id = row.Id,
        storyId = row.StoryId,
        storyTitle = row.StoryTitle,
        storyDescription = row.StoryDescription,
        storyPoints = row.StoryPoints,
        repoId = row.RepoId,
        phase = row.Phase,
        // Phase 45 — operator-chosen base branch (defaults to "main"). The
        // empty-string fallback covers rows persisted before Phase 45's
        // backfill ran; the orchestrator treats those as "main" as well.
        baseBranch = string.IsNullOrEmpty(row.BaseBranch) ? "main" : row.BaseBranch,
        // Phase 46 — auto-merge flag.
        autoMerge = row.AutoMerge,
        planBranch = row.PlanBranch,
        planPrId = row.PlanPrId,
        // Phase 53 — sequential plan-doc PRs. Null on legacy spawns;
        // non-null array on new spawns. UI prefers this list over the
        // single planPrId/planBranch when populated.
        planDocPrs = PlanDocStateHelpers.IsSequentialMode(row)
            ? (object)PlanDocStateHelpers.GetPlanDocStates(row)
                .Select(e => new { key = e.Key, branch = e.Branch, prId = e.PrId })
                .ToArray()
            : null,
        decompositionJson = row.DecompositionJson,
        decompositionApproved = row.DecompositionApproved,
        qaBranch = row.QaBranch,
        qaPrId = row.QaPrId,
        createdAt = row.CreatedAt,
        updatedAt = row.UpdatedAt,
        // Phase 37 — operator-visible failure surfaced on the workstream UI.
        // Cleared by the orchestrator on the next successful step or by an
        // explicit POST /api/stories/{storyId}/spawn/retry from the UI.
        lastError = row.LastError,
        lastErrorAt = row.LastErrorAt,
        // Phase 43 — operator-uploaded reference documents.
        attachments = string.IsNullOrEmpty(row.AttachmentsJson)
            ? (object)Array.Empty<object>()
            : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(row.AttachmentsJson)!,
        // Phase 50 — operator-pinned decisions prepended to every iter
        // prompt. Empty array when no pins exist; UI uses the count to
        // surface a "📌 N pins" badge on the spawn header.
        decisionPins = string.IsNullOrEmpty(row.DecisionPinsJson)
            ? (object)Array.Empty<object>()
            : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(row.DecisionPinsJson)!,
        // Phase 63 — the phase the spawn was in before the operator paused it.
        // Null unless phase == "paused". UI can use this to show "paused from implement" etc.
        pausedFromPhase = row.PausedFromPhase,
    };

    private static string SlugFor(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return string.IsNullOrEmpty(s) ? "ws" : s;
    }

    private static bool HasCycle(Dictionary<string, List<string>> adjacency)
    {
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // 0 = unvisited, 1 = on-stack, 2 = done.
        foreach (var node in adjacency.Keys)
        {
            if (DfsHasCycle(node, adjacency, visited)) return true;
        }
        return false;
    }

    private static bool DfsHasCycle(
        string node,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, int> visited)
    {
        if (!visited.TryGetValue(node, out var state)) state = 0;
        if (state == 1) return true;
        if (state == 2) return false;
        visited[node] = 1;
        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var n in neighbors)
            {
                if (DfsHasCycle(n, adjacency, visited)) return true;
            }
        }
        visited[node] = 2;
        return false;
    }

    // ── Phase 43 — story attachments ─────────────────────────────────────

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".csv", ".yaml", ".yml", ".toml", ".xml", ".log", ".ini", ".cfg",
    };

    private const int MaxFiles = 10;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    private static async Task<IResult> UploadAttachments(
        HttpContext ctx,
        IForgeDbPaths paths)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (!ctx.Request.HasFormContentType)
        {
            return Results.Problem(title: "Expected multipart/form-data", statusCode: 400);
        }

        var form = await ctx.Request.ReadFormAsync();
        var files = form.Files;
        if (files.Count == 0) return Results.Problem(title: "No files uploaded", statusCode: 400);
        if (files.Count > MaxFiles) return Results.Problem(title: $"Max {MaxFiles} files allowed", statusCode: 400);

        var stateDir = paths.Folder;
        var uploadId = Guid.NewGuid().ToString("N")[..12];
        var uploadDir = Path.Combine(stateDir, "attachments", uploadId);
        Directory.CreateDirectory(uploadDir);

        var result = new List<object>();
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(ext))
            {
                return Results.Problem(
                    title: $"File type '{ext}' not allowed. Allowed: {string.Join(", ", AllowedExtensions)}",
                    statusCode: 400);
            }
            if (file.Length > MaxFileSizeBytes)
            {
                return Results.Problem(title: $"File '{file.FileName}' exceeds 5 MB limit", statusCode: 400);
            }

            var safeFileName = SanitizeFileName(file.FileName);
            var diskPath = Path.Combine(uploadDir, safeFileName);
            await using var stream = new FileStream(diskPath, FileMode.Create);
            await file.CopyToAsync(stream);

            result.Add(new
            {
                id = uploadId,
                fileName = file.FileName,
                storagePath = Path.Combine("attachments", uploadId, safeFileName).Replace('\\', '/'),
                mimeType = file.ContentType ?? "application/octet-stream",
                sizeBytes = file.Length,
            });
        }

        return Results.Ok(new { uploadId, files = result });
    }

    internal static List<object> ResolveUploadedFiles(
        IReadOnlyList<string> uploadIds, IForgeDbPaths paths)
    {
        var stateDir = paths.Folder;
        var attachments = new List<object>();

        foreach (var uploadId in uploadIds)
        {
            var uploadDir = Path.Combine(stateDir, "attachments", uploadId);
            if (!Directory.Exists(uploadDir)) continue;

            foreach (var filePath in Directory.EnumerateFiles(uploadDir))
            {
                var fi = new FileInfo(filePath);
                attachments.Add(new
                {
                    id = uploadId,
                    fileName = fi.Name,
                    storagePath = Path.Combine("attachments", uploadId, fi.Name).Replace('\\', '/'),
                    mimeType = GuessMimeType(fi.Extension),
                    sizeBytes = fi.Length,
                });
            }
        }

        return attachments;
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        var safe = sb.ToString().Trim('-');
        if (string.IsNullOrEmpty(safe)) safe = "file";
        return safe + ext;
    }

    private static IResult GetAttachmentContent(
        string uploadId, string fileName, IForgeDbPaths paths)
    {
        var stateDir = paths.Folder;
        var filePath = Path.Combine(stateDir, "attachments", uploadId, fileName);
        if (!File.Exists(filePath))
            return Results.Problem(title: "Attachment not found", statusCode: 404);
        var ext = Path.GetExtension(fileName);
        var mime = GuessMimeType(ext);
        return Results.File(filePath, mime, fileName);
    }

    private static string GuessMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".yaml" or ".yml" => "text/yaml",
        ".toml" => "text/toml",
        ".xml" => "application/xml",
        _ => "application/octet-stream",
    };
}

/// <summary>
/// forge_implementation.md Phase 28 — small helper for parsing the JSON
/// arrays stored on <see cref="WorkstreamRow.CardsJson"/> /
/// <see cref="WorkstreamRow.PrsJson"/>. Mirrors <c>CardMapper</c> but
/// scoped narrowly so this endpoint compiles independently.
/// </summary>
internal static class StoriesJsonHelpers
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static List<string> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, Opts) ?? new List<string>(); }
        catch { return new List<string>(); }
    }
}
