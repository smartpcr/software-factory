// -----------------------------------------------------------------------
// <copyright file="BuildValidationWorker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 29 — background worker that processes build-validation jobs
/// (init → restore → build → test) asynchronously. One job at a time
/// per repo to prevent filesystem contention. Supports per-run cancellation.
/// </summary>
public sealed class BuildValidationWorker : BackgroundService
{
    private readonly Channel<BuildValidationJob> channel;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<BuildValidationWorker> logger;
    private readonly ConcurrentDictionary<string, string> activeRepos = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> runCts = new();

    public BuildValidationWorker(
        Channel<BuildValidationJob> channel,
        IServiceScopeFactory scopeFactory,
        ILogger<BuildValidationWorker> logger)
    {
        this.channel = channel;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    /// <summary>
    /// Enqueue a validation job. Returns false if a validation is
    /// already running for the same repo.
    /// </summary>
    public bool TryEnqueue(BuildValidationJob job)
    {
        if (!this.activeRepos.TryAdd(job.RepoId, job.RunId))
            return false;

        if (!this.channel.Writer.TryWrite(job))
        {
            this.activeRepos.TryRemove(job.RepoId, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when this worker instance is actively tracking a run for the
    /// given repo (queued in the in-memory channel or currently executing).
    /// Lets the validate endpoint distinguish a genuinely in-flight run from
    /// an ORPHANED DB row — one left at <c>queued</c>/<c>running</c> by a
    /// server restart that cleared the in-memory channel. Without this, an
    /// orphaned row makes every future <c>validate-build</c> return a false
    /// "already running" 409 forever (and the worker never picks the orphan
    /// up, so it shows "Preparing pipeline…" with no log indefinitely).
    /// </summary>
    public bool IsRepoActive(string repoId) => this.activeRepos.ContainsKey(repoId);

    /// <summary>Cancel a running or queued validation run.</summary>
    public bool TryCancel(string runId)
    {
        if (this.runCts.TryGetValue(runId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in this.channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await this.ProcessJobAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Build validation run {RunId} for repo {RepoId} failed unexpectedly", job.RunId, job.RepoId);
            }
            finally
            {
                this.activeRepos.TryRemove(job.RepoId, out _);
                if (this.runCts.TryRemove(job.RunId, out var cts))
                    cts.Dispose();
            }
        }
    }

    private async Task ProcessJobAsync(BuildValidationJob job, CancellationToken hostCt)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        this.runCts[job.RunId] = jobCts;
        var ct = jobCts.Token;

        await using var scope = this.scopeFactory.CreateAsyncScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ForgeHub>>();

        // Route to the JOB's product DB. A background worker has no HTTP
        // request, so the request-scoped ProductDbContext (CreateForCurrentRequest)
        // can't resolve the per-product {slug}.db — it throws / falls back to the
        // control DB, so the run row (which lives in the product DB after the
        // Phase 80/F split) is invisible here and the job is silently dropped,
        // leaving it stuck "queued" / "Preparing pipeline…" forever with no log.
        // Resolve the repo's product slug from the control registry, then open
        // that product's DB explicitly.
        var factory = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();
        var reg = await control.RepoRegistry.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == job.RepoId && r.DeletedAt == null, CancellationToken.None);
        if (reg is null || string.IsNullOrEmpty(reg.ProductSlug))
        {
            this.logger.LogError(
                "Build validation run {RunId}: repo {RepoId} has no control-registry entry; cannot resolve its product DB.",
                job.RunId, job.RepoId);
            return;
        }
        var db = factory.CreateFor(reg.ProductSlug);

        var run = await db.BuildValidationRuns.FirstOrDefaultAsync(r => r.Id == job.RunId, CancellationToken.None);
        if (run is null) return;

        if (ct.IsCancellationRequested)
        {
            run.Status = "cancelled";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.ErrorMessage = "Cancelled before execution started";
            await db.SaveChangesAsync(CancellationToken.None);
            await this.NotifyCompletionAsync(hub, run);
            return;
        }

        run.Status = "running";
        run.StartedAt = DateTime.UtcNow.ToString("O");
        await db.SaveChangesAsync(CancellationToken.None);

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RepoId, CancellationToken.None);
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot) || !Directory.Exists(repo.CloneRoot))
        {
            run.Status = "failed";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.ErrorMessage = "Repo has no local clone";
            await db.SaveChangesAsync(CancellationToken.None);
            await this.NotifyCompletionAsync(hub, run);
            return;
        }

