// -----------------------------------------------------------------------
// <copyright file="Phase18ScheduleGateNightBandTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 18.3 — night-band coverage for <see cref="ScheduleGate"/>. The
/// existing <c>Phase14ScheduleGateTests</c> suite covers the day band only;
/// this suite exercises the half-open <c>[21:00, 09:00)</c> band that
/// belongs to the work day on which it *starts* (Q18.1: Sat-night spilling
/// into Sun morning is on-shift, but Sun-night spilling into Mon morning
/// is not — Sunday is not a workday so its night block is dead).
/// </summary>
public class Phase18ScheduleGateNightBandTests
{
    private static CrewRow MakeCrew(
        string tz = "America/Los_Angeles",
        bool enabled = true,
        string shift = "night") => new()
    {
        Id = "crew-test", Name = "T", Summary = string.Empty,
        Model = "local-stub", ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
        Timezone = tz, ScheduleEnabled = enabled, Shift = shift,
    };

    // 2026-01-12 is a Monday (PT). Calendar reference for everything below:
    //   Mon = Jan 12, Tue = Jan 13, Wed = Jan 14, …
    //   Sat = Jan 17, Sun = Jan 18.

    // ── IsWithinShift truth table ────────────────────────────────────────

    [Fact]
    public void Night_OnShift_Mon_22()
    {
        // Mon 22:00 PT — inside the Mon-night block.
        var t = new DateTimeOffset(2026, 1, 12, 22, 0, 0, TimeSpan.FromHours(-8));
        Assert.True(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Night_OnShift_Tue_02()
    {
        // Tue 02:00 PT — Mon-night spilling into Tue morning. Mon was a
        // workday, so the carry-over is on-shift.
        var t = new DateTimeOffset(2026, 1, 13, 2, 0, 0, TimeSpan.FromHours(-8));
        Assert.True(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Night_OnShift_Sat_22()
    {
        // Sat 22:00 PT — Sat-night through Sun 09:00 is on (Q18.1 explicit example).
        var t = new DateTimeOffset(2026, 1, 17, 22, 0, 0, TimeSpan.FromHours(-8));
        Assert.True(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Night_OnShift_Sun_02()
    {
        // Sun 02:00 PT — still Sat-night spilling into Sun morning.
        var t = new DateTimeOffset(2026, 1, 18, 2, 0, 0, TimeSpan.FromHours(-8));
        Assert.True(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Night_OffShift_Sun_22()
    {
        // Sun 22:00 PT — Sun is not a workday, so its night block is dead
        // (Q18.1 explicit counter-example).
        var t = new DateTimeOffset(2026, 1, 18, 22, 0, 0, TimeSpan.FromHours(-8));
        Assert.False(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Night_OffShift_Mon_02()
    {
        // Mon 02:00 PT — would be Sun-night carry-over, but Sunday isn't a
        // workday, so this slot is off-shift.
        var t = new DateTimeOffset(2026, 1, 12, 2, 0, 0, TimeSpan.FromHours(-8));
        Assert.False(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Night_OffShift_Mon_12()
    {
        // Monday noon — not in the night band; only members of a day crew
        // would be on at noon.
        var t = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.FromHours(-8));
        Assert.False(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    [Fact]
    public void Day_OffShift_Mon_22_Regression()
    {
        // Regression — the existing day band must remain off at 22:00 even
        // after the night-band code paths landed.
        var t = new DateTimeOffset(2026, 1, 12, 22, 0, 0, TimeSpan.FromHours(-8));
        Assert.False(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftDay));
    }

    // ── Boundaries: half-open semantics for both bands ────────────────────

    [Fact]
    public void Boundary_21_Sharp_Night_On_Day_Off()
    {
        // 21:00:00 sharp on a Mon: night = open, day = closed (half-open
        // intervals — [09,21) for day, [21,09) for night).
        var t = new DateTimeOffset(2026, 1, 12, 21, 0, 0, TimeSpan.FromHours(-8));
        Assert.True(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
        Assert.False(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftDay));
    }

    [Fact]
    public void Boundary_09_Sharp_Day_On_Night_Off()
    {
        // 09:00:00 sharp on a Mon: day = open, night = closed.
        var t = new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.FromHours(-8));
        Assert.True(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftDay));
        Assert.False(ScheduleGate.IsWithinShift(t, ScheduleGate.ShiftNight));
    }

    // ── Evaluate routes shift through the crew row ────────────────────────

    [Fact]
    public void Evaluate_NightCrew_OnShift_At_Mon_22()
    {
        var t = new DateTimeOffset(2026, 1, 12, 22, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(shift: "night"), t.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    [Fact]
    public void Evaluate_NightCrew_OffShift_At_Mon_Noon()
    {
        var t = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(shift: "night"), t.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OffShift, r.Verdict);
    }

    [Fact]
    public void Evaluate_UnknownShift_Normalises_To_Day()
    {
        // Corruption-tolerance — a crew row with a malformed Shift value
        // should not crash the gate; the normaliser falls back to "day"
        // so the operator can fix the row without an outage.
        var noon = new DateTimeOffset(2026, 1, 13, 12, 0, 0, TimeSpan.FromHours(-8));
        var r = ScheduleGate.Evaluate(MakeCrew(shift: "swing"), noon.ToUniversalTime());
        Assert.Equal(ScheduleGate.Verdict.OnShift, r.Verdict);
    }

    // ── NextTransitionLocal — night band ─────────────────────────────────

    [Fact]
    public void NextTransition_Night_OnShift_Mon_22_Is_Tue_09()
    {
        // On-shift → next transition is the morning close at 09:00.
        var t = new DateTimeOffset(2026, 1, 12, 22, 0, 0, TimeSpan.FromHours(-8));
        var next = ScheduleGate.NextTransitionLocal(t, ScheduleGate.ShiftNight);
        Assert.Equal(13, next.Day);
        Assert.Equal(9, next.Hour);
    }

    [Fact]
    public void NextTransition_Night_OffShift_Mon_Noon_Is_Mon_21()
    {
        // Off-shift mid-day → next on-shift is today's 21:00 open.
        var t = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.FromHours(-8));
        var next = ScheduleGate.NextTransitionLocal(t, ScheduleGate.ShiftNight);
        Assert.Equal(12, next.Day);
        Assert.Equal(21, next.Hour);
    }

    [Fact]
    public void NextTransition_Night_OffShift_Sun_Noon_Is_Mon_21()
    {
        // Sunday is dead for the night band — next on-shift is Mon 21:00.
        var t = new DateTimeOffset(2026, 1, 18, 12, 0, 0, TimeSpan.FromHours(-8));
        var next = ScheduleGate.NextTransitionLocal(t, ScheduleGate.ShiftNight);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(21, next.Hour);
    }

    // ── Constants ────────────────────────────────────────────────────────

    [Fact]
    public void Constants_Match_Documented_Values()
    {
        // External callers branch on these — guard against accidental rename.
        Assert.Equal("day", ScheduleGate.ShiftDay);
        Assert.Equal("night", ScheduleGate.ShiftNight);
    }
}
