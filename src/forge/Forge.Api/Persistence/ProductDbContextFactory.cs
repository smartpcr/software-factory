// -----------------------------------------------------------------------
// <copyright file="ProductDbContextFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <inheritdoc cref="IProductDbContextFactory" />
public sealed class ProductDbContextFactory : IProductDbContextFactory, IDisposable
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key used by <c>ProductScopeMiddleware</c>
    /// to stash the resolved product slug (lowercased). The factory reads
    /// it in <see cref="CreateForCurrentRequest"/>.
    /// </summary>
    public const string SlugItemKey = "Forge.Persistence.ResolvedProductSlug";

    /// <summary>
    /// Phase E — AsyncLocal slug push. <see cref="ProductFanout"/> sets
    /// this around each per-product callback so that deep-nested code
    /// inside the callback (which may create its own DI scopes via
    /// <see cref="IServiceScopeFactory"/>) still resolves the correct
    /// <see cref="ProductDbContext"/> without threading the slug
    /// through every call signature.
    /// </summary>
    private static readonly AsyncLocal<string?> CurrentSlugAsync = new();

    /// <summary>Read-only access to the AsyncLocal slug (used by tests + diagnostics).</summary>
    public static string? CurrentAsyncLocalSlug => CurrentSlugAsync.Value;

    /// <summary>
    /// Push <paramref name="slug"/> onto the AsyncLocal stack for the
    /// duration of the returned scope. <see cref="ProductFanout"/>
    /// uses this; production code rarely needs it directly.
    /// </summary>
    public static IDisposable PushAsyncLocalSlug(string? slug)
    {
        var prev = CurrentSlugAsync.Value;
        CurrentSlugAsync.Value = slug;
        return new SlugRestoreScope(prev);
    }

    private sealed class SlugRestoreScope : IDisposable
    {
        private readonly string? prev;
        public SlugRestoreScope(string? prev) { this.prev = prev; }
        public void Dispose() => CurrentSlugAsync.Value = this.prev;
    }

    private readonly IForgeDbPaths paths;
    private readonly IHttpContextAccessor httpContext;
    private readonly ILogger<ProductDbContextFactory>? logger;
    private readonly bool failLoudOnUnscoped;
    private readonly Dictionary<string, ProductDbContext> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object cacheLock = new();
    private bool disposed;

    /// <summary>
    /// Process-wide bag of slugs we've already logged a half-applied
    /// warning for. Keeps the log readable when the factory is
    /// re-resolved per-request (AddScoped) so we'd otherwise emit the
    /// same WARN hundreds of times per minute.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> WarnedHalfAppliedSlugs =
        new(StringComparer.OrdinalIgnoreCase);

    public ProductDbContextFactory(IForgeDbPaths paths, IHttpContextAccessor httpContext)
        : this(paths, httpContext, logger: null, configuration: null)
    {
    }

    public ProductDbContextFactory(
        IForgeDbPaths paths,
        IHttpContextAccessor httpContext,
        ILogger<ProductDbContextFactory>? logger)
        : this(paths, httpContext, logger, configuration: null)
    {
    }

    public ProductDbContextFactory(
        IForgeDbPaths paths,
        IHttpContextAccessor httpContext,
        ILogger<ProductDbContextFactory>? logger,
        IConfiguration? configuration)
    {
        this.paths = paths;
        this.httpContext = httpContext;
        this.logger = logger;
        // Phase 73 Stage 2D — opt-in fail-loud for unscoped writes
        // AFTER the split marker has been stamped. Default false so
        // the legacy CreateUnscoped() fallback stays intact during the
        // Phase 73 cutover. Operators flip this once they've verified
        // every writer pushes a slug (Stage 2B) and the cross-product
        // write integration test (Stage 2E) is green.
        this.failLoudOnUnscoped = configuration is not null
            && string.Equals(
                configuration["Forge:Db:FailLoudOnUnscoped"],
                "true",
                StringComparison.OrdinalIgnoreCase);
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

        lock (this.cacheLock)
        {
            if (this.cache.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            // Phase D — until Phase F runs, the per-product DB file does not
            // yet exist. Fall back to the control DB so the existing schema
            // and rows remain visible. After Phase F, the per-product file
            // is the source of truth and we use it directly.
            //
            // Phase 73 half-applied guard: even when the per-product file
            // exists, refuse to open it unless `phase73-split-applied` is
            // stamped in `forge_seed_state`. Without the marker, the file
            // is a stale/aborted snapshot from a partial SplitApplyService
            // run and the live data is still in the control DB (because
            // writers haven't been re-routed yet). Routing reads to a
            // stale per-product file in that state produces silent drift
            // (see docs/forge/split-db.md "half-applied recovery").
            var productPath = this.paths.ProductDbPath(normalized);
            var perProductFileExists = File.Exists(productPath);
            var splitApplied = SplitMarkerCache.IsPhase73Applied(this.paths);
            string dbPath;
            if (perProductFileExists && splitApplied)
            {
                dbPath = productPath;
            }
            else
            {
                dbPath = this.paths.ControlDbPath();
                if (perProductFileExists && !splitApplied
                    && WarnedHalfAppliedSlugs.TryAdd(normalized, 0))
                {
                    this.logger?.LogWarning(
                        "Phase 73 half-applied split detected for product slug '{Slug}': "
                        + "per-product DB '{ProductPath}' exists but `phase73-split-applied` "
                        + "marker is missing from forge_seed_state. Routing all reads/writes "
                        + "for this product to control DB '{ControlPath}'. Resolve by either "
                        + "completing Phase 73 (SplitApplyService.ApplyAsync) or moving the "
                        + "per-product file aside.",
                        normalized, productPath, this.paths.ControlDbPath());
                }
            }

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
    }

    /// <inheritdoc />
    public ProductDbContext CreateForCurrentRequest()
    {
        // Phase E priority order:
        //   1) AsyncLocal slug pushed by ProductFanout — covers background
        //      workers that scope-create child DI containers per product.
        //   2) HttpContext.Items[SlugItemKey] — covers HTTP requests via
        //      ProductScopeMiddleware.
        //   3) Fall back to control DB so admin paths without a product
        //      scope still resolve.
        var asyncSlug = CurrentSlugAsync.Value;
        if (!string.IsNullOrEmpty(asyncSlug))
        {
            return this.CreateFor(asyncSlug);
        }
        var slug = this.httpContext.HttpContext?.Items[SlugItemKey] as string;
        if (!string.IsNullOrEmpty(slug))
        {
            return this.CreateFor(slug);
        }

        // Phase 73 Stage 2D — fail-loud guard. Once the split marker
        // is stamped AND the operator flips `Forge:Db:FailLoudOnUnscoped`
        // to true, any writer that lands here is a bug we want to
        // surface immediately rather than silently route to forge.db
        // (which would silently corrupt product-DB partitioning once
        // the source tables have been dropped). Default-off so this
        // stage can ship without a same-tick regression.
        if (this.failLoudOnUnscoped && SplitMarkerCache.IsPhase73Applied(this.paths))
        {
            throw new InvalidOperationException(
                "ProductDbContextFactory.CreateForCurrentRequest invoked without a "
                + "product slug (no AsyncLocal slug pushed via "
                + "ProductDbContextFactory.PushAsyncLocalSlug, no "
                + "X-Forge-Product-Id header on the HTTP request) after "
                + "`phase73-split-applied` has been stamped. The unscoped "
                + "fallback to control DB '" + this.paths.ControlDbPath()
                + "' would mix product writes across DBs. Push a slug at "
                + "the call site (most likely a background worker entry "
                + "point missing a `using var _ = "
                + "ProductDbContextFactory.PushAsyncLocalSlug(...)` "
                + "around its per-product unit of work) or set "
                + "Forge:Db:FailLoudOnUnscoped=false to suppress.");
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
        lock (this.cacheLock)
        {
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
    }

    public void Dispose()
    {
        lock (this.cacheLock)
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
}
