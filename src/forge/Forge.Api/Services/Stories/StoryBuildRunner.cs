// -----------------------------------------------------------------------
// <copyright file="StoryBuildRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Forge.Api.Agents;

/// <summary>
/// Phase 47e — runs <c>dotnet build</c> against a workstream's worktree
/// and returns a structured <see cref="StoryBuildResult"/> the orchestrator
/// can act on. Used by both the per-iter build check inside the gen↔eval
/// loop and the post-pass build gate before commit. The build is the
/// authoritative source of truth for "does this code compile" — the
/// evaluator cannot see compiler errors and was observed (2026-05-09)
/// scoring the engineer agent's diff at 68 even though the agent had
/// deleted <c>src/ast/Ast.Memory/Evolution/EvolutionEngine.cs</c> along
/// with 17 other production-code files. A real <c>dotnet build</c> would
/// have caught it on the first iter.
/// </summary>
public static class StoryBuildRunner
{
    /// <summary>Default per-build wall-clock cap; large enough for a cold restore on Workflow.sln.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(8);

    /// <summary>How many error-level lines we capture into <see cref="StoryBuildResult.ErrorSummary"/>.</summary>
    public const int ErrorSampleLimit = 8;

    /// <summary>Hard cap on the summary's character length.</summary>
    public const int ErrorSummaryMaxChars = 2400;

