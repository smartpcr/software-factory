// -----------------------------------------------------------------------
// <copyright file="ProductFanout.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Phase E — helper that iterates every product in the control DB and
/// invokes a per-product callback against the product's own
/// <see cref="ProductDbContext"/> obtained from
/// <see cref="IProductDbContextFactory"/>.
///
/// <para>Background workers that today scan <c>forge.db</c> for work
/// (e.g., <c>SELECT * FROM task_queue WHERE State='queued'</c>) must
/// switch to this helper before Phase F lays down per-product files,
/// otherwise each tick will only see one product's queue and silently
/// miss the rest.</para>
///
/// <para>Typical usage in a hosted worker tick:</para>
/// <code>
/// await fanout.ForEachProductAsync(async (productDb, ct) =>
/// {
///     var due = await productDb.TaskQueue
///         .Where(t =&gt; t.State == "queued")
///         .ToListAsync(ct);
///     foreach (var t in due) await ProcessAsync(t, ct);
/// }, ct);
/// </code>
///
/// <para>Parallelism is bounded by <c>Forge:Db:MaxConcurrentProductDbs</c>
/// (default 8) so a 50-product install does not exhaust SQLite handles
/// or DbContext pool size. Within a single tick, products are processed
/// in slug order so any side-effects (logs, events) are deterministic.</para>
/// </summary>
public sealed class ProductFanout
{
    private readonly ControlDbContext control;
    private readonly IProductDbContextFactory factory;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly int maxConcurrent;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? http;

    public ProductFanout(
        ControlDbContext control,
        IProductDbContextFactory factory,
        IServiceScopeFactory scopeFactory,
        Microsoft.Extensions.Configuration.IConfiguration config,
        Microsoft.AspNetCore.Http.IHttpContextAccessor? http = null)
    {
        this.control = control;
        this.factory = factory;
        this.scopeFactory = scopeFactory;
        this.http = http;
        var raw = config["Forge:Db:MaxConcurrentProductDbs"];
        this.maxConcurrent = (int.TryParse(raw, out var v) && v > 0) ? v : 8;
    }

    /// <summary>
    /// Iterate every active product in the control DB, invoking
    /// <paramref name="action"/> against each product's <c>ProductDbContext</c>.
    /// Errors in the per-product action propagate via
    /// <see cref="AggregateException"/> after every product has been
    /// attempted — a single bad product doesn't halt the tick.
    /// </summary>
    public async Task<int> ForEachProductAsync(
        Func<ControlDbContext, ProductDbContext, string, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        var products = await this.control.Products
            .AsNoTracking()
            .OrderBy(p => p.Slug)
            .Select(p => p.Slug)
            .ToListAsync(ct);

        return await this.ForEachProductAsync(products, action, ct);
    }

    /// <summary>
    /// Iterate a caller-supplied slug list (e.g., "due" products from a
    /// control-side index) instead of every product. Same error handling
    /// + parallelism semantics as the no-arg overload.
    /// </summary>
    public async Task<int> ForEachProductAsync(
        IReadOnlyList<string> productSlugs,
        Func<ControlDbContext, ProductDbContext, string, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        productSlugs = this.FilterRequestProducts(productSlugs);
        if (productSlugs.Count == 0) return 0;

        var errors = new List<Exception>();
        using var semaphore = new SemaphoreSlim(this.maxConcurrent, this.maxConcurrent);
        var tasks = new List<Task>(productSlugs.Count);

        foreach (var slug in productSlugs)
        {
            await semaphore.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                // Phase G — each fan-out task runs in its OWN DI scope so
                // both ControlDbContext and ProductDbContext are unique per
                // task. Without this, concurrent callbacks share the outer
                // scope's ControlDbContext (single-threaded EF) and throw
                // "second operation started on context" under load.
                using var taskScope = this.scopeFactory.CreateScope();
                using var _ = ProductDbContextFactory.PushAsyncLocalSlug(slug);
                try
                {
                    var taskControl = taskScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                    var productDb = taskScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    await action(taskControl, productDb, slug, ct);
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        if (errors.Count > 0)
        {
            throw new AggregateException(
                $"ProductFanout: {errors.Count} of {productSlugs.Count} product(s) threw during fan-out.",
                errors);
        }

        return productSlugs.Count;
    }

    /// <summary>
    /// Convenience: gather a per-product result, in slug order, with
    /// the same parallelism + error semantics as the <c>void</c>
    /// overloads.
    /// </summary>
    public async Task<Dictionary<string, T>> CollectAsync<T>(
        Func<ControlDbContext, ProductDbContext, string, CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        var products = await this.control.Products
            .AsNoTracking()
            .OrderBy(p => p.Slug)
            .Select(p => p.Slug)
            .ToListAsync(ct);

        products = this.FilterRequestProducts(products).ToList();
        var results = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<Exception>();
        using var semaphore = new SemaphoreSlim(this.maxConcurrent, this.maxConcurrent);
        var tasks = new List<Task>(products.Count);

        foreach (var slug in products)
        {
            await semaphore.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                // Phase G — per-task scope (see ForEachProductAsync above).
                using var taskScope = this.scopeFactory.CreateScope();
                using var _ = ProductDbContextFactory.PushAsyncLocalSlug(slug);
                try
                {
                    var taskControl = taskScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                    var productDb = taskScope.ServiceProvider.GetRequiredService<ProductDbContext>();
                    var r = await action(taskControl, productDb, slug, ct);
                    lock (results) results[slug] = r;
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        if (errors.Count > 0)
        {
            throw new AggregateException(
                $"ProductFanout.Collect: {errors.Count} of {products.Count} product(s) threw.",
                errors);
        }

        return results;
    }

    private IReadOnlyList<string> FilterRequestProducts(IReadOnlyList<string> slugs)
    {
        if (this.http?.HttpContext is not { } ctx) return slugs;
        var readable = ctx.Items[Forge.Api.Auth.ForgeEndpointCapabilityMiddleware.ReadableProductsKey] as IReadOnlySet<string>;
        return readable is null ? Array.Empty<string>() : slugs.Where(readable.Contains).ToArray();
    }
}
