// -----------------------------------------------------------------------
// <copyright file="CopilotServerManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using GitHub.Copilot.SDK;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Manages a single shared copilot.exe server process that all agent
/// invocations connect to via <see cref="CopilotClientOptions.CliUrl"/>.
/// This eliminates the per-invocation process spawn pattern that caused
/// "An error occurred trying to start process" failures when multiple
/// agents dispatched concurrently.
///
/// <para>
/// The server runs in TCP mode (<c>UseStdio=false</c>) on an OS-assigned
/// port. Each agent invocation creates its own <see cref="CopilotSession"/>
/// on the shared server — sessions are logically isolated. If the server
/// dies, <see cref="EnsureRunningAsync"/> detects this via
/// <see cref="CopilotClient.PingAsync"/> and restarts it.
/// </para>
///
/// <para>
/// Registered as a singleton + <see cref="IHostedService"/>. Startup is
/// lazy — the hosted service <see cref="StartAsync"/> is a no-op so that
/// Forge.Api boots even if copilot.exe is unavailable. The server is
/// started on first use via <see cref="GetClientOptionsAsync"/>.
/// </para>
/// </summary>
public sealed class CopilotServerManager : IHostedService, IAsyncDisposable
{
    private readonly IOptions<AgentRunnerOptions> options;
    private readonly ILogger<CopilotServerManager> log;
    private readonly SemaphoreSlim startLock = new(1, 1);

    private CopilotClient? serverClient;
    private int resolvedPort;
    private bool disposed;

    public CopilotServerManager(
        IOptions<AgentRunnerOptions> options,
        ILogger<CopilotServerManager> log)
    {
        this.options = options;
        this.log = log;
    }

    /// <summary>
    /// Returns <see cref="CopilotClientOptions"/> configured with
    /// <see cref="CopilotClientOptions.CliUrl"/> pointing to the shared
    /// server. Callers create their own <see cref="CopilotClient"/> with
    /// these options — the client connects without spawning a process.
    /// </summary>
    public async Task<CopilotClientOptions> GetClientOptionsAsync(CancellationToken ct)
    {
        await this.EnsureRunningAsync(ct);
        return new CopilotClientOptions
        {
            CliUrl = $"localhost:{this.resolvedPort}",
        };
    }

    /// <summary>
    /// Whether the shared server is currently running and healthy.
    /// </summary>
    public bool IsRunning => this.serverClient is not null;

    // ── IHostedService ────────────────────────────────────────────────────

    /// <summary>
    /// No-op — startup is lazy so Forge.Api boots even if copilot.exe is
    /// unavailable. The server starts on first <see cref="GetClientOptionsAsync"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.DisposeServerAsync();
    }

    // ── lifecycle ─────────────────────────────────────────────────────────

    private async Task EnsureRunningAsync(CancellationToken ct)
    {
        // Fast path — server is up and healthy.
        if (this.serverClient is not null)
        {
            try
            {
                await this.serverClient.PingAsync("health", ct);
                return;
            }
            catch
            {
                this.log.LogWarning("Shared Copilot server ping failed, restarting");
            }
        }

        await this.startLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock.
            if (this.serverClient is not null)
            {
                try
                {
                    await this.serverClient.PingAsync("health", ct);
                    return;
                }
                catch
                {
                    // Server died — dispose and recreate.
                    await this.DisposeServerAsync();
                }
            }

            var cliPath = ResolveCopilotCli(this.options.Value.CopilotCliPath);
            this.log.LogInformation(
                "Starting shared Copilot server: cli={Cli}", cliPath);

            var serverOptions = new CopilotClientOptions
            {
                CliPath = cliPath,
                CliArgs = this.options.Value.CopilotCliExtraArgs,
                UseStdio = false,
                Port = 0,
                AutoStart = false,
            };

            var client = new CopilotClient(serverOptions);
            await client.StartAsync();
            this.resolvedPort = client.ActualPort
                ?? throw new InvalidOperationException("Copilot server started but ActualPort is null");
            this.serverClient = client;

            this.log.LogInformation(
                "Shared Copilot server started on port {Port}", this.resolvedPort);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, "Failed to start shared Copilot server");
            throw;
        }
        finally
        {
            this.startLock.Release();
        }
    }

    private async Task DisposeServerAsync()
    {
        var client = this.serverClient;
        this.serverClient = null;
        this.resolvedPort = 0;
        if (client is null) return;

        try
        {
            await client.ForceStopAsync();
        }
        catch (Exception ex)
        {
            this.log.LogDebug(ex, "ForceStopAsync on shared Copilot server errored");
        }

        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            this.log.LogDebug(ex, "DisposeAsync on shared Copilot server errored");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string ResolveCopilotCli(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var npmExe = Path.Combine(appData, "npm", "node_modules", "@github/copilot",
                "node_modules", "@github/copilot-win32-x64", "copilot.exe");
            if (File.Exists(npmExe)) return npmExe;
            var npmCmd = Path.Combine(appData, "npm", "copilot.cmd");
            if (File.Exists(npmCmd)) return npmCmd;
        }

        return "copilot";
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;
        await this.DisposeServerAsync();
        this.startLock.Dispose();
    }
}
