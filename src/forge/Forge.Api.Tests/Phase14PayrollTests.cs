// -----------------------------------------------------------------------
// <copyright file="Phase14PayrollTests.cs" company="Microsoft Corp.">
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
/// Phase 14.8 — payroll calculator pure tests + endpoint round-trips.
/// </summary>
public class Phase14PayrollTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static CrewRow Crew(bool enabled = true, string tz = "Etc/UTC") => new()
    {
        Id = "crew-test", Name = "T", Summary = string.Empty,
        Model = "local-stub", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
        Timezone = tz, ScheduleEnabled = enabled,
    };

    [Fact]
    public void Disabled_Schedule_Yields_Zero_Hours()
    {
        var monday9 = new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero);
        var monday17 = new DateTimeOffset(2026, 1, 12, 17, 0, 0, TimeSpan.Zero);
        var hours = PayrollCalculator.OnShiftHoursInRange(Crew(enabled: false), monday9, monday17);
        Assert.Equal(0, hours);
    }

    [Fact]
    public void Full_Workday_In_UTC_Yields_12_Hours()
    {
        // 2026-01-12 is a Monday. Full 09:00..21:00 UTC = 12h.
        var start = new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero);
        var end   = new DateTimeOffset(2026, 1, 12, 21, 0, 0, TimeSpan.Zero);
        var hours = PayrollCalculator.OnShiftHoursInRange(Crew(), start, end);
        Assert.Equal(12, hours);
    }

    [Fact]
    public void Sunday_Yields_Zero_Hours()
    {
        // 2026-01-11 is a Sunday — never on shift.
        var start = new DateTimeOffset(2026, 1, 11, 9, 0, 0, TimeSpan.Zero);
        var end   = new DateTimeOffset(2026, 1, 11, 21, 0, 0, TimeSpan.Zero);
        var hours = PayrollCalculator.OnShiftHoursInRange(Crew(), start, end);
        Assert.Equal(0, hours);
    }

    [Fact]
    public void Partial_Day_Clipped_At_Both_Ends()
    {
        // Mon 10:00–14:00 UTC = 4h within the band.
        var start = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero);
        var end   = new DateTimeOffset(2026, 1, 12, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(4, PayrollCalculator.OnShiftHoursInRange(Crew(), start, end));
    }

    [Fact]
    public void Pre_9am_Range_Yields_Zero()
    {
        var start = new DateTimeOffset(2026, 1, 12, 5, 0, 0, TimeSpan.Zero);
        var end   = new DateTimeOffset(2026, 1, 12, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, PayrollCalculator.OnShiftHoursInRange(Crew(), start, end));
    }

    [Fact]
    public void Sat_To_Mon_Skips_Sunday()
    {
        // Sat 15:00 → Mon 12:00 UTC = 6h Sat (15..21) + 0h Sun + 3h Mon (9..12) = 9h.
        var start = new DateTimeOffset(2026, 1, 17, 15, 0, 0, TimeSpan.Zero); // Sat
        var end   = new DateTimeOffset(2026, 1, 19, 12, 0, 0, TimeSpan.Zero); // Mon
        Assert.Equal(9, PayrollCalculator.OnShiftHoursInRange(Crew(), start, end));
    }

    [Fact]
    public void ComputeTotal_Multiplies_Hours_By_Each_Members_Hourly()
    {
        var start = new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero);
        var end   = new DateTimeOffset(2026, 1, 12, 21, 0, 0, TimeSpan.Zero); // 12h
        var members = new List<AgentRow>
        {
            new() { Id = "a", Name = "A", Role = "eng", Level = 0,  HourlyUsd = ScoringConstants.HourlyForLevel(0)  }, // $4
            new() { Id = "b", Name = "B", Role = "rev", Level = 5,  HourlyUsd = ScoringConstants.HourlyForLevel(5)  }, // $19
            new() { Id = "c", Name = "C", Role = "qa",  Level = 10, HourlyUsd = ScoringConstants.HourlyForLevel(10) }, // $34
        };
        var total = PayrollCalculator.ComputeTotal(Crew(), members, start, end);
        // 12 * (4 + 19 + 34) = 12 * 57 = 684
        Assert.Equal(684m, total);
    }

    [Fact]
    public async Task GET_Payroll_Returns_Today_Sprint_Month_And_Sparkline()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        // Create a crew + a couple of members directly in the DB.
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Crews.Add(new CrewRow
            {
                Id = "crew-payroll", Name = "P", Summary = "",
                Model = "local-stub", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                Lead = "lead1", Timezone = "Etc/UTC", ScheduleEnabled = true,
            });
            control.Agents.Add(new AgentRow { Id = "lead1", Name = "Lead", Role = "eng", Level = 5, HourlyUsd = ScoringConstants.HourlyForLevel(5) });
            control.Agents.Add(new AgentRow { Id = "m1",    Name = "M1",   Role = "eng", Level = 3, HourlyUsd = ScoringConstants.HourlyForLevel(3) });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = "crew-payroll", AgentId = "m1", Role = "eng", AddedAt = DateTimeOffset.UtcNow });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await c.GetAsync("/api/crews/crew-payroll/payroll");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("crew-payroll", body.GetProperty("crewId").GetString());
        Assert.Equal(14, body.GetProperty("sparkline").GetArrayLength());

        // Today / sprint / month must be non-negative (could be 0 outside shift).
        Assert.True(body.GetProperty("today").GetDecimal()  >= 0);
        Assert.True(body.GetProperty("sprint").GetDecimal() >= 0);
        Assert.True(body.GetProperty("month").GetDecimal()  >= 0);

        // Month must be ≥ sprint must be ≥ today.
        Assert.True(body.GetProperty("month").GetDecimal()  >= body.GetProperty("sprint").GetDecimal());
        Assert.True(body.GetProperty("sprint").GetDecimal() >= body.GetProperty("today").GetDecimal());
    }

    [Fact]
    public async Task GET_Payroll_Unknown_Crew_Returns_404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        var resp = await c.GetAsync("/api/crews/ghost/payroll");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Spend_Alias_Returns_The_Same_Shape_As_Salary()
    {
        using var f = new ForgeWebApplicationFactory();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", "test@forge.local");

        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            control.Crews.Add(new CrewRow
            {
                Id = "crew-spend", Name = "S", Summary = "",
                Model = "local-stub", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                Timezone = "Etc/UTC",
            });
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }

        var resp = await c.GetAsync("/api/crews/crew-spend/spend");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("crew-spend", body.GetProperty("crewId").GetString());
        Assert.True(body.TryGetProperty("today", out _));
        Assert.True(body.TryGetProperty("sprint", out _));
        Assert.True(body.TryGetProperty("month", out _));
        Assert.True(body.TryGetProperty("sparkline", out var spark));
        Assert.Equal(14, spark.GetArrayLength());
    }
}
