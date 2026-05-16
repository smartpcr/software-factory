// -----------------------------------------------------------------------
// <copyright file="ProductDbContextFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <inheritdoc cref="IProductDbContextFactory" />
public sealed class ProductDbContextFactory : IProductDbContextFactory, IDisposable
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key used by <c>ProductScopeMiddleware</c>
    /// to stash the resolved product slug (lowercased). The factory reads
    /// it in <see cref="CreateForCurrentRequest"/>.
    /// </summary>
    public const string SlugItemKey = "Forge.Persistence.ResolvedProductSlug";

    private readonly IForgeDbPaths paths;
    private readonly IHttpContextAccessor httpContext;
    private readonly Dictionary<string, ProductDbContext> cache = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public ProductDbContextFactory(IForgeDbPaths paths, IHttpContextAccessor httpContext)
    {
        this.paths = paths;
        this.httpContext = httpContext;
    }

    /// <inheritdoc />
    public ProductDbContext CreateFor(string productSlug)
    {
        if (this.disposed) throw new ObjectDisposedException(nameof(ProductDbContextFactory));
        if (!ForgeDbPaths.TryValidateSlug(productSlug, out var error))
        {
            throw new ArgumentException(error, nameof(productSlug));
        }
        var normalized = productSlug.ToLowerInvariant();

        if (this.cache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        // Phase D — until Phase F runs, the per-product DB file does not
        // yet exist. Fall back to the control DB so the existing schema
        // and rows remain visible. After Phase F, the per-product file
        // is the source of truth and we use it directly.
        var productPath = this.paths.ProductDbPath(normalized);
        var dbPath = File.Exists(productPath) ? productPath : this.paths.ControlDbPath();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
        }.ToString();

        var options = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var ctx = new ProductDbContext(options) { ProductSlug = normalized };
        this.cache[normalized] = ctx;
        return ctx;
    }

    /// <inheritdoc />
    public ProductDbContext CreateForCurrentRequest()
    {
        var slug = this.httpContext.HttpContext?.Items[SlugItemKey] as string;
        if (!string.IsNullOrEmpty(slug))
        {
            return this.CreateFor(slug);
        }

        // No slug on the request — fall back to a context bound to the
        // control DB so endpoints with no product scope (e.g., admin
        // tooling, sync workers) still work during the Phase D interim.
        // After Phase F, callers without a product slug must explicitly
        // pick a product or enumerate them all.
        return this.CreateUnscoped();
    }

    private ProductDbContext CreateUnscoped()
    {
        const string unscopedKey = "__unscoped__";
        if (this.cache.TryGetValue(unscopedKey, out var cached))
        {
            return cached;
        }
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.paths.ControlDbPath(),
            DefaultTimeout = 30,
        }.ToString();
        var options = new DbContextOptionsBuilder<ProductDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var ctx = new ProductDbContext(options) { ProductSlug = null };
        this.cache[unscopedKey] = ctx;
        return ctx;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        foreach (var ctx in this.cache.Values)
        {
            ctx.Dispose();
        }
        this.cache.Clear();
    }
}