        // Phase 84 — per-repo elevation requirement. A repo flagged
        // RunTestsElevated must run its build/test gate as Administrator (its
        // tests write HKLM / bind HttpListener URLs, which need elevation). The
        // subprocess inherits the Forge.Api host's token (the launcher
        // self-elevates), so verify the host IS elevated and fail fast with an
        // actionable message rather than letting every test fail cryptically
        // with access-denied 20 minutes in.
        if (repo.RunTestsElevated == true && !IsProcessElevated())
        {
            run.Status = "failed";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.ErrorMessage =
                "This repo is configured to run build/test elevated (Administrator), but Forge.Api is " +
                "not running elevated. Restart it via scripts\\run-forge-ui.ps1 (it self-elevates — " +
                "accept the UAC prompt), then re-run validation.";
            await db.SaveChangesAsync(CancellationToken.None);
            await this.NotifyCompletionAsync(hub, run);
            return;
        }

        var detected = BuildSystemDetector.Detect(repo.CloneRoot);
        var initCmd = !string.IsNullOrEmpty(repo.InitCommand) ? repo.InitCommand : detected.InitCommand;
        var restoreCmd = !string.IsNullOrEmpty(repo.RestoreCommand) ? repo.RestoreCommand : detected.RestoreCommand;
        var buildCmd = !string.IsNullOrEmpty(repo.BuildCommand) ? repo.BuildCommand : detected.BuildCommand;
        var testCmd = !string.IsNullOrEmpty(repo.TestCommand) ? repo.TestCommand : detected.TestCommand;

        run.Ecosystem = detected.Ecosystem;

        var steps = new List<StepResult>();
        var allPassed = true;

        // Collect all configured steps.
        var pipeline = new List<(string name, string cmd, int? timeout)>();
        if (!string.IsNullOrEmpty(initCmd)) pipeline.Add(("init", initCmd, repo.BuildTimeoutSec));
        if (!string.IsNullOrEmpty(restoreCmd)) pipeline.Add(("restore", restoreCmd, repo.BuildTimeoutSec));
        if (!string.IsNullOrEmpty(buildCmd)) pipeline.Add(("build", buildCmd, repo.BuildTimeoutSec));
        if (!string.IsNullOrEmpty(testCmd)) pipeline.Add(("test", testCmd, repo.TestTimeoutSec));

        if (pipeline.Count == 0)
        {
            // Nothing to validate.
            run.AllPassed = true;
            run.Status = "succeeded";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.StepsJson = "[]";
            await db.SaveChangesAsync(CancellationToken.None);
            await this.NotifyCompletionAsync(hub, run);
            return;
        }

