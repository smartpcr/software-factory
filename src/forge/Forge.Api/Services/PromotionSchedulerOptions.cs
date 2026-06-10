// -----------------------------------------------------------------------
// <copyright file="PromotionSchedulerOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Phase 91 — config for the nightly auto-promotion scheduler
/// (<see cref="PromotionScheduler"/>). Bound from
/// <c>Forge:Promotion:Scheduler:*</c>.
/// <para>
/// The scheduler runs <see cref="IPromotionRunner.RunAsync(PromotionRunOptions, System.Threading.CancellationToken)"/>
/// with <see cref="PromotionRunOptions.NightlyAuto"/> once per day at
/// <see cref="RunHourLocal"/> in <see cref="Timezone"/> (default 02:00
/// America/Los_Angeles — the "nightly job @ 02:00 PT" the Performance view
/// advertises). Promotion-only, gated by avg &gt;
/// <see cref="ScoringConstants.AutoPromoteMinAvg"/> + throughput &ge;
/// <see cref="ScoringConstants.AutoPromoteMinThroughput"/>.
/// </para>
/// </summary>
public sealed class PromotionSchedulerOptions
{
    public const string SectionName = "Forge:Promotion:Scheduler";

    /// <summary>
    /// When false, the scheduler is registered but its loop does not run.
    /// Tests resolve <see cref="PromotionScheduler"/> directly and drive
    /// <see cref="PromotionScheduler.RunOnceAsync"/> deterministically.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local hour-of-day (0–23) the nightly run fires. Default 2 (02:00).</summary>
    public int RunHourLocal { get; set; } = 2;

    /// <summary>
    /// IANA timezone the <see cref="RunHourLocal"/> is interpreted in.
    /// Default <see cref="ScoringConstants.DefaultTimezone"/>
    /// (<c>America/Los_Angeles</c>). Falls back to the equivalent Windows id
    /// when the IANA id is not resolvable on the host.
    /// </summary>
    public string Timezone { get; set; } = ScoringConstants.DefaultTimezone;
}
