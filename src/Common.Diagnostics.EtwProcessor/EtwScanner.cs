// -----------------------------------------------------------------------
// <copyright file="EtwScanner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using Common.Diagnostics.EtwParser.Core;
    using Common.Diagnostics.EtwParser.Models;
    using Common.Diagnostics.EtwParser.Parsers;
    using Microsoft.Extensions.Logging;
    using Spectre.Console;

    public class EtwScanner
    {
        private readonly ILogger<EtwScanner> logger;
        private readonly ZipHelper zipHelper;
        private string inputDirectory = string.Empty;
        private string tempDirectory = string.Empty;
        private CancellationToken cancellationToken;

        public EtwScanner(ILogger<EtwScanner> logger, ZipHelper zipHelper)
        {
            this.logger = logger;
            this.zipHelper = zipHelper;
        }

        public string TempDirectory => this.tempDirectory;

        public void Configure(string inputDirectory, CancellationToken cancellationToken = default)
        {
            this.inputDirectory = inputDirectory;
            this.tempDirectory = Path.Combine(Path.GetTempPath(), $"EtwScanner_{Guid.NewGuid():N}");
            this.cancellationToken = cancellationToken;
        }

        public int Run()
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                this.logger.LogInformation("Discovering files...");
                var traceFiles = this.DiscoverTraceFiles();

                this.cancellationToken.ThrowIfCancellationRequested();

                this.logger.LogInformation(
                    "Found {TotalFiles} trace files ({EtlCount} ETL, {EvtxCount} EVTX)",
                    traceFiles.Count,
                    traceFiles.Count(f => f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)),
                    traceFiles.Count(f => f.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase)));

                if (traceFiles.Count == 0)
                {
                    this.logger.LogWarning("No trace files found.");
                    return 0;
                }

                this.logger.LogInformation("Scanning events...");
                var providerStats = this.ScanProviders(traceFiles);

                this.cancellationToken.ThrowIfCancellationRequested();

                this.PrintScanResults(providerStats);

                stopwatch.Stop();
                this.logger.LogInformation("Scan completed in {ElapsedSeconds:F2} seconds", stopwatch.Elapsed.TotalSeconds);

                return 0;
            }
            catch (OperationCanceledException)
            {
                this.logger.LogWarning("Scan cancelled by user");
                return 1;
            }
            finally
            {
                this.CleanupTempDirectory();
            }
        }

        public void CleanupTempDirectory()
        {
            if (!string.IsNullOrEmpty(this.tempDirectory) && Directory.Exists(this.tempDirectory))
            {
                try
                {
                    this.logger.LogInformation("Cleaning up temp directory: {TempDirectory}", this.tempDirectory);
                    Directory.Delete(this.tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning("Failed to cleanup temp directory: {ErrorMessage}", ex.Message);
                }
            }
        }

        private Dictionary<string, ProviderStatistics> ScanProviders(List<string> traceFiles)
        {
            var stats = new ConcurrentDictionary<string, ProviderStatistics>();
            var processed = 0;
            var total = traceFiles.Count;
            var totalEvents = 0L;

            AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    var scanTask = ctx.AddTask(
                        $"[green]Scanning files[/] [dim](0/{total})[/]",
                        maxValue: total);

                    try
                    {
                        Parallel.ForEach(
                            traceFiles,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = Environment.ProcessorCount,
                                CancellationToken = this.cancellationToken
                            },
                            file =>
                            {
                                try
                                {
                                    var parser = CreateParser(file);
                                    var schemas = new Dictionary<EventIdentifier, TraceEventSchema>();
                                    parser.DiscoverSchemas(schemas);

                                    var events = parser.ExtractEvents(schemas);

                                    foreach (var kvp in events)
                                    {
                                        var providerName = kvp.Key.ProviderName;
                                        var records = kvp.Value;

                                        Interlocked.Add(ref totalEvents, records.Count);

                                        stats.AddOrUpdate(
                                            providerName,
                                            _ =>
                                            {
                                                var minTime = records.Min(r => r.TimeStamp);
                                                var maxTime = records.Max(r => r.TimeStamp);
                                                return new ProviderStatistics
                                                {
                                                    ProviderName = providerName,
                                                    EventCount = records.Count,
                                                    StartTime = minTime,
                                                    EndTime = maxTime,
                                                    EventTypes = new ConcurrentDictionary<string, long>(
                                                        new[] { KeyValuePair.Create(kvp.Key.EventName, (long)records.Count) })
                                                };
                                            },
                                            (_, existing) =>
                                            {
                                                lock (existing)
                                                {
                                                    existing.EventCount += records.Count;
                                                    var minTime = records.Min(r => r.TimeStamp);
                                                    var maxTime = records.Max(r => r.TimeStamp);
                                                    if (minTime < existing.StartTime) existing.StartTime = minTime;
                                                    if (maxTime > existing.EndTime) existing.EndTime = maxTime;
                                                    existing.EventTypes.AddOrUpdate(
                                                        kvp.Key.EventName,
                                                        records.Count,
                                                        (_, count) => count + records.Count);
                                                }
                                                return existing;
                                            });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    this.logger.LogWarning("Failed to scan {FileName}: {ErrorMessage}", Path.GetFileName(file), ex.Message);
                                }

                                var current = Interlocked.Increment(ref processed);
                                var eventCount = Interlocked.Read(ref totalEvents);
                                scanTask.Description = $"[green]Scanning files[/] [dim]({current}/{total}, {eventCount:N0} events)[/]";
                                scanTask.Increment(1);
                            });

                        scanTask.Description = $"[green]Scan complete[/] [dim]({total} files, {Interlocked.Read(ref totalEvents):N0} events)[/]";
                    }
                    catch (OperationCanceledException)
                    {
                        scanTask.Description = "[yellow]Scan cancelled[/]";
                        throw;
                    }
                });

            return stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        private void PrintScanResults(Dictionary<string, ProviderStatistics> stats)
        {
            var sortedProviders = stats.Values.OrderByDescending(p => p.EventCount).ToList();
            var totalEvents = sortedProviders.Sum(p => p.EventCount);
            var globalStart = sortedProviders.Min(p => p.StartTime);
            var globalEnd = sortedProviders.Max(p => p.EndTime);

            this.logger.LogInformation("=== Scan Results ===");
            this.logger.LogInformation("Total Providers: {ProviderCount}", sortedProviders.Count);
            this.logger.LogInformation("Total Events: {EventCount:N0}", totalEvents);
            this.logger.LogInformation("Time Range: {StartTime:yyyy-MM-dd HH:mm:ss} to {EndTime:yyyy-MM-dd HH:mm:ss}", globalStart, globalEnd);

            this.logger.LogInformation("{Separator}", new string('-', 100));
            this.logger.LogInformation("{Provider,-50} {Events,12} {StartTime,18} {EndTime,18}", "Provider", "Events", "Start Time", "End Time");
            this.logger.LogInformation("{Separator}", new string('-', 100));

            foreach (var provider in sortedProviders)
            {
                var name = provider.ProviderName.Length > 50
                    ? provider.ProviderName[..47] + "..."
                    : provider.ProviderName;

                this.logger.LogInformation(
                    "{Provider,-50} {EventCount,12:N0} {StartTime,18:yyyy-MM-dd HH:mm} {EndTime,18:yyyy-MM-dd HH:mm}",
                    name,
                    provider.EventCount,
                    provider.StartTime,
                    provider.EndTime);
            }

            this.logger.LogInformation("{Separator}", new string('-', 100));
            this.logger.LogInformation("{Provider,-50} {EventCount,12:N0}", "TOTAL", totalEvents);
        }

        private List<string> DiscoverTraceFiles()
        {
            var traceFiles = new List<string>();
            var zipFiles = Directory.GetFiles(this.inputDirectory, "*.zip", SearchOption.AllDirectories);

            if (zipFiles.Length > 0)
            {
                this.logger.LogInformation("Found {ZipCount} ZIP files to extract...", zipFiles.Length);
                Directory.CreateDirectory(this.tempDirectory);

                this.zipHelper.ExtractAllWithProgress(zipFiles, this.tempDirectory, this.cancellationToken);

                traceFiles.AddRange(FileHelper.FindTraceFiles(this.tempDirectory));
            }

            traceFiles.AddRange(FileHelper.FindTraceFiles(this.inputDirectory));

            return traceFiles
                .Distinct()
                .Where(f => new FileInfo(f).Length > 0)
                .ToList();
        }

        private static ITraceEventParser CreateParser(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".etl" => new EtlFileParser(filePath),
                ".evtx" => new EvtxFileParser(filePath),
                _ => throw new NotSupportedException($"Unsupported file type: {extension}")
            };
        }
    }
}
