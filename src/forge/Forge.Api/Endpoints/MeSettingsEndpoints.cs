// -----------------------------------------------------------------------
// <copyright file="MeSettingsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 10.1 / 10.2 — per-user preferences. Single-user mode keys on
/// <c>"default"</c> until a real signed-in user exists; once the operator
/// signs in via Phase 9, the row is keyed on their UserRow.Id.
/// </summary>
public static class MeSettingsEndpoints
{
    public static WebApplication MapMeSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me/settings", GetSettings)
            .WithTags("Me").WithName("GetMySettings");
        app.MapPatch("/api/me/settings", PatchSettings)
            .WithTags("Me").WithName("PatchMySettings");
        return app;
    }

    private static async Task<IResult> GetSettings(ForgeDbContext db, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(db, ct);
        var row = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct)
                  ?? new UserSettingsRow { UserId = userId };
        return Results.Ok(MapSettings(row));
    }

    private static async Task<IResult> PatchSettings(
        HttpContext ctx,
        [FromBody] UpdateUserSettingsRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var userId = await ResolveUserIdAsync(db, ct);
        var row = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var inserting = row is null;
        if (row is null)
        {
            row = new UserSettingsRow { UserId = userId };
            db.UserSettings.Add(row);
        }

        // Treat missing keys as "leave alone"; explicit empty string clears.
        if (req.DefaultAdoOrgUrl is not null)   row.DefaultAdoOrgUrl   = NullIfEmpty(req.DefaultAdoOrgUrl);
        if (req.DefaultAdoProject is not null)  row.DefaultAdoProject  = NullIfEmpty(req.DefaultAdoProject);
        if (req.DefaultGitHubOwner is not null) row.DefaultGitHubOwner = NullIfEmpty(req.DefaultGitHubOwner);
        if (req.DefaultProductId is not null)   row.DefaultProductId   = NullIfEmpty(req.DefaultProductId);
        if (req.DefaultRepoId is not null)      row.DefaultRepoId      = NullIfEmpty(req.DefaultRepoId);
        if (req.BrowserPreference is not null)      row.BrowserPreference      = NullIfEmpty(req.BrowserPreference);
        if (req.Theme is not null)                  row.Theme                  = NullIfEmpty(req.Theme);
        if (req.NotificationsEnabled is bool b)     row.NotificationsEnabled   = b;
        if (req.PreferredAdoAccount is not null)    row.PreferredAdoAccount    = NullIfEmpty(req.PreferredAdoAccount);
        if (req.PreferredGitHubAccount is not null) row.PreferredGitHubAccount = NullIfEmpty(req.PreferredGitHubAccount);
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return inserting ? Results.Created($"/api/me/settings", MapSettings(row)) : Results.Ok(MapSettings(row));
    }

    /// <summary>
    /// Resolves the user id whose settings to read/write. In single-user
    /// mode this is the lone signed-in UserRow if present, else the
    /// <c>"default"</c> singleton key.
    /// </summary>
    public static async Task<string> ResolveUserIdAsync(ForgeDbContext db, CancellationToken ct)
    {
        // Single-user mode: there's at most one row. SQLite can't ORDER BY
        // DateTimeOffset, so we just take any user — keying on Id below is
        // stable enough for first-run.
        var first = await db.Users.AsNoTracking()
            .Select(u => new { u.Id })
            .FirstOrDefaultAsync(ct);
        return first?.Id ?? "default";
    }

    private static UserSettings MapSettings(UserSettingsRow r) => new(
        r.UserId,
        r.DefaultAdoOrgUrl,
        r.DefaultAdoProject,
        r.DefaultGitHubOwner,
        r.DefaultProductId,
        r.DefaultRepoId,
        r.BrowserPreference,
        r.Theme,
        r.NotificationsEnabled,
        r.PreferredAdoAccount,
        r.PreferredGitHubAccount,
        r.UpdatedAt);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
