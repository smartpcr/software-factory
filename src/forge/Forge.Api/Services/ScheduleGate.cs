// -----------------------------------------------------------------------
// <copyright file="ScheduleGate.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;

/// <summary>
/// Phase 14.2 — schedule gate. Pure logic: given a crew row and a
/// "current time" instant, decides whether the crew is currently within
/// its 9-9-6 working window. Endpoints (plan / code dispatch) call
/// <see cref="Evaluate"/> before doing any work and return 409 +
/// <c>crew.off_shift</c> on a refusal verdict.
/// <para>
/// Time math uses <see cref="TimeZoneInfo"/> to convert UTC to the crew's
/// local time, then checks <see cref="ScoringConstants.WorkDays"/> +
/// <see cref="ScoringConstants.ShiftStartHour"/> /
/// <see cref="ScoringConstants.ShiftEndHour"/>. Day shift = Mon–Sat
/// 09:00–21:00; night shift = WorkDay 21:00 → 09:00 next morning
/// (Sat 21:00 → Sun 09:00 counts as the Sat night block).
/// </para>
/// </summary>
public static class ScheduleGate
{
    public const string ShiftDay = "day";
    public const string ShiftNight = "night";

    /// <summary>Outcome of a schedule check.</summary>
    public enum Verdict
    {
        /// <summary>Crew is on shift right now — dispatch may proceed.</summary>
        OnShift,

        /// <summary>Crew is outside its 9-9-6 window — refuse with 409.</summary>
        OffShift,

        /// <summary>Schedule enforcement disabled on this crew — bypass.</summary>
        Bypassed,
    }

    /// <summary>Detailed result from <see cref="Evaluate"/>.</summary>
    public readonly record struct Result(
        Verdict Verdict,
        DateTimeOffset LocalNow,
        TimeZoneInfo Tz,
        DateTimeOffset NextTransitionLocal);

    /// <summary>
    /// Decide whether <paramref name="crew"/> is on shift at
    /// <paramref name="utcNow"/>. Pure — no DB, no clock; suitable for
    /// unit tests with arbitrary instants.
    /// </summary>
    public static Result Evaluate(CrewRow crew, DateTimeOffset utcNow)
    {
        var tz = ResolveTimeZone(crew.Timezone);
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        var shift = NormaliseShift(crew.Shift);

        if (!crew.ScheduleEnabled)
        {
            return new Result(Verdict.Bypassed, local, tz, NextTransitionLocal(local, shift));
        }

        var verdict = IsWithinShift(local, shift) ? Verdict.OnShift : Verdict.OffShift;
        return new Result(verdict, local, tz, NextTransitionLocal(local, shift));
    }

    /// <summary>
    /// Phase 18.6 (Q18.3) — per-agent overload. The crew lead is exempt from
    /// the day/night band and the Mon–Sat filter: <see cref="Verdict.OnShift"/>
    /// is returned whenever <paramref name="agentId"/> equals
    /// <see cref="CrewRow.Lead"/>. Members fall through to the existing
    /// <see cref="Evaluate(CrewRow, DateTimeOffset)"/> path so the day/night/
    /// work-day logic is unchanged for them. Pure — no DB, no clock.
    /// </summary>
    public static Result EvaluateAgent(CrewRow crew, string? agentId, DateTimeOffset utcNow)
    {
        if (!string.IsNullOrEmpty(agentId)
            && !string.IsNullOrEmpty(crew.Lead)
            && string.Equals(agentId, crew.Lead, StringComparison.Ordinal))
        {
            var tz = ResolveTimeZone(crew.Timezone);
            var local = TimeZoneInfo.ConvertTime(utcNow, tz);
            var shift = NormaliseShift(crew.Shift);
            return new Result(Verdict.OnShift, local, tz, NextTransitionLocal(local, shift));
        }
        return Evaluate(crew, utcNow);
    }

