// -----------------------------------------------------------------------
// <copyright file="CopilotServerManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using System.Text.Json;
using Forge.Api.Auth;
using GitHub.Copilot;
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
    private readonly AgentProviderSettingsStore? agentProviders;
    private readonly SemaphoreSlim startLock = new(1, 1);

    /// <summary>
    /// File under the Forge state folder recording the PID of the shared
    /// copilot.exe server this process spawned. Read once at startup to reap
    /// a server orphaned by a PRIOR Forge.Api that died without running its
    /// graceful teardown (hard <c>taskkill</c> from the launcher, a crash, an
    /// IDE stop where the JobObject self-assign was denied). An orphaned
    /// server keeps each worktree's <c>.forge/memory/session-store.db</c> open,
    /// which then breaks the next run's worktree reset. See <see cref="ReapStaleServerFromPriorRun"/>.
    /// </summary>
    private readonly string pidFilePath;

    /// <summary>
    /// 2026-05-20 — disposal-cascade hysteresis. After a fast-path ping
    /// fails on the shared <see cref="serverClient"/>, the lock-acquired
    /// re-ping waits this long before its single retry. Anything below
    /// ~100ms doesn't actually let a transient blip recover (it's the
    /// same syscall arena); anything above ~1s delays legitimate restarts
    /// noticeably under load. 250ms empirically rides the line — long
    /// enough to absorb the JSON-RPC pressure / GC pause that triggered
    /// the false-positive, short enough that a real server death only
    /// adds a quarter-second to the restart-and-recover sequence.
    /// </summary>
    private const int PingRetryDelayMs = 250;

    private CopilotClient? serverClient;
    private string? connectionToken;
    private string? serverAccount;
    private string? lastStartError;
    private DateTimeOffset? lastStartErrorAt;
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
        IEnumerable<IAuthService> authServices,
        // Optional so the existing unit-test constructions keep working; in
        // the DI container it is always resolved to the real singleton.
        Forge.Api.Persistence.IForgeDbPaths? dbPaths = null,
        // Optional for the same reason; null = always use the GitHub
        // provider's account (pre-agent-provider-settings behaviour).
        AgentProviderSettingsStore? agentProviders = null)
    {
        this.options = options;
        this.log = log;
        this.agentProviders = agentProviders;
        this.pidFilePath = Path.Combine(dbPaths?.Folder ?? Path.GetTempPath(), "forge-copilot-server.pid");
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

        // Per-session clients attach to the shared runtime. It was
        // authenticated at spawn (EnsureRunningAsync), so they carry no
        // GitHub token -- only the connection token the runtime was started
        // with (SDK 1.0.x authenticates every TCP client with it).
        return new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForUri($"localhost:{this.resolvedPort}", this.connectionToken),
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
    ///
    /// <para>2026-05-20 — Phase 75c: snapshot
    /// <see cref="serverClient"/> BEFORE awaiting CreateSessionAsync so
    /// a concurrent <see cref="DisposeServerInnerAsync"/> (triggered by
    /// the next caller's ping-fail → restart path) can't dispose the
    /// instance we're about to call on. The caller still sees
    /// <see cref="System.ObjectDisposedException"/> when the snapshot
    /// itself races a dispose — that surfaces via
    /// <c>AgentRunnerDispatcher.IsProcessLaunchError</c> and routes
    /// through the Phase 75 transient-transport recovery path.</para>
    /// </summary>
    public async Task<CopilotSession?> CreateSessionAsync(SessionConfig config, CancellationToken ct)
    {
        await this.EnsureRunningAsync(ct);
        var snapshot = this.serverClient;
        if (snapshot is null) return null;
        return await snapshot.CreateSessionAsync(config, ct);
    }

    /// <summary>
    /// 2026-05-30 — lists the models the authenticated Copilot session can
    /// use, with their capabilities (context window, reasoning support, etc.).
    /// Uses the shared, already-authenticated client so it works without the
    /// editor `ghu_` token the raw <c>copilot_internal/v2/token</c> exchange
    /// requires. Returns the SDK's model objects boxed as <see cref="object"/>
    /// so the admin endpoint can serialize them verbatim. Null when the
    /// server isn't running.
    /// </summary>
    public async Task<IReadOnlyList<System.Text.Json.JsonElement>?> ListModelsAsync(CancellationToken ct)
    {
        await this.EnsureRunningAsync(ct);
        var snapshot = this.serverClient;
        if (snapshot is null) return null;
        var models = await snapshot.ListModelsAsync(ct);
        if (models is null) return null;
        // Serialize by RUNTIME type — boxing the SDK model objects as
        // `object` would make System.Text.Json emit empty {} (it serializes
        // by declared type). SerializeToElement(m, m.GetType()) captures the
        // model's real capability fields verbatim.
        var list = new List<System.Text.Json.JsonElement>();
        foreach (var m in models)
        {
            if (m is null) continue;
            list.Add(System.Text.Json.JsonSerializer.SerializeToElement(m, m.GetType()));
        }
        return list;
    }

    /// <summary>
    /// Resumes a provider-persisted session on the shared, authenticated
    /// <see cref="CopilotClient"/>. Mirrors <see cref="CreateSessionAsync"/>
    /// so callers do not create unauthenticated clients against the shared
    /// server's URL.
    /// </summary>
    public async Task<CopilotSession?> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken ct)
    {
        await this.EnsureRunningAsync(ct);
        var snapshot = this.serverClient;
        if (snapshot is null) return null;
        return await snapshot.ResumeSessionAsync(sessionId, config, ct);
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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Reap a shared copilot.exe server orphaned by a PRIOR Forge.Api that
        // didn't run its graceful teardown. Never throws — boot must proceed
        // even if reaping fails.
        this.ReapStaleServerFromPriorRun();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads <see cref="pidFilePath"/> and, if it names a still-running
    /// <c>copilot</c>-process from a prior Forge.Api run, kills its entire
    /// tree, then deletes the file. PID-reuse safe: the recorded PID is only
    /// killed when the live process's name still matches a Copilot binary, so
    /// a recycled PID now owned by an unrelated process is left alone.
    /// </summary>
    private void ReapStaleServerFromPriorRun()
    {
        try
        {
            if (!File.Exists(this.pidFilePath)) return;
            var raw = File.ReadAllText(this.pidFilePath).Trim();
            File.Delete(this.pidFilePath);
            if (!int.TryParse(raw, out var pid) || pid <= 0) return;
            if (pid == Environment.ProcessId) return;

            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; } // already gone

            using (proc)
            {
                // PID-reuse guard: only reap if this is actually a Copilot
                // process (the recorded PID could have been recycled).
                string name;
                try { name = proc.ProcessName; }
                catch { return; }
                if (name.IndexOf("copilot", StringComparison.OrdinalIgnoreCase) < 0) return;
                if (proc.HasExited) return;

                proc.Kill(entireProcessTree: true);
                this.log.LogWarning(
                    "Reaped orphaned shared Copilot server PID {Pid} ('{Name}') left by a prior Forge.Api run "
                    + "(it would have pinned worktree session-store.db files).",
                    pid, name);
            }
        }
        catch (Exception ex)
        {
            this.log.LogDebug(ex, "ReapStaleServerFromPriorRun failed (non-fatal).");
        }
    }

    /// <summary>Persists the spawned shared-server PID so a future run can
    /// reap it if this process dies without graceful teardown.</summary>
    private void RecordServerPid(int pid)
    {
        try
        {
            var dir = Path.GetDirectoryName(this.pidFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(this.pidFilePath, pid.ToString());
        }
        catch (Exception ex)
        {
            this.log.LogDebug(ex, "Failed to record Copilot server PID to {Path}", this.pidFilePath);
        }
    }

    /// <summary>Clears the recorded PID after we've torn the server down
    /// ourselves, so the next run's reaper has nothing stale to act on.</summary>
    private void ClearRecordedPid()
    {
        try { if (File.Exists(this.pidFilePath)) File.Delete(this.pidFilePath); }
        catch (Exception ex) { this.log.LogDebug(ex, "Failed to clear Copilot server PID file {Path}", this.pidFilePath); }
    }

    /// <summary>
    /// GitHub token for the account Copilot agents run as: the Settings →
    /// Agent providers choice, else the GitHub provider's account. Every
    /// Copilot session and CLI run must use this, not the GitHub provider's
    /// token directly: the per-session token decides whose Copilot quota a
    /// request is billed to, so a session carrying the provider's token
    /// bypasses the chosen account even though the shared server runs as it.
    /// </summary>
    public async Task<string?> GetAgentGitHubTokenAsync(CancellationToken ct)
    {
        var chosen = this.agentProviders?.CopilotAccount;
        if (chosen is not null)
        {
            return this.ghAuth is GitHubAuthService gh ? await gh.GetAccessTokenForAccountAsync(chosen, ct) : null;
        }
        return this.ghAuth is null ? null : await this.ghAuth.GetAccessTokenAsync(ct);
    }

    /// <summary>Running state for the agent-provider settings page.</summary>
    public CopilotServerState State => new(
        this.serverClient is not null, this.serverAccount, this.lastStartError, this.lastStartErrorAt);

    /// <summary>
    /// Tear down the shared server so the next agent turn starts a fresh one
    /// with the currently selected account. Unlike <see cref="StopAsync"/> it
    /// does not mark the manager as stopping. In-flight Copilot turns lose
    /// their connection.
    /// </summary>
    public async Task RestartForAccountChangeAsync()
    {
        if (this.disposed || this.stopping) return;
        await this.startLock.WaitAsync();
        try
        {
            await this.DisposeServerInnerAsync();
            this.lastStartError = null;
            this.lastStartErrorAt = null;
        }
        finally
        {
            this.startLock.Release();
        }
    }

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException)
            {
                // 2026-05-20 — Copilot SDK 1.0.0-beta.x PingAsync protocol
                // mismatch: the server returns the ping response's
                // `timestamp` field as a JSON STRING but the SDK's
                // deserializer demands System.Int64, so EVERY successful
                // ping raises `JsonException("The JSON value could not be
                // converted to System.Int64. Path: $.timestamp …")`. The
                // round-trip completed — the SDK got a response back
                // through the JSON-RPC transport — which means the server
                // is alive and the connection is healthy. Treat parse
                // failures as proof-of-life and skip the restart.
                //
                // Without this whitelist, every EnsureRunningAsync caller
                // would unconditionally restart the (healthy) shared
                // server; concurrent CopilotSession instances bound to
                // the just-killed CopilotClient then hit
                // ObjectDisposedException on their next SendAsync, burning
                // one iter per workstream as a clustered "0 score" event.
                // Observed 2026-05-20 on code-intelligence:CLEAN-CODE
                // architecture.md iters 2/4/6/8/10 (every even iter
                // failed; every odd iter succeeded because the prior even
                // iter's restart left a freshly-started server).
                return;
            }
            catch (Exception pingEx)
            {
                // 2026-05-20 — don't decide to restart here. A single ping
                // blip (transient JSON-RPC pressure, brief pipe stall, GC
                // pause on the SDK side) is not proof the server is dead.
                // Fall through to the lock-acquired branch which now
                // delay+retries before committing to DisposeServerInnerAsync;
                // that centralizes the restart decision under startLock so
                // concurrent callers can't independently tear down a healthy
                // server. Log the first-strike type for diagnosis.
                this.log.LogInformation(
                    "Shared Copilot server ping failed once ({Ex}); will verify under lock.",
                    pingEx.GetType().Name);
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (JsonException)
                {
                    // 2026-05-20 — same SDK 1.0.0-beta.x PingAsync schema
                    // mismatch as the fast path. The server replied; the
                    // SDK just can't parse `timestamp`. Server is alive.
                    return;
                }
                catch (Exception firstStrikeEx)
                {
                    // 2026-05-20 — disposal-cascade hysteresis. Pre-fix this
                    // catch went straight to DisposeServerInnerAsync, which
                    // killed the shared serverClient on a single back-to-back
                    // ping pair (fast-path + this one, run essentially without
                    // any time gap when the lock isn't contended). Every
                    // in-flight CopilotSession across every concurrent
                    // workstream — all bound to that disposed client — then
                    // hit ObjectDisposedException on their next SendAsync,
                    // burning one iter per workstream as a "0 score"
                    // generator-inactive (transient → iterate) verdict
                    // clustered around the same restart event.
                    //
                    // Fix: delay briefly and re-ping ONCE before declaring
                    // the server dead. A real transient blip recovers inside
                    // ~250ms; a genuinely dead server stays dead and the
                    // second strike fires the dispose path with bounded
                    // extra latency. We're inside startLock so concurrent
                    // callers can't make independent restart decisions
                    // during the delay window.
                    try
                    {
                        await Task.Delay(PingRetryDelayMs, ct);
                        var snapshot = this.serverClient;
                        if (snapshot is not null)
                        {
                            await snapshot.PingAsync("health", ct);
                            this.log.LogInformation(
                                "Shared Copilot server ping recovered on retry after {Delay}ms; "
                                + "suppressing restart (first strike was {Ex}).",
                                PingRetryDelayMs, firstStrikeEx.GetType().Name);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (JsonException)
                    {
                        // 2026-05-20 — SDK 1.0.0-beta.x ping-response
                        // schema mismatch; round-trip succeeded so server
                        // is alive (see fast-path comment above). The
                        // first strike was something else (the catch
                        // above caught a non-Json exception then we
                        // landed here with a clean Json parse-failure),
                        // which means the underlying transport recovered
                        // within PingRetryDelayMs. Suppress restart.
                        this.log.LogInformation(
                            "Shared Copilot server ping recovered on retry after {Delay}ms "
                            + "(retry hit known SDK timestamp-schema mismatch; server alive); "
                            + "suppressing restart (first strike was {Ex}).",
                            PingRetryDelayMs, firstStrikeEx.GetType().Name);
                        return;
                    }
                    catch (Exception retryStrikeEx)
                    {
                        this.log.LogWarning(
                            retryStrikeEx,
                            "Shared Copilot server ping failed twice ({Delay}ms apart) — restarting. "
                            + "First strike was {FirstEx}.",
                            PingRetryDelayMs, firstStrikeEx.GetType().Name);
                        await this.DisposeServerInnerAsync();
                    }
                }
            }

            var cliPath = ResolveCopilotCli(this.options.Value.CopilotCliPath, AppContext.BaseDirectory);
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
            // Build the Copilot CLI's environment from the PRISTINE baseline
            // (captured in Program.cs before MSBuildLocator ran), NOT from
            // Forge.Api's live (polluted) process env. Without this the agent's
            // `dotnet restore`/`build` inherited MSBUILD_EXE_PATH /
            // MSBuildSDKsPath and died with MSB4276 (see ChildProcessEnv). The
            // baseline keeps PATH/USERPROFILE/SYSTEMROOT/etc. so the Node-based
            // CLI still starts (a full env-clear crashed it — ncrypto::CSPRNG).
            var spawnEnv = ChildProcessEnv.ToDictionary();
            string? ghToken = null;
            var chosenAccount = this.agentProviders?.CopilotAccount;
            if (chosenAccount is not null)
            {
                // Agent-provider setting: run Copilot as this account,
                // without switching gh's active account (which the GitHub
                // repository provider and git keep using).
                ghToken = this.ghAuth is GitHubAuthService gh
                    ? await gh.GetAccessTokenForAccountAsync(chosenAccount, ct)
                    : null;
                if (ghToken is null)
                {
                    throw new InvalidOperationException(
                        $"Copilot is set to GitHub account '{chosenAccount}', but gh has no token for it. "
                        + "Sign it in with `gh auth login`, or pick another account in Settings → Agent providers.");
                }
                this.serverAccount = chosenAccount;
            }
            else if (this.ghAuth is not null)
            {
                this.serverAccount = null;
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

            // SDK 1.0.x: the runtime is spawned through a connection object.
            // TCP on a free port so per-session clients can attach; the
            // connection token is ours so GetClientOptionsAsync can hand it
            // to them. Environment goes on the connection (setting it on the
            // options as well throws).
            this.connectionToken = Guid.NewGuid().ToString("N");
            var connection = RuntimeConnection.ForTcp(0, this.connectionToken, cliPath, this.options.Value.CopilotCliExtraArgs);
            connection.Environment = spawnEnv;
            var serverOptions = new CopilotClientOptions
            {
                Connection = connection,
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
            this.resolvedPort = client.RuntimePort
                ?? throw new InvalidOperationException("Copilot server started but RuntimePort is null");
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
                this.RecordServerPid(this.serverProcess.Id);
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
            this.lastStartError = ex.Message;
            this.lastStartErrorAt = DateTimeOffset.UtcNow;
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
        // We're tearing the server down ourselves — drop the recorded PID so
        // the next run's startup reaper has nothing stale to act on.
        this.ClearRecordedPid();

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

    /// <summary>
    /// Which Copilot runtime to spawn: an explicit, existing
    /// <c>Forge:Agents:CopilotCliPath</c>; else the runtime the SDK package
    /// bundles into <c>runtimes\win-x64\native\</c> at build time; else the
    /// legacy npm / PATH lookups. The bundled copy is pinned to the SDK's own
    /// protocol version. A system <c>copilot.exe</c> updates itself (1.0.115
    /// moved to protocol 4 while SDK 1.0.34 speaks 3), which is what broke
    /// every Copilot agent on 2026-11-19.
    /// </summary>
    internal static string ResolveCopilotCli(string? overridePath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var bundled = Path.Combine(baseDirectory, "runtimes", "win-x64", "native", "copilot.exe");
        if (File.Exists(bundled)) return bundled;

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

/// <summary>Shared Copilot server state for Settings → Agent providers. <c>Account</c> is null when it runs as the GitHub provider's account.</summary>
public sealed record CopilotServerState(bool Running, string? Account, string? LastStartError, DateTimeOffset? LastStartErrorAt);