    private static readonly Regex ErrorLineRegex = new(
        @"\):\s+error\s+\w+:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WarningLineRegex = new(
        @"\):\s+warning\s+\w+:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Run <c>dotnet build --nologo --verbosity minimal</c> in
    /// <paramref name="workspace"/> by default, OR the caller-supplied
    /// <paramref name="buildCommand"/> when set (Phase 49 — Forge supports
    /// non-.NET ecosystems; the command is whatever
    /// <see cref="Forge.Api.Services.BuildSystemDetector.Detect"/> returns
    /// for the workspace, e.g. <c>cargo build</c> for a Rust workspace,
    /// <c>go build ./...</c> for Go, <c>npm run build</c> for Node). The
    /// command string is split on whitespace into program + args; quotes
    /// are not honoured (production build commands don't need them).
    /// Captures stdout+stderr, counts errors / warnings, samples the
    /// first few error lines for the caller to display in transcript /
    /// log output. Never throws on a build failure — failures are
    /// reported via <see cref="StoryBuildResult.Success"/>; only
    /// catastrophic process errors (e.g. the program not on PATH)
    /// propagate.
    ///
    /// <para>Phase 59 Stage B — when <paramref name="logFilePath"/> is
    /// set, combined stdout+stderr is streamed line-by-line to that
    /// file as the process runs (StreamWriter with line buffering, NOT
    /// buffered into a single string) so a 200 MB log doesn't OOM the
    /// host. Parsing (error/warning counts, first 8 sampled errors)
    /// happens inline during the pump — no second pass over the data.
    /// The resulting <see cref="StoryBuildResult.LogPath"/> /
    /// <see cref="StoryBuildResult.LogRelPath"/> point the caller at
    /// the persisted file so a SignalR transcript event can include a
    /// <c>logUrl</c> the UI renders as a 📎 view-log chip
    /// (<c>TranscriptLine.tsx</c>). Best-effort: a failed open / write
    /// silently falls back to in-memory parsing only — the gate
    /// outcome stays trustworthy even when log persistence fails.</para>
    /// </summary>
    public static async Task<StoryBuildResult> RunBuildAsync(
        string workspace,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? buildCommand = null,
        string? logFilePath = null,
        string? logRelPath = null,
        // 2026-05-22 — when non-empty, these setup commands (e.g.
        // init.cmd, restore) run, in order, in the SAME cmd.exe session
        // that runs the build/test command. See the session block below.
        IReadOnlyList<string>? sessionSetupCommands = null,
        // 2026-05-25 — when true AND ExitCode != 0, scan the workspace for
        // *.trx files written during this run, parse their failed
        // UnitTestResult nodes, and prepend a "FAILED TESTS:" block to
        // ErrorSummary. Falls back to a regex scrape of stdout for
        // "  Failed FQTN" lines when no TRX was produced (e.g. the
        // `update` repo's build.proj driver). The caller (the test gate)
        // sets this; quality/build gates leave it false because their
        // failures are compiler/linter errors that the existing regex
        // already captures. New TRX files are also copied into
        // {workspace}/.forge/logs/trx/{iter|gate}/ so they survive a
        // subsequent `dotnet clean`; TrxRelPaths on the result lists the
        // copies relative to the workspace.
        bool collectTestFailures = false,
        // Stable suffix appended to the per-iter TRX copy dir so a
        // re-run of the same iter doesn't collide with the prior attempt
        // (the caller typically uses iter + ticks).
        string? trxScopeTag = null,
        // When true, the log file is OPENED FOR APPEND instead of being
        // truncated. Used by RunGoWorkspaceAsync to stream several
        // per-module runs (each with its own header) into one combined
        // gate log. Default false preserves the single-run truncate.
        bool appendLog = false)
    {
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
        {
            return new StoryBuildResult(
                Success: false,
                ExitCode: -1,
                ErrorCount: 0,
                WarningCount: 0,
                ErrorSummary: $"workspace path does not exist: '{workspace}'",
                Duration: TimeSpan.Zero,
                TimedOut: false);
        }

        // Phase 49 — split buildCommand into program + args via the
        // quote-aware parser so paths with spaces (e.g. `dotnet build
        // "My Solution.sln"` from BuildSystemDetector.QuotePath) survive
        // splitting. Empty / null buildCommand falls back to
        // `dotnet build --nologo --verbosity minimal` (the original
        // Phase 47e default for .NET stories).
        string program;
        string[] args;
        if (string.IsNullOrWhiteSpace(buildCommand))
        {
            program = "dotnet";
            args = new[] { "build", "--nologo", "--verbosity", "minimal" };
        }
        else
        {
            var tokens = ParseCommandLine(buildCommand);
            if (tokens.Length == 0)
            {
                program = "dotnet";
                args = new[] { "build", "--nologo", "--verbosity", "minimal" };
            }
            else
            {
                program = tokens[0];
                args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();
            }
        }

        // 2026-05-22 — "same session" gate execution (RepoRow.InitCommand
        // / RestoreCommand). init.cmd sets PATH (incl. the .dev dir that
        // puts `restore` on PATH) and CDP_* env vars that restore.cmd and
        // the build itself depend on; those mutations only survive WITHIN
        // one shell process, so firing init / restore / build as three
        // separate Process.Start calls loses the environment between them
        // (the gate was running a bare `dotnet build` with the wrong
        // dotnet + no restore). When the caller supplies setup commands we
        // generate a single .cmd that `call`s each setup step then runs
        // the build/test command verbatim, and execute the whole thing in
        // ONE cmd.exe session. Null/empty keeps the direct-exec path
        // (cargo / npm / go / non-Windows ecosystems, which have no init).
        string? sessionScriptPath = null;
        if (sessionSetupCommands is { Count: > 0 } && OperatingSystem.IsWindows())
        {
            var finalCommandLine = string.IsNullOrWhiteSpace(buildCommand)
                ? "dotnet build --nologo --verbosity minimal"
                : buildCommand!;
            sessionScriptPath = WriteSessionScript(workspace, sessionSetupCommands, finalCommandLine);
            program = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            args = new[] { "/c", sessionScriptPath };
        }

        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Phase 49 — honour the parsed args from buildCommand instead of
        // hard-coding dotnet-flavour switches. The default buildCommand
        // path (when the caller passes null) populates `args` with
        // ["build","--nologo","--verbosity","minimal"], so behaviour is
        // preserved for the .NET fallback. For non-.NET ecosystems
        // (cargo build, npm run build, go build ./..., mvn package …)
        // the runner now actually invokes the right tool with the right
        // arguments. --no-restore is omitted from the dotnet default
        // because a from-scratch project (e.g. the engineer just
        // scaffolded a new csproj) needs nuget restore to produce its
        // assets.json before build can resolve types; the budget below
        // caps wall-clock if restore is too slow.
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // 2026-05-21 — disable MSBuild node reuse + the persistent build
        // server so each gate build/test invocation doesn't leave long-lived
        // MSBuild worker nodes behind. ~700 leaked `dotnet` processes
        // accumulated after a day of gate runs on the `update` repo, which
        // burned memory and made the launcher's process scan crawl. Disabling
        // node reuse also lets the timeout process-tree kill land on testhost
        // cleanly (no persistent server parent to re-home it), which reduces
        // the orphaned-testhost worktree locks this gate has hit.
        // Isolate the child build/test from Forge.Api's own (MSBuildLocator-
        // polluted) environment: rebuild it from the pristine baseline
        // captured before RegisterDefaults ran, so SDK resolution behaves like
        // a fresh shell (no inherited MSBUILD_EXE_PATH / MSBuildSDKsPath → no
        // MSB4276; matches how the engineer builds by hand). The repo's own
        // init.cmd / restore.cmd then establish PATH/CDP context from the
        // worktree. See ChildProcessEnv. Re-assert the build-tuning vars AFTER
        // the clean rebuild so they survive.
        ChildProcessEnv.Apply(psi);
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        // Phase 49 — convert process-start failures (program not on
        // PATH, .NET SDK missing for a `dotnet build`, cargo absent
        // for a Rust workspace, etc.) into a StoryBuildResult instead
        // of letting Win32Exception propagate. A propagating exception
        // would hit StorySpawnOrchestrator's generic catch and route
        // the workstream into the retry loop instead of the BuildFailed
        // → blocked path, masking the real problem (missing toolchain)
        // behind retry backoff noise.
        // 2026-05-25 — snapshot UtcNow BEFORE start so the post-run TRX
        // scan only picks up files written by THIS run (collectTestFailures
        // path). Snapshot a second earlier to ride out clock resolution
        // quirks on Windows (FAT-style mtime is 2 s precision).
        var trxScanStartUtc = DateTime.UtcNow.AddSeconds(-1);
        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            sw.Stop();
            return new StoryBuildResult(
                Success: false,
                ExitCode: -1,
                ErrorCount: 0,
                WarningCount: 0,
                ErrorSummary: $"Could not start build program '{program}': {ex.Message}. " +
                              "Confirm the toolchain is installed and on PATH for the worker process.",
                Duration: sw.Elapsed,
                TimedOut: false);
        }

        if (proc is null)
        {
            sw.Stop();
            return new StoryBuildResult(
                Success: false,
                ExitCode: -1,
                ErrorCount: 0,
                WarningCount: 0,
                ErrorSummary: $"Process.Start returned null for program '{program}' — toolchain likely missing.",
                Duration: sw.Elapsed,
                TimedOut: false);
        }

        using var _ = proc;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget = timeout ?? DefaultTimeout;
        if (budget > TimeSpan.Zero) timeoutCts.CancelAfter(budget);
        var effectiveCt = timeoutCts.Token;

        // Phase 59 Stage B — open the log file lazily before draining
        // the streams. Best-effort: if the directory can't be created
        // or the file can't be opened, fall through to in-memory-only
        // parsing (logFile stays null + LogPath unset in the result).
        StreamWriter? logWriter = null;
        string? effectiveLogPath = null;
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                logWriter = new StreamWriter(
                    new FileStream(logFilePath!, appendLog ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read),
                    System.Text.Encoding.UTF8);
                logWriter.AutoFlush = false; // batch per-line flushes via the pump
                effectiveLogPath = logFilePath;
            }
            catch
            {
                logWriter = null;
                effectiveLogPath = null;
            }
        }

        // Inline-parse stats that previously came out of Parse() —
        // counts + first 8 sampled errors. Bounded memory regardless
        // of output size.
        var errorCount = 0;
        var warningCount = 0;
        var samples = new List<string>(ErrorSampleLimit);
        // Tail ring for the no-parsed-errors fallback (last ~1200 chars).
        var tail = new TailRing(1200);
        var lockObj = new object();

        async Task PumpAsync(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(effectiveCt).ConfigureAwait(false)) is not null)
            {
                lock (lockObj)
                {
                    if (ErrorLineRegex.IsMatch(line))
                    {
                        errorCount++;
                        if (samples.Count < ErrorSampleLimit) samples.Add(line.Trim());
                    }
                    else if (WarningLineRegex.IsMatch(line))
                    {
                        warningCount++;
                    }
                    tail.Append(line);
                    if (logWriter is not null)
                    {
                        try { logWriter.WriteLine(line); }
                        catch { /* best-effort; subsequent writes also no-op naturally */ }
                    }
                }
            }
        }

        var stdoutTask = PumpAsync(proc.StandardOutput);
        var stderrTask = PumpAsync(proc.StandardError);
        var timedOut = false;

        try
        {
            await proc.WaitForExitAsync(effectiveCt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }

        // Drain the pumps. Both swallow per-line read errors so the
        // outer await only sees cancellation, which we treat as "done
        // reading whatever was available".
        try { await stdoutTask; } catch { /* swallow */ }
        try { await stderrTask; } catch { /* swallow */ }

        if (logWriter is not null)
        {
            try { await logWriter.FlushAsync(); } catch { /* best-effort */ }
            try { logWriter.Dispose(); } catch { /* best-effort */ }
        }

        sw.Stop();

        // Best-effort cleanup of the one-shot session script (temp dir).
        // Runs before both the timeout and normal returns below; cmd.exe
        // has exited (or been tree-killed) so the file is no longer held.
        if (sessionScriptPath is not null)
        {
            try { File.Delete(sessionScriptPath); } catch { /* best-effort temp cleanup */ }
        }

        if (timedOut)
        {
            // 2026-05-19 — pre-fix this said "`dotnet` build command
            // timed out" regardless of what command actually ran;
            // misleading when the gate was running `dotnet test`.
            // Show the verbatim command + tail of stdout so the
            // operator can see WHICH project / test the killed
            // process was mid-run on instead of a single useless
            // line of generic timeout text.
            var commandLine = buildCommand ?? $"{program} {string.Join(' ', args)}";
            var tailSnapshot = tail.Snapshot().Trim();
            var sb = new System.Text.StringBuilder();
            sb.Append('`').Append(commandLine).Append("` timed out after ")
                .AppendFormat("{0:0.0}", budget.TotalMinutes).Append("min in '")
                .Append(workspace).Append('\'');
            if (errorCount > 0)
            {
                sb.Append(" (").Append(errorCount).Append(" error line(s) parsed before kill)");
            }
            if (!string.IsNullOrEmpty(tailSnapshot))
            {
                sb.Append("\n\nLast output before kill:\n").Append(tailSnapshot);
            }
            var timeoutSummary = sb.ToString();
            if (timeoutSummary.Length > ErrorSummaryMaxChars)
            {
                timeoutSummary = timeoutSummary[..ErrorSummaryMaxChars] + "…(truncated; full log at the chip link)";
            }
            return new StoryBuildResult(
                Success: false,
                ExitCode: -1,
                ErrorCount: errorCount,
                WarningCount: warningCount,
                ErrorSummary: timeoutSummary,
                Duration: sw.Elapsed,
                TimedOut: true,
                LogPath: effectiveLogPath,
                LogRelPath: logRelPath);
        }

        var summary = string.Join("\n", samples);
        if (summary.Length > ErrorSummaryMaxChars)
        {
            summary = summary[..ErrorSummaryMaxChars] + "…(truncated)";
        }
        if (string.IsNullOrEmpty(summary) && proc.ExitCode != 0)
        {
            // Build failed but no parseable error lines — surface the
            // tail of the combined stream so the operator has
            // something to look at. The dotnet compiler routes errors
            // to stdout, so the earlier stderr-only tail missed
            // everything for `dotnet build`; cargo fmt --check writes
            // its diff to stdout too. Combined tail is the safe
            // universal default.
            summary = "(no parsed error lines; raw output tail below)\n" + tail.Snapshot().Trim();
        }

        // 2026-05-25 — test-gate path: scan for new TRX, parse failed
        // UnitTestResult nodes, fall back to stdout regex when no TRX,
        // and PREPEND the resulting "FAILED TESTS" block to ErrorSummary
        // so the next-iter generator's "TEST FAILURE FROM PRIOR
        // ITERATION" block shows actionable per-test detail (the
        // existing ErrorLineRegex matches `\):\s+error\s+\w+:` which
        // catches compiler errors only, not dotnet-test failures).
        var trxRelPaths = (IReadOnlyList<string>?)null;
        if (collectTestFailures && proc.ExitCode != 0)
        {
            var trxFailures = new List<GateLogPaths.TrxFailure>();
            try
            {
                // Exclude .forge/logs/trx/ so we don't re-pick-up our own
                // prior copies, and node_modules to keep the walk fast.
                var newTrxPaths = GateLogPaths.CollectNewTrxFiles(
                    workspace,
                    trxScanStartUtc,
                    excludeDirNames: new[] { ".forge", "node_modules", ".git" });
                if (newTrxPaths.Count > 0)
                {
                    var scopeTag = trxScopeTag ?? $"test-{DateTime.UtcNow.Ticks}";
                    var trxDir = Path.Combine(workspace, ".forge", "logs", "trx", scopeTag);
                    var copied = new List<string>();
                    try { Directory.CreateDirectory(trxDir); } catch { /* best-effort */ }
                    foreach (var trx in newTrxPaths.Take(50))
                    {
                        try
                        {
                            var fileName = Path.GetFileName(trx);
                            var dest = Path.Combine(trxDir, fileName);
                            File.Copy(trx, dest, overwrite: true);
                            copied.Add(Path.GetRelativePath(workspace, dest).Replace('\\', '/'));
                            trxFailures.AddRange(GateLogPaths.ParseTrxFailures(dest));
                        }
                        catch { /* best-effort per-file */ }
                    }
                    if (copied.Count > 0) trxRelPaths = copied;
                }
            }
            catch { /* never block the gate on TRX side-effects */ }

            // Fallback: scrape regex from the buffered tail + the samples
            // we already captured. We don't have full stdout here (line
            // streaming bounded by TailRing), so the tail is the best
            // available source for failure name extraction.
            if (trxFailures.Count == 0)
            {
                var tailText = tail.Snapshot();
                trxFailures.AddRange(GateLogPaths.ParseDotnetTestFailuresFromText(tailText));
            }

            if (trxFailures.Count > 0)
            {
                var failureBlock = GateLogPaths.FormatFailureSummary(trxFailures);
                if (!string.IsNullOrEmpty(failureBlock))
                {
                    // Prepend before the existing summary so the prompt
                    // block at CrewStoryArtifactStrategy.cs ~5645 shows
                    // names first, compiler/tail text second.
                    var combined = failureBlock + "\n" + summary;
                    summary = combined.Length > ErrorSummaryMaxChars
                        ? combined[..ErrorSummaryMaxChars] + "…(truncated; full log at the chip link)"
                        : combined;
                }
            }
        }

        return new StoryBuildResult(
            Success: proc.ExitCode == 0,
            ExitCode: proc.ExitCode,
            ErrorCount: errorCount,
            WarningCount: warningCount,
            ErrorSummary: summary,
            Duration: sw.Elapsed,
            TimedOut: false,
            LogPath: effectiveLogPath,
            LogRelPath: logRelPath,
            TrxRelPaths: trxRelPaths);
    }

    /// <summary>
    /// Bounded ring buffer over the last N characters of output —
    /// used for the "raw output tail" fallback when the build failed
    /// but the regex parse found no error lines. Memory is capped at
    /// <c>capacity</c> bytes regardless of total output size.
    /// </summary>
    private sealed class TailRing
    {
        private readonly char[] buf;
        private int len;
        private int start;

        public TailRing(int capacity)
        {
            this.buf = new char[capacity];
            this.len = 0;
            this.start = 0;
        }

        public void Append(string line)
        {
            // Append the line + a newline; if total exceeds capacity,
            // advance the start pointer so we keep the most recent
            // capacity chars.
            this.AppendRaw(line);
            this.AppendRaw("\n");
        }

        private void AppendRaw(string s)
        {
            foreach (var ch in s)
            {
                if (this.len < this.buf.Length)
                {
                    this.buf[(this.start + this.len) % this.buf.Length] = ch;
                    this.len++;
                }
                else
                {
                    this.buf[this.start] = ch;
                    this.start = (this.start + 1) % this.buf.Length;
                }
            }
        }

        public string Snapshot()
        {
            if (this.len == 0) return string.Empty;
            var result = new char[this.len];
            for (var i = 0; i < this.len; i++)
            {
                result[i] = this.buf[(this.start + i) % this.buf.Length];
            }
            return new string(result);
        }
    }

    /// <summary>
    /// 2026-05-22 — write a one-shot batch script that runs the supplied
    /// setup commands (e.g. <c>init.cmd</c>, <c>restore</c>) and then the
    /// final build/test command in a SINGLE cmd.exe session, so PATH / env
    /// mutations from the setup steps survive into the build (see the
    /// session block in <see cref="RunBuildAsync"/>). Each setup step is
    /// invoked with <c>call</c> (so a <c>.cmd</c> returns control here with
    /// its env intact) and aborts the chain on a non-zero exit. Written to
    /// the OS temp dir (no worktree pollution); the caller runs it with
    /// <c>WorkingDirectory = workspace</c>, so a relative <c>call init.cmd</c>
    /// resolves against the worktree root and the build's exit code (the
    /// last line) becomes the script's exit code.
    /// </summary>
    private static string WriteSessionScript(string workspace, IReadOnlyList<string> setupCommands, string finalCommand)
    {
        var path = Path.Combine(Path.GetTempPath(), $"forge-gate-{Guid.NewGuid():N}.cmd");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        // Re-enable cwd search for bare command names. When the host
        // process tree has NoDefaultCurrentDirectoryInExePath set (some
        // shells / sandboxes set it), cmd will NOT find `init.cmd` in the
        // current directory by bare name — `'init.cmd' is not recognized`
        // even with the right cwd. Clearing it for this session restores
        // the normal "search cwd first" behaviour so `call init.cmd`
        // resolves the worktree's script regardless of the parent env.
        sb.AppendLine("set \"NoDefaultCurrentDirectoryInExePath=\"");
        // Pin cwd to the worktree explicitly: a relative setup command
        // (`call init.cmd`) resolves against cwd, and we can't depend on
        // the spawning shell inheriting it (the runner sets
        // ProcessStartInfo.WorkingDirectory, but `cd /d` makes the script
        // self-contained regardless of how it's launched).
        sb.AppendLine($"cd /d \"{workspace}\"");
        foreach (var cmd in setupCommands)
        {
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            sb.AppendLine($"call {cmd}");
            sb.AppendLine("if errorlevel 1 exit /b %errorlevel%");
        }
        sb.AppendLine(finalCommand);
        sb.AppendLine("exit /b %errorlevel%");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>
    /// Phase 49 — quote-aware command-line splitter so build commands
    /// containing paths with spaces (e.g. <c>dotnet build "My Solution.sln"</c>
    /// emitted by <see cref="Forge.Api.Services.BuildSystemDetector.Detect"/>'s
    /// <c>QuotePath</c> helper) survive parsing without the quoted token
    /// being shredded into <c>"My</c> and <c>Solution.sln"</c>. Mirrors
    /// <c>CrewQueueWorker.ParseCommandLine</c> intentionally — both classes
    /// need the same semantics and a duplicated 10-line helper is cheaper
    /// than introducing a shared utility namespace.
    /// </summary>
    internal static string[] ParseCommandLine(string command)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if ((ch == ' ' || ch == '\t') && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    /// <summary>
    /// Runs a root-scoped Go gate command (e.g. <c>go test ./...</c> /
    /// <c>go build ./...</c>) once per workspace module and aggregates the
    /// results into a single <see cref="StoryBuildResult"/>. This is the
    /// multi-module (<c>go.work</c>) path: <c>./...</c> doesn't cross module
    /// boundaries, so the gate must descend into each module's directory
    /// (see <see cref="GoWorkspaceGate"/> for the why + module selection).
    ///
    /// <para><paramref name="moduleRelDirs"/> are workspace-relative module
    /// directories (root is <c>"."</c>); <paramref name="scopedCommand"/> is
    /// the command already rewritten to that module's <c>./...</c> via
    /// <see cref="GoWorkspaceGate.ScopeCommandToModule"/>. Each module's
    /// output is streamed into <paramref name="logFilePath"/> under a
    /// <c>==== module &lt;rel&gt; ====</c> header so the combined gate log
    /// reads top-to-bottom. The aggregate is a failure if ANY module fails;
    /// the wall-clock budget is shared across modules (a module that would
    /// start past the budget is not run and the aggregate is marked timed
    /// out).</para>
    /// </summary>
    public static async Task<StoryBuildResult> RunGoWorkspaceAsync(
        string workspace,
        IReadOnlyList<string> moduleRelDirs,
        string scopedCommand,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? logFilePath = null,
        string? logRelPath = null,
        IReadOnlyList<string>? sessionSetupCommands = null,
        bool collectTestFailures = false,
        string? trxScopeTag = null)
    {
        var budget = timeout ?? DefaultTimeout;
        var swAll = Stopwatch.StartNew();
        var combinedSuccess = true;
        var firstNonZeroExit = 0;
        var totalErrors = 0;
        var totalWarnings = 0;
        var anyTimedOut = false;
        var summaries = new List<string>();
        var trx = new List<string>();

        for (var i = 0; i < moduleRelDirs.Count; i++)
        {
            var rel = moduleRelDirs[i];
            string moduleDir;
            try { moduleDir = Path.GetFullPath(Path.Combine(workspace, rel)); }
            catch { moduleDir = workspace; }

            // Header so the combined log shows which module each block is.
            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using var hw = new StreamWriter(
                        new FileStream(logFilePath!, i == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read),
                        System.Text.Encoding.UTF8);
                    hw.WriteLine($"==== module {rel} :: {scopedCommand} ====");
                }
                catch { /* best-effort; the run still proceeds without a header */ }
            }

            var remaining = budget - swAll.Elapsed;
            if (remaining <= TimeSpan.FromSeconds(5))
            {
                anyTimedOut = true;
                combinedSuccess = false;
                summaries.Add($"[module {rel}] skipped — workspace gate wall-clock budget ({budget.TotalMinutes:0.0}min) exhausted before this module ran.");
                if (firstNonZeroExit == 0) firstNonZeroExit = -1;
                continue;
            }

            var r = await RunBuildAsync(
                moduleDir,
                ct,
                remaining,
                buildCommand: scopedCommand,
                logFilePath: logFilePath,
                logRelPath: logRelPath,
                sessionSetupCommands: sessionSetupCommands,
                collectTestFailures: collectTestFailures,
                trxScopeTag: trxScopeTag is null ? null : $"{trxScopeTag}-{i}",
                // Append so every module's output lands in ONE combined log
                // (the i==0 header already truncated the file above).
                appendLog: true);

            totalErrors += r.ErrorCount;
            totalWarnings += r.WarningCount;
            anyTimedOut |= r.TimedOut;
            if (r.TrxRelPaths is { Count: > 0 }) trx.AddRange(r.TrxRelPaths);
            if (!r.Success)
            {
                combinedSuccess = false;
                if (firstNonZeroExit == 0) firstNonZeroExit = r.ExitCode;
                var head = string.IsNullOrWhiteSpace(r.ErrorSummary) ? "(no parsed error lines)" : r.ErrorSummary;
                summaries.Add($"[module {rel}] {head}");
            }
        }

        swAll.Stop();
        var summary = string.Join("\n\n", summaries);
        if (summary.Length > ErrorSummaryMaxChars) summary = summary[..ErrorSummaryMaxChars];

        return new StoryBuildResult(
            Success: combinedSuccess,
            ExitCode: combinedSuccess ? 0 : firstNonZeroExit,
            ErrorCount: totalErrors,
            WarningCount: totalWarnings,
            ErrorSummary: summary,
            Duration: swAll.Elapsed,
            TimedOut: anyTimedOut,
            LogPath: logFilePath,
            LogRelPath: logRelPath,
            TrxRelPaths: trx.Count > 0 ? trx : null);
    }
}

/// <summary>Phase 47e — outcome of a single <see cref="StoryBuildRunner.RunBuildAsync"/> call.</summary>
public sealed record StoryBuildResult(
    bool Success,
    int ExitCode,
    int ErrorCount,
    int WarningCount,
    string ErrorSummary,
    TimeSpan Duration,
    bool TimedOut,
    // Phase 59 Stage B — absolute filesystem path to the persisted
    // log file (combined stdout+stderr, line-streamed). Null when the
    // caller didn't request log persistence or when the file open
    // failed (best-effort).
    string? LogPath = null,
    // Worktree-relative variant of LogPath suitable for the
    // /api/workstreams/{id}/logs/{*relPath} endpoint URL. Caller
    // computes this against its own root convention; the runner just
    // round-trips whatever the caller passed in.
    string? LogRelPath = null,
    // 2026-05-25 — workspace-relative paths to TRX files captured from
    // a test-gate run (collectTestFailures: true). Empty/null when no
    // TRX was found or the run passed. Stable across `dotnet clean` —
    // the files have been copied into {workspace}/.forge/logs/trx/.
    IReadOnlyList<string>? TrxRelPaths = null);
