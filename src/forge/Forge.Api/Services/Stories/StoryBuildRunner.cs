// -----------------------------------------------------------------------
// <copyright file="StoryBuildRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Diagnostics;
using System.Text.RegularExpressions;

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
        string? logRelPath = null)
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

        // Phase 49 — convert process-start failures (program not on
        // PATH, .NET SDK missing for a `dotnet build`, cargo absent
        // for a Rust workspace, etc.) into a StoryBuildResult instead
        // of letting Win32Exception propagate. A propagating exception
        // would hit StorySpawnOrchestrator's generic catch and route
        // the workstream into the retry loop instead of the BuildFailed
        // → blocked path, masking the real problem (missing toolchain)
        // behind retry backoff noise.
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
                    new FileStream(logFilePath!, FileMode.Create, FileAccess.Write, FileShare.Read),
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

        return new StoryBuildResult(
            Success: proc.ExitCode == 0,
            ExitCode: proc.ExitCode,
            ErrorCount: errorCount,
            WarningCount: warningCount,
            ErrorSummary: summary,
            Duration: sw.Elapsed,
            TimedOut: false,
            LogPath: effectiveLogPath,
            LogRelPath: logRelPath);
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
    string? LogRelPath = null);
