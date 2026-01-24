// -----------------------------------------------------------------------
// <copyright file="Neo4jDockerFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.IntegrationTests;

using System.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fixture that ensures Neo4j is running in Docker before tests.
/// docker-compose.yml is copied to the output directory via the csproj link.
/// Uses 'docker compose' on Linux and 'podman compose' on Windows.
/// </summary>
public static class Neo4jDockerFixture
{
    private static readonly string dockerComposeDir = AppContext.BaseDirectory;
    private static readonly object syncLock = new();
    private static bool initialized;
    private static CancellationTokenSource? healthMonitorCts;
    private static Task? healthMonitorTask;
    private static bool isHealthy;
    private static string? lastHealthError;

    /// <summary>
    /// Default timeout for health checks.
    /// </summary>
    public static TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval for background health monitoring.
    /// </summary>
    public static TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    // Lazy logger to ensure it's created after TestLoggerFactory.Initialize is called
    private static ILogger Logger => TestLoggerFactory.CreateLogger(nameof(Neo4jDockerFixture));

    private static string ComposeCommand => OperatingSystem.IsWindows() ? "podman" : "docker";
    private static string ComposeArgs => "compose";

    /// <summary>
    /// Gets the Neo4j connection URI.
    /// </summary>
    public static string Neo4jUri => "bolt://localhost:7687";

    /// <summary>
    /// Gets the Neo4j username.
    /// </summary>
    public static string Neo4jUsername => "neo4j";

    /// <summary>
    /// Gets the Neo4j password.
    /// </summary>
    public static string Neo4jPassword => "password123";

    /// <summary>
    /// Gets whether Neo4j is currently healthy.
    /// </summary>
    public static bool IsHealthy => isHealthy;

    /// <summary>
    /// Gets the last health check error message.
    /// </summary>
    public static string? LastHealthError => lastHealthError;

    /// <summary>
    /// Ensures Neo4j is running and starts background health monitoring.
    /// </summary>
    public static async Task EnsureNeo4jRunningAsync(CancellationToken cancellationToken = default)
    {
        lock (syncLock)
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
        }

        Logger.LogInformation("Checking if Neo4j is running...");

        // Check if Neo4j is already running
        var (healthy, error) = await CheckHealthWithTimeoutAsync(cancellationToken);
        if (healthy)
        {
            Logger.LogInformation("Neo4j is already running and healthy");
            isHealthy = true;
            StartHealthMonitor();
            return;
        }

        Logger.LogInformation("Starting Neo4j via {Command} {Args}...", ComposeCommand, ComposeArgs);

        // Start Neo4j using docker/podman compose
        var dockerComposePath = Path.GetFullPath(Path.Combine(dockerComposeDir, "docker-compose.yml"));

        if (!File.Exists(dockerComposePath))
        {
            throw new InvalidOperationException($"docker-compose.yml not found at: {dockerComposePath}");
        }

        await RunComposeCommandAsync($"-f \"{dockerComposePath}\" up -d neo4j", cancellationToken);

        Logger.LogInformation("Waiting for Neo4j to be healthy...");

        // Wait for Neo4j to be healthy with timeout
        var maxWaitTime = TimeSpan.FromMinutes(2);
        var startTime = DateTime.UtcNow;
        var attemptCount = 0;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptCount++;

            var (isHealthyNow, healthError) = await CheckHealthWithTimeoutAsync(cancellationToken);
            if (isHealthyNow)
            {
                Logger.LogInformation("Neo4j is now healthy and ready after {Attempts} attempts", attemptCount);
                isHealthy = true;
                StartHealthMonitor();
                return;
            }

            if (healthError != null)
            {
                Logger.LogDebug("Health check attempt {Attempt} failed: {Error}", attemptCount, healthError);
            }

            // Check container logs for errors
            if (attemptCount % 3 == 0)
            {
                await LogContainerStatusAsync(cancellationToken);
            }

