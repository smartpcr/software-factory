//-------------------------------------------------------------------------------
// <copyright file="BatchTraceProcessor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Common.Diagnostics.EtwParser.Core
{
    using System.Collections.Concurrent;
    using Common.Diagnostics.EtwParser.Models;
    using Common.Diagnostics.EtwParser.Parsers;

    /// <summary>
    /// Processes multiple trace files in parallel batches
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="BatchTraceProcessor"/> class.
    /// </remarks>
    /// <param name="maxDegreeOfParallelism">Maximum number of parallel operations (default: CPU count)</param>
    /// <param name="batchSize">Number of files to process per batch (default: all files in one batch)</param>
    public class BatchTraceProcessor(int? maxDegreeOfParallelism = null, int batchSize = int.MaxValue)
    {
        private readonly int maxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount;
        private readonly int batchSize = batchSize;

        /// <summary>
        /// Discovers schemas from multiple trace files in parallel
        /// </summary>
        /// <param name="filePaths">Paths to trace files</param>
        /// <param name="progressCallback">Optional progress callback</param>
        /// <returns>Dictionary of discovered schemas</returns>
        public IDictionary<EventIdentifier, TraceEventSchema> DiscoverSchemas(
            IEnumerable<string> filePaths,
            Action<int, int>? progressCallback = null)
        {
            var allSchemas = new ConcurrentDictionary<EventIdentifier, TraceEventSchema>();
            var files = filePaths.ToArray();
            var processed = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = this.maxDegreeOfParallelism
            };

            Parallel.ForEach(files, parallelOptions, filePath =>
            {
                try
                {
                    var parser = CreateParser(filePath);
                    var localSchemas = new Dictionary<EventIdentifier, TraceEventSchema>();

                    var result = parser.DiscoverSchemas(localSchemas);

                    if (result.Success)
                    {
                        foreach (var kvp in localSchemas)
                        {
                            allSchemas.TryAdd(kvp.Key, kvp.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
                }
                finally
                {
                    var currentProcessed = Interlocked.Increment(ref processed);
                    progressCallback?.Invoke(currentProcessed, files.Length);
                }
            });

            return allSchemas;
        }

        /// <summary>
        /// Extracts events from multiple trace files in parallel
        /// </summary>
        /// <param name="filePaths">Paths to trace files</param>
        /// <param name="knownSchemas">Known event schemas</param>
        /// <param name="progressCallback">Optional progress callback</param>
        /// <returns>Dictionary of extracted events</returns>
        public IDictionary<EventIdentifier, IList<TraceEventRecord>> ExtractEvents(
            IEnumerable<string> filePaths,
            IDictionary<EventIdentifier, TraceEventSchema> knownSchemas,
            Action<int, int>? progressCallback = null)
        {
            var allEvents = new ConcurrentDictionary<EventIdentifier, IList<TraceEventRecord>>();
            var files = filePaths.ToArray();
            var processed = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = this.maxDegreeOfParallelism
            };

            Parallel.ForEach(files, parallelOptions, filePath =>
            {
                try
                {
                    var parser = CreateParser(filePath);
                    var events = parser.ExtractEvents(knownSchemas);

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
                    Console.WriteLine($"Error processing file {filePath}: {ex.Message}");
                }
                finally
                {
                    var currentProcessed = Interlocked.Increment(ref processed);
                    progressCallback?.Invoke(currentProcessed, files.Length);
                }
            });

            return allEvents;
        }

        /// <summary>
        /// Processes trace files in batches with memory management
        /// </summary>
        public async Task ProcessInBatchesAsync(
            IEnumerable<string> filePaths,
            IDictionary<EventIdentifier, TraceEventSchema> allSchemas,
            IEventExporter exporter,
            Action<int, int>? batchProgressCallback = null)
        {
            var files = filePaths.ToArray();
            var totalBatches = (int)Math.Ceiling((double)files.Length / this.batchSize);

            for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
            {
                var batchFiles = files.Skip(batchIndex * this.batchSize).Take(this.batchSize);

                // Extract events for this batch
                var batchEvents = this.ExtractEvents(batchFiles, allSchemas);

                // Export the batch
                await exporter.ExportAsync(batchEvents, allSchemas);

                // Clear memory
                batchEvents.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                batchProgressCallback?.Invoke(batchIndex + 1, totalBatches);
            }
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