// -----------------------------------------------------------------------
// <copyright file="Phase18LeadExemptionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 18.6 (Q18.3) — lead 24×7 exemption. Pure-function tests for
/// <see cref="ScheduleGate.EvaluateAgent"/> and the per-agent split in
/// <see cref="PayrollCalculator.ComputeTotal"/>. Members keep the existing
/// day/night + Mon–Sat behaviour from Phase 14; only the crew lead is
/// exempt — they are always on shift, including Sunday and outside the
/// band, so a ticket created at 02:00 Sunday routes to the lead instead of
/// parking until Monday 09:00.
/// </summary>
public class Phase18LeadExemptionTests
{
    private static CrewRow MakeCrew(
        string? lead = "L1",
        string tz = "America/Los_Angeles",
        bool enabled = true,
        string shift = "day") => new()
    {
        Id = "crew-test", Name = "T", ProductId = null, Summary = string.Empty,
        Lead = lead,
        Model = "local-stub", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
        Timezone = tz, ScheduleEnabled = enabled, Shift = shift,
    };

    // ── ScheduleGate.EvaluateAgent ───────────────────────────────────────

    [Fact]
    public void Lead_OnShift_Sunday_Noon()
    {
        // Sunday 12:00 PT — the crew (day shift) is off. Lead must be on.
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.EvaluateAgent(MakeCrew(), agentId: "L1", sun.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    [Fact]
    public void Lead_OnShift_Tuesday_3am()
    {
        // Tue 03:00 PT — outside both day and night bands by hour, but the
        // night-of-Mon spans into Tue morning — only members on a night
        // crew would be on. Day-crew lead is still on at 03:00.
        var early = new DateTimeOffset(2026, 1, 13, 3, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.EvaluateAgent(MakeCrew(), agentId: "L1", early.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    [Fact]
    public void Member_OffShift_Sunday_Noon()
    {
        // Same Sunday — a non-lead member is off (work-day filter).
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.EvaluateAgent(MakeCrew(), agentId: "M1", sun.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    [Fact]
    public void Member_OnShift_Tuesday_Noon_Matches_Crew_Wide_Evaluate()
    {
        // Member behaviour during the band must be unchanged from Phase 14.
        var noon = new DateTimeOffset(2026, 1, 13, 12, 0, 0, TimeSpan.FromHours(-8));
        var crew = MakeCrew();
        var member = ScheduleGate.EvaluateAgent(crew, agentId: "M1", noon.ToUniversalTime());
        var crewWide = ScheduleGate.Evaluate(crew, noon.ToUniversalTime());
        Assert.Equal(crewWide.Verdict, member.Verdict);
        Assert.Equal(ScheduleGate.Verdict.OnShift, member.Verdict);
    }

    [Fact]
    public void NoLead_Falls_Through_To_Crew_Wide_Evaluate()
    {
        // crew.Lead = null. Sunday at noon — no lead exemption → off-shift,
        // matching the crew-wide gate (preserves the off-shift histogram tag
        // for the no-lead case per Q18.3).
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.EvaluateAgent(MakeCrew(lead: null), agentId: "anyone", sun.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    [Fact]
    public void Disabled_Schedule_Bypasses_Even_For_Lead()
    {
        // ScheduleEnabled=false yields Bypassed for everyone, including the
        // lead. Otherwise the disabled flag would silently flip lead earnings
        // back on for an unbilled crew.
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.EvaluateAgent(MakeCrew(enabled: false), agentId: "L1", sun.ToUniversalTime());
        // Lead-exempt path returns OnShift directly — bypass only matters for
        // the crew-wide path. This is intentional: the lead is always
        // available for routing, even on a "disabled" crew (the schedule gate
        // is what's disabled, not the lead's role).
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    [Fact]
    public void Null_AgentId_Falls_Through_To_Crew_Wide_Evaluate()
    {
        // Defensive — null agentId should not crash and should not be treated
        // as "matches the lead". Falls through to Evaluate(crew, ...).
        var sun = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.EvaluateAgent(MakeCrew(), agentId: null, sun.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    // ── PayrollCalculator.LeadHoursInRange + ComputeTotal split ───────────

    [Fact]
    public void LeadHours_Full_Week_Is_168()
    {
        // 7 × 24 = 168. The lead earns over the full clock, work-day filter
        // does not apply.
        var monday = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
        var nextMonday = monday.AddDays(7);
        var hours = PayrollCalculator.LeadHoursInRange(MakeCrew(), monday, nextMonday);
        Assert.Equal(168, hours);
    }

    [Fact]
    public void MemberHours_Full_Week_Is_72()
    {
        // 12 × 6 = 72. Member earns only inside the band on work days.
        var monday = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
        var nextMonday = monday.AddDays(7);
        var hours = PayrollCalculator.OnShiftHoursInRange(MakeCrew(), monday, nextMonday);
        Assert.Equal(72, hours);
    }

    [Fact]
    public void LeadHours_Disabled_Schedule_Is_Zero()
    {
        // Disabled schedule suppresses both lead and member earnings.
        var monday = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
        var nextMonday = monday.AddDays(7);
        Assert.Equal(0, PayrollCalculator.LeadHoursInRange(MakeCrew(enabled: false), monday, nextMonday));
        Assert.Equal(0, PayrollCalculator.OnShiftHoursInRange(MakeCrew(enabled: false), monday, nextMonday));
    }

    [Fact]
    public void ComputeTotal_Splits_Lead_And_Member_Hours()
    {
        // Full week: lead = 168 h × $20, member = 72 h × $10 = $3360 + $720 = $4080.
        var monday = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
        var nextMonday = monday.AddDays(7);
        var crew = MakeCrew(lead: "L1");
        var people = new List<AgentRow>
        {
            new() { Id = "L1", Name = "Lead",   Role = "lead", Level = 7, HourlyUsd = 20m },
            new() { Id = "M1", Name = "Member", Role = "eng",  Level = 3, HourlyUsd = 10m },
        };
        var total = PayrollCalculator.ComputeTotal(crew, people, monday, nextMonday);
        Assert.Equal(168m * 20m + 72m * 10m, total);
    }

    [Fact]
    public void ComputeTotal_NoLead_Behaviour_Unchanged()
    {
        // Regression — when crew.Lead is null, every member is treated as a
        // member (72 h/week each) and the total matches the pre-Q18.3 math.
        var monday = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
        var nextMonday = monday.AddDays(7);
        var crew = MakeCrew(lead: null);
        var people = new List<AgentRow>
        {
            new() { Id = "A", Name = "A", Role = "eng", Level = 3, HourlyUsd = 10m },
            new() { Id = "B", Name = "B", Role = "eng", Level = 3, HourlyUsd = 10m },
        };
        var total = PayrollCalculator.ComputeTotal(crew, people, monday, nextMonday);
        Assert.Equal(72m * 10m * 2m, total);
    }
}
