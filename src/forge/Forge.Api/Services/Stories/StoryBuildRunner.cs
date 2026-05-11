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
    /// </summary>
    public static async Task<StoryBuildResult> RunBuildAsync(
        string workspace,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? buildCommand = null)
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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(effectiveCt);
        var stderrTask = proc.StandardError.ReadToEndAsync(effectiveCt);
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

        string stdout = string.Empty;
        string stderr = string.Empty;
        try { stdout = await stdoutTask; } catch { /* swallow */ }
        try { stderr = await stderrTask; } catch { /* swallow */ }

        sw.Stop();

        if (timedOut)
        {
            return new StoryBuildResult(
                Success: false,
                ExitCode: -1,
                ErrorCount: 0,
                WarningCount: 0,
                ErrorSummary: $"`{program}` build command timed out after {budget.TotalMinutes:0.0}min in '{workspace}'",
                Duration: sw.Elapsed,
                TimedOut: true);
        }

        var combined = stdout + "\n" + stderr;
        var (errorCount, warningCount, sampledErrors) = Parse(combined);
        var summary = string.Join("\n", sampledErrors);
        if (summary.Length > ErrorSummaryMaxChars)
        {
            summary = summary[..ErrorSummaryMaxChars] + "…(truncated)";
        }
        if (string.IsNullOrEmpty(summary) && proc.ExitCode != 0)
        {
            // Build failed but no parseable error lines — surface the tail
            // of the combined stream so the operator has something to look
            // at. The dotnet compiler routes errors to stdout, so the
            // earlier stderr-only tail missed everything for `dotnet build`;
            // cargo fmt --check writes its diff to stdout too. Combined
            // tail is the safe universal default.
            var combinedTail = combined.Length > 1200 ? combined[^1200..] : combined;
            summary = "(no parsed error lines; raw output tail below)\n" + combinedTail.Trim();
        }

        return new StoryBuildResult(
            Success: proc.ExitCode == 0,
            ExitCode: proc.ExitCode,
            ErrorCount: errorCount,
            WarningCount: warningCount,
            ErrorSummary: summary,
            Duration: sw.Elapsed,
            TimedOut: false);
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

    private static (int Errors, int Warnings, IReadOnlyList<string> Samples) Parse(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return (0, 0, Array.Empty<string>());
        }

        var errors = 0;
        var warnings = 0;
        var samples = new List<string>(ErrorSampleLimit);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ErrorLineRegex.IsMatch(line))
            {
                errors++;
                if (samples.Count < ErrorSampleLimit) samples.Add(line.Trim());
            }
            else if (WarningLineRegex.IsMatch(line))
            {
                warnings++;
            }
        }
        return (errors, warnings, samples);
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
    bool TimedOut);
