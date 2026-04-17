// -----------------------------------------------------------------------
// <copyright file="RunCleanupHostedService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Background service that deletes run JSONL files older than the configured retention period.
/// </summary>
public class RunCleanupHostedService : BackgroundService
{
    private readonly PersistenceOptions options;
    private readonly IClock clock;
    private readonly ILogger<RunCleanupHostedService> logger;
    private readonly TimeSpan interval = TimeSpan.FromHours(1);

    public RunCleanupHostedService(
        IOptions<PersistenceOptions> options,
        IClock clock,
        ILogger<RunCleanupHostedService> logger)
    {
        this.options = options.Value;
        this.clock = clock;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                this.CleanOldRuns();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Run cleanup failed");
            }

            await Task.Delay(this.interval, stoppingToken);
        }
    }

    /// <summary>
    /// Removes run directories older than RunRetentionDays.
    /// </summary>
    public void CleanOldRuns()
    {
        var runsRoot = this.options.RunsRoot;
        if (!Directory.Exists(runsRoot))
        {
            return;
        }

        var cutoff = this.clock.UtcNow.AddDays(-this.options.RunRetentionDays);
        var runDirs = Directory.GetDirectories(runsRoot);

        foreach (var dir in runDirs)
        {
            var eventsFile = Path.Combine(dir, "events.jsonl");
            if (!File.Exists(eventsFile))
            {
                continue;
            }

            var lastWrite = new FileInfo(eventsFile).LastWriteTimeUtc;
            if (lastWrite < cutoff)
            {
                Directory.Delete(dir, recursive: true);
                this.logger.LogInformation("Cleaned run directory {Dir} (last write {LastWrite})", dir, lastWrite);
            }
        }
    }
}
