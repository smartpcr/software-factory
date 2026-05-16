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
/// Phase 10.1 / 10.2 — operator preferences + provider defaults.
/// Forge runs as a single-operator instance, so every caller reads/writes
/// the same row keyed on <see cref="GlobalSettingsKey"/>. Earlier builds
/// keyed on a per-user UserRow.Id, which broke once multiple user rows
/// accumulated (sign-in flows + e2e fixtures): the lookup picked an
/// arbitrary "first" user, so provider defaults appeared and disappeared
/// between page loads depending on SQLite scan order.
/// </summary>
public static class MeSettingsEndpoints
{
    /// <summary>
    /// Sentinel UserId for the singleton settings row. Underscore prefix
    /// to keep it sortable below real user ids and obvious in the DB.
    /// </summary>
    public const string GlobalSettingsKey = "_global";

    public static WebApplication MapMeSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me/settings", GetSettings)
            .WithTags("Me").WithName("GetMySettings");
        app.MapPatch("/api/me/settings", PatchSettings)
            .WithTags("Me").WithName("PatchMySettings");
        return app;
    }

    private static async Task<IResult> GetSettings(ControlDbContext db, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(db, ct);
        var row = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null)
        {
            row = await SeedGlobalFromLegacyAsync(db, ct);
        }
        return Results.Ok(MapSettings(row));
    }

    /// <summary>
    /// One-shot migration: when no <see cref="GlobalSettingsKey"/> row
    /// exists, build it by merging every legacy per-user row in
    /// most-recently-updated order (first non-null wins per field). Persists
    /// the merged row when there is anything worth keeping so the operator
    /// doesn't have to re-enter ADO/GitHub defaults after the move from
    /// per-user to global keying.
    /// </summary>
    private static async Task<UserSettingsRow> SeedGlobalFromLegacyAsync(ControlDbContext db, CancellationToken ct)
    {
        var legacy = await db.UserSettings.AsNoTracking()
            .Where(x => x.UserId != GlobalSettingsKey)
            .ToListAsync(ct);
        legacy.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

        var row = new UserSettingsRow { UserId = GlobalSettingsKey, UpdatedAt = DateTimeOffset.UtcNow };
        foreach (var l in legacy)
        {
            row.DefaultAdoOrgUrl       ??= l.DefaultAdoOrgUrl;
            row.DefaultAdoProject      ??= l.DefaultAdoProject;
            row.DefaultGitHubOwner     ??= l.DefaultGitHubOwner;
            row.DefaultProductId       ??= l.DefaultProductId;
            row.DefaultRepoId          ??= l.DefaultRepoId;
            row.BrowserPreference      ??= l.BrowserPreference;
            row.Theme                  ??= l.Theme;
            row.PreferredAdoAccount    ??= l.PreferredAdoAccount;
            row.PreferredGitHubAccount ??= l.PreferredGitHubAccount;
        }
        // NotificationsEnabled has no nullable form — keep enabled if any
        // legacy row had it on, matching the schema default.
        row.NotificationsEnabled = legacy.Count == 0 || legacy.Any(l => l.NotificationsEnabled);

        if (legacy.Count > 0)
        {
            db.UserSettings.Add(row);
            await db.SaveChangesAsync(ct);
        }
        return row;
    }

    private static async Task<IResult> PatchSettings(
        HttpContext ctx,
        [FromBody] UpdateUserSettingsRequest req,
        ControlDbContext db,
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
    /// Settings are an instance-wide singleton in this build. The async
    /// signature is preserved so both call sites stay shape-compatible
    /// with the prior per-user resolution.
    /// </summary>
    public static Task<string> ResolveUserIdAsync(ControlDbContext db, CancellationToken ct)
        => Task.FromResult(GlobalSettingsKey);

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
