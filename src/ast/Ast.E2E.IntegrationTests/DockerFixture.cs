// -----------------------------------------------------------------------
// <copyright file="DockerFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.E2E.IntegrationTests;

using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Qdrant.Client;

/// <summary>
/// Fixture that ensures Neo4j and Qdrant are running in Docker before tests.
/// docker-compose.yml is copied to the output directory via the csproj link.
/// Uses 'docker compose' on Linux and 'podman compose' on Windows.
/// </summary>
public static class DockerFixture
{
    private static readonly string dockerComposeDir = AppContext.BaseDirectory;
    private static readonly object syncLock = new();
    private static bool initialized;
    private static CancellationTokenSource? healthMonitorCts;
    private static Task? healthMonitorTask;
    private static bool neo4jHealthy;
    private static bool qdrantHealthy;
    private static string? lastNeo4jError;
    private static string? lastQdrantError;

    /// <summary>
    /// Default timeout for health checks.
    /// </summary>
    public static TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval for background health monitoring.
    /// </summary>
    public static TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    // Lazy logger to ensure it's created after TestLoggerFactory.Initialize is called
    private static ILogger Logger => TestLoggerFactory.CreateLogger(nameof(DockerFixture));

    private static string ComposeCommand => OperatingSystem.IsWindows() ? "podman" : "docker";
    private static string ComposeArgs => "compose";

    #region Neo4j Configuration

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
    public static bool IsNeo4jHealthy => neo4jHealthy;

    /// <summary>
    /// Gets the last Neo4j health check error message.
    /// </summary>
    public static string? LastNeo4jError => lastNeo4jError;

    #endregion

    #region Qdrant Configuration

    /// <summary>
    /// Gets the Qdrant host.
    /// </summary>
    public static string QdrantHost => "localhost";

    /// <summary>
    /// Gets the Qdrant HTTP port.
    /// </summary>
    public static int QdrantHttpPort => 6333;

    /// <summary>
    /// Gets the Qdrant gRPC port.
    /// </summary>
    public static int QdrantGrpcPort => 6334;

    /// <summary>
    /// Gets whether Qdrant is currently healthy.
    /// </summary>
    public static bool IsQdrantHealthy => qdrantHealthy;

    /// <summary>
    /// Gets the last Qdrant health check error message.
    /// </summary>
    public static string? LastQdrantError => lastQdrantError;

    #endregion

    /// <summary>
    /// Gets whether all services are healthy.
    /// </summary>
    public static bool IsAllHealthy => neo4jHealthy && qdrantHealthy;

    /// <summary>
    /// Ensures both Neo4j and Qdrant are running and starts background health monitoring.
    /// </summary>
    public static async Task EnsureServicesRunningAsync(CancellationToken cancellationToken = default)
    {
        lock (syncLock)
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
        }

        Logger.LogInformation("Checking if services are running...");

        // Check if services are already running
        var neo4jTask = CheckNeo4jHealthAsync(cancellationToken);
        var qdrantTask = CheckQdrantHealthAsync(cancellationToken);

        await Task.WhenAll(neo4jTask, qdrantTask);

        var (neo4jOk, neo4jErr) = await neo4jTask;
        var (qdrantOk, qdrantErr) = await qdrantTask;

        if (neo4jOk && qdrantOk)
        {
            Logger.LogInformation("All services are already running and healthy");
            neo4jHealthy = true;
            qdrantHealthy = true;
            StartHealthMonitor();
            return;
        }

        Logger.LogInformation("Starting services via {Command} {Args}...", ComposeCommand, ComposeArgs);

        // Start services using docker/podman compose
        var dockerComposePath = GetDockerComposePath();

        if (!File.Exists(dockerComposePath))
        {
            throw new InvalidOperationException($"docker-compose.yml not found at: {dockerComposePath}");
        }

        await RunComposeCommandAsync($"-f \"{dockerComposePath}\" up -d", cancellationToken);

