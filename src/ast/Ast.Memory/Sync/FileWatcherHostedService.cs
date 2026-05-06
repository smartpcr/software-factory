// -----------------------------------------------------------------------
// <copyright file="FileWatcherHostedService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IHostedService"/> that starts and stops the
/// <see cref="IIncrementalSync"/> file-system watcher as part of the
/// ASP.NET Core / Generic Host lifecycle. Register this service in DI
/// to automatically watch for file saves on startup and clean up on
/// shutdown. See architecture.md §3.6.
///
/// The service resolves the repository root from <see cref="MemoryOptions.RepoRoot"/>
/// (which should be set at startup via configuration or <c>IOptions&lt;MemoryOptions&gt;</c>).
/// </summary>
public sealed class FileWatcherHostedService : IHostedService
{
    private readonly IIncrementalSync incrementalSync;
    private readonly MemoryOptions options;
    private readonly ILogger<FileWatcherHostedService> logger;

    public FileWatcherHostedService(
        IIncrementalSync incrementalSync,
        IOptions<MemoryOptions> options,
        ILogger<FileWatcherHostedService> logger)
    {
        this.incrementalSync = incrementalSync;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var repoRoot = this.options.RepoRoot;
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            this.logger.LogWarning(
                "MemoryOptions.RepoRoot is not configured — file watcher will not start");
            return Task.CompletedTask;
        }

        if (!Directory.Exists(repoRoot))
        {
            this.logger.LogWarning(
                "MemoryOptions.RepoRoot '{RepoRoot}' does not exist — file watcher will not start",
                repoRoot);
            return Task.CompletedTask;
        }

        this.logger.LogInformation(
            "Starting file-system watcher on {RepoRoot}",
            repoRoot);

        this.incrementalSync.StartFileWatcher(repoRoot);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.logger.LogInformation("Stopping file-system watcher");
        this.incrementalSync.StopFileWatcher();
        return Task.CompletedTask;
    }
}