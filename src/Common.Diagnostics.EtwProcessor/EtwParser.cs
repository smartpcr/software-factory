// -----------------------------------------------------------------------
// <copyright file="EtwParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Text;
    using Common.Diagnostics.EtwParser.Core;
    using Common.Diagnostics.EtwParser.Extensions;
    using Common.Diagnostics.EtwParser.Models;
    using Common.Diagnostics.EtwParser.Parsers;
    using Microsoft.Extensions.Logging;

    public class EtwParser(ILogger<EtwParser> logger, ZipHelper zipHelper)
    {
        private readonly ILogger<EtwParser> logger = logger;
        private readonly ZipHelper zipHelper = zipHelper;
        private string inputDirectory = string.Empty;
        private string outputDirectory = string.Empty;
        private string tempDirectory = string.Empty;
        private CancellationToken cancellationToken;

        public string TempDirectory => this.tempDirectory;

        public void Configure(string inputDirectory, string outputDirectory, CancellationToken cancellationToken = default)
        {
            this.inputDirectory = inputDirectory;
            this.outputDirectory = outputDirectory;
            this.tempDirectory = Path.Combine(Path.GetTempPath(), $"EtwParser_{Guid.NewGuid():N}");
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

                this.logger.LogInformation("Found {FileCount} trace files", traceFiles.Count);

                if (traceFiles.Count == 0)
                {
                    this.logger.LogWarning("No trace files found.");
                    return 0;
                }

                this.logger.LogInformation("Discovering schemas...");
                var allSchemas = new ConcurrentDictionary<EventIdentifier, TraceEventSchema>();
                this.DiscoverSchemas(traceFiles, allSchemas);

                this.cancellationToken.ThrowIfCancellationRequested();

                this.logger.LogInformation("Discovered {SchemaCount} unique event schemas", allSchemas.Count);

                this.logger.LogInformation("Extracting and exporting events by provider...");
                var totalRecords = this.ExtractAndExportByProvider(traceFiles, allSchemas);

                this.cancellationToken.ThrowIfCancellationRequested();

                this.logger.LogInformation("Exported {TotalRecords:N0} total records", totalRecords);

                stopwatch.Stop();
                this.logger.LogInformation("Parse completed in {ElapsedSeconds:F2} seconds", stopwatch.Elapsed.TotalSeconds);
                this.logger.LogInformation("Output directory: {OutputDirectory}", this.outputDirectory);

                return 0;
            }
            catch (OperationCanceledException)
            {
                this.logger.LogWarning("Parse cancelled by user");
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

        private void DiscoverSchemas(List<string> traceFiles, ConcurrentDictionary<EventIdentifier, TraceEventSchema> schemas)
        {
            var processed = 0;
            var total = traceFiles.Count;

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
                        var localSchemas = new Dictionary<EventIdentifier, TraceEventSchema>();
                        parser.DiscoverSchemas(localSchemas);

                        foreach (var kvp in localSchemas)
                        {
                            schemas.TryAdd(kvp.Key, kvp.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning("{FileName}: {ErrorMessage}", Path.GetFileName(file), ex.Message);
                    }

                    var current = Interlocked.Increment(ref processed);
                    if (current % 10 == 0 || current == total)
                    {
                        this.logger.LogInformation("Schema discovery: {Current}/{Total} files...", current, total);
                    }
                });
        }

        private long ExtractAndExportByProvider(
            List<string> traceFiles,
            ConcurrentDictionary<EventIdentifier, TraceEventSchema> schemas)
        {
            // Group schemas by provider
            var schemasByProvider = schemas
                .GroupBy(kvp => kvp.Key.ProviderName)
                .ToDictionary(g => g.Key, g => g.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

            var allEvents = new ConcurrentDictionary<EventIdentifier, IList<TraceEventRecord>>();
            var processed = 0;
            var total = traceFiles.Count;

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
                        var events = parser.ExtractEvents(schemas);

                        foreach (var kvp in events)
                        {
                            var list = allEvents.GetOrAdd(kvp.Key, _ => []);
                            lock (list)
                            {
                                foreach (var record in kvp.Value)
                                {
                                    list.Add(record);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning("{FileName}: {ErrorMessage}", Path.GetFileName(file), ex.Message);
                    }

                    var current = Interlocked.Increment(ref processed);
                    if (current % 10 == 0 || current == total)
                    {
                        this.logger.LogInformation("Event extraction: {Current}/{Total} files...", current, total);
                    }
                });

            // Export by provider
            long totalRecords = 0;
            var eventsByProvider = allEvents
                .GroupBy(kvp => kvp.Key.ProviderName)
                .ToDictionary(g => g.Key, g => g.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

            foreach (var providerGroup in eventsByProvider)
            {
                this.cancellationToken.ThrowIfCancellationRequested();

                var providerName = providerGroup.Key;
                var safeProviderName = MakeSafeDirectoryName(providerName);
                var providerDir = Path.Combine(this.outputDirectory, safeProviderName);
                Directory.CreateDirectory(providerDir);

                var providerSchemas = schemasByProvider.TryGetValue(providerName, out var ps)
                    ? ps
                    : [];

                var exporter = new CsvExporter(providerDir, filePrefix: null, includeHeaders: true);
                var result = exporter.ExportAsync(providerGroup.Value, providerSchemas).GetAwaiter().GetResult();

                if (result.Success)
                {
                    totalRecords += result.RecordCount;
                    this.logger.LogDebug("  {ProviderName}: {RecordCount:N0} records", providerName, result.RecordCount);
                }
            }

            return totalRecords;
        }

        private static string MakeSafeDirectoryName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name);
            foreach (var c in invalid)
            {
                sb.Replace(c, '_');
            }
            return sb.ToString();
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

            return [.. traceFiles
                .Distinct()
                .Where(f => new FileInfo(f).Length > 0)];
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