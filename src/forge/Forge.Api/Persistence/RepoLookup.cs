// -----------------------------------------------------------------------
// <copyright file="RepoLookup.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 80.B — resolves repo rows now that the full <see cref="RepoRow"/>
/// lives in the per-product <c>{slug}.db</c> while the control DB keeps only
/// the thin <see cref="RepoRegistryRow"/> index.
///
/// <para>Given only a <c>repoId</c> (the common case — a work item / PR /
/// workstream / spawn carries one), this routes
/// <c>repo_registry</c> → owning product's slug → that product's DB →
/// <see cref="RepoRow"/>. Callers that previously did
/// <c>control.Repos.FirstOrDefaultAsync(r =&gt; r.Id == id)</c> become
/// <c>repoLookup.GetByIdAsync(id)</c> and stay correct regardless of the
/// request's active product scope.</para>
///
/// <para>Routing-only consumers (PR coordinate resolution, worktree clone
/// root, sync-due enumeration, global uniqueness) can read the registry
/// fields directly via <see cref="GetRegistryAsync"/> / the control
/// <c>RepoRegistry</c> set without opening a product DB.</para>
///
/// <para>Scoped service. The per-product contexts come from
/// <see cref="IProductDbContextFactory.CreateFor"/>, which caches per request
/// and disposes at scope end — safe to call repeatedly.</para>
/// </summary>
public sealed class RepoLookup
{
    private readonly ControlDbContext control;
    private readonly IProductDbContextFactory factory;

    public RepoLookup(ControlDbContext control, IProductDbContextFactory factory)
    {
        this.control = control;
        this.factory = factory;
    }

    /// <summary>
    /// Full repo config row by id, or <c>null</c> when unknown / soft-deleted.
    /// Routes registry → owning product DB.
    /// </summary>
    public async Task<RepoRow?> GetByIdAsync(string? repoId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoId)) return null;
        var reg = await this.control.RepoRegistry.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repoId && r.DeletedAt == null, ct);
        if (reg is null || string.IsNullOrEmpty(reg.ProductSlug)) return null;
        var db = this.factory.CreateFor(reg.ProductSlug);
        return await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId, ct);
    }

    /// <summary>
    /// The registry row (routing + cursor + global-uniqueness fields) by id,
    /// without opening the product DB. Excludes soft-deleted rows.
    /// </summary>
    public async Task<RepoRegistryRow?> GetRegistryAsync(string? repoId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoId)) return null;
        return await this.control.RepoRegistry.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repoId && r.DeletedAt == null, ct);
    }

    /// <summary>
    /// All repos for a product, ordered by <c>Sort</c>, read from that
    /// product's DB. Empty when the product is unknown or has no repos.
    /// </summary>
    public async Task<List<RepoRow>> ListByProductAsync(string productId, CancellationToken ct = default)
    {
        var slug = await this.control.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => p.Slug)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(slug)) return new List<RepoRow>();
        var db = this.factory.CreateFor(slug);
        return await db.Repos.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderBy(r => r.Sort)
            .ToListAsync(ct);
    }

    /// <summary>Repo ids belonging to a product (from the control registry — no product DB open).</summary>
    public async Task<List<string>> RepoIdsForProductAsync(string productId, CancellationToken ct = default) =>
        await this.control.RepoRegistry.AsNoTracking()
            .Where(r => r.ProductId == productId && r.DeletedAt == null)
            .Select(r => r.Id)
            .ToListAsync(ct);
}
