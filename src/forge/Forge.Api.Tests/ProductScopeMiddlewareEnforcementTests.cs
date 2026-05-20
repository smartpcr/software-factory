// -----------------------------------------------------------------------
// <copyright file="ProductScopeMiddlewareEnforcementTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 59 follow-up — pins the 2026-05-19 fix for the silent-empty-
/// data bug the user hit during the Phase 59 diagnostic when
/// <c>GET /api/work-items/.../execution</c> returned
/// <c>deliverables: 0</c> for a workstream that actually had 2 because
/// the missing <c>X-Forge-Product-Id</c> header silently fell back to
/// the control DB (no per-product tables post-Phase-F).
///
/// <para>Fix shape:</para>
/// <list type="number">
///   <item><b>Auto-bind</b> to the single product when only one is
///         registered. The test factory seeds exactly one product
///         (<c>atlas</c>), so existing tests that never sent the
///         scope header keep working — the middleware fills it in
///         automatically.</item>
///   <item><b>Pass through</b> in the multi-product case. Operators
///         with 2+ products MUST send the header (the UI does this
///         automatically once an active product is selected via the
///         switcher). Pre-fix behaviour preserved here so a UI cold-
///         start without <c>localStorage.forge.activeProductId</c>
///         doesn't 400.</item>
///   <item><b>Diagnostic header</b>
///         <see cref="ProductScopeMiddleware.ScopeResolvedResponseHeader"/>
///         echoes the resolved value (<c>{slug}</c> / <c>all</c> /
///         <c>(none)</c> / <c>(auto-{slug})</c> / <c>(unknown:{value})</c>)
///         on every response so the silent fallback is observable.</item>
/// </list>
/// </summary>
public class ProductScopeMiddlewareEnforcementTests
{
    // ── Single-product auto-bind (the user's bug fix) ───────────────────

    [Fact]
    public async Task SingleProduct_NoScope_AutoBindsAndReturnsRealData()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Confirm the test factory seeded exactly one product.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var count = await control.Products.AsNoTracking().CountAsync();
            Assert.Equal(1, count);
        }

        // No X-Forge-Product-Id header. With one product registered
        // the middleware auto-binds + the response should:
        //   (a) succeed (200, no 400)
        //   (b) carry the diagnostic header so operators see what scope ran
        var resp = await client.GetAsync("/api/workstreams/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var resolved = resp.Headers.GetValues(ProductScopeMiddleware.ScopeResolvedResponseHeader).Single();
        Assert.StartsWith("(auto-", resolved);
    }

    [Fact]
    public async Task SingleProduct_NoScope_WorkItemEndpoint_AutoBinds()
    {
        // The exact endpoint shape that triggered the user's bug
        // (/api/work-items/{id}/execution).
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Hit the endpoint with an unknown id — we don't care about
        // the row contents, just that the request gets past the
        // middleware + reaches the handler. Without auto-bind it would
        // route to control DB → 404. With auto-bind it routes to the
        // sole product's DB → still 404 (no work item exists) but the
        // diagnostic header confirms auto-bind fired.
        var resp = await client.GetAsync("/api/work-items/wi-anything/execution");

        var resolved = resp.Headers.GetValues(ProductScopeMiddleware.ScopeResolvedResponseHeader).Single();
        Assert.StartsWith("(auto-", resolved);
    }

    // ── Explicit scope wins over auto-bind ───────────────────────────────

    [Fact]
    public async Task ExplicitScope_OverridesAutoBind()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        // Find the seeded product's slug.
        string seededSlug;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            seededSlug = await control.Products.AsNoTracking().Select(p => p.Slug).FirstAsync();
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/workstreams/");
        req.Headers.Add("X-Forge-Product-Id", seededSlug);
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var resolved = resp.Headers.GetValues(ProductScopeMiddleware.ScopeResolvedResponseHeader).Single();
        Assert.Equal(seededSlug, resolved); // NOT "(auto-...)" — explicit
    }

    // ── Unknown product slug surfaces in the diagnostic header ──────────

    [Fact]
    public async Task UnknownSlug_NoBind_DiagnosticHeaderFlagsIt()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/workstreams/");
        req.Headers.Add("X-Forge-Product-Id", "totally-not-a-real-product");
        using var resp = await client.SendAsync(req);

        // Middleware doesn't 400 — endpoint handles unknown id via
        // its own response (empty list, since no slug was stashed).
        // The diagnostic header tells the operator why: the value
        // was unknown.
        var resolved = resp.Headers.GetValues(ProductScopeMiddleware.ScopeResolvedResponseHeader).Single();
        Assert.Equal("(unknown:totally-not-a-real-product)", resolved);
    }

    // ── `all` scope passes through as-is ────────────────────────────────

    [Fact]
    public async Task AllScope_PassesThroughExplicitly()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();

        var resp = await client.GetAsync("/api/workstreams/?productId=all");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var resolved = resp.Headers.GetValues(ProductScopeMiddleware.ScopeResolvedResponseHeader).Single();
        Assert.Equal(ProductScope.AllScope, resolved);
    }

    // ── Multi-product, no scope: pass-through preserved (UI cold-start) ─

    [Fact]
    public async Task MultiProduct_NoScope_PassesThroughWithNoneTag()
    {
        // Seed a second product so the registry has 2+ rows.
        using var f = new ForgeWebApplicationFactory();
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.Products.Add(new ProductRow
            {
                Id = "extra",
                Slug = "extra",
                Name = "Extra",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await control.SaveChangesAsync();
        }

        using var client = f.CreateClient();
        var resp = await client.GetAsync("/api/workstreams/");

        // Pass through — endpoint runs unscoped (silent-empty), but the
        // diagnostic header makes it visible. Operators on multi-product
        // installs need to send the header explicitly (the UI does this
        // automatically once an active product is picked).
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var resolved = resp.Headers.GetValues(ProductScopeMiddleware.ScopeResolvedResponseHeader).Single();
        Assert.Equal("(none)", resolved);
    }

    // ── Non-product routes always pass + carry the header too ───────────

    [Fact]
    public async Task ProductsListEndpoint_NoScope_PassesThrough()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        var resp = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Diagnostic header is always set, even on non-product-data routes.
        Assert.True(resp.Headers.Contains(ProductScopeMiddleware.ScopeResolvedResponseHeader));
    }

    [Fact]
    public async Task StateEndpoint_NoScope_PassesThrough()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        var resp = await client.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.Contains(ProductScopeMiddleware.ScopeResolvedResponseHeader));
    }
}