    /// <summary>
    /// True iff <paramref name="local"/> is within the crew's shift band.
    /// Day band is half-open <c>[09:00, 21:00)</c> on Mon–Sat. Night band
    /// is <c>[21:00, 24:00)</c> when today is a work day, or
    /// <c>[00:00, 09:00)</c> when yesterday was a work day.
    /// </summary>
    public static bool IsWithinShift(DateTimeOffset local, string shift = ShiftDay)
    {
        shift = NormaliseShift(shift);
        var hour = local.Hour;
        if (shift == ShiftNight)
        {
            if (hour >= ScoringConstants.ShiftEndHour && IsWorkDay(local.DayOfWeek)) return true;
            if (hour < ScoringConstants.ShiftStartHour && IsWorkDay(PreviousDay(local.DayOfWeek))) return true;
            return false;
        }

        if (!IsWorkDay(local.DayOfWeek)) return false;
        return hour >= ScoringConstants.ShiftStartHour && hour < ScoringConstants.ShiftEndHour;
    }

    private static bool IsWorkDay(DayOfWeek day)
    {
        for (var i = 0; i < ScoringConstants.WorkDays.Length; i++)
        {
            if (ScoringConstants.WorkDays[i] == day) return true;
        }
        return false;
    }

    private static DayOfWeek PreviousDay(DayOfWeek d) => (DayOfWeek)(((int)d + 6) % 7);

    private static string NormaliseShift(string? s) =>
        string.Equals(s, ShiftNight, StringComparison.OrdinalIgnoreCase) ? ShiftNight : ShiftDay;

    /// <summary>
    /// Resolve an IANA id like <c>America/Los_Angeles</c> to a
    /// <see cref="TimeZoneInfo"/>. .NET 8 on Windows accepts both IANA
    /// and Windows ids; falls back to <see cref="TimeZoneInfo.Utc"/> if
    /// the id is unknown so a misconfigured crew never crashes the
    /// dispatcher.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Next on/off transition after <paramref name="local"/> for the given
    /// shift. Day shift on → today 21:00, day shift off → next work day 09:00.
    /// Night shift on → next 09:00 (today if hour&lt;9, else tomorrow);
    /// night shift off → next work-day 21:00.
    /// </summary>
    public static DateTimeOffset NextTransitionLocal(DateTimeOffset local, string shift = ShiftDay)
    {
        shift = NormaliseShift(shift);
        return shift == ShiftNight
            ? NextTransitionNight(local)
            : NextTransitionDay(local);
    }

    private static DateTimeOffset NextTransitionDay(DateTimeOffset local)
    {
        if (IsWithinShift(local, ShiftDay))
        {
            return new DateTimeOffset(local.Year, local.Month, local.Day,
                ScoringConstants.ShiftEndHour, 0, 0, local.Offset);
        }
        var probe = local.Date;
        if (IsWorkDay(local.DayOfWeek) && local.Hour < ScoringConstants.ShiftStartHour)
        {
            return new DateTimeOffset(probe.Year, probe.Month, probe.Day,
                ScoringConstants.ShiftStartHour, 0, 0, local.Offset);
        }
        do { probe = probe.AddDays(1); } while (!IsWorkDay(probe.DayOfWeek));
        return new DateTimeOffset(probe.Year, probe.Month, probe.Day,
            ScoringConstants.ShiftStartHour, 0, 0, local.Offset);
    }

    private static DateTimeOffset NextTransitionNight(DateTimeOffset local)
    {
        if (IsWithinShift(local, ShiftNight))
        {
            // On-shift: next transition is the morning close at 09:00.
            // If we're past midnight, that's today; if we're after 21:00,
            // that's tomorrow at 09:00.
            var probe = local.Date;
            if (local.Hour >= ScoringConstants.ShiftEndHour) probe = probe.AddDays(1);
            return new DateTimeOffset(probe.Year, probe.Month, probe.Day,
                ScoringConstants.ShiftStartHour, 0, 0, local.Offset);
        }
        // Off-shift: next transition is the next work-day's 21:00 open.
        var open = local.Date;
        if (!IsWorkDay(local.DayOfWeek) || local.Hour >= ScoringConstants.ShiftEndHour)
        {
            do { open = open.AddDays(1); } while (!IsWorkDay(open.DayOfWeek));
        }
        return new DateTimeOffset(open.Year, open.Month, open.Day,
            ScoringConstants.ShiftEndHour, 0, 0, local.Offset);
    }
}