        Logger.LogInformation("Waiting for services to be healthy...");

        // Wait for services to be healthy with timeout
        var maxWaitTime = TimeSpan.FromMinutes(3);
        var startTime = DateTime.UtcNow;
        var attemptCount = 0;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptCount++;

            neo4jTask = CheckNeo4jHealthAsync(cancellationToken);
            qdrantTask = CheckQdrantHealthAsync(cancellationToken);

            await Task.WhenAll(neo4jTask, qdrantTask);

            (neo4jOk, neo4jErr) = await neo4jTask;
            (qdrantOk, qdrantErr) = await qdrantTask;

            if (neo4jOk && qdrantOk)
            {
                Logger.LogInformation("All services are now healthy after {Attempts} attempts", attemptCount);
                neo4jHealthy = true;
                qdrantHealthy = true;
                StartHealthMonitor();
                return;
            }

            if (!neo4jOk)
            {
                Logger.LogDebug("Neo4j health check attempt {Attempt} failed: {Error}", attemptCount, neo4jErr);
            }
            if (!qdrantOk)
            {
                Logger.LogDebug("Qdrant health check attempt {Attempt} failed: {Error}", attemptCount, qdrantErr);
            }

            // Check container logs for errors periodically
            if (attemptCount % 3 == 0)
            {
                await LogContainerStatusAsync(cancellationToken);
            }

