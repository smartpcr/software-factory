// -----------------------------------------------------------------------
// <copyright file="AuditEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>Phase 6.10 — read-only audit log endpoint.</summary>
public static class AuditEndpoints
{
    public static WebApplication MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/audit", async (ControlDbContext control, int? take) =>
            {
                var n = Math.Clamp(take ?? 200, 1, 1000);
                var rows = await control.Audit.AsNoTracking()
                    .OrderByDescending(a => a.Id)
                    .Take(n)
                    .ToListAsync();
                return Results.Ok(rows);
            })
            .WithTags("Audit").WithName("GetAudit");

        return app;
    }
}
