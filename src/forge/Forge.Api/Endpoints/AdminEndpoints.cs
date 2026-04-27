// -----------------------------------------------------------------------
// <copyright file="AdminEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

        return app;
    }
}