            await Task.Delay(5000, cancellationToken);
        }

        // Final attempt to get error details
        await LogContainerStatusAsync(cancellationToken);
        var logs = await GetContainerLogsAsync("neo4j", 30, cancellationToken);

        throw new TimeoutException(
            $"Timed out waiting for services to become healthy after {attemptCount} attempts.\n" +
            $"Neo4j error: {lastNeo4jError}\n" +
            $"Qdrant error: {lastQdrantError}\n" +
            $"Container logs:\n{logs}");
    }

    /// <summary>
    /// Ensures only Neo4j is running.
    /// </summary>
    public static async Task EnsureNeo4jRunningAsync(CancellationToken cancellationToken = default)
    {
        var (healthy, _) = await CheckNeo4jHealthAsync(cancellationToken);
        if (healthy)
        {
            neo4jHealthy = true;
            return;
        }

        var dockerComposePath = GetDockerComposePath();
        await RunComposeCommandAsync($"-f \"{dockerComposePath}\" up -d neo4j", cancellationToken);

        var maxWaitTime = TimeSpan.FromMinutes(2);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (isHealthyNow, _) = await CheckNeo4jHealthAsync(cancellationToken);
            if (isHealthyNow)
            {
                neo4jHealthy = true;
                return;
            }

            await Task.Delay(5000, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for Neo4j to become healthy");
    }

    /// <summary>
    /// Ensures only Qdrant is running.
    /// </summary>
    public static async Task EnsureQdrantRunningAsync(CancellationToken cancellationToken = default)
    {
        var (healthy, _) = await CheckQdrantHealthAsync(cancellationToken);
        if (healthy)
        {
            qdrantHealthy = true;
            return;
        }

        var dockerComposePath = GetDockerComposePath();
        await RunComposeCommandAsync($"-f \"{dockerComposePath}\" up -d qdrant", cancellationToken);

        var maxWaitTime = TimeSpan.FromMinutes(2);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (isHealthyNow, _) = await CheckQdrantHealthAsync(cancellationToken);
            if (isHealthyNow)
            {
                qdrantHealthy = true;
                return;
            }

            await Task.Delay(5000, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for Qdrant to become healthy");
    }

    /// <summary>
    /// Checks Neo4j health with a timeout.
    /// </summary>
    public static async Task<(bool isHealthy, string? error)> CheckNeo4jHealthAsync(
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

            await using var session = driver.AsyncSession();
            var result = await session.RunAsync("RETURN 1 as n");
            await result.ConsumeAsync();

            lastNeo4jError = null;
            return (true, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var error = $"Health check timed out after {HealthCheckTimeout.TotalSeconds}s";
            lastNeo4jError = error;
            return (false, error);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            lastNeo4jError = error;
            return (false, error);
        }
    }

    /// <summary>
    /// Checks Qdrant health with a timeout.
    /// </summary>
    public static async Task<(bool isHealthy, string? error)> CheckQdrantHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(HealthCheckTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            // Try HTTP health check first
            using var httpClient = new HttpClient { Timeout = HealthCheckTimeout };
            var response = await httpClient.GetAsync($"http://{QdrantHost}:{QdrantHttpPort}/healthz", linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                lastQdrantError = null;
                return (true, null);
            }

            var error = $"HTTP health check returned {response.StatusCode}";
            lastQdrantError = error;
            return (false, error);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var error = $"Health check timed out after {HealthCheckTimeout.TotalSeconds}s";
            lastQdrantError = error;
            return (false, error);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            lastQdrantError = error;
            return (false, error);
        }
    }

    /// <summary>
    /// Asserts that all services are healthy, throwing if not.
    /// </summary>
    public static async Task AssertAllHealthyAsync(CancellationToken cancellationToken = default)
    {
        var neo4jTask = CheckNeo4jHealthAsync(cancellationToken);
        var qdrantTask = CheckQdrantHealthAsync(cancellationToken);

        await Task.WhenAll(neo4jTask, qdrantTask);

        var (neo4jOk, neo4jErr) = await neo4jTask;
        var (qdrantOk, qdrantErr) = await qdrantTask;

        if (!neo4jOk || !qdrantOk)
        {
            var errors = new List<string>();
            if (!neo4jOk) errors.Add($"Neo4j: {neo4jErr}");
            if (!qdrantOk) errors.Add($"Qdrant: {qdrantErr}");

            throw new InvalidOperationException($"Services are not healthy:\n{string.Join("\n", errors)}");
        }
    }

    /// <summary>
    /// Executes an operation with health check, failing fast if services are unhealthy.
    /// </summary>
    public static async Task<T> ExecuteWithHealthCheckAsync<T>(
        Func<Task<T>> operation,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await AssertAllHealthyAsync(cancellationToken);

        var operationTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutCts = new CancellationTokenSource(operationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var (neo4jOk, neo4jErr) = await CheckNeo4jHealthAsync(CancellationToken.None);
            var (qdrantOk, qdrantErr) = await CheckQdrantHealthAsync(CancellationToken.None);

            if (!neo4jOk || !qdrantOk)
            {
                throw new InvalidOperationException(
                    $"Operation timed out and services are unhealthy:\n" +
                    $"Neo4j: {(neo4jOk ? "OK" : neo4jErr)}\n" +
                    $"Qdrant: {(qdrantOk ? "OK" : qdrantErr)}");
            }
            throw new TimeoutException($"Operation timed out after {operationTimeout.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Gets recent container logs for a specific service.
    /// </summary>
    public static async Task<string> GetContainerLogsAsync(
        string serviceName,
        int tailLines = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dockerComposePath = GetDockerComposePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ComposeCommand,
                Arguments = $"{ComposeArgs} -f \"{dockerComposePath}\" logs --tail {tailLines} {serviceName}",
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

                    var neo4jTask = CheckNeo4jHealthAsync(token);
                    var qdrantTask = CheckQdrantHealthAsync(token);

                    await Task.WhenAll(neo4jTask, qdrantTask);

                    var (neo4jOk, neo4jErr) = await neo4jTask;
                    var (qdrantOk, qdrantErr) = await qdrantTask;

                    neo4jHealthy = neo4jOk;
                    qdrantHealthy = qdrantOk;

                    if (!neo4jOk)
                    {
                        Logger.LogWarning("Background Neo4j health check failed: {Error}", neo4jErr);
                    }
                    if (!qdrantOk)
                    {
                        Logger.LogWarning("Background Qdrant health check failed: {Error}", qdrantErr);
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