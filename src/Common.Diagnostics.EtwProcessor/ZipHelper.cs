// -----------------------------------------------------------------------
// <copyright file="ZipHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using System.IO.Compression;
    using Microsoft.Extensions.Logging;
    using Spectre.Console;

    public class ZipHelper(ILogger<ZipHelper> logger)
    {
        private readonly ILogger<ZipHelper> logger = logger;

        public void ExtractAllWithProgress(
            IEnumerable<string> zipFiles,
            string extractPath,
            CancellationToken cancellationToken = default)
        {
            var zipList = zipFiles.ToList();
            if (zipList.Count == 0)
            {
                return;
            }

            var processedCount = 0;
            var maxParallelism = Environment.ProcessorCount;

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
                    var mainTask = ctx.AddTask(
                        $"[green]Extracting ZIP files[/] [dim](0/{zipList.Count})[/]",
                        maxValue: zipList.Count);

                    try
                    {
                        Parallel.ForEach(
                            zipList,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = maxParallelism,
                                CancellationToken = cancellationToken
                            },
                            zipFile =>
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                var fileName = Path.GetFileName(zipFile);
                                this.logger.LogDebug("Extracting {FileName}", fileName);

                                this.ExtractZipRecursively(zipFile, extractPath, cancellationToken);

                                var current = Interlocked.Increment(ref processedCount);
                                mainTask.Description = $"[green]Extracting ZIP files[/] [dim]({current}/{zipList.Count})[/]";
                                mainTask.Increment(1);
                            });

                        mainTask.Description = $"[green]ZIP extraction complete[/] [dim]({zipList.Count} files)[/]";
                    }
                    catch (OperationCanceledException)
                    {
                        mainTask.Description = "[yellow]ZIP extraction cancelled[/]";
                        throw;
                    }
                });
        }

        public void ExtractZipRecursively(
            string zipPath,
            string extractPath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetDir = Path.Combine(extractPath, Path.GetFileNameWithoutExtension(zipPath));
                Directory.CreateDirectory(targetDir);

                using var archive = ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var destinationPath = Path.Combine(targetDir, entry.FullName.Replace("/", Path.DirectorySeparatorChar.ToString()));

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else if (entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        var nestedDir = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(entry.Name));
                        Directory.CreateDirectory(nestedDir);

                        var tempZipPath = Path.Combine(nestedDir, entry.Name);
                        var dir = Path.GetDirectoryName(tempZipPath);
                        if (dir != null) Directory.CreateDirectory(dir);

                        entry.ExtractToFile(tempZipPath, overwrite: true);
                        this.ExtractZipRecursively(tempZipPath, nestedDir, cancellationToken);
                        File.Delete(tempZipPath);
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(destinationPath);
                        if (dir != null) Directory.CreateDirectory(dir);

                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning("Failed to extract {ZipPath}: {ErrorMessage}", zipPath, ex.Message);
            }
        }
    }
}