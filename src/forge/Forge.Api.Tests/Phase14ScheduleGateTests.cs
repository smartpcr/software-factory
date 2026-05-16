// -----------------------------------------------------------------------
// <copyright file="Phase14ScheduleGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 14.2 contract tests. Pure-function tests for
/// <see cref="ScheduleGate"/> on top, plus integration tests that drive
/// the <c>POST /api/crews/{id}/plan|code</c> endpoints with the gate
/// wired in.
/// </summary>
public class Phase14ScheduleGateTests
{
    // ── Pure ScheduleGate.IsWithinShift / Evaluate tests ────────────────

    private static CrewRow MakeCrew(string tz = "America/Los_Angeles", bool enabled = true) => new()
    {
        Id = "crew-test", Name = "T", Summary = string.Empty,
        Model = "local-stub", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
        Timezone = tz, ScheduleEnabled = enabled,
    };

    [Fact]
    public void OnShift_Mid_Tuesday_At_Noon_LA()
    {
        // 2026-01-13 is a Tuesday. 12:00 PT is well inside the band.
        var noonPt = new DateTimeOffset(2026, 1, 13, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(), noonPt.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    [Fact]
    public void OffShift_Sunday_Even_At_Noon()
    {
        // 2026-01-11 is a Sunday — never on shift, regardless of hour.
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(), sun.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    [Fact]
    public void OffShift_Tuesday_At_8am()
    {
        // 08:00 PT on a work day is before the shift opens.
        var early = new DateTimeOffset(2026, 1, 13, 8, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(), early.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    [Fact]
    public void OnShift_Boundary_9am_Sharp_Is_Inclusive()
    {
        var nine = new DateTimeOffset(2026, 1, 13, 9, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(), nine.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    [Fact]
    public void OffShift_Boundary_9pm_Sharp_Is_Exclusive()
    {
        // 21:00 sharp = closed already; only 20:59:59 is the last on-shift second.
        var nine = new DateTimeOffset(2026, 1, 13, 21, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(), nine.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    [Fact]
    public void Bypass_When_ScheduleEnabled_False_Even_On_Sunday()
    {
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(enabled: false), sun.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.Bypassed, r.Verdict);
    }

    [Fact]
    public void Bad_Timezone_Falls_Back_To_Utc_Without_Crashing()
    {
        var noonUtc = new DateTimeOffset(2026, 1, 13, 12, 0, 0, TimeSpan.Zero);
        var r = ScheduleGate.Evaluate(MakeCrew(tz: "Mars/Phobos"), noonUtc);
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
        Assert.Equal(TimeZoneInfo.Utc.Id, r.Tz.Id);
    }

    [Fact]
    public void NextTransition_From_OnShift_Is_Same_Day_9pm()
    {
        var noonPt = new DateTimeOffset(2026, 1, 13, 12, 0, 0, TimeSpan.FromHours(-8));
        var next = ScheduleGate.NextTransitionLocal(noonPt);
        Assert.Equal(13, next.Day);
        Assert.Equal(21, next.Hour);
    }

    [Fact]
    public void NextTransition_From_Sunday_Is_Monday_9am()
    {
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var next = ScheduleGate.NextTransitionLocal(sun);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(9, next.Hour);
    }

    [Fact]
    public void NextTransition_Saturday_Evening_Is_Monday_9am()
    {
        // 2026-01-17 is Saturday; 22:00 is off-shift; next on-shift = Mon 09:00.
        var sat = new DateTimeOffset(2026, 1, 17, 22, 0, 0, TimeSpan.FromHours(-8));
        var next = ScheduleGate.NextTransitionLocal(sat);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(9, next.Hour);
    }

    // ── Integration tests through POST /api/crews/{id}/plan|code ───────

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<string> CreateCrew(HttpClient c, string id, bool scheduleEnabled = true)
    {
        var body = new
        {
            id, name = id, summary = string.Empty,
            lead = (string?)null, model = "local-stub",
            modelInputPricePerMTok = 1m, modelOutputPricePerMTok = 1m,
        };
        var resp = await c.PostAsJsonAsync("/api/crews/", body, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return id;
    }

    private static async Task SetCrewScheduleEnabled(ForgeWebApplicationFactory f, string id, bool enabled)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db.Crews.FirstAsync(x => x.Id == id);
        row.ScheduleEnabled = enabled;
        await db.SaveChangesAsync();
    }

    private static async Task PinCrewScheduleOff(ForgeWebApplicationFactory f, string id)
    {
        // Force the crew into a "definitely off-shift" timezone by using
        // Etc/GMT+0 (UTC offset 0) and relying on the test wall-clock —
        // but that's flaky. Instead we set ScheduleEnabled=true and pick
        // a timezone whose 9-9-6 window does NOT include the current
        // wall-clock UTC instant. Easiest: use a TZ with extreme offset
        // so that "now" lands in the dead zone deterministically.
        // Strategy: use Etc/GMT-12 (UTC+12) — at any UTC instant <=21:00,
        // the local time is +12h so it falls within shift OR hits 09:00..21:00.
        // The reliable approach is to manipulate the crew row directly:
        // set ScheduleEnabled=true and pick a timezone where the offset
        // makes the band NOT contain UTC-now. We just toggle to a known-bad
        // TZ for this run.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db.Crews.FirstAsync(x => x.Id == id);
        row.ScheduleEnabled = true;
        // Pick the TZ that is currently OUTSIDE the 09:00-21:00 window.
        // We compute it from the test process's wall clock — hour-aware so
        // the integration test is stable regardless of when CI runs.
        row.Timezone = ChooseOffShiftTimezoneForNow();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Pick an IANA timezone whose local hour right now is in [22..05] so
    /// the schedule band cannot contain it. Walks the offset wheel and
    /// returns the first match.
    /// </summary>
    private static string ChooseOffShiftTimezoneForNow()
    {
        var utcNow = DateTimeOffset.UtcNow;
        // Try offsets from -12 to +14 in 1-hour steps; pick one where local
        // hour is in the dead zone (22, 23, 0..5).
        for (var off = -12; off <= 14; off++)
        {
            var localHour = (utcNow.Hour + off + 24) % 24;
            if (localHour >= 22 || localHour <= 5)
            {
                // Synthesize an Etc/GMT id. POSIX inverts the sign:
                // Etc/GMT-5 means UTC+5, Etc/GMT+5 means UTC-5.
                if (off == 0) return "Etc/UTC";
                return off > 0 ? $"Etc/GMT-{off}" : $"Etc/GMT+{-off}";
            }
        }
        // Should never happen — fall back to somewhere we know is off.
        return "Etc/GMT-3";
    }

    [Fact]
    public async Task Plan_Returns_409_OffShift_When_Crew_Pinned_To_Dead_Zone()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var id = await CreateCrew(c, "crew-off");
        await PinCrewScheduleOff(f, id);

        var resp = await c.PostAsJsonAsync($"/api/crews/{id}/plan", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("crew.off_shift", body);
    }

    [Fact]
    public async Task Code_Returns_409_OffShift_When_Crew_Pinned_To_Dead_Zone()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var id = await CreateCrew(c, "crew-off2");
        await PinCrewScheduleOff(f, id);

        var resp = await c.PostAsJsonAsync($"/api/crews/{id}/code", new { }, JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Plan_Bypasses_Gate_When_ScheduleEnabled_False()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var id = await CreateCrew(c, "crew-bypass");
        // Pin to a known dead-zone TZ so the gate would refuse if enabled.
        await PinCrewScheduleOff(f, id);
        // Now disable the schedule entirely — gate must bypass.
        await SetCrewScheduleEnabled(f, id, enabled: false);

        var resp = await c.PostAsJsonAsync($"/api/crews/{id}/plan", new { }, JsonOpts);
        // Gate bypassed → falls through to the validation that requires
        // workItemId. Body is empty so we expect 400, NOT a schedule-gate
        // 409. (Phase 13.4 stub used to be 501; that's been implemented and
        // is now a real queue-and-dispatch path covered elsewhere.)
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Plan_Override_True_Writes_DispatchAfterHours_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var id = await CreateCrew(c, "crew-override");
        await PinCrewScheduleOff(f, id);

        var resp = await c.PostAsJsonAsync($"/api/crews/{id}/plan?override=true", new { }, JsonOpts);
        // After the override succeeds the request falls through to the
        // body validation (workItemId required) — empty body → 400.
        // The dispatch-after-hours history row is the real assertion below.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var hist = await db.CrewHistory.AsNoTracking()
            .Where(h => h.CrewId == id && h.Kind == "dispatch-after-hours")
            .ToListAsync();
        Assert.Single(hist);
        Assert.Equal("plan", hist[0].FromValue);
    }
}
