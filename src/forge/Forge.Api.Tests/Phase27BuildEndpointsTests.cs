// -----------------------------------------------------------------------
// <copyright file="Phase27BuildEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 27 — read endpoints + write authority gate.
/// </summary>
public class Phase27BuildEndpointsTests
{
    [Fact]
    public async Task GET_prs_unknown_id_returns_404()
    {
        await using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        var resp = await c.GetAsync("/api/prs/no-such-pr/builds");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_builds_returns_404_for_missing_build()
    {
        await using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        var resp = await c.GetAsync("/api/builds/9999999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_rerun_is_501_at_v0_1()
    {
        await using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        // Seed a build to give the endpoint a target. Without seeding, the
        // endpoint short-circuits — but the spec requires the 501 response
        // regardless of build existence at v0.1, so the unauth guard +
        // missing build can return earlier. We still assert the public
        // wiring is reachable.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            using (f.CreateClient()) { }
            db.Builds.Add(new BuildRow
            {
                Provider = "ado",
                ProviderBuildId = "9001",
                RepoId = "repo-x",
                State = "failed",
                IsActive = false,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        // Re-fetch the seeded build's id.
        long id;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            id = db.Builds.OrderByDescending(b => b.Id).First().Id;
        }

        var resp = await c.PostAsync($"/api/builds/{id}/rerun", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task POST_annotate_refuses_unauthenticated_callers()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }

        // Seed a build first so we hit the auth gate, not the 404.
        long buildId;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Builds.Add(new BuildRow
            {
                Provider = "ado", ProviderBuildId = "1234",
                RepoId = "repo-x", State = "failed", IsActive = false,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            buildId = db.Builds.OrderByDescending(b => b.Id).First().Id;
        }

        var anon = f.CreateClient();
        // No X-Forge-User header — IsLeadOrPo returns false.
        var resp = await anon.PostAsJsonAsync($"/api/builds/{buildId}/annotate", new { category = "infra" });
        // Expect a non-2xx; auth middleware may also short-circuit before
        // reaching the endpoint, so accept the broader refusal range.
        ((int)resp.StatusCode >= 400).Should().BeTrue();
    }

    [Fact]
    public async Task POST_annotate_succeeds_for_lead_or_po_caller()
    {
        await using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }

        long buildId;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.Builds.Add(new BuildRow
            {
                Provider = "github", ProviderBuildId = "5678",
                RepoId = "repo-y", State = "failed", IsActive = false,
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            buildId = db.Builds.OrderByDescending(b => b.Id).First().Id;
        }

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        var resp = await c.PostAsJsonAsync($"/api/builds/{buildId}/annotate",
            new { category = "infra", rootCause = "agent disconnect" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
