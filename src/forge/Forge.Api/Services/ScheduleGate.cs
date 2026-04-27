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
/// <see cref="ScoringConstants.ShiftEndHour"/>. Sunday + outside the
/// 09:00–21:00 band → off-shift.
/// </para>
/// </summary>
public static class ScheduleGate
{
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

        if (!crew.ScheduleEnabled)
        {
            return new Result(Verdict.Bypassed, local, tz, NextTransitionLocal(local));
        }

        var verdict = IsWithinShift(local) ? Verdict.OnShift : Verdict.OffShift;
        return new Result(verdict, local, tz, NextTransitionLocal(local));
    }

    /// <summary>
    /// True iff <paramref name="local"/> is within Mon–Sat 09:00–21:00.
    /// Edge cases: 09:00:00 is on-shift; 21:00:00 is off-shift (band is
    /// half-open <c>[09:00, 21:00)</c>).
    /// </summary>
    public static bool IsWithinShift(DateTimeOffset local)
    {
        if (!IsWorkDay(local.DayOfWeek)) return false;
        var hour = local.Hour;
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
    /// Next on/off transition after <paramref name="local"/>. If currently
    /// on shift → next 21:00 today; if currently off shift → next 09:00
    /// of the next work day. Used by clients to render "back at 09:00 Mon".
    /// </summary>
    public static DateTimeOffset NextTransitionLocal(DateTimeOffset local)
    {
        if (IsWithinShift(local))
        {
            // Next transition = today at 21:00 in the same offset.
            return new DateTimeOffset(local.Year, local.Month, local.Day,
                ScoringConstants.ShiftEndHour, 0, 0, local.Offset);
        }
        // Walk forward day-by-day until we hit the next work day's 09:00.
        var probe = local.Date;
        // If current time is before 09:00 on a work day, the next transition is today at 09:00.
        if (IsWorkDay(local.DayOfWeek) && local.Hour < ScoringConstants.ShiftStartHour)
        {
            return new DateTimeOffset(probe.Year, probe.Month, probe.Day,
                ScoringConstants.ShiftStartHour, 0, 0, local.Offset);
        }
        // Otherwise advance to the next work day.
        do { probe = probe.AddDays(1); } while (!IsWorkDay(probe.DayOfWeek));
        return new DateTimeOffset(probe.Year, probe.Month, probe.Day,
            ScoringConstants.ShiftStartHour, 0, 0, local.Offset);
    }
}
