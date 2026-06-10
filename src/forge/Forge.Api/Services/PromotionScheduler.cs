// -----------------------------------------------------------------------
// <copyright file="PromotionScheduler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Diagnostics.Metrics;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 91 — the nightly auto-promotion scheduler. This is what "enable
/// automatic promotion" wires up: a <see cref="BackgroundService"/> that, once
/// per day at <see cref="PromotionSchedulerOptions.RunHourLocal"/> in the
/// configured timezone (default 02:00 America/Los_Angeles — the "nightly job @
/// 02:00 PT" the Performance scoreboard advertises), runs
/// <see cref="IPromotionRunner"/> with
/// <see cref="PromotionRunOptions.NightlyAuto"/>.
///
/// <para><b>Gate.</b> NightlyAuto is promotion-only and layers an evidence gate
/// on top of the existing tier verdict: an agent is auto-promoted only when the
/// tier policy already reads <see cref="PromotionPolicy.ReasonPromote"/> (avg ≥
/// tier threshold, zero rejects, cooldown clear — i.e. Promo-ETA "ready") AND
/// its trailing-window average is strictly &gt;
/// <see cref="ScoringConstants.AutoPromoteMinAvg"/> AND its throughput is ≥
/// <see cref="ScoringConstants.AutoPromoteMinThroughput"/>. The displayed
/// verdict / Promo-ETA on the scoreboard are unchanged — only the automatic
/// level mutation is gated. Demotions and manual operator overrides are
/// unaffected.</para>
///
/// <para>Each applied promotion broadcasts <c>agent.level.changed</c> over
/// SignalR (same payload shape as the Phase 89 PO-override endpoints, with a
/// null actor to denote the system), so any live scoreboard subscriber sees the
/// bump.</para>
/// </summary>
public sealed class PromotionScheduler : BackgroundService
{
    private static readonly Meter Meter = LeadAutoDispatcher.Meter;
    private static readonly Counter<long> RunsCounter =
        Meter.CreateCounter<long>("forge.promotion.scheduler.runs");
    private static readonly Counter<long> PromotedCounter =
        Meter.CreateCounter<long>("forge.promotion.scheduler.promoted");

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IHubContext<ForgeHub> hub;
    private readonly IOptionsMonitor<PromotionSchedulerOptions> options;
    private readonly ILogger<PromotionScheduler> logger;

    public PromotionScheduler(
        IServiceScopeFactory scopeFactory,
        IHubContext<ForgeHub> hub,
        IOptionsMonitor<PromotionSchedulerOptions> options,
        ILogger<PromotionScheduler> logger)
    {
        this.scopeFactory = scopeFactory;
        this.hub = hub;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = this.options.CurrentValue;
            var tz = ResolveTimeZone(opts.Timezone);
            var delay = ComputeNextRunDelay(DateTimeOffset.UtcNow, tz, opts.RunHourLocal);

            this.logger.LogInformation(
                "PromotionScheduler — next auto-promotion run in {Delay} (at {Hour:00}:00 {Tz})",
                delay, opts.RunHourLocal, tz.Id);

            try { await Task.Delay(delay, stoppingToken); }
            catch (TaskCanceledException) { return; }

            if (stoppingToken.IsCancellationRequested) return;

            if (!this.options.CurrentValue.Enabled)
            {
                continue;
            }

            try
            {
                await this.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PromotionScheduler tick errored — continuing");
            }
        }
    }

    /// <summary>
    /// One nightly tick. Public + returns the run result so tests can drive it
    /// deterministically without waiting for the timer. Runs the promotion
    /// runner in <see cref="PromotionRunOptions.NightlyAuto"/> mode and
    /// broadcasts <c>agent.level.changed</c> for each applied promotion.
    /// </summary>
    public async Task<PromotionRunResult> RunOnceAsync(CancellationToken ct)
    {
        RunsCounter.Add(1);

        using var scope = this.scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IPromotionRunner>();

        var result = await runner.RunAsync(PromotionRunOptions.NightlyAuto, ct);

        if (result.Promoted > 0)
        {
            PromotedCounter.Add(result.Promoted);
            this.logger.LogInformation(
                "PromotionScheduler promoted {Promoted}/{Scanned} agents (auto gate avg>{Avg} & throughput>={Tp}).",
                result.Promoted, result.AgentsScanned,
                ScoringConstants.AutoPromoteMinAvg, ScoringConstants.AutoPromoteMinThroughput);
        }

        foreach (var d in result.Decisions)
        {
            if (d.Reason != PromotionPolicy.ReasonPromote) continue;

            // Mirror the Phase 89 OperatorDecisionResult shape so any live
            // scoreboard subscriber handles nightly + manual promotions the
            // same way. actor=null denotes the system (nightly job).
            var payload = new
            {
                agentId = d.AgentId,
                oldLevel = d.OldLevel,
                newLevel = d.NewLevel,
                hourlyUsd = (double)ScoringConstants.HourlyForLevel(d.NewLevel),
                reason = PromotionPolicy.ReasonPromote,
                actor = (string?)null,
                at = result.At,
            };
            await this.hub.Clients.All.SendAsync("agent.level.changed", payload, ct);
        }

        return result;
    }

    /// <summary>
    /// Pure delay calculation: the time from <paramref name="nowUtc"/> until the
    /// next occurrence of <paramref name="runHourLocal"/>:00 wall-clock in
    /// <paramref name="tz"/>. If the target hour has already passed today
    /// (local), returns the delay until tomorrow's occurrence. DST-safe — the
    /// target local time is converted to UTC through the timezone rules.
    /// </summary>
    public static TimeSpan ComputeNextRunDelay(DateTimeOffset nowUtc, TimeZoneInfo tz, int runHourLocal)
    {
        ArgumentNullException.ThrowIfNull(tz);
        var hour = Math.Clamp(runHourLocal, 0, 23);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, tz);
        var candidateLocal = new DateTime(
            nowLocal.Year, nowLocal.Month, nowLocal.Day, hour, 0, 0, DateTimeKind.Unspecified);
        if (candidateLocal <= nowLocal)
        {
            candidateLocal = candidateLocal.AddDays(1);
        }

        var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(candidateLocal, tz);
        var delay = candidateUtc - nowUtc.UtcDateTime;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>
    /// Resolve a timezone by IANA id, falling back to the common Windows
    /// equivalent (and finally UTC) so the scheduler boots on any host.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // Windows hosts without ICU IANA data — map the spec default.
        if (string.Equals(id, "America/Los_Angeles", StringComparison.OrdinalIgnoreCase))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }
}
