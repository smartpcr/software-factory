// -----------------------------------------------------------------------
// <copyright file="Phase18PatchShiftTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 18.4 — contract tests for <c>PATCH /api/crews/{id}</c> with the
/// new <c>shift</c> field. Mirrors the Backend / InvokeMode style: case-
/// insensitive whitelist (<c>'day' | 'night'</c>), <c>crew_history</c>
/// audit row on change, 400 on unknown values.
/// </summary>
public class Phase18PatchShiftTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task Make(HttpClient c, string id, string? shift = null)
    {
        var resp = await c.PostAsJsonAsync("/api/crews/", new
        {
            id,
            name = id,
            productId = (string?)null,
            summary = string.Empty,
            lead = (string?)null,
            model = "local-stub",
            modelInputPricePerMTok = 1m,
            modelOutputPricePerMTok = 1m,
            shift,
        }, JsonOpts);
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"create {id}: {(int)resp.StatusCode} {body}");
        }
    }

    [Fact]
    public async Task Patch_Shift_Night_Persists_And_Returns_Night()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "p-night");
        var resp = await c.PatchAsJsonAsync("/api/crews/p-night", new { shift = "night" }, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("night", body.GetProperty("shift").GetString());

        // GET round-trip — the read endpoint must agree.
        var get = await c.GetAsync("/api/crews/p-night");
        var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("night", getBody.GetProperty("shift").GetString());
    }

    [Fact]
    public async Task Patch_Shift_Day_Persists_And_Returns_Day()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        // Create as night, flip back to day.
        await Make(c, "p-day", shift: "night");
        var resp = await c.PatchAsJsonAsync("/api/crews/p-day", new { shift = "day" }, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("day", body.GetProperty("shift").GetString());
    }

    [Fact]
    public async Task Patch_Shift_Unknown_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "p-bad");
        var resp = await c.PatchAsJsonAsync("/api/crews/p-bad", new { shift = "swing" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("crew.unknown_shift", body);
    }

    [Fact]
    public async Task Patch_Shift_Case_Insensitive()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "p-case");
        // "NIGHT" must normalise to "night" — same convention as
        // Backend / InvokeMode patches.
        var resp = await c.PatchAsJsonAsync("/api/crews/p-case", new { shift = "NIGHT" }, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("night", body.GetProperty("shift").GetString());

        // And "Day" round-trips back too.
        var resp2 = await c.PatchAsJsonAsync("/api/crews/p-case", new { shift = "Day" }, JsonOpts);
        Assert.True(resp2.IsSuccessStatusCode);
        var body2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("day", body2.GetProperty("shift").GetString());
    }

    [Fact]
    public async Task Patch_Shift_Writes_Crew_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "p-hist");
        var patch = await c.PatchAsJsonAsync("/api/crews/p-hist", new { shift = "night" }, JsonOpts);
        Assert.True(patch.IsSuccessStatusCode);

        // Read history via the dedicated endpoint (Phase 13.1 readout).
        var hist = await c.GetAsync("/api/crews/p-hist/history");
        var rows = JsonDocument.Parse(await hist.Content.ReadAsStringAsync()).RootElement;
        // Find the shift row — there may be other rows from create-time defaults.
        var shiftRows = new List<JsonElement>();
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            if (row.GetProperty("kind").GetString() == "shift") shiftRows.Add(row);
        }
        Assert.Single(shiftRows);
        Assert.Equal("day", shiftRows[0].GetProperty("fromValue").GetString());
        Assert.Equal("night", shiftRows[0].GetProperty("toValue").GetString());
    }

    [Fact]
    public async Task Patch_Same_Shift_Does_Not_Add_History_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "p-noop");
        // Crew defaults to 'day'; PATCH-ing to 'day' is a no-op.
        var resp = await c.PatchAsJsonAsync("/api/crews/p-noop", new { shift = "day" }, JsonOpts);
        Assert.True(resp.IsSuccessStatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var hist = await db.CrewHistory.AsNoTracking()
            .Where(h => h.CrewId == "p-noop" && h.Kind == "shift")
            .ToListAsync();
        Assert.Empty(hist);
    }

    [Fact]
    public async Task Create_Unknown_Shift_Returns_400()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var resp = await c.PostAsJsonAsync("/api/crews/", new
        {
            id = "c-bad",
            name = "C",
            productId = (string?)null,
            summary = string.Empty,
            model = "local-stub",
            modelInputPricePerMTok = 1m,
            modelOutputPricePerMTok = 1m,
            shift = "swing",
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("crew.unknown_shift", body);
    }

    [Fact]
    public async Task Create_Without_Shift_Defaults_To_Day()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        await Make(c, "c-default"); // no shift
        var resp = await c.GetAsync("/api/crews/c-default");
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("day", body.GetProperty("shift").GetString());
    }

    [Fact]
    public async Task Patch_Shift_Returns_403_For_Non_PO_Caller()
    {
        // Phase 13.2 product-scoped PO gate is shared across the whole PATCH
        // handler — there's no field-specific bypass for shift. This test
        // verifies the lock applies to shift mutation by triggering it via
        // a populated-install role-gate path: once a UserRow with role=human
        // exists, writes from a different identity get demoted out of PO.
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();

        // Step 1 — create the crew as the PO (no UserRow yet → gate is open).
        c.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");
        await Make(c, "p-rolegate");

        // Step 2 — populate a non-PO user. Now any caller other than the PO
        // hits the human fallback in `RequireRole.CheckForProduct`.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            db.Users.Add(new UserRow
            {
                Id = "u-non-po",
                DisplayName = "Non PO",
                Email = "non-po@forge.local",
                Role = "human",
            });
            await db.SaveChangesAsync();
        }

        // Step 3 — PATCH shift as the non-PO user → 403.
        c.DefaultRequestHeaders.Remove("X-Forge-User");
        c.DefaultRequestHeaders.Add("X-Forge-User", "non-po@forge.local");
        var resp = await c.PatchAsJsonAsync("/api/crews/p-rolegate", new { shift = "night" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // And the row must NOT have been mutated.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var row = await db.Crews.AsNoTracking().FirstAsync(x => x.Id == "p-rolegate");
            Assert.Equal("day", row.Shift);
        }
    }
}
