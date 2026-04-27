// -----------------------------------------------------------------------
// <copyright file="AdoConfigResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 10.1 + 10.5 — resolves the effective ADO config for a request.
/// Resolution order:
///   1) per-repo override (Repo.AdoOrg + Repo.AdoProject) when a specific repoId is given,
///   2) user settings (UserSettings.DefaultAdoOrgUrl + DefaultAdoProject),
///   3) static AdoOptions (appsettings / user-secrets).
/// Cached briefly (1s) to avoid hammering SQLite per-request — settings change
/// rarely and the user-experience impact of a 1s lag after PATCH is negligible.
/// </summary>
public interface IAdoConfigResolver
{
    Task<AdoEffectiveConfig> ResolveAsync(string? repoId = null, CancellationToken ct = default);
}

public sealed record AdoEffectiveConfig(
    string OrgUrl,
    string Project,
    string Source);

public class AdoConfigResolver : IAdoConfigResolver
{
    private const string CacheKey = "forge.ado.effective-config";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly AdoOptions fallback;
    private readonly IMemoryCache cache;

    public AdoConfigResolver(IServiceScopeFactory scopeFactory, IOptions<AdoOptions> fallback, IMemoryCache cache)
    {
        this.scopeFactory = scopeFactory;
        this.fallback = fallback.Value;
        this.cache = cache;
    }

    public async Task<AdoEffectiveConfig> ResolveAsync(string? repoId = null, CancellationToken ct = default)
    {
        // Per-repo override hits the DB directly (no caching) since picking a
        // repo is intentional and the operator expects the effective config
        // to update immediately.
        using var scope = this.scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        if (!string.IsNullOrWhiteSpace(repoId))
        {
            var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId, ct);
            if (repo is not null && !string.IsNullOrWhiteSpace(repo.AdoOrg) && !string.IsNullOrWhiteSpace(repo.AdoProject))
            {
                var orgUrl = ResolveOrgUrl(repo.AdoOrg!);
                return new AdoEffectiveConfig(orgUrl, repo.AdoProject!, $"repo:{repo.Id}");
            }
        }

        // User-settings + fallback path is cached briefly to avoid per-request DB hits.
        if (this.cache.TryGetValue<AdoEffectiveConfig>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var settings = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var effective = (settings, this.fallback) switch
        {
            ({ DefaultAdoOrgUrl: { Length: > 0 } orgUrl, DefaultAdoProject: { Length: > 0 } project }, _) =>
                new AdoEffectiveConfig(orgUrl, project, "user-settings"),
            (_, { OrgUrl: { Length: > 0 } orgUrl, Project: { Length: > 0 } project }) =>
                new AdoEffectiveConfig(orgUrl, project, "appsettings"),
            _ => new AdoEffectiveConfig(string.Empty, string.Empty, "unconfigured"),
        };
        this.cache.Set(CacheKey, effective, CacheTtl);
        return effective;
    }

    /// <summary>
    /// Drops the cached effective config so a PATCH /api/me/settings is visible
    /// immediately to the next AdoProvider call. Endpoints invoke this after
    /// writing to UserSettings.
    /// </summary>
    public void Invalidate() => this.cache.Remove(CacheKey);

    private static string ResolveOrgUrl(string adoOrg)
    {
        // Operator can store either a bare org name ("contoso") or a full URL.
        if (Uri.TryCreate(adoOrg, UriKind.Absolute, out var _)) return adoOrg.TrimEnd('/');
        return $"https://dev.azure.com/{adoOrg}";
    }
}
