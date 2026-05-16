// -----------------------------------------------------------------------
// <copyright file="AgentMemoryReaper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Phase 55 / Phase G (docs/forge/sloppy_guard.md) -- periodic reaper
/// that deletes <see cref="AgentMemoryRow"/> rows whose
/// <see cref="AgentMemoryRow.UpdatedAt"/> is older than the configured
/// retention window. Stops <c>agent_memory</c> from growing forever as
/// workstreams complete: a row that hasn't been written by the
/// gen-eval loop for the retention window is presumed dormant
/// (terminal verdict, cancelled, or restart-replaced) and gets
/// dropped on the next tick.
///
/// <para><b>Why UpdatedAt instead of a join on work-item state:</b>
/// SQLite cross-table joins on a non-indexed predicate would scan
/// every memory row on every tick; the timestamp predicate hits the
/// row directly. Operators who restart a long-dormant workstream get
/// fresh memory on the first iter, which is the same shape as a
/// reassigned pair — acceptable, documented in the plan's "open
/// questions" section.</para>
///
/// <para><b>Configuration</b> (defaults shown):</para>
/// <list type="bullet">
/// <item><c>Forge:Memory:RetentionDays</c> -- 7. Rows older than this
/// many days get deleted on the next tick.</item>
/// <item><c>Forge:Memory:TickInterval</c> -- 1:00:00 (one hour). Time
/// between sweeps. The reaper's work is cheap (single DELETE) so an
/// hourly cadence keeps the table tight without measurable load.</item>
/// <item><c>Forge:Memory:Enabled</c> -- true. Set false to disable
/// reaping entirely (useful for dev or audit-window scenarios).</item>
/// </list>
/// </summary>
public sealed class AgentMemoryReaper : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration config;
    private readonly ILogger<AgentMemoryReaper> logger;

    public AgentMemoryReaper(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<AgentMemoryReaper> logger)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a beat before the first tick so app boot finishes
        // (migrations, seed). Same pattern as BudgetCooldownReconciler.
        var interval = this.GetTickInterval();
        try { await Task.Delay(interval, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (this.IsEnabled())
                {
                    await this.RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "AgentMemoryReaper tick errored — continuing");
            }

            try { await Task.Delay(this.GetTickInterval(), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick. Public so a test or admin endpoint can drive it
    /// without waiting for the interval timer. Returns the number of
    /// rows deleted.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var retention = this.GetRetentionDays();
        if (retention <= 0) return 0;

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retention);

        using var scope = this.scopeFactory.CreateScope();
        // Phase E — fan out per product so each product's agent_memory is
        // pruned independently. The AsyncLocal slug in ProductFanout means
        // nested GetRequiredService<ProductDbContext> calls inside the
        // callback automatically resolve to THIS product's DB.
        var fanout = scope.ServiceProvider.GetRequiredService<ProductFanout>();
        var totalDeleted = 0;
        var lockObj = new object();

        await fanout.ForEachProductAsync(async (db, slug, fct) =>
        {
            // SQLite EF cannot reliably translate DateTimeOffset comparisons
            // (TEXT-stored ISO 8601 mostly orders correctly, but mixed-nullable
            // casts trip the provider). Pull ids whose UpdatedAt is < cutoff
            // client-side, then bulk-delete by primary key.
            var stale = (await db.AgentMemory.AsNoTracking()
                .Select(m => new { m.Id, m.UpdatedAt })
                .ToListAsync(fct))
                .Where(m => m.UpdatedAt < cutoff)
                .Select(m => m.Id)
                .ToList();
            var deleted = stale.Count == 0 ? 0 : await db.AgentMemory
                .Where(m => stale.Contains(m.Id))
                .ExecuteDeleteAsync(fct);

            if (deleted > 0)
            {
                this.logger.LogInformation(
                    "AgentMemoryReaper: deleted {Count} memory row(s) older than {Cutoff} (retention {Days} days) in product={Slug}.",
                    deleted, cutoff, retention, slug);
                lock (lockObj) totalDeleted += deleted;
            }
        }, ct);

        return totalDeleted;
    }

    private TimeSpan GetTickInterval()
    {
        var raw = this.config["Forge:Memory:TickInterval"];
        if (TimeSpan.TryParse(raw, out var parsed) && parsed > TimeSpan.Zero) return parsed;
        return TimeSpan.FromHours(1);
    }

    private int GetRetentionDays()
    {
        var raw = this.config["Forge:Memory:RetentionDays"];
        if (int.TryParse(raw, out var parsed) && parsed > 0) return parsed;
        return 7;
    }

    private bool IsEnabled()
    {
        var raw = this.config["Forge:Memory:Enabled"];
        return string.IsNullOrEmpty(raw) || !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }
}
