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

        return app;
    }
}
