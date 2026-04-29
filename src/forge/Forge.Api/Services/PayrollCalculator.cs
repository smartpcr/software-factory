// -----------------------------------------------------------------------
// <copyright file="PayrollCalculator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;

/// <summary>
/// Phase 14.8 — pure HR-payroll math. Distinct from the existing
/// token-spend rollup (which Q14.7 renames to "spend"): payroll is a
/// function of <c>(level → hourly_usd) × on_shift_hours_in_period</c>
/// and never reads from the cost ledger.
/// <para>
/// On-shift hours: each work day (<see cref="ScoringConstants.WorkDays"/>)
/// the crew was scheduled and on shift contributes
/// <see cref="ScoringConstants.ShiftHoursPerDay"/> hours; partial days
/// (e.g. "today so far") clip at the period boundary in the crew's local
/// time. <c>ScheduleEnabled = false</c> → no payroll accrues.
/// </para>
/// </summary>
public static class PayrollCalculator
{
    /// <summary>
    /// Compute the total on-shift hours for <paramref name="crew"/> in
    /// the half-open UTC range <c>[fromUtc, toUtc)</c>. Returns 0 when
    /// <see cref="CrewRow.ScheduleEnabled"/> is false. Pure.
    /// </summary>
    public static int OnShiftHoursInRange(CrewRow crew, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (!crew.ScheduleEnabled) return 0;
        if (toUtc <= fromUtc) return 0;

        var tz = ScheduleGate.ResolveTimeZone(crew.Timezone);
        var fromLocal = TimeZoneInfo.ConvertTime(fromUtc, tz);
        var toLocal = TimeZoneInfo.ConvertTime(toUtc, tz);

        var total = 0;

        // Walk one calendar day at a time in the crew's local TZ.
        var probe = new DateTimeOffset(fromLocal.Year, fromLocal.Month, fromLocal.Day, 0, 0, 0, fromLocal.Offset);
        var endProbe = new DateTimeOffset(toLocal.Year, toLocal.Month, toLocal.Day, 0, 0, 0, toLocal.Offset);

        // Inclusive of both endpoints — `endProbe` is the day containing
        // toLocal so we visit it to compute the partial.
        while (probe <= endProbe)
        {
            if (IsWorkDay(probe.DayOfWeek))
            {
                var shiftStart = new DateTimeOffset(
                    probe.Year, probe.Month, probe.Day,
                    ScoringConstants.ShiftStartHour, 0, 0, probe.Offset);
                var shiftEnd = new DateTimeOffset(
                    probe.Year, probe.Month, probe.Day,
                    ScoringConstants.ShiftEndHour, 0, 0, probe.Offset);

                // Clip the shift band to the requested period.
                var dayStart = shiftStart > fromLocal ? shiftStart : fromLocal;
                var dayEnd = shiftEnd < toLocal ? shiftEnd : toLocal;

                if (dayEnd > dayStart)
                {
                    var hours = (int)(dayEnd - dayStart).TotalHours;
                    if (hours > 0) total += hours;
                }
            }
            probe = probe.AddDays(1);
        }

        return total;
    }

    private static bool IsWorkDay(DayOfWeek d)
    {
        for (var i = 0; i < ScoringConstants.WorkDays.Length; i++)
        {
            if (ScoringConstants.WorkDays[i] == d) return true;
        }
        return false;
    }

    /// <summary>
    /// Phase 18.6 (Q18.3) — clock hours in the half-open UTC range,
    /// regardless of work-day or shift-band filters. The crew lead is
    /// always on shift (24×7), so payroll for the lead uses this full-clock
    /// number instead of <see cref="OnShiftHoursInRange"/>. Returns 0 when
    /// <see cref="CrewRow.ScheduleEnabled"/> is false (unbilled crew) so a
    /// disabled schedule still suppresses both lead and member earnings.
    /// </summary>
    public static int LeadHoursInRange(CrewRow crew, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (!crew.ScheduleEnabled) return 0;
        if (toUtc <= fromUtc) return 0;
        return (int)(toUtc - fromUtc).TotalHours;
    }

    /// <summary>
    /// Compute payroll for one crew over a half-open UTC range. Each member
    /// contributes <c>HourlyUsd × hours</c> where <c>hours</c> is
    /// <see cref="LeadHoursInRange"/> for the crew lead (24×7 per Q18.3) and
    /// <see cref="OnShiftHoursInRange"/> for everyone else. The caller is
    /// responsible for resolving membership at the time of the period (v0.1
    /// simplification: assumes the agent was a member for the full window).
    /// </summary>
    public static decimal ComputeTotal(CrewRow crew, IReadOnlyList<AgentRow> members, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var memberHours = OnShiftHoursInRange(crew, fromUtc, toUtc);
        var leadHours = LeadHoursInRange(crew, fromUtc, toUtc);
        if (memberHours == 0 && leadHours == 0) return 0m;
        decimal total = 0;
        for (var i = 0; i < members.Count; i++)
        {
            var isLead = !string.IsNullOrEmpty(crew.Lead)
                && string.Equals(members[i].Id, crew.Lead, StringComparison.Ordinal);
            var hours = isLead ? leadHours : memberHours;
            if (hours > 0) total += members[i].HourlyUsd * hours;
        }
        return total;
    }
}
