// -----------------------------------------------------------------------
// <copyright file="Neo4jDockerFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.IntegrationTests;

using System.Diagnostics;

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
    /// Ensures Neo4j is running.
    /// </summary>
    public static async Task EnsureNeo4jRunningAsync()
    {
        lock (syncLock)
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
        }

        Console.WriteLine("Checking if Neo4j is running...");

        // Check if Neo4j is already running
        if (await IsNeo4jHealthyAsync())
        {
            Console.WriteLine("Neo4j is already running and healthy.");
            return;
        }

        Console.WriteLine($"Starting Neo4j via {ComposeCommand} {ComposeArgs}...");

        // Start Neo4j using docker/podman compose
        var dockerComposePath = Path.GetFullPath(Path.Combine(dockerComposeDir, "docker-compose.yml"));

        if (!File.Exists(dockerComposePath))
        {
            throw new InvalidOperationException($"docker-compose.yml not found at: {dockerComposePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ComposeCommand,
            Arguments = $"{ComposeArgs} -f \"{dockerComposePath}\" up -d neo4j",
            WorkingDirectory = Path.GetDirectoryName(dockerComposePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start {ComposeCommand} {ComposeArgs}");
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{ComposeCommand} {ComposeArgs} failed with exit code {process.ExitCode}:\n{error}\n{output}");
        }

        Console.WriteLine("Waiting for Neo4j to be healthy...");

        // Wait for Neo4j to be healthy
        var maxWaitTime = TimeSpan.FromMinutes(2);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            if (await IsNeo4jHealthyAsync())
            {
                Console.WriteLine("Neo4j is now healthy and ready.");
                return;
            }

            Console.WriteLine("  Waiting for Neo4j...");
            await Task.Delay(5000);
        }

        throw new TimeoutException("Timed out waiting for Neo4j to become healthy.");
    }

    /// <summary>
    /// Checks if Neo4j is healthy by attempting a connection.
    /// </summary>
    public static async Task<bool> IsNeo4jHealthyAsync()
    {
        try
        {
            using var driver = Neo4j.Driver.GraphDatabase.Driver(
                Neo4jUri,
                Neo4j.Driver.AuthTokens.Basic(Neo4jUsername, Neo4jPassword));

            await driver.VerifyConnectivityAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the docker-compose file path.
    /// </summary>
    public static string GetDockerComposePath()
    {
        return Path.GetFullPath(Path.Combine(dockerComposeDir, "docker-compose.yml"));
    }
}