        // Build a single cmd.exe script with echo markers between steps
        // so that environment from init.cmd is preserved across all steps.
        // Doskey macros from init.cmd don't work in batch scripts, so we
        // scan init.cmd for doskey definitions and expand them inline.
        var doskeyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(initCmd))
        {
            // initCmd may be a bare "init" that names the repo's init.cmd
            // (AzureStack convention). Resolve the actual script file so the
            // doskey scan reads it (a bare "init" path wouldn't exist on disk).
            var initFirst = initCmd.Split(' ')[0];
            var initScript = ResolveRootScript(repo.CloneRoot, initFirst);
            var initPath = Path.Combine(repo.CloneRoot, initScript ?? initFirst);
            if (File.Exists(initPath))
            {
                foreach (var line in File.ReadLines(initPath))
                {
                    var trimmed = line.Trim();
                    // doskey restore=call %REPOROOT%.pipelines\restore\restore-wrapper.cmd
                    if (trimmed.StartsWith("doskey ", StringComparison.OrdinalIgnoreCase))
                    {
                        var eqIdx = trimmed.IndexOf('=', 7);
                        if (eqIdx > 7)
                        {
                            var alias = trimmed[7..eqIdx].Trim();
                            var expansion = trimmed[(eqIdx + 1)..].Trim();
                            doskeyMap[alias] = expansion;
                        }
                    }
                }
            }
        }

        var scriptLines = new List<string> { "@echo off" };
        foreach (var (name, cmd, _) in pipeline)
        {
            scriptLines.Add($"echo ===STEP_START:{name}===");

            // Resolve doskey aliases to their actual commands.
            var resolvedCmd = cmd;
            var firstToken = cmd.Split(' ')[0];
            if (doskeyMap.TryGetValue(firstToken, out var expansion))
            {
                // Replace the alias with its expansion; append any extra args.
                var extra = cmd.Length > firstToken.Length ? cmd[firstToken.Length..] : "";
                resolvedCmd = expansion + extra;
            }

            // NoDefaultCurrentDirectoryInExePath (set on many corporate
            // machines) stops cmd.exe from finding a repo-root script by bare
            // name (e.g. "init" -> the repo's init.cmd). Rewrite the first
            // token to an explicit ".\name" so it resolves regardless of that
            // policy; the `call` logic below then runs it without exiting the
            // parent script. Skip if already a path / already a `call`.
            if (!resolvedCmd.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
            {
                var t0 = resolvedCmd.Split(' ')[0];
                var rootScript = ResolveRootScript(repo.CloneRoot, t0);
                if (rootScript is not null)
                {
                    var extra = resolvedCmd.Length > t0.Length ? resolvedCmd[t0.Length..] : "";
                    resolvedCmd = $".\\{rootScript}{extra}";
                }
            }

            // Use `call` for .cmd/.bat so cmd.exe doesn't exit early.
            var exe = resolvedCmd.Split(' ')[0];
            var needsCall = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            // Also call if it already starts with "call ".
            if (resolvedCmd.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
                needsCall = false; // already has call prefix

            scriptLines.Add(needsCall ? $"call {resolvedCmd}" : resolvedCmd);
            scriptLines.Add($"echo ===STEP_END:{name}:EXIT:%errorlevel%===");
            scriptLines.Add($"if %errorlevel% neq 0 exit /b %errorlevel%");
        }

        var script = string.Join("\r\n", scriptLines);
        var overallTimeout = pipeline.Sum(p => p.timeout ?? 600);

        try
        {
            ct.ThrowIfCancellationRequested();
            var (exitCode, stdout, stderr, timedOut) = await RunScriptAsync(
                script, repo.CloneRoot, overallTimeout);

            // Parse per-step results from stdout markers.
            steps = ParseStepResults(stdout, stderr, pipeline, timedOut);
            allPassed = steps.All(s => s.Passed);

            // Persist after completion.
            run.StepsJson = JsonSerializer.Serialize(steps, JsonOpts);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            run.Status = "cancelled";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.StepsJson = JsonSerializer.Serialize(steps, JsonOpts);
            run.ErrorMessage = "Cancelled by user";
            await db.SaveChangesAsync(CancellationToken.None);
            await this.NotifyCompletionAsync(hub, run);
            return;
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.StepsJson = JsonSerializer.Serialize(steps, JsonOpts);
            run.ErrorMessage = $"Unexpected error: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);
            await this.NotifyCompletionAsync(hub, run);
            return;
        }

        run.AllPassed = allPassed;
        run.Status = allPassed ? "succeeded" : "failed";
        run.FinishedAt = DateTime.UtcNow.ToString("O");
        run.StepsJson = JsonSerializer.Serialize(steps, JsonOpts);
        await db.SaveChangesAsync(CancellationToken.None);
        await this.NotifyCompletionAsync(hub, run);
    }

    /// <summary>
    /// Resolve a command's first token to a script file in the repo root
    /// (<c>init</c> → <c>init.cmd</c>/<c>init.bat</c>, or an explicit
    /// <c>init.cmd</c>). Returns the matched file name (repo-relative) or null
    /// when the token is a path / has no matching root script. Used so a bare
    /// repo-root script command (the AzureStack <c>init</c> convention)
    /// resolves even when <c>NoDefaultCurrentDirectoryInExePath</c> is set —
    /// cmd.exe then won't search the current directory by bare name.
    /// </summary>
    private static string? ResolveRootScript(string cloneRoot, string firstToken)
    {
        if (string.IsNullOrEmpty(firstToken) || firstToken.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            return null;
        foreach (var name in new[] { firstToken, firstToken + ".cmd", firstToken + ".bat" })
        {
            if ((name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                && File.Exists(Path.Combine(cloneRoot, name)))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>
    /// Recompose the subprocess environment the way Windows builds a NEW
    /// interactive shell: overlay the live Machine then User registry
    /// environment onto the inherited process env. This makes <c>validate</c>
    /// build/test run exactly as the operator runs it by hand on their devbox —
    /// the Forge.Api host captures its environment once at launch (detached, and
    /// possibly under a more limited context), so its <c>PATH</c>/<c>PSModulePath</c>
    /// can be stale, causing the repo's PowerShell build targets (PSScriptAnalyzer
    /// encoding checks, module-manifest updates) to resolve different modules and
    /// fail spuriously.
    ///
    /// <para><c>PATH</c> and <c>PSModulePath</c> are rebuilt as Machine + User
    /// (concatenated, the logon-session order); all other registry vars take the
    /// User value over Machine. Volatile session vars already on the process
    /// (USERNAME / USERPROFILE / APPDATA / TEMP / SystemRoot / ComSpec / …) are
    /// preserved unless a registry value overrides them. REG_EXPAND_SZ values are
    /// expanded. Best-effort: a registry read failure leaves the inherited env.</para>
    /// </summary>
    private static void ApplyInteractiveEnvironment(System.Diagnostics.ProcessStartInfo psi)
    {
        // Rebuild the two search-path vars from scratch so the Machine+User
        // concat doesn't pile onto the already-composed inherited value.
        psi.Environment.Remove("Path");
        psi.Environment.Remove("PSModulePath");

        // Strip the MSBuild SDK-resolver vars that MSBuildLocator.RegisterDefaults()
        // (Program.cs, for Roslyn's MSBuildWorkspace) injects into THIS process,
        // pinned to the net8.0 SDK Forge.Api runs on (e.g. 8.0.421). They are
        // process env vars, so every child cmd.exe -> `dotnet build` inherits them
        // and is FORCED to resolve SDKs against that 8.x SDK's Sdks dir — which
        // lacks Microsoft.Build.Traversal (the repo's build.proj SDK), so the build
        // dies during evaluation with MSB4276 in ~4s ("Build FAILED. 0 Error(s)").
        // A fresh devbox shell never has these set, so the operator's manual build
        // succeeds. Removing them restores clean, global.json-driven SDK resolution.
        psi.Environment.Remove("MSBUILD_EXE_PATH");
        psi.Environment.Remove("MSBuildExtensionsPath");
        psi.Environment.Remove("MSBuildExtensionsPath32");
        psi.Environment.Remove("MSBuildExtensionsPath64");
        psi.Environment.Remove("MSBuildSDKsPath");

        void Overlay(EnvironmentVariableTarget target)
        {
            System.Collections.IDictionary vars;
            try { vars = Environment.GetEnvironmentVariables(target); }
            catch { return; }
            foreach (System.Collections.DictionaryEntry e in vars)
            {
                var key = (string)e.Key;
                var raw = (string?)e.Value;
                if (string.IsNullOrEmpty(raw)) continue;
                var val = Environment.ExpandEnvironmentVariables(raw);
                if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "PSModulePath", StringComparison.OrdinalIgnoreCase))
                {
                    psi.Environment.TryGetValue(key, out var cur);
                    psi.Environment[key] = string.IsNullOrEmpty(cur) ? val : cur.TrimEnd(';') + ";" + val;
                }
                else
                {
                    psi.Environment[key] = val;
                }
            }
        }

        Overlay(EnvironmentVariableTarget.Machine);
        Overlay(EnvironmentVariableTarget.User);
    }

    /// <summary>
    /// Runs a batch script in a single cmd.exe session so environment is
    /// shared across all steps (init sets vars, restore/build/test inherit them).
    /// The script is written inside <paramref name="cwd"/> so that
    /// <c>%~dp0</c> resolves to the repo root (init.cmd depends on this).
    /// </summary>
    private static async Task<(int code, string stdout, string stderr, bool timedOut)> RunScriptAsync(
        string script, string cwd, int timeoutSec)
    {
        // Place the temp script in the repo root so %~dp0 = repo root.
        var tmpFile = Path.Combine(cwd, $".forge-validate-{Guid.NewGuid():N}.cmd");
        try
        {
            await File.WriteAllTextAsync(tmpFile, script);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(tmpFile);

            // Devbox parity: run the build/test in the same environment a FRESH
            // interactive shell composes (live Machine + User registry), not the
            // Forge.Api host's captured-at-launch process env. Otherwise the
            // repo's PowerShell build targets (PSScriptAnalyzer, module-manifest
            // updates, tool lookups) resolve different modules/tools than when
            // the operator builds by hand, and fail spuriously.
            ApplyInteractiveEnvironment(psi);

            using var proc = System.Diagnostics.Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                return (proc.ExitCode, await stdoutTask, await stderrTask, false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                string pOut = "", pErr = "";
                try
                {
                    using var rCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    pOut = await proc.StandardOutput.ReadToEndAsync(rCts.Token);
                    pErr = await proc.StandardError.ReadToEndAsync(rCts.Token);
                }
                catch { }
                return (-1, pOut, pErr + "\n[TIMED OUT]", true);
            }
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    /// <summary>
    /// Parses per-step pass/fail from the echo markers embedded in stdout.
    /// </summary>
    private static List<StepResult> ParseStepResults(
        string stdout, string stderr,
        List<(string name, string cmd, int? timeout)> pipeline,
        bool overallTimedOut)
    {
        var results = new List<StepResult>();
        var lines = stdout.Split('\n');

        foreach (var (name, cmd, _) in pipeline)
        {
            // Find the output between STEP_START and STEP_END markers.
            var startMarker = $"===STEP_START:{name}===";
            var endPrefix = $"===STEP_END:{name}:EXIT:";
            var startIdx = Array.FindIndex(lines, l => l.Trim().Contains(startMarker));
            var endIdx = Array.FindIndex(lines, l => l.Trim().StartsWith(endPrefix));

            if (startIdx < 0)
            {
                // Step never started — pipeline aborted before reaching it.
                break;
            }

            var stepOutput = new List<string>();
            for (var i = startIdx + 1; i < lines.Length && i != endIdx; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("===STEP_END:")) break;
                stepOutput.Add(lines[i]);
            }

            var exitCode = -1;
            var passed = false;
            if (endIdx >= 0)
            {
                var endLine = lines[endIdx].Trim();
                var exitStr = endLine.Replace(endPrefix, "").Replace("===", "").Trim();
                if (int.TryParse(exitStr, out var ec)) exitCode = ec;
                passed = exitCode == 0;
            }

            var stepStdout = string.Join("\n", stepOutput).Trim();
            // stderr is shared across all steps; include it on the failing step.
            var stepStderr = !passed ? Truncate(stderr) : "";
            var timedOut = !passed && overallTimedOut && endIdx < 0;

            results.Add(new StepResult(name, cmd, passed, exitCode, timedOut, Truncate(stepStdout), stepStderr));

            if (!passed) break; // pipeline aborts on first failure
        }

        return results;
    }

    private async Task NotifyCompletionAsync(IHubContext<ForgeHub> hub, BuildValidationRunRow run)
    {
        try
        {
            await hub.Clients.All.SendAsync("repo.validation-complete", new
            {
                runId = run.Id,
                repoId = run.RepoId,
                status = run.Status,
                allPassed = run.AllPassed,
                ecosystem = run.Ecosystem,
                errorMessage = run.ErrorMessage,
                steps = !string.IsNullOrEmpty(run.StepsJson)
                    ? JsonSerializer.Deserialize<List<StepResult>>(run.StepsJson, JsonOpts)
                    : null,
            });
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to send SignalR validation-complete for run {RunId}", run.Id);
        }
    }

    private static string Truncate(string s) => s.Length > 4000 ? s[^4000..] : s;

    /// <summary>
    /// True when the current process runs with an elevated (Administrator,
    /// high-integrity) token. Windows-only; returns false elsewhere. Used to
    /// enforce a repo's <see cref="BuildValidationRunRow"/> elevation
    /// requirement (RunTestsElevated) before launching the build/test session.
    /// </summary>
    private static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public sealed record StepResult(
        string Step,
        string Command,
        bool Passed,
        int ExitCode,
        bool TimedOut,
        string Stdout,
        string Stderr);
}

/// <summary>
/// Job queued into the <see cref="BuildValidationWorker"/> channel.
/// </summary>
public sealed record BuildValidationJob(string RunId, string RepoId);
