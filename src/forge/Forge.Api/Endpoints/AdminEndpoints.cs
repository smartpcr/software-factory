// -----------------------------------------------------------------------
// <copyright file="AdminEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 17 follow-up — admin-only diagnostic endpoints. Lets the operator
/// manually fire the queue worker tick (debug "why isn't this claiming")
/// and see the per-sweep counts.
/// </summary>
public static class AdminEndpoints
{
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/queue/tick", async (CrewQueueWorker worker, CancellationToken ct) =>
        {
            var r = await worker.RunOnceAsync(ct);
            return Results.Ok(new
            {
                ackTimeouts = r.AckTimeouts,
                claims = r.Claims,
                strainSignals = r.StrainSignals,
                recovered = r.Recovered,
                claimSkips = r.ClaimSkips ?? Array.Empty<CrewQueueWorker.ClaimSkip>(),
                at = DateTimeOffset.UtcNow,
            });
        }).WithTags("Admin").WithName("AdminQueueTick");

        // 2026-05-07 — manual transcript prune. Useful for the one-off
        // "reclaim 2.2 GB right now" run after the janitor lands. The
        // periodic background tick handles ongoing maintenance; this
        // endpoint exists so operators can also (a) dry-run the policy
        // before flipping it on and (b) opt into VACUUM (which the
        // periodic tick deliberately skips because it stalls writers on
        // a multi-GB DB). Returns full PruneStats so the caller sees
        // exactly which work items were touched and how many rows went.
        app.MapPost("/api/admin/transcripts/prune", async (
            HttpContext ctx,
            TranscriptRetentionJanitor janitor,
            [FromQuery] bool? dryRun,
            [FromQuery] bool? vacuum,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            var stats = await janitor.RunOnceAsync(dryRun: dryRun ?? false, vacuum: vacuum ?? false, ct);
            return Results.Ok(stats);
        }).WithTags("Admin").WithName("AdminPruneTranscripts");

        // 2026-05-07 — DB stats for the Settings → Maintenance UI. Returns
        // file size and per-table breakdowns so the operator sees whether
        // a prune will actually help BEFORE running it (and the savings
        // AFTER). Read-only — safe to call on every Settings page load.
        app.MapGet("/api/admin/db/stats", async (
            Forge.Api.Persistence.IForgeDbPaths paths,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            CancellationToken ct) =>
        {
            var dbPath = paths.ControlDbPath();
            long fileBytes = 0;
            DateTimeOffset? modifiedAt = null;
            if (File.Exists(dbPath))
            {
                var fi = new FileInfo(dbPath);
                fileBytes = fi.Length;
                modifiedAt = fi.LastWriteTimeUtc;
            }

            // Counts on the rows that dominate disk: transcript +
            // deliverables. Cheap aggregate queries (≤ 100 ms even at 2 GB).
            var transcriptRows = await db.TaskTranscriptEvents.AsNoTracking().CountAsync(ct);
            var deliverableRows = await db.Deliverables.AsNoTracking().CountAsync(ct);
            var transcriptBytes = await db.TaskTranscriptEvents.AsNoTracking()
                .SumAsync(e => (long?)e.PayloadJson.Length, ct) ?? 0;

            return Results.Ok(new
            {
                dbPath,
                fileBytes,
                modifiedAt,
                transcriptRows,
                transcriptPayloadBytes = transcriptBytes,
                deliverableRows,
            });
        }).WithTags("Admin").WithName("AdminDbStats");

        // Phase 49 follow-up — prune stale per-workstream worktrees from
        // unstarted (`Status="planning"`) or tombstoned
        // (`historic` / `cancelled` / `complete`) workstreams. The
        // orchestrator used to pre-create a worktree at claim time even
        // when the agent never ran (EnsureBranchAsync materialised one
        // just to `git push` a new branch), and those stuck around at
        // 100+ char Windows paths. The Phase 49 EnsureBranchAsync rewrite
        // stops new accumulation; this endpoint cleans the existing pile.
        // Returns the list of removed paths for the audit log.
        //
        // Optional `?dryRun=true` returns what WOULD be removed without
        // touching disk.
        app.MapPost("/api/admin/worktrees/prune", async (
            HttpContext ctx,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            Forge.Api.Services.Stories.IStoryGitOps git,
            [FromQuery] bool? dryRun,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;

            var dry = dryRun ?? false;
            var removed = new List<object>();
            var skipped = new List<object>();

            // Bucket workstreams by repo so we enumerate each repo's
            // .worktree/ directory only once.
            var workstreams = await db.Workstreams.AsNoTracking()
                .Where(w => !string.IsNullOrEmpty(w.RepoId))
                .ToListAsync(ct);
            // Phase 80.B — repos live in the per-product DB; this handler is
            // scoped to the active product (it reads db.Workstreams above), so
            // read the full repo rows from the same product context.
            var repos = await db.Repos.AsNoTracking()
                .Where(r => !string.IsNullOrEmpty(r.CloneRoot))
                .ToListAsync(ct);
            var byRepo = workstreams.GroupBy(w => w.RepoId!, StringComparer.Ordinal);

            foreach (var group in byRepo)
            {
                var repo = repos.FirstOrDefault(r => r.Id == group.Key);
                if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) continue;
                if (!Directory.Exists(repo.CloneRoot)) continue;

                // Workstreams whose worktree is safe to remove:
                //   - planning   (never claimed by any agent yet)
                //   - cancelled  (operator-killed)
                //   - historic   (tombstoned by the reconciler — superseded
                //                 by a plan-doc reshape)
                //   - complete   (PR merged, work shipped to main)
                // We KEEP worktrees for: active / review / merging / blocked
                // / stalled — those are in-flight or in operator triage and
                // re-reading the worktree may still surface useful diff.
                static bool ShouldPrune(string status) => status switch
                {
                    "planning" => true,
                    "cancelled" => true,
                    Forge.Api.Services.Stories.WorkstreamStatuses.Historic => true,
                    "complete" => true,
                    _ => false,
                };

                foreach (var ws in group.Where(w => ShouldPrune(w.Status ?? string.Empty)))
                {
                    if (string.IsNullOrEmpty(ws.Branch)) continue;

                    if (dry)
                    {
                        removed.Add(new { wsId = ws.Id, status = ws.Status, branch = ws.Branch, dryRun = true });
                        continue;
                    }

                    try
                    {
                        await git.RemoveWorktreeAsync(repo, ws.Branch, ct);
                        removed.Add(new { wsId = ws.Id, status = ws.Status, branch = ws.Branch });
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new { wsId = ws.Id, error = ex.Message });
                    }
                }

                // Phase 49 follow-up — also catch orphan worktrees whose
                // branch has no matching workstream row at all. These
                // happen when the plan-doc reconciler renames a phase /
                // stage: it generates a NEW workstream row (with the new
                // branch name) and tombstones the OLD row, but the old
                // row's git worktree was created by the legacy
                // EnsureBranchAsync path and survives the row deletion.
                // The current branch→row join misses these because no
                // row carries the old branch anymore. Walk
                // `git worktree list --porcelain`, find every registered
                // worktree under {cloneRoot}/.worktree/ whose branch
                // matches no live workstream row, and remove it.
                var liveBranches = new HashSet<string>(
                    group.Select(w => w.Branch).Where(b => !string.IsNullOrEmpty(b))!,
                    StringComparer.Ordinal);

                IReadOnlyList<(string Path, string Branch)> registered;
                try
                {
                    registered = await EnumerateWorktreesAsync(repo.CloneRoot, ct);
                }
                catch (Exception ex)
                {
                    skipped.Add(new { repo = repo.Id, orphanScanError = ex.Message });
                    continue;
                }

                foreach (var (path, branch) in registered)
                {
                    // Only consider worktrees that look like per-WS dirs:
                    // canonical engineer slugs (`stage-…`) and ad-hoc
                    // crew-queue rows (`ws-…`). Plan / QA / base-branch
                    // worktrees use stable names and are owned by other
                    // lifecycle hooks (MergeAsync's merge-{base} fallback,
                    // DeleteSpawn's plan/qa/merge cleanup).
                    var leaf = System.IO.Path.GetFileName(path) ?? string.Empty;
                    var isPerWs = leaf.StartsWith("stage-", StringComparison.Ordinal)
                        || leaf.StartsWith("ws-", StringComparison.Ordinal);
                    if (!isPerWs) continue;

                    if (string.IsNullOrEmpty(branch)) continue;
                    if (liveBranches.Contains(branch)) continue; // matched by the workstreams loop above

                    if (dry)
                    {
                        removed.Add(new { wsId = (string?)null, status = "orphan", branch, path, dryRun = true });
                        continue;
                    }

                    try
                    {
                        await git.RemoveWorktreeAsync(repo, branch, ct);
                        removed.Add(new { wsId = (string?)null, status = "orphan", branch, path });
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new { branch, path, error = ex.Message });
                    }
                }
            }

            return Results.Ok(new
            {
                dryRun = dry,
                removedCount = removed.Count,
                skippedCount = skipped.Count,
                removed,
                skipped,
                at = DateTimeOffset.UtcNow,
            });
        }).WithTags("Admin").WithName("AdminPruneWorktrees");

        // Phase 49 follow-up — backfill engineer replies on resolved PR
        // comments that were resolved BEFORE the reply-or-fix patch shipped
        // (2026-05-10 ~15:39 PDT). Re-runs ProduceReviewFixAsync for each
        // `state=resolved` reviewer comment that has no child reply row,
        // posts the reply on the PR thread, persists a child PrCommentRow.
        // No commit even when the engineer returns Path-A output — the PR
        // is already merged, the file change has shipped, only the missing
        // reply needs to be filled in. Supports `?dryRun=true` to see what
        // would run before paying the LLM cost.
        app.MapPost("/api/admin/pr-comments/backfill-replies", async (
            HttpContext ctx,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            Forge.Api.Services.Stories.IStoryArtifactStrategy strategy,
            Forge.Api.Services.Stories.IStoryProviderOps provider,
            [FromQuery] string prId,
            [FromQuery] bool? dryRun,
            [FromQuery] bool? includeActive,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            if (string.IsNullOrWhiteSpace(prId))
                return Results.Problem(title: "prId query parameter is required", statusCode: 400);

            var dry = dryRun ?? false;
            var allowActive = includeActive ?? false;

            // Find resolved reviewer comments on this PR whose ThreadId
            // has NO reply row (ParentCommentId pointing at them). Those
            // are the ones the old commit-only path resolved without
            // posting a response.
            var allComments = await db.PrComments.AsNoTracking()
                .Where(c => c.PrId == prId)
                .ToListAsync(ct);
            if (allComments.Count == 0)
                return Results.Problem(title: $"No comments found for PR '{prId}'", statusCode: 404);

            var replyParentIds = new HashSet<long>(
                allComments.Where(c => c.ParentCommentId.HasValue).Select(c => c.ParentCommentId!.Value));
            // 2026-05-12 — when `includeActive=true`, also surface comments
            // that were stranded in state="active" because something merged
            // the PR before the orchestrator's DriveReviewAsync loop could
            // address them (e.g. AutoMergeWatcher race before its
            // active-comments guard was added). We post the reply AND flip
            // the local row to resolved so the UI clears.
            var needsReply = allComments
                .Where(c => c.AuthorKind == "agent" && c.AuthorId != "eng-default")
                .Where(c => (c.State == "resolved" || (allowActive && c.State == "active"))
                    && !replyParentIds.Contains(c.Id))
                .Where(c => !c.ParentCommentId.HasValue) // top-level only — replies don't need their own reply
                .ToList();

            if (needsReply.Count == 0)
            {
                return Results.Ok(new
                {
                    dryRun = dry,
                    prId,
                    needsReplyCount = 0,
                    backfilled = Array.Empty<object>(),
                    message = "No top-level resolved reviewer comments are missing replies on this PR.",
                });
            }

            // Look up the workstream + spawn that owns this PR so we can
            // call ProduceReviewFixAsync (needs spawn + workstream context).
            var pr = await db.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (pr is null || string.IsNullOrEmpty(pr.Workstream))
                return Results.Problem(title: $"PR '{prId}' is not bound to a workstream", statusCode: 404);

            var ws = await db.Workstreams.AsNoTracking().FirstOrDefaultAsync(w => w.Id == pr.Workstream, ct);
            if (ws is null || string.IsNullOrEmpty(ws.StorySpawnId))
                return Results.Problem(title: $"Workstream '{pr.Workstream}' has no story spawn binding", statusCode: 404);

            var spawn = await db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (spawn is null)
                return Results.Problem(title: $"Spawn '{ws.StorySpawnId}' not found", statusCode: 404);

            if (dry)
            {
                return Results.Ok(new
                {
                    dryRun = true,
                    prId,
                    needsReplyCount = needsReply.Count,
                    wouldBackfill = needsReply.Select(c => new { c.Id, c.FilePath, c.Line, bodyPreview = (c.Body ?? string.Empty).Length > 120 ? c.Body![..120] + "…" : c.Body }).ToList(),
                });
            }

            var engineerId = "eng-default";
            // Best-effort: pull the engineer agent that originally addressed
            // the comments so the reply attribution matches.
            var agentList = Forge.Api.Persistence.CardMapper.Deserialize<List<string>>(ws.AgentsJson) ?? new();
            if (agentList.Count > 0) engineerId = agentList[0];

            var backfilled = new List<object>();
            var failed = new List<object>();

            foreach (var c in needsReply)
            {
                try
                {
                    var context = new Forge.Api.Services.Stories.ReviewCommentContext(
                        FilePath: c.FilePath ?? string.Empty,
                        Line: c.Line ?? 0,
                        Body: c.Body ?? string.Empty,
                        AuthoringAgentId: engineerId);
                    var fix = await strategy.ProduceReviewFixAsync(spawn, ws, context, ct);
                    // Backfill ONLY the reply — no commit, the file already
                    // landed via the original commit-only path.
                    var replyId = await provider.ReplyPrCommentAsync(c.Id, fix.ReplyBody, engineerId, ct);
                    // If the comment was active (stranded — PR merged before
                    // DriveReviewAsync resolved it), also flip the parent row
                    // to resolved so the local state matches the UI's
                    // expectation. Resolved-already rows are no-ops here.
                    if (c.State == "active")
                    {
                        try
                        {
                            await provider.ResolvePrCommentAsync(c.Id, engineerId, ct);
                        }
                        catch (Exception resolveEx)
                        {
                            // Reply landed but resolve flip failed — log and
                            // continue so the next comment still processes.
                            failed.Add(new { commentId = c.Id, error = $"reply OK, resolve failed: {resolveEx.Message}" });
                            continue;
                        }
                    }
                    backfilled.Add(new { commentId = c.Id, replyRowId = replyId, file = c.FilePath, line = c.Line, isReplyOnly = fix.IsReplyOnly, wasActive = c.State == "active" });
                }
                catch (Exception ex)
                {
                    failed.Add(new { commentId = c.Id, error = ex.Message });
                }
            }

            return Results.Ok(new
            {
                dryRun = false,
                prId,
                needsReplyCount = needsReply.Count,
                backfilledCount = backfilled.Count,
                failedCount = failed.Count,
                backfilled,
                failed,
                at = DateTimeOffset.UtcNow,
            });
        }).WithTags("Admin").WithName("AdminBackfillPrReplies");

        // Phase 49 follow-up — backfill missing ThreadId values on
        // PrComments rows. Pre-fix data had ThreadId="" for every
        // production-path comment (ProviderStoryProviderOps was minting
        // empty strings instead of unique guids), which collapsed every
        // comment on a PR into one UI thread. The post-fix grouping
        // logic in PrCommentsList.tsx has a defensive fallback for empty
        // threadIds, but persisting real ids brings the cached rows in
        // line with newly-created ones and removes the legacy code path
        // once data is migrated.
        //
        // Algorithm:
        //   1. For each PrCommentRow with ParentCommentId == null and
        //      empty ThreadId: set ThreadId = "thread-{Id}-{guid}".
        //   2. For each row with ParentCommentId != null and empty
        //      ThreadId: inherit the parent's (now-populated) ThreadId.
        //
        // Idempotent — only touches rows where ThreadId is empty.
        // Supports ?dryRun=true to see what would change.
        app.MapPost("/api/admin/pr-comments/backfill-thread-ids", async (
            HttpContext ctx,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            [FromQuery] bool? dryRun,
            [FromQuery] string? prId,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            var dry = dryRun ?? false;

            // Pull only rows that need fixing. Scoping by PrId is
            // optional — null/empty = global backfill across all PRs.
            var query = db.PrComments.Where(c => c.ThreadId == string.Empty);
            if (!string.IsNullOrWhiteSpace(prId)) query = query.Where(c => c.PrId == prId);
            var rows = await query.ToListAsync(ct);

            if (rows.Count == 0)
            {
                return Results.Ok(new
                {
                    dryRun = dry,
                    scope = prId ?? "(all PRs)",
                    rowsNeedingThreadId = 0,
                    message = "No rows with empty ThreadId — nothing to do.",
                });
            }

            // Step 1: parents (ParentCommentId is null) get a fresh guid.
            //         We mint deterministic-ish ids tied to the row Id so a
            //         second run on the same dataset produces the same value
            //         (helpful when comparing snapshots across environments).
            var parents = rows.Where(c => !c.ParentCommentId.HasValue).ToList();
            var replies = rows.Where(c => c.ParentCommentId.HasValue).ToList();

            var parentChanges = new List<object>();
            foreach (var p in parents)
            {
                var newId = $"thread-{p.Id}-{Guid.NewGuid():N}";
                if (!dry) p.ThreadId = newId;
                p.UpdatedAt = DateTimeOffset.UtcNow;
                parentChanges.Add(new { rowId = p.Id, prId = p.PrId, oldThreadId = string.Empty, newThreadId = newId });
            }

            // Step 2: replies inherit their parent's ThreadId. The parent
            //         may itself have been backfilled in Step 1 (already
            //         in-memory via EF tracking) or may already have a
            //         valid ThreadId from earlier.
            var replyChanges = new List<object>();
            var unresolvedReplies = new List<object>();
            foreach (var r in replies)
            {
                var parent = await db.PrComments.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == r.ParentCommentId!.Value, ct);
                if (parent is null)
                {
                    unresolvedReplies.Add(new { rowId = r.Id, parentCommentId = r.ParentCommentId, reason = "parent-not-found" });
                    continue;
                }

                // If parent was in our backfill set, EF's change tracker
                // returns the in-memory copy with the updated ThreadId
                // when we query via the tracked context. Force a fetch
                // through the tracked DbSet to get that.
                var trackedParent = parents.FirstOrDefault(p => p.Id == r.ParentCommentId!.Value);
                var effectiveThreadId = trackedParent?.ThreadId ?? parent.ThreadId;
                if (string.IsNullOrEmpty(effectiveThreadId))
                {
                    // Parent ALSO has empty threadId and wasn't in our
                    // step-1 batch — mint one for it now so the reply
                    // has somewhere to land. (Edge case: parent was
                    // scoped out by prId filter mismatch.)
                    effectiveThreadId = $"thread-{parent.Id}-{Guid.NewGuid():N}";
                    if (!dry)
                    {
                        var tp = await db.PrComments.FirstOrDefaultAsync(x => x.Id == parent.Id, ct);
                        if (tp is not null)
                        {
                            tp.ThreadId = effectiveThreadId;
                            tp.UpdatedAt = DateTimeOffset.UtcNow;
                        }
                    }
                }

                if (!dry) r.ThreadId = effectiveThreadId;
                r.UpdatedAt = DateTimeOffset.UtcNow;
                replyChanges.Add(new { rowId = r.Id, prId = r.PrId, parentCommentId = r.ParentCommentId, inheritedThreadId = effectiveThreadId });
            }

            if (!dry) await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                dryRun = dry,
                scope = prId ?? "(all PRs)",
                rowsNeedingThreadId = rows.Count,
                parentsBackfilled = parentChanges.Count,
                repliesBackfilled = replyChanges.Count,
                unresolvedReplies = unresolvedReplies.Count,
                parents = parentChanges,
                replies = replyChanges,
                problems = unresolvedReplies,
                at = DateTimeOffset.UtcNow,
            });
        }).WithTags("Admin").WithName("AdminBackfillPrCommentThreadIds");

        // Phase 49 follow-up — backfill stale stats (Commits, Files,
        // Additions, Deletions) on already-merged PRs. Once a PR
        // transitions to merged, PrSyncJob's IsActive=true candidate
        // filter excludes it and the cached row freezes at whatever
        // CreatePullRequestAsync recorded — typically the initial-push
        // count, not the post-review-fix count. The merge-path now
        // captures these in MergePrAsync but for PRs merged BEFORE that
        // fix shipped, the values are still stale. This endpoint pulls
        // the latest stats from each provider and writes them back.
        //
        // Supports two scopes:
        //   POST /api/admin/prs/{prId}/refresh-stats        — single PR
        //   POST /api/admin/prs/backfill-merged-stats       — all merged
        //                                                     PRs in DB
        // Both honour ?dryRun=true to preview without writes.
        app.MapPost("/api/admin/prs/{prId}/refresh-stats", async (
            HttpContext ctx,
            string prId,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            IServiceProvider sp,
            [FromQuery] bool? dryRun,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            var dry = dryRun ?? false;

            var row = await db.Prs.FirstOrDefaultAsync(p => p.Id == prId, ct);
            if (row is null)
                return Results.Problem(title: $"PR '{prId}' not found", statusCode: 404);
            if (string.IsNullOrEmpty(row.Provider))
                return Results.Problem(title: $"PR '{prId}' has no provider configured", statusCode: 400);

            var providers = sp.GetServices<Forge.Api.Providers.IForgeProvider>();
            var provider = providers.FirstOrDefault(p =>
                string.Equals(p.Kind, row.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return Results.Problem(title: $"Provider '{row.Provider}' not registered", statusCode: 500);

            try
            {
                var latest = await provider.GetPullRequestAsync(prId, ct);
                if (latest is null)
                    return Results.Problem(title: $"Provider returned no PR for '{prId}'", statusCode: 404);

                var before = new { row.Commits, row.Files, row.Additions, row.Deletions };
                var after = new { latest.Commits, latest.Files, latest.Additions, latest.Deletions };

                if (!dry)
                {
                    row.Commits = latest.Commits;
                    row.Files = latest.Files;
                    row.Additions = latest.Additions;
                    row.Deletions = latest.Deletions;
                    row.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(new { dryRun = dry, prId, before, after, at = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: $"Provider GetPullRequestAsync failed for '{prId}'",
                    detail: ex.Message,
                    statusCode: 502);
            }
        }).WithTags("Admin").WithName("AdminRefreshPrStats");

        app.MapPost("/api/admin/prs/backfill-merged-stats", async (
            HttpContext ctx,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            IServiceProvider sp,
            [FromQuery] bool? dryRun,
            [FromQuery] int? limit,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            var dry = dryRun ?? false;
            var cap = Math.Max(1, Math.Min(limit ?? 200, 1000));

            // Merged PRs only (Col=="merged" OR IsActive=false). Ordered by
            // most-recently-updated so the freshest data refreshes first
            // when the cap is hit.
            var candidates = await db.Prs
                .Where(p => !p.IsActive || p.Col == "merged")
                .Where(p => !string.IsNullOrEmpty(p.Provider))
                .OrderByDescending(p => p.UpdatedAt ?? string.Empty)
                .Take(cap)
                .ToListAsync(ct);

            var providers = sp.GetServices<Forge.Api.Providers.IForgeProvider>().ToList();
            var updated = new List<object>();
            var failed = new List<object>();
            foreach (var row in candidates)
            {
                var provider = providers.FirstOrDefault(p =>
                    string.Equals(p.Kind, row.Provider, StringComparison.OrdinalIgnoreCase));
                if (provider is null)
                {
                    failed.Add(new { prId = row.Id, reason = $"provider '{row.Provider}' not registered" });
                    continue;
                }
                try
                {
                    var latest = await provider.GetPullRequestAsync(row.Id, ct);
                    if (latest is null)
                    {
                        failed.Add(new { prId = row.Id, reason = "provider returned null" });
                        continue;
                    }
                    var changed =
                        row.Commits != latest.Commits
                        || row.Files != latest.Files
                        || row.Additions != latest.Additions
                        || row.Deletions != latest.Deletions;
                    if (changed && !dry)
                    {
                        row.Commits = latest.Commits;
                        row.Files = latest.Files;
                        row.Additions = latest.Additions;
                        row.Deletions = latest.Deletions;
                        row.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
                    }
                    if (changed)
                    {
                        updated.Add(new
                        {
                            prId = row.Id,
                            commits = new { before = row.Commits, after = latest.Commits },
                            files = new { before = row.Files, after = latest.Files },
                            additions = new { before = row.Additions, after = latest.Additions },
                            deletions = new { before = row.Deletions, after = latest.Deletions },
                        });
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new { prId = row.Id, reason = ex.Message });
                }
            }

            if (!dry) await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                dryRun = dry,
                scope = "merged",
                considered = candidates.Count,
                changedCount = updated.Count,
                failedCount = failed.Count,
                changed = updated,
                failed,
                at = DateTimeOffset.UtcNow,
            });
        }).WithTags("Admin").WithName("AdminBackfillMergedPrStats");

        // Phase 49 follow-up — migrate legacy "blocked" workstream rows
        // to the new state vocabulary. Pre-fix code conflated two
        // semantics under Status="blocked":
        //   (a) waiting for upstream deps  ← should be re-evaluated
        //   (b) failure triage             ← now spelled "stalled"
        // After the split, (b) writes Status="stalled" + StallReason.
        // Rows still sitting at "blocked" with NO StallReason are (a):
        // they were either dep-waiters from a legacy build OR speculative
        // runs that ran before the dep-graph guard was tightened
        // (observed 2026-05-10 on failover-cluster:XRAFT stages 2.1/2.2/2.3
        // — all ran iters yesterday against a base missing their upstream
        // crate scaffolding, achieved iterate/pass on a stale tree, then
        // sat at "blocked" indefinitely).
        //
        // This endpoint:
        //   1. Finds workstreams where Status='blocked' AND StallReason IS NULL.
        //   2. Optionally scoped by spawnId (?spawnId=spawn-foo).
        //   3. For each: clears deliverables (results were against stale base),
        //      clears AgentsJson (late-binding picks fresh on next claim),
        //      bumps Status to 'planning' so the orchestrator's claim sweep
        //      sees it on the next tick. Worktree + branch left in place;
        //      EnsureWorktreeAsync/EnsureBranchAsync handle fresh re-creation
        //      from the now-current base.
        //   4. ?dryRun=true to preview.
        //
        // Idempotent: a second call finds nothing to do (Status is no
        // longer "blocked"). Safe to run anytime.
        app.MapPost("/api/admin/workstreams/unblock-stale", async (
            HttpContext ctx,
            Forge.Api.Persistence.ControlDbContext control,
            Forge.Api.Persistence.ProductDbContext db,
            Microsoft.AspNetCore.SignalR.IHubContext<Forge.Api.Realtime.ForgeHub>? hub,
            [FromQuery] bool? dryRun,
            [FromQuery] string? spawnId,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            var dry = dryRun ?? false;

            var query = db.Workstreams
                .Where(w => w.Status == "blocked" && (w.StallReason == null || w.StallReason == string.Empty));
            if (!string.IsNullOrWhiteSpace(spawnId))
                query = query.Where(w => w.StorySpawnId == spawnId);
            var rows = await query.ToListAsync(ct);

            if (rows.Count == 0)
            {
                return Results.Ok(new
                {
                    dryRun = dry,
                    scope = spawnId ?? "(all spawns)",
                    matched = 0,
                    message = "No blocked workstreams without a StallReason — nothing to migrate.",
                });
            }

            var nowIso = DateTimeOffset.UtcNow.ToString("O");
            var migrated = new List<object>();

            foreach (var w in rows)
            {
                var beforeStatus = w.Status;
                var deliverablesNuked = 0;

                if (!dry)
                {
                    // Wipe deliverables — they were scored against a
                    // base missing upstream crate code, so they're
                    // worthless for the fresh run.
                    var deliverables = await db.Deliverables.Where(d => d.WorkItemId == w.Id).ToListAsync(ct);
                    deliverablesNuked = deliverables.Count;
                    if (deliverables.Count > 0) db.Deliverables.RemoveRange(deliverables);

                    // Reset for fresh claim. Worktree + branch left in
                    // place; EnsureWorktreeAsync handles re-creation
                    // (or pruning + recreation if the slug changed).
                    w.Status = "planning";
                    w.AgentsJson = "[]";
                    w.StallReason = null;
                    w.LastActive = 0;
                }
                else
                {
                    deliverablesNuked = await db.Deliverables.CountAsync(d => d.WorkItemId == w.Id, ct);
                }

                migrated.Add(new
                {
                    workstreamId = w.Id,
                    name = w.Name,
                    storySpawnId = w.StorySpawnId,
                    beforeStatus,
                    afterStatus = "planning",
                    deliverablesCleared = deliverablesNuked,
                });
            }

            if (!dry)
            {
                await db.SaveChangesAsync(ct);

                // Best-effort SignalR push so the UI re-renders without
                // a hard refresh.
                if (hub is not null)
                {
                    foreach (var w in rows)
                    {
                        try
                        {
                            await hub.Clients.All.SendAsync("workstream.changed",
                                new { kind = "unblocked-stale", id = w.Id }, ct);
                        }
                        catch { /* best-effort */ }
                    }
                }
            }

            return Results.Ok(new
            {
                dryRun = dry,
                scope = spawnId ?? "(all spawns)",
                matched = rows.Count,
                migrated,
                at = nowIso,
            });
        }).WithTags("Admin").WithName("AdminUnblockStaleWorkstreams");

        // 2026-05-30 — authoritative Copilot model catalog. Exchanges Forge's
        // GitHub token (which has Copilot access — it's what spawns the CLI)
        // for a Copilot token, then queries api.githubcopilot.com/models for
        // the REAL per-model capabilities: context window, output cap,
        // reasoning support, tool/streaming support, premium flag + billing
        // multiplier. This is the source of truth for the values we otherwise
        // hand-maintain (and repeatedly got wrong) in agents/Models/models.json.
        // Read-only; surfaces a `mismatches` block comparing the live catalog
        // to the local registry so drift is obvious.
        app.MapGet("/api/admin/copilot-models", async (
            HttpContext ctx,
            IEnumerable<IAuthService> authServices,
            IHttpClientFactory httpFactory,
            Forge.Api.Agents.IModelRegistry registry,
            Forge.Api.Agents.CopilotServerManager copilotServer,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;

            // Preferred path: the SDK's already-authenticated client lists
            // models + capabilities WITHOUT the editor-only
            // copilot_internal/v2/token exchange (which 404s for OAuth-app /
            // PAT tokens — both gh's gho_ and Forge's GitHub token). Returns
            // the raw SDK model objects (serialized by runtime type) alongside
            // the local registry's window/reasoning for side-by-side drift.
            try
            {
                var sdkModels = await copilotServer.ListModelsAsync(ct);
                if (sdkModels is { Count: > 0 })
                {
                    return Results.Ok(new
                    {
                        source = "copilot-sdk:ListModelsAsync",
                        count = sdkModels.Count,
                        models = sdkModels,
                        registry = registry.All
                            .Select(r => new { r.Id, r.ContextWindow, r.SupportsReasoning, r.InputPricePerMTok, r.OutputPricePerMTok })
                            .ToList(),
                    });
                }
            }
            catch (Exception ex)
            {
                // Fall through to the REST exchange attempt (which likely
                // 404s, but its error message is still informative).
                ctx.Items["copilot-sdk-list-error"] = ex.Message;
            }

            var gh = authServices.FirstOrDefault(a => a.Kind == "github");
            var ghToken = gh is null ? null : await gh.GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(ghToken))
            {
                return Results.Problem(title: "No GitHub token", detail: "Forge has no signed-in GitHub account to authorize the Copilot models API.", statusCode: 400);
            }

            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            // 1. Exchange the GitHub token for a short-lived Copilot token.
            string? copilotToken;
            try
            {
                using var exReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/v2/token");
                exReq.Headers.TryAddWithoutValidation("Authorization", $"token {ghToken}");
                exReq.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.0");
                exReq.Headers.TryAddWithoutValidation("User-Agent", "GithubCopilot/1.99.0");
                using var exResp = await http.SendAsync(exReq, ct);
                var exBody = await exResp.Content.ReadAsStringAsync(ct);
                if (!exResp.IsSuccessStatusCode)
                {
                    return Results.Problem(title: "Copilot token exchange failed",
                        detail: $"{(int)exResp.StatusCode} {exResp.StatusCode}: {exBody[..Math.Min(300, exBody.Length)]}", statusCode: 502);
                }
                using var exDoc = System.Text.Json.JsonDocument.Parse(exBody);
                copilotToken = exDoc.RootElement.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Copilot token exchange error", detail: ex.Message, statusCode: 502);
            }
            if (string.IsNullOrEmpty(copilotToken))
            {
                return Results.Problem(title: "Copilot token exchange returned no token", statusCode: 502);
            }

            // 2. Query the models endpoint with editor headers.
            string modelsBody;
            try
            {
                using var mReq = new HttpRequestMessage(HttpMethod.Get, "https://api.githubcopilot.com/models");
                mReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {copilotToken}");
                mReq.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
                mReq.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.0");
                using var mResp = await http.SendAsync(mReq, ct);
                modelsBody = await mResp.Content.ReadAsStringAsync(ct);
                if (!mResp.IsSuccessStatusCode)
                {
                    return Results.Problem(title: "Copilot models query failed",
                        detail: $"{(int)mResp.StatusCode} {mResp.StatusCode}: {modelsBody[..Math.Min(300, modelsBody.Length)]}", statusCode: 502);
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Copilot models query error", detail: ex.Message, statusCode: 502);
            }

            // 3. Parse capabilities into a flat per-model shape.
            var models = new List<object>();
            var liveById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(modelsBody);
                var root = doc.RootElement;
                var data = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("data", out var dEl)
                    ? dEl : root;
                if (data.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        string? id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        var caps = m.TryGetProperty("capabilities", out var cEl) ? cEl : default;
                        var limits = caps.ValueKind == System.Text.Json.JsonValueKind.Object && caps.TryGetProperty("limits", out var lEl) ? lEl : default;
                        var supports = caps.ValueKind == System.Text.Json.JsonValueKind.Object && caps.TryGetProperty("supports", out var sEl) ? sEl : default;
                        var billing = m.TryGetProperty("billing", out var bEl) ? bEl : default;

                        int? GetInt(System.Text.Json.JsonElement parent, string name)
                            => parent.ValueKind == System.Text.Json.JsonValueKind.Object && parent.TryGetProperty(name, out var v)
                               && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : (int?)null;
                        bool? GetBool(System.Text.Json.JsonElement parent, string name)
                            => parent.ValueKind == System.Text.Json.JsonValueKind.Object && parent.TryGetProperty(name, out var v)
                               && (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False) ? v.GetBoolean() : (bool?)null;

                        var ctxWin = GetInt(limits, "max_context_window_tokens");
                        if (ctxWin is int cw) liveById[id] = cw;
                        models.Add(new
                        {
                            id,
                            name = m.TryGetProperty("name", out var nEl) ? nEl.GetString() : null,
                            vendor = m.TryGetProperty("vendor", out var vEl) ? vEl.GetString() : null,
                            ctx = ctxWin,
                            maxInput = GetInt(limits, "max_prompt_tokens"),
                            maxOutput = GetInt(limits, "max_output_tokens"),
                            reasoning = GetBool(supports, "reasoning_effort") ?? GetBool(supports, "thinking"),
                            streaming = GetBool(supports, "streaming"),
                            tools = GetBool(supports, "tool_calls"),
                            isPremium = GetBool(billing, "is_premium"),
                            multiplier = billing.ValueKind == System.Text.Json.JsonValueKind.Object && billing.TryGetProperty("multiplier", out var mlEl) && mlEl.ValueKind == System.Text.Json.JsonValueKind.Number ? (double?)mlEl.GetDouble() : null,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Copilot models parse error", detail: ex.Message, statusCode: 502);
            }

            // 4. Drift report — where the local registry's contextWindow differs
            // from the live catalog (the source of the bugs we keep hitting).
            var mismatches = new List<object>();
            foreach (var local in registry.All)
            {
                if (liveById.TryGetValue(local.Id, out var liveCtx) && liveCtx != local.ContextWindow)
                {
                    mismatches.Add(new { id = local.Id, registryContextWindow = local.ContextWindow, liveContextWindow = liveCtx });
                }
            }

            return Results.Ok(new { count = models.Count, models, registryMismatches = mismatches });
        }).WithTags("Admin").WithName("AdminCopilotModels");

        return app;
    }

    /// <summary>
    /// Phase 49 follow-up — parse <c>git worktree list --porcelain</c> to
    /// return every registered worktree as <c>(path, branch)</c>. Used by
    /// the worktree-prune endpoint to detect plan-reshape orphans
    /// (worktrees on branches whose workstream row was tombstoned by the
    /// reconciler and ultimately deleted; the on-disk worktree survives
    /// because nothing else cleans it up).
    /// </summary>
    private static async Task<IReadOnlyList<(string Path, string Branch)>> EnumerateWorktreesAsync(
        string cloneRoot, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cloneRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Forge.Api.Services.Git.GitProcessEnv.ApplyFailFast(psi);
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--porcelain");
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started — is it on PATH?");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        if (proc.ExitCode != 0) return Array.Empty<(string, string)>();

        var results = new List<(string Path, string Branch)>();
        string? currentPath = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal) && currentPath is not null)
            {
                var branch = line["branch refs/heads/".Length..].Trim();
                results.Add((currentPath, branch));
                currentPath = null;
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                currentPath = null;
            }
        }
        return results;
    }
}
