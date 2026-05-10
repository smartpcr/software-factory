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
            IConfiguration config,
            Forge.Api.Persistence.ForgeDbContext db,
            CancellationToken ct) =>
        {
            var dbPath = config["Forge:Db:Path"] ?? @"E:\forge\forge.db";
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
            Forge.Api.Persistence.ForgeDbContext db,
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
                    // Only consider worktrees that look like per-WS dirs.
                    // The plan / qa / feature shared worktrees use stable
                    // names and stay alive across plan reshapes; their
                    // branches always have a matching spawn row.
                    var leaf = System.IO.Path.GetFileName(path) ?? string.Empty;
                    var isPerWs = leaf.StartsWith("spawn-ws-", StringComparison.Ordinal)
                        || leaf.StartsWith("stage-", StringComparison.Ordinal);
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
            Forge.Api.Persistence.ForgeDbContext db,
            Forge.Api.Services.Stories.IStoryArtifactStrategy strategy,
            Forge.Api.Services.Stories.IStoryProviderOps provider,
            [FromQuery] string prId,
            [FromQuery] bool? dryRun,
            CancellationToken ct) =>
        {
            if (ForgeUser.RequireWriter(ctx) is { } unauth) return unauth;
            if (string.IsNullOrWhiteSpace(prId))
                return Results.Problem(title: "prId query parameter is required", statusCode: 400);

            var dry = dryRun ?? false;

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
            var needsReply = allComments
                .Where(c => c.AuthorKind == "agent" && c.AuthorId != "eng-default")
                .Where(c => c.State == "resolved" && !replyParentIds.Contains(c.Id))
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
                    backfilled.Add(new { commentId = c.Id, replyRowId = replyId, file = c.FilePath, line = c.Line, isReplyOnly = fix.IsReplyOnly });
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
