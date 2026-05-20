// -----------------------------------------------------------------------
// <copyright file="CopilotServerManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using Forge.Api.Auth;
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
    private readonly IAuthService? ghAuth;
    private readonly SemaphoreSlim startLock = new(1, 1);

    private CopilotClient? serverClient;
    private Process? serverProcess;
    private int resolvedPort;
    private bool disposed;
    private bool stopping;

    /// <summary>
    /// PID of the SDK-spawned <c>copilot.exe</c> root, or 0 if no server
    /// is running. Surfaced as a static so a process-exit panic handler
    /// in <c>Program.cs</c> can synchronously kill the tree without DI
    /// resolution or async work. Updated atomically via
    /// <see cref="System.Threading.Interlocked.Exchange(ref int, int)"/>.
    /// </summary>
    public static int LastSpawnedPid;

    public CopilotServerManager(
        IOptions<AgentRunnerOptions> options,
        ILogger<CopilotServerManager> log,
        IEnumerable<IAuthService> authServices)
    {
        this.options = options;
        this.log = log;
        // 2026-05-20 — Phase 75b: SDK 1.0.0-beta.x requires CLI-level
        // authentication via env var (COPILOT_GITHUB_TOKEN). The 0.3.0
        // SDK accepted SessionConfig.GitHubToken at per-session
        // CreateSessionAsync time and bound the shared CLI in
        // unauthenticated mode; 1.0.x rejects every JSON-RPC call
        // (including VerifyProtocolVersionAsync) with
        // RemoteRpcException("AUTHENTICATION_FAILED") when the CLI was
        // launched without credentials. Resolve the GitHub auth service
        // (registered as one of multiple IAuthService impls) so the
        // shared CLI spawn at EnsureRunningAsync can pass through the
        // operator's signed-in token. Null when no GitHub auth provider
        // is registered (e.g. ADO-only deployments) — the spawn will
        // still proceed and fail with the same AUTHENTICATION_FAILED on
        // first session call, giving the operator a clear diagnostic.
        this.ghAuth = authServices.FirstOrDefault(a => a.Kind == "github");
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

        // 2026-05-20 — Phase 75b: In CliUrl mode the SDK 1.0.0-beta.x
        // explicitly rejects `GitHubToken` and `UseLoggedInUser` —
        // "external server manages its own auth" — so per-session
        // clients connect bare. The shared server was already
        // authenticated at EnsureRunningAsync time.
        return new CopilotClientOptions
        {
            CliUrl = $"localhost:{this.resolvedPort}",
        };
    }

    /// <summary>
    /// 2026-05-20 — Phase 75b: SDK 1.0.0-beta.x rejects per-session
    /// <see cref="CopilotClient"/> instances constructed against the
    /// shared server's <c>CliUrl</c> with <c>AUTHENTICATION_FAILED</c>
    /// even when the shared server itself is authenticated — auth state
    /// is bound to the originating <see cref="CopilotClient"/> instance,
    /// not the CLI process. This method lets callers create sessions on
    /// the manager's already-authenticated client, then dispose the
    /// returned session (but NOT the underlying client) when done.
    /// Returns null when the shared server isn't running and can't be
    /// started.
    /// </summary>
    public async Task<CopilotSession?> CreateSessionAsync(SessionConfig config, CancellationToken ct)
    {
        await this.EnsureRunningAsync(ct);
        if (this.serverClient is null) return null;
        return await this.serverClient.CreateSessionAsync(config, ct);
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (this.disposed) return Task.CompletedTask;
        return this.DisposeServerAsync(graceful: true);
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
            if (this.stopping || this.disposed)
            {
                throw new InvalidOperationException(
                    "CopilotServerManager is shutting down; refusing to start a new shared Copilot server.");
            }

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
                    // Server died — dispose and recreate. We're already
                    // inside startLock, so call the lock-free inner.
                    await this.DisposeServerInnerAsync();
                }
            }

            var cliPath = ResolveCopilotCli(this.options.Value.CopilotCliPath);
            this.log.LogInformation(
                "Starting shared Copilot server: cli={Cli}", cliPath);

            // Snapshot the live copilot.exe PID set BEFORE spawning so we
            // can identify the new SDK-internal root after StartAsync().
            // Without this, FindNewCopilotDescendant would mistake a
            // pre-existing operator-launched copilot.exe for ours.
            var snapshotBefore = WindowsProcessTree.Snapshot();
            var baselinePids = WindowsProcessTree.SnapshotCopilotPids(snapshotBefore);

            // 2026-05-20 — Phase 75b: SDK 1.0.0-beta.x requires the
            // copilot.exe CLI to be authenticated at spawn time. The 0.3.0
            // SDK accepted SessionConfig.GitHubToken at per-session
            // CreateSessionAsync time and tolerated an unauthenticated
            // shared CLI; 1.0.x rejects every JSON-RPC call (including
            // VerifyProtocolVersionAsync) with
            // RemoteRpcException("AUTHENTICATION_FAILED") when the CLI
            // wasn't authenticated at spawn.
            //
            // Strategy: feed the CLI auth two ways for robustness:
            //   1. CopilotClientOptions.GitHubToken — when we have a host
            //      gh token, pass it via the SDK's documented API. When
            //      provided, the SDK sets `UseLoggedInUser = false`
            //      automatically (per README §CopilotClient Options).
            //   2. Environment forwarding — also set
            //      COPILOT_GITHUB_TOKEN on the spawned process env (the
            //      CLI's own auth path per `copilot login --help`).
            //      Inherit the parent process's full environment first;
            //      a bare `Environment = { COPILOT_GITHUB_TOKEN = ... }`
            //      replaces the spawn's env entirely, which crashed the
            //      Node runtime ("Assertion failed: ncrypto::CSPRNG")
            //      because PATH/USERPROFILE/SYSTEMROOT/etc were lost.
            //
            // Fall-back when no host gh token is available: rely on
            // `UseLoggedInUser = true` (the SDK default) so the CLI uses
            // credentials from a prior interactive `copilot login` stored
            // in the OS credential store / ~/.copilot/. That suffices on
            // a workstation where the operator already signed in
            // (verified by `copilot -p "ping"` returning a real response).
            var spawnEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    spawnEnv[key] = value;
                }
            }
            string? ghToken = null;
            if (this.ghAuth is not null)
            {
                try { ghToken = await this.ghAuth.GetAccessTokenAsync(ct); }
                catch (Exception ex)
                {
                    this.log.LogWarning(ex,
                        "Failed to obtain GitHub token for shared Copilot CLI spawn; "
                        + "falling back to UseLoggedInUser=true (operator must have run `copilot login`).");
                }
            }
            if (!string.IsNullOrEmpty(ghToken))
            {
                spawnEnv["COPILOT_GITHUB_TOKEN"] = ghToken;
            }

            var serverOptions = new CopilotClientOptions
            {
                CliPath = cliPath,
                CliArgs = this.options.Value.CopilotCliExtraArgs,
                UseStdio = false,
                Port = 0,
                AutoStart = false,
                Environment = spawnEnv,
            };
            // Set client-level auth options ONLY when we have an explicit
            // token; otherwise the SDK default (UseLoggedInUser=true)
            // handles credential lookup. Setting GitHubToken=null
            // explicitly is harmless but UseLoggedInUser doesn't expose
            // its default reset on this property when nulling.
            if (!string.IsNullOrEmpty(ghToken))
            {
                serverOptions.GitHubToken = ghToken;
                this.log.LogInformation(
                    "Authenticating shared Copilot CLI via CopilotClientOptions.GitHubToken (length={Len}) "
                    + "+ COPILOT_GITHUB_TOKEN env var (belt-and-suspenders; parent-env inherited, {Count} vars).",
                    ghToken.Length, spawnEnv.Count);
            }
            else
            {
                this.log.LogInformation(
                    "Authenticating shared Copilot CLI via UseLoggedInUser=true (SDK default; no host gh token). "
                    + "Requires a prior `copilot login` in the OS credential store. Parent-env inherited ({Count} vars).",
                    spawnEnv.Count);
            }

            var client = new CopilotClient(serverOptions);
            await client.StartAsync();
            this.resolvedPort = client.ActualPort
                ?? throw new InvalidOperationException("Copilot server started but ActualPort is null");
            this.serverClient = client;

            // Take explicit ownership of the SDK-spawned copilot.exe root.
            // The SDK doesn't expose the underlying Process, so we
            // discover it via Toolhelp32 and store the handle for
            // deterministic Kill(entireProcessTree:true) on dispose.
            this.serverProcess = await this.AttachToSpawnedServerAsync(baselinePids, ct);
            if (this.serverProcess is not null)
            {
                Interlocked.Exchange(ref LastSpawnedPid, this.serverProcess.Id);
                ChildProcessTracker.AddProcess(this.serverProcess);
                this.log.LogInformation(
                    "Shared Copilot server started on port {Port} as PID {Pid} (job-tracked={JobActive}).",
                    this.resolvedPort, this.serverProcess.Id, ChildProcessTracker.IsActive);
            }
            else
            {
                Interlocked.Exchange(ref LastSpawnedPid, 0);
                this.log.LogWarning(
                    "Shared Copilot server started on port {Port} but no spawned copilot.exe descendant was found in 1s. "
                    + "Explicit ownership unavailable; relying on SDK ForceStopAsync + JobObject (active={JobActive}). "
                    + "Children may leak if Forge.Api dies abruptly.",
                    this.resolvedPort, ChildProcessTracker.IsActive);
            }
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

    /// <summary>
    /// Polls Toolhelp32 with backoff for up to ~1s after
    /// <c>client.StartAsync()</c> to find the SDK-spawned copilot.exe
    /// descendant. Returns the topmost newly-spawned copilot.exe PID in
    /// the descendant chain of Forge.Api, or <c>null</c> if discovery
    /// fails. Polling handles the race where StartAsync returns before
    /// the OS has fully published the child to the snapshot.
    /// </summary>
    private async Task<Process?> AttachToSpawnedServerAsync(
        IReadOnlySet<int> baselinePids,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;

        // Total budget ~1.2s spread across 6 attempts. The 0ms first attempt
        // covers the common case where the child process is already in the
        // snapshot by the time StartAsync returns.
        int[] backoffMs = { 0, 50, 100, 200, 350, 500 };
        foreach (var delay in backoffMs)
        {
            if (delay > 0)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return null; }
            }

            var pid = WindowsProcessTree.FindNewCopilotDescendant(
                rootPid: Environment.ProcessId,
                baselinePids: baselinePids);
            if (pid is null) continue;

            try
            {
                var proc = Process.GetProcessById(pid.Value);
                if (!proc.HasExited) return proc;
                proc.Dispose();
            }
            catch (ArgumentException)
            {
                // Process exited between snapshot and GetProcessById.
                // Loop and try the next backoff slot.
            }
            catch (Exception ex)
            {
                this.log.LogDebug(ex, "GetProcessById({Pid}) failed during owner attach", pid.Value);
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Dispose the running server. Takes <see cref="startLock"/> so a
    /// concurrent <see cref="EnsureRunningAsync"/> can't interleave a
    /// fresh spawn while we're tearing down. Sets
    /// <see cref="stopping"/> so subsequent EnsureRunningAsync calls
    /// fail fast instead of resurrecting the server during shutdown.
    /// </summary>
    private async Task DisposeServerAsync(bool graceful)
    {
        if (this.disposed) return;
        try
        {
            await this.startLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // Lifecycle race: another disposer reached startLock.Dispose()
            // before we got here. Nothing left to do — the server has
            // already been torn down by whoever disposed the semaphore.
            return;
        }
        try
        {
            if (graceful) this.stopping = true;
            await this.DisposeServerInnerAsync();
        }
        finally
        {
            try { this.startLock.Release(); }
            catch (ObjectDisposedException) { /* lifecycle race; safe to ignore */ }
        }
    }

    /// <summary>
    /// Lock-free dispose body. Caller MUST hold <see cref="startLock"/>.
    /// </summary>
    private async Task DisposeServerInnerAsync()
    {
        var client = this.serverClient;
        var proc = this.serverProcess;
        this.serverClient = null;
        this.serverProcess = null;
        this.resolvedPort = 0;
        Interlocked.Exchange(ref LastSpawnedPid, 0);

        if (client is not null)
        {
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

        // Belt-and-suspenders: even if the SDK's Force/Dispose path
        // gracefully shut the server, kill the OS process tree
        // explicitly so we don't depend on SDK internals or JobObject
        // timing. This is the user-directive "parent manages and
        // disposes when done" behaviour.
        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    var pid = proc.Id;
                    proc.Kill(entireProcessTree: true);
                    if (!proc.WaitForExit(5_000))
                    {
                        this.log.LogWarning(
                            "Shared Copilot server PID {Pid} did not exit within 5s of Kill(entireProcessTree:true)",
                            pid);
                    }
                    else
                    {
                        this.log.LogInformation(
                            "Shared Copilot server PID {Pid} terminated cleanly.", pid);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited — nothing to kill.
            }
            catch (Exception ex)
            {
                this.log.LogDebug(ex,
                    "Force-kill on shared Copilot server PID {Pid} threw", proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
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

            var sdkExe = Path.Combine(appData, "npm", "node_modules", "@github/copilot-sdk",
                "node_modules", "@github/copilot-win32-x64", "copilot.exe");
            if (File.Exists(sdkExe)) return sdkExe;

            var npmCmd = Path.Combine(appData, "npm", "copilot.cmd");
            if (File.Exists(npmCmd)) return npmCmd;

            var sdkCmd = Path.Combine(appData, "npm", "node_modules", "@github/copilot-sdk",
                "node_modules", ".bin", "copilot.cmd");
            if (File.Exists(sdkCmd)) return sdkCmd;
        }

        return "copilot";
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;
        await this.DisposeServerAsync(graceful: true);
        this.startLock.Dispose();
    }
}
