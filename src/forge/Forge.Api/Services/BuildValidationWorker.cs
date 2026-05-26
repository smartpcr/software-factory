// -----------------------------------------------------------------------
// <copyright file="BuildValidationWorker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
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
        // subprocess inherits the Forge.Api host's token (scripts\run-forge-ui.ps1
        // self-elevates at launch, see its self-elevation block), so verify the
        // host IS elevated and fail fast with an actionable message rather than
        // letting every test fail cryptically with access-denied 20 minutes in.
        // Phase 84 / 2026-05-26 — per-repo elevation. A repo flagged
        // RunTestsElevated runs its build/test as Administrator (its tests
        // write HKLM / bind HttpListener URLs). Forge.Api itself is NON-elevated
        // now; the elevated work is marshalled to the elevated broker (a
        // separate `Forge.Api.exe --elevated-broker` the launcher starts as
        // admin). Verify the broker is reachable and fail fast with an
        // actionable message rather than running the tests non-elevated (cryptic
        // access-denied) or silently dropping the elevation requirement.
        var runElevated = repo.RunTestsElevated == true;
        if (runElevated && !await Forge.Api.Agents.ElevatedCommandBroker.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false))
        {
            run.Status = "failed";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.ErrorMessage =
                "This repo runs build/test elevated (Administrator), but the elevated test broker is not " +
                "reachable. Start it via scripts\\run-forge-ui.ps1 (it launches the broker elevated — accept " +
                "the UAC prompt), then re-run validation.";
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

        // 2026-05-25 — durable build/test log + TRX capture (rubber-duck
        // critique addressed):
        //   1. Pre-flight: ensure `.forge/` is gitignored in this repo so
        //      the log file + copied TRX we're about to write never get
        //      committed. Mirrors the per-workstream behaviour in
        //      CrewQueueWorker (now sharing GateLogPaths.EnsureGitignoreLinesAsync).
        //   2. Snapshot UtcNow *before* launching cmd.exe so the post-run
        //      TRX scan only picks up files written by THIS run.
        //   3. Combined log streams to disk line-by-line (RunScriptAsync
        //      below) so even a 200 MB MSBuild verbose log doesn't OOM the
        //      worker — but we still keep the full stdout in memory for
        //      ParseStepResults (which needs to locate the STEP markers).
        //      An 8 MB in-memory cap on the StringBuilder bounds memory
        //      for pathological output sizes (see RunScriptAsync).
        //   4. After exit: collect new *.trx files anywhere under the repo
        //      clone root EXCEPT under `.worktree/` (concurrent gate runs)
        //      and `.forge/logs/trx/` (our own prior copies). Copy each
        //      into the per-run TRX dir, parse for failed UnitTestResult
        //      nodes, and PREPEND the failure summary to the test step's
        //      stdout before persistence so the 16 KB DB tail starts with
        //      actionable names (not random "[end] GetActionPlanInstances"
        //      noise). If the repo emits no TRX (e.g. `update` uses a
        //      driver that doesn't pass --logger), fall back to a regex
        //      scan of stdout for "  Failed FQTN" lines.
        var beforeUtc = DateTime.UtcNow.AddSeconds(-1);
        var validateLog = GateLogPaths.ForValidateRun(repo.CloneRoot, run.Id);
        await GateLogPaths.EnsureForgeGitignoredAsync(repo.CloneRoot, CancellationToken.None);

        try
        {
            ct.ThrowIfCancellationRequested();
            // RunTestsElevated repos run the validation script via the elevated
            // broker (Forge.Api is non-elevated); everything else runs locally.
            var (exitCode, stdout, stderr, timedOut) = runElevated
                ? await Forge.Api.Agents.ElevatedCommandBroker.RunScriptAsync(
                    new Forge.Api.Agents.ElevatedScriptRequest(script, repo.CloneRoot, overallTimeout, validateLog.LogPath), ct)
                : await RunScriptAsync(script, repo.CloneRoot, overallTimeout, validateLog.LogPath);

            // Collect any TRX files written during this run, excluding the
            // worktree subtree (a concurrent iteration gate may be writing
            // TRX there) and our own .forge/logs/trx/ subtree (prior copies).
            var newTrxPaths = GateLogPaths.CollectNewTrxFiles(
                repo.CloneRoot,
                beforeUtc,
                excludeDirNames: new[] { ".worktree", ".forge", "node_modules" });
            var copiedTrxRel = new List<string>();
            var trxFailures = new List<GateLogPaths.TrxFailure>();
            if (newTrxPaths.Count > 0)
            {
                try { Directory.CreateDirectory(validateLog.TrxDir); }
                catch { /* best-effort; copy below will just fail per-file */ }
                // Cap how many TRX files we copy (some repos produce hundreds)
                // so disk + parse cost stays bounded. Newest first so a
                // capped result is the most relevant set.
                var ordered = newTrxPaths
                    .Select(p => new { Path = p, Info = SafeFileInfo(p) })
                    .OrderByDescending(x => x.Info?.LastWriteTimeUtc ?? DateTime.MinValue)
                    .Take(50)
                    .ToList();
                foreach (var entry in ordered)
                {
                    try
                    {
                        var fileName = Path.GetFileName(entry.Path);
                        var dest = Path.Combine(validateLog.TrxDir, fileName);
                        File.Copy(entry.Path, dest, overwrite: true);
                        var rel = Path.GetRelativePath(repo.CloneRoot, dest).Replace('\\', '/');
                        copiedTrxRel.Add(rel);
                        trxFailures.AddRange(GateLogPaths.ParseTrxFailures(dest));
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogDebug(ex, "TRX copy/parse failed for {Trx}", entry.Path);
                    }
                }
            }

            // Fallback: if no TRX failures were parseable, scrape stdout for
            // `dotnet test` "  Failed FQTN" lines so repos that don't emit
            // TRX (e.g. `update`'s build.proj driver) still surface names.
            if (trxFailures.Count == 0)
            {
                trxFailures.AddRange(GateLogPaths.ParseDotnetTestFailuresFromText(stdout));
            }

            // Parse per-step results; the failure-summary block is prepended
            // to the test step's stdout BEFORE truncation so the DB tail
            // starts with actionable failure names.
            steps = ParseStepResults(
                stdout, stderr, pipeline, timedOut,
                trxFailures, validateLog.LogRelPath, copiedTrxRel);
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
        // succeeds. This path rebuilds PATH from the registry above (interactive
        // devbox simulation), so we only need to strip the SDK-resolver denylist
        // here — centralised in ChildProcessEnv so the list has one home.
        ChildProcessEnv.StripDenied(psi);

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
    ///
    /// <para>2026-05-25 — combined stdout+stderr is streamed line-by-line to
    /// <paramref name="logFilePath"/> as the process runs (best-effort: a
    /// failed open silently falls back to in-memory-only). The full output
    /// is also kept in a StringBuilder capped at 8 MB so <see cref="ParseStepResults"/>
    /// can find STEP_START/STEP_END markers; beyond the cap a single
    /// truncation marker is appended and subsequent lines are written to
    /// disk only. Stderr is interleaved with stdout in the disk log (the
    /// order is approximate — pumps are independent — but useful enough for
    /// diagnostics) and also returned separately for the "stderr on failed
    /// step" UI.</para>
    /// </summary>
    // internal so the elevated broker (Forge.Api.exe --elevated-broker) can
    // run a RunTestsElevated repo's validation script as Administrator while
    // the non-elevated Forge.Api host marshals it over the pipe.
    internal static async Task<(int code, string stdout, string stderr, bool timedOut)> RunScriptAsync(
        string script, string cwd, int timeoutSec, string? logFilePath = null)
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

            // Open the log file lazily; best-effort.
            StreamWriter? logWriter = null;
            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    logWriter = new StreamWriter(
                        new FileStream(logFilePath!, FileMode.Create, FileAccess.Write, FileShare.Read),
                        Encoding.UTF8)
                    { AutoFlush = false };
                }
                catch
                {
                    logWriter = null;
                }
            }

            // In-memory accumulators. ParseStepResults needs the full stdout
            // to find markers, so we keep it in a StringBuilder. Capped at
            // ~8 MB so pathological build output (e.g. detailed MSBuild diag)
            // doesn't blow process memory. Stderr is typically tiny (cmd.exe
            // batch error messages); cap at 1 MB.
            const int StdoutMemCap = 8 * 1024 * 1024;
            const int StderrMemCap = 1 * 1024 * 1024;
            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            var stdoutTruncated = false;
            var stderrTruncated = false;
            var writeLock = new object();

            async Task PumpAsync(StreamReader reader, StringBuilder buf, int cap, bool trackStderrTruncated)
            {
                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null)
                {
                    lock (writeLock)
                    {
                        if (buf.Length < cap)
                        {
                            // Reserve a few bytes for the truncation marker so
                            // we don't overshoot the cap by an unbounded amount.
                            if (buf.Length + line.Length + 1 <= cap)
                            {
                                buf.AppendLine(line);
                            }
                            else
                            {
                                if (trackStderrTruncated) stderrTruncated = true;
                                else stdoutTruncated = true;
                                buf.AppendLine("…[output truncated in-memory; full log on disk]");
                                // Pin buf.Length above cap so further appends short-circuit.
                                while (buf.Length < cap) buf.Append('x');
                            }
                        }
                        if (logWriter is not null)
                        {
                            try { logWriter.WriteLine(line); }
                            catch { /* best-effort */ }
                        }
                    }
                }
            }

            var stdoutTask = PumpAsync(proc.StandardOutput, stdoutBuf, StdoutMemCap, trackStderrTruncated: false);
            var stderrTask = PumpAsync(proc.StandardError, stderrBuf, StderrMemCap, trackStderrTruncated: true);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
                try { await stdoutTask; } catch { /* drain swallow */ }
                try { await stderrTask; } catch { /* drain swallow */ }
                if (logWriter is not null)
                {
                    try { await logWriter.FlushAsync(); } catch { }
                    try { logWriter.Dispose(); } catch { }
                }
                _ = stdoutTruncated; _ = stderrTruncated; // currently informational only
                return (proc.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString(), false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { await stdoutTask; } catch { }
                try { await stderrTask; } catch { }
                stderrBuf.AppendLine("\n[TIMED OUT]");
                if (logWriter is not null)
                {
                    try { await logWriter.WriteLineAsync("[TIMED OUT]"); } catch { }
                    try { await logWriter.FlushAsync(); } catch { }
                    try { logWriter.Dispose(); } catch { }
                }
                return (-1, stdoutBuf.ToString(), stderrBuf.ToString(), true);
            }
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    /// <summary>Best-effort <see cref="FileInfo"/> ctor that doesn't throw.</summary>
    private static FileInfo? SafeFileInfo(string path)
    {
        try { return new FileInfo(path); }
        catch { return null; }
    }

    /// <summary>
    /// Parses per-step pass/fail from the echo markers embedded in stdout.
    /// When <paramref name="testFailures"/> is non-empty AND we're processing
    /// the <c>test</c> step, the formatted failure block is prepended to that
    /// step's stdout BEFORE truncation so the 16 KB DB tail starts with
    /// actionable failure names (not random tail noise). The combined log
    /// path + copied-TRX rel paths are attached to every step row for UI
    /// visibility — they're the same per run.
    /// </summary>
    private static List<StepResult> ParseStepResults(
        string stdout, string stderr,
        List<(string name, string cmd, int? timeout)> pipeline,
        bool overallTimedOut,
        IReadOnlyList<GateLogPaths.TrxFailure>? testFailures = null,
        string? combinedLogRelPath = null,
        IReadOnlyList<string>? trxRelPaths = null)
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

            // Prepend failed-test summary to the test step's stdout BEFORE
            // truncation so the visible tail starts with names, not noise.
            // Only applied on failure (no point spamming a passing run with
            // an empty FAILED TESTS header, and TRX-on-pass would be empty
            // anyway).
            if (name == "test" && !passed && testFailures is { Count: > 0 })
            {
                var summary = GateLogPaths.FormatFailureSummary(testFailures);
                if (!string.IsNullOrEmpty(summary))
                {
                    stepStdout = summary + "\n────────────────────────────\n" + stepStdout;
                }
            }

            // stderr is shared across all steps; include it on the failing step.
            var stepStderr = !passed ? Truncate(stderr) : "";
            var timedOut = !passed && overallTimedOut && endIdx < 0;

            results.Add(new StepResult(
                name, cmd, passed, exitCode, timedOut,
                Truncate(stepStdout), stepStderr,
                LogRelPath: combinedLogRelPath,
                TrxRelPaths: name == "test" ? trxRelPaths : null));

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

    // 2026-05-25 — raised from 4 KB → 16 KB so the tail of a dotnet test
    // step preserves failed test names + assertion messages. The test
    // step's stdout is also prepended with a FAILED TESTS summary by
    // ParseStepResults so the head of the truncated text always shows
    // actionable names regardless of cap.
    private static string Truncate(string s) => s.Length > 16000 ? s[^16000..] : s;

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
        string Stderr,
        // 2026-05-25 — repo-relative path to the combined stdout+stderr
        // log under .forge/logs/. Same value on every step in a run
        // (one log per run). UI shows a 📎 view-log chip.
        string? LogRelPath = null,
        // Repo-relative paths to TRX files copied from the test step.
        // Only populated on the test step; null elsewhere.
        IReadOnlyList<string>? TrxRelPaths = null);
}

/// <summary>
/// Job queued into the <see cref="BuildValidationWorker"/> channel.
/// </summary>
public sealed record BuildValidationJob(string RunId, string RepoId);
