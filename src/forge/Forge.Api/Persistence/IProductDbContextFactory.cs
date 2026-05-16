// -----------------------------------------------------------------------
// <copyright file="IProductDbContextFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

/// <summary>
/// Phase D — scoped factory that produces a <see cref="ProductDbContext"/>
/// bound to a given product's per-product DB file (<c>{folder}\{slug}.db</c>).
///
/// <para>During Phase D (single-file mode, before Phase F splits), every
/// call returns a context bound to the control DB
/// (<see cref="IForgeDbPaths.ControlDbPath"/>) so existing data is
/// visible. After Phase F lays down the per-product files, the same
/// factory transparently switches to <see cref="IForgeDbPaths.ProductDbPath"/>.</para>
///
/// <para>Resolved contexts are cached per-request keyed on slug to amortise
/// connection setup across endpoints that touch the same product multiple
/// times. The factory disposes every cached context at request scope end.</para>
/// </summary>
public interface IProductDbContextFactory
{
    /// <summary>
    /// Resolve a <see cref="ProductDbContext"/> for the given product slug.
    /// </summary>
    /// <param name="productSlug">The product's slug (Phase 8 column).</param>
    /// <returns>A live context. Same-slug calls return the same cached instance.</returns>
    /// <exception cref="ArgumentException">When <paramref name="productSlug"/> is invalid (see <see cref="ForgeDbPaths.TryValidateSlug"/>).</exception>
    ProductDbContext CreateFor(string productSlug);

    /// <summary>
    /// Convenience overload that resolves the slug from
    /// <see cref="ProductScope"/> (header / query) or, when none is supplied,
    /// throws because there is no canonical "default product" for a write.
    /// </summary>
    ProductDbContext CreateForCurrentRequest();
}