            await Task.Delay(5000, cancellationToken);
        }

        // Final attempt to get error details
        await LogContainerStatusAsync(cancellationToken);
        var logs = await GetContainerLogsAsync(50, cancellationToken);

        throw new TimeoutException(
            $"Timed out waiting for Neo4j to become healthy after {attemptCount} attempts.\n" +
            $"Last error: {lastHealthError}\n" +
            $"Container logs:\n{logs}");
    }

    /// <summary>
    /// Checks Neo4j health with a timeout.
    /// </summary>
    public static async Task<(bool isHealthy, string? error)> CheckHealthWithTimeoutAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(HealthCheckTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            using var driver = Neo4j.Driver.GraphDatabase.Driver(
                Neo4jUri,
                Neo4j.Driver.AuthTokens.Basic(Neo4jUsername, Neo4jPassword));

            await driver.VerifyConnectivityAsync();

            // Run a simple query to verify the database is responsive
            await using var session = driver.AsyncSession();
            var result = await session.RunAsync("RETURN 1 as n");
            await result.ConsumeAsync();

            lastHealthError = null;
            return (true, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var error = $"Health check timed out after {HealthCheckTimeout.TotalSeconds}s";
            lastHealthError = error;
            return (false, error);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            lastHealthError = error;
            return (false, error);
        }
    }

    /// <summary>
    /// Asserts that Neo4j is healthy, throwing if not.
    /// </summary>
    public static async Task AssertHealthyAsync(CancellationToken cancellationToken = default)
    {
        var (healthy, error) = await CheckHealthWithTimeoutAsync(cancellationToken);
        if (!healthy)
        {
            var logs = await GetContainerLogsAsync(30, cancellationToken);
            throw new InvalidOperationException(
                $"Neo4j is not healthy: {error}\n" +
                $"Container logs:\n{logs}");
        }
    }

    /// <summary>
    /// Executes an operation with health check, failing fast if database is unhealthy.
    /// </summary>
    public static async Task<T> ExecuteWithHealthCheckAsync<T>(
        Func<Task<T>> operation,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // First verify health
        await AssertHealthyAsync(cancellationToken);

        var operationTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutCts = new CancellationTokenSource(operationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Check if it's a health issue
            var (healthy, error) = await CheckHealthWithTimeoutAsync(CancellationToken.None);
            if (!healthy)
            {
                var logs = await GetContainerLogsAsync(30, CancellationToken.None);
                throw new InvalidOperationException(
                    $"Operation timed out and Neo4j is unhealthy: {error}\n" +
                    $"Container logs:\n{logs}");
            }
            throw new TimeoutException($"Operation timed out after {operationTimeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            // Check if it's a health issue
            var (healthy, error) = await CheckHealthWithTimeoutAsync(CancellationToken.None);
            if (!healthy)
            {
                var logs = await GetContainerLogsAsync(30, CancellationToken.None);
                throw new InvalidOperationException(
                    $"Operation failed and Neo4j is unhealthy: {error}\n" +
                    $"Original error: {ex.Message}\n" +
                    $"Container logs:\n{logs}",
                    ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Gets recent container logs.
    /// </summary>
    public static async Task<string> GetContainerLogsAsync(int tailLines = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var dockerComposePath = GetDockerComposePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ComposeCommand,
                Arguments = $"{ComposeArgs} -f \"{dockerComposePath}\" logs --tail {tailLines} neo4j",
                WorkingDirectory = Path.GetDirectoryName(dockerComposePath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return "[Failed to start process to get logs]";
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            return string.IsNullOrEmpty(output) ? error : output;
        }
        catch (Exception ex)
        {
            return $"[Failed to get logs: {ex.Message}]";
        }
    }

    /// <summary>
    /// Logs the current container status.
    /// </summary>
    public static async Task LogContainerStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dockerComposePath = GetDockerComposePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ComposeCommand,
                Arguments = $"{ComposeArgs} -f \"{dockerComposePath}\" ps",
                WorkingDirectory = Path.GetDirectoryName(dockerComposePath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            Logger.LogInformation("Container status:\n{Status}", output);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Failed to get container status: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Stops the health monitor.
    /// </summary>
    public static void StopHealthMonitor()
    {
        healthMonitorCts?.Cancel();
        try
        {
            healthMonitorTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore cancellation exceptions
        }
        healthMonitorCts?.Dispose();
        healthMonitorCts = null;
        healthMonitorTask = null;
    }

    /// <summary>
    /// Gets the docker-compose file path.
    /// </summary>
    public static string GetDockerComposePath()
    {
        return Path.GetFullPath(Path.Combine(dockerComposeDir, "docker-compose.yml"));
    }

    private static void StartHealthMonitor()
    {
        healthMonitorCts = new CancellationTokenSource();
        healthMonitorTask = Task.Run(async () =>
        {
            var token = healthMonitorCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HealthCheckInterval, token);

                    var (healthy, error) = await CheckHealthWithTimeoutAsync(token);
                    isHealthy = healthy;

                    if (!healthy)
                    {
                        Logger.LogWarning("Background health check failed: {Error}", error);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error in health monitor");
                }
            }
        });
    }

    private static async Task RunComposeCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ComposeCommand,
            Arguments = $"{ComposeArgs} {arguments}",
            WorkingDirectory = dockerComposeDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {ComposeCommand} {ComposeArgs}");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{ComposeCommand} {ComposeArgs} failed with exit code {process.ExitCode}:\n{error}\n{output}");
        }
    }
}