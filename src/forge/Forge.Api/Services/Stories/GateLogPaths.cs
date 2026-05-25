// -----------------------------------------------------------------------
// <copyright file="GateLogPaths.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Phase 59 Stage B — single source of truth for the per-attempt gate
/// log path convention. Kept tiny + dependency-free so both the gate
/// runner (<c>StoryBuildRunner</c>) and the log-streaming endpoint
/// (<c>WorkstreamsEndpoints.GetLog</c>) agree byte-for-byte on the
/// file layout.
///
/// <para>Layout: <c>{worktreePath}/.forge/logs/{gate}-{iter}-{utcTicks}.log</c>
/// where <c>gate ∈ {build, quality, test}</c>. The directory is
/// auto-created by <see cref="StoryBuildRunner.RunBuildAsync"/> on
/// first write. The matching URL the UI consumes is
/// <c>/api/workstreams/{wsId}/logs/{relPath}</c> where <c>relPath</c>
/// is the path relative to <c>{worktreePath}/.forge/logs/</c>.</para>
/// </summary>
public static class GateLogPaths
{
    /// <summary>Relative dir under each worktree where Forge persists gate logs.</summary>
    public const string LogsRelDir = ".forge/logs";

    /// <summary>Per-gate retention cap — older files are pruned after each successful gate.</summary>
    public const int RetentionPerGate = 20;

    /// <summary>
    /// Build the absolute log-file path for a gate attempt. Returns
    /// null when <paramref name="worktreePath"/> is null/empty (the
    /// caller falls through to in-memory-only parsing).
    /// </summary>
    public static (string? LogPath, string? LogRelPath) For(
        string? worktreePath, string gateName, int iter)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return (null, null);
        if (string.IsNullOrWhiteSpace(gateName)) return (null, null);

        var safeGate = SanitiseGate(gateName);
        var filename = $"{safeGate}-{iter}-{DateTime.UtcNow.Ticks}.log";
        var dir = Path.Combine(worktreePath, ".forge", "logs");
        return (
            LogPath: Path.Combine(dir, filename),
            LogRelPath: filename);
    }

    /// <summary>
    /// Absolute path to the per-worktree logs directory. Used by the
    /// retention pruner + the streaming endpoint's path-traversal
    /// guard.
    /// </summary>
    public static string DirectoryFor(string worktreePath) =>
        Path.Combine(worktreePath, ".forge", "logs");

    /// <summary>
    /// Phase 59.9 — prune <c>.forge/logs/</c> to the newest
    /// <see cref="RetentionPerGate"/> files per gate kind. Older
    /// files (matching the same gate prefix) are deleted on disk;
    /// IO errors are swallowed (retention is best-effort, never
    /// blocks the gate). Returns the number of files deleted across
    /// all gate kinds.
    /// </summary>
    public static int PruneToRetention(string worktreePath)
    {
        var dir = DirectoryFor(worktreePath);
        if (!Directory.Exists(dir)) return 0;

        var deleted = 0;
        foreach (var gateKind in KnownGateKinds)
        {
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(dir)
                    .EnumerateFiles($"{gateKind}-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
            }
            catch
            {
                continue;
            }
            for (var i = RetentionPerGate; i < files.Length; i++)
            {
                try { files[i].Delete(); deleted++; }
                catch { /* best-effort */ }
            }
        }
        return deleted;
    }

    /// <summary>
    /// Phase 59.9 — full directory prune; called when a workstream
    /// reaches a terminal state (complete / cancelled) or on Restart
    /// so an operator's pending retries always start clean. Best-
    /// effort: swallow IO errors so retention noise never bubbles
    /// into the orchestrator.
    /// </summary>
    public static bool PruneAll(string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return false;
        var dir = DirectoryFor(worktreePath!);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Path-traversal guard — refuse a caller-supplied relPath that
    /// resolves outside <c>{worktreePath}/.forge/logs/</c>. Used by
    /// the streaming endpoint to defuse <c>../../secret.txt</c>
    /// attempts.
    /// </summary>
    public static bool IsSafeRelPath(string worktreePath, string relPath, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(worktreePath) || string.IsNullOrWhiteSpace(relPath))
        {
            return false;
        }
        var dir = Path.GetFullPath(DirectoryFor(worktreePath)) + Path.DirectorySeparatorChar;
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(dir, relPath));
        }
        catch
        {
            return false;
        }
        if (!candidate.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        absolutePath = candidate;
        return true;
    }

    private static string SanitiseGate(string gate)
    {
        // gate flows from RunGateChainAsync as a literal "build" /
        // "quality" / "test" today. Defensively clamp to alnum +
        // dash + underscore so a future caller can't inject path
        // separators.
        var span = gate.AsSpan();
        var buf = new char[span.Length];
        var len = 0;
        foreach (var ch in span)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                buf[len++] = char.ToLowerInvariant(ch);
            }
        }
        return len == 0 ? "gate" : new string(buf, 0, len);
    }

    /// <summary>
    /// Closed set of gate-kind tokens the runner emits. Anything
    /// else is sanitised through <see cref="SanitiseGate"/> and won't
    /// be touched by retention (extra paranoia: an unknown gate
    /// keeps its log forever until a workstream-level prune sweeps
    /// the directory).
    /// </summary>
    private static readonly IReadOnlyList<string> KnownGateKinds = new[] { "build", "quality", "test" };

    // -------------------------------------------------------------------------
    // 2026-05-25 — TRX capture + per-run log helpers (used by both
    // BuildValidationWorker repo-validate runs and StoryBuildRunner
    // iteration gates). Kept in this file so the directory layout
    // convention stays a single source of truth.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-validate-run log path bundle. <see cref="LogPath"/> is the absolute
    /// combined stdout+stderr log; <see cref="LogRelPath"/> is repo-relative
    /// (forward-slash) for SignalR/UI URLs; <see cref="TrxDir"/> is the
    /// absolute directory where TRX files captured from the test step are
    /// copied. All three live under <c>{repoCloneRoot}/.forge/logs/</c>.
    /// </summary>
    public sealed record ValidateRunLogPaths(string LogPath, string LogRelPath, string TrxDir, string TrxRelDir);

    /// <summary>
    /// Resolve the per-run log + TRX paths for a <c>BuildValidationWorker</c>
    /// run. The run id is sliced to its first 12 chars so the on-disk name is
    /// stable but readable. Caller is responsible for creating the directories
    /// (they're created lazily by the streaming writer / TRX copier).
    /// </summary>
    public static ValidateRunLogPaths ForValidateRun(string repoCloneRoot, string runId)
    {
        if (string.IsNullOrWhiteSpace(repoCloneRoot)) throw new ArgumentException("repoCloneRoot required", nameof(repoCloneRoot));
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("runId required", nameof(runId));
        var safeId = SanitiseRunId(runId);
        var logFile = $"validate-{safeId}.log";
        var trxRel = $"trx/validate-{safeId}";
        return new ValidateRunLogPaths(
            LogPath: Path.Combine(repoCloneRoot, ".forge", "logs", logFile),
            LogRelPath: logFile,
            TrxDir: Path.Combine(repoCloneRoot, ".forge", "logs", "trx", $"validate-{safeId}"),
            TrxRelDir: trxRel);
    }

    /// <summary>
    /// Per-iteration TRX directory under a worktree. Used by
    /// <c>StoryBuildRunner</c> when <c>collectTestFailures: true</c>. The
    /// ticks suffix means a re-run of the same iter doesn't collide with
    /// the previous attempt's copied TRX.
    /// </summary>
    public static (string TrxDir, string TrxRelDir) ForGateTrx(string worktreePath, string gate, int iter, long ticks)
    {
        var safeGate = SanitiseGate(gate);
        var rel = $"trx/{safeGate}-{iter}-{ticks}";
        return (
            Path.Combine(worktreePath, ".forge", "logs", "trx", $"{safeGate}-{iter}-{ticks}"),
            rel);
    }

    /// <summary>
    /// Idempotent .gitignore mutation: ensure every supplied line appears
    /// (trim-compared) in <c>{repoCloneRoot}/.gitignore</c>. Creates the file
    /// if missing. Best-effort: I/O failures are swallowed (returns false).
    /// Replaces the inline block in <c>CrewQueueWorker</c> so the validate
    /// path (which has no per-workstream init step) gets the same treatment.
    /// </summary>
    public static async Task<bool> EnsureGitignoreLinesAsync(
        string repoCloneRoot, IReadOnlyList<string> linesToEnsure, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoCloneRoot)) return false;
        if (linesToEnsure is null || linesToEnsure.Count == 0) return false;
        if (!Directory.Exists(repoCloneRoot)) return false;

        try
        {
            var gitignorePath = Path.Combine(repoCloneRoot, ".gitignore");
            var existing = File.Exists(gitignorePath)
                ? await File.ReadAllLinesAsync(gitignorePath, ct).ConfigureAwait(false)
                : Array.Empty<string>();
            var seen = new HashSet<string>(existing.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
            var toAppend = new List<string>();
            foreach (var raw in linesToEnsure)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var trimmed = raw.Trim();
                // Treat `name/` and `name` as equivalent for de-dup purposes
                // so we never double-add a trailing-slash variant.
                var stripped = trimmed.TrimEnd('/');
                if (seen.Contains(trimmed) || seen.Contains(stripped) || seen.Contains(stripped + "/"))
                    continue;
                toAppend.Add(trimmed);
                seen.Add(trimmed);
            }
            if (toAppend.Count == 0) return true;
            await File.AppendAllTextAsync(
                gitignorePath,
                "\n" + string.Join("\n", toAppend) + "\n",
                ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Convenience: ensure <c>.forge/</c> alone is gitignored.</summary>
    public static Task<bool> EnsureForgeGitignoredAsync(string repoCloneRoot, CancellationToken ct = default) =>
        EnsureGitignoreLinesAsync(repoCloneRoot, new[] { ".forge/" }, ct);

    /// <summary>
    /// Recursively find <c>*.trx</c> files under <paramref name="searchRoot"/>
    /// whose <see cref="FileInfo.LastWriteTimeUtc"/> is at or after
    /// <paramref name="sinceUtc"/>. Skips any directory whose name appears in
    /// <paramref name="excludeDirNames"/> (case-insensitive) at any depth —
    /// this is critical for repo-level validate runs which must NOT pick up
    /// TRX files written by concurrent iteration-gate runs under <c>.worktree/</c>,
    /// and must NOT re-pick-up TRX already copied into <c>.forge/logs/trx/</c>.
    /// Best-effort: enumeration errors on individual subtrees are swallowed.
    /// </summary>
    public static IReadOnlyList<string> CollectNewTrxFiles(
        string searchRoot,
        DateTime sinceUtc,
        IReadOnlyCollection<string>? excludeDirNames = null)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
        {
            return results;
        }
        var excluded = excludeDirNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(excludeDirNames, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(searchRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.trx", SearchOption.TopDirectoryOnly); }
            catch { files = Array.Empty<string>(); }
            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc >= sinceUtc) results.Add(file);
                }
                catch { /* skip unreadable file */ }
            }
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (excluded.Contains(name)) continue;
                stack.Push(sub);
            }
        }
        return results;
    }

    /// <summary>
    /// Failed-test row extracted from a TRX file (vstest schema). All fields
    /// except <see cref="TestName"/> are best-effort: malformed XML or
    /// non-standard loggers may leave them empty.
    /// </summary>
    public sealed record TrxFailure(
        string TestName,
        string Outcome,
        double DurationSec,
        string? Message,
        string? StackTrace);

    /// <summary>
    /// Parse failed <c>UnitTestResult</c> nodes from a VSTest-schema TRX file.
    /// Handles the standard <c>http://microsoft.com/schemas/VisualStudio/TeamTest/2010</c>
    /// namespace + namespace-less variants emitted by older loggers. Returns
    /// an empty list on parse failure (best-effort: never throws). Each
    /// failure's <see cref="TrxFailure.Message"/> is the verbatim
    /// <c>ErrorInfo/Message</c> contents (caller is responsible for any
    /// truncation before prompt-injection).
    /// </summary>
    public static IReadOnlyList<TrxFailure> ParseTrxFailures(string trxPath)
    {
        var results = new List<TrxFailure>();
        if (string.IsNullOrWhiteSpace(trxPath) || !File.Exists(trxPath)) return results;
        XDocument doc;
        try { doc = XDocument.Load(trxPath, LoadOptions.None); }
        catch { return results; }

        // Try the standard namespaced lookup first; fall back to anonymous
        // when an older logger emits namespace-less XML. We compare local
        // names so both shapes resolve through the same code path.
        var failedResults = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
            .Where(e =>
            {
                var outcome = e.Attribute("outcome")?.Value;
                return string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase);
            });
        foreach (var r in failedResults)
        {
            var testName = r.Attribute("testName")?.Value ?? r.Attribute("testId")?.Value ?? "(unnamed test)";
            var outcome = r.Attribute("outcome")?.Value ?? "Failed";
            var durationStr = r.Attribute("duration")?.Value;
            var durationSec = ParseTrxDuration(durationStr);
            var output = r.Elements().FirstOrDefault(c => string.Equals(c.Name.LocalName, "Output", StringComparison.Ordinal));
            var errorInfo = output?.Elements().FirstOrDefault(c => string.Equals(c.Name.LocalName, "ErrorInfo", StringComparison.Ordinal));
            var message = errorInfo?.Elements().FirstOrDefault(c => string.Equals(c.Name.LocalName, "Message", StringComparison.Ordinal))?.Value;
            var stack = errorInfo?.Elements().FirstOrDefault(c => string.Equals(c.Name.LocalName, "StackTrace", StringComparison.Ordinal))?.Value;
            results.Add(new TrxFailure(testName, outcome, durationSec, message, stack));
        }
        return results;
    }

    private static double ParseTrxDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return 0d;
        if (TimeSpan.TryParse(duration, out var ts)) return ts.TotalSeconds;
        return 0d;
    }

    /// <summary>
    /// Regex-based fallback that scrapes <c>dotnet test</c> stdout for
    /// <c>  Failed FullyQualifiedTestName</c> lines (with optional duration
    /// suffix). Used when no TRX file was produced (some repos' <c>build.proj</c>
    /// drivers don't emit TRX by default — the <c>update</c> repo is a known
    /// example). Captured names are deduped and capped at 50 entries to bound
    /// prompt size; the trailing message body, if any, is taken from the next
    /// <c>Error Message:</c> line for actionable context.
    /// </summary>
    public static IReadOnlyList<TrxFailure> ParseDotnetTestFailuresFromText(string text)
    {
        var results = new List<TrxFailure>();
        if (string.IsNullOrWhiteSpace(text)) return results;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length && results.Count < 50; i++)
        {
            var m = DotnetTestFailedLine.Match(lines[i]);
            if (!m.Success) continue;
            var name = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            string? message = null;
            // Look ahead a few lines for "Error Message:" then capture the
            // following non-empty line as the message body.
            for (var j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
            {
                if (lines[j].TrimStart().StartsWith("Error Message:", StringComparison.OrdinalIgnoreCase))
                {
                    for (var k = j + 1; k < Math.Min(j + 4, lines.Length); k++)
                    {
                        var candidate = lines[k].Trim();
                        if (!string.IsNullOrEmpty(candidate)) { message = candidate; break; }
                    }
                    break;
                }
            }
            results.Add(new TrxFailure(name, "Failed", 0d, message, null));
        }
        return results;
    }

    // ^ optional indent, the word "Failed", whitespace, then a dotted
    // identifier (allow generics + nested types) and an optional duration
    // bracket. Matches:
    //   "  Failed Foo.Bar.Baz"
    //   "  Failed Foo.Bar.Baz [Duration: 5s]"
    //   "Failed Foo.Bar.Baz [5 s]"
    // and ignores the summary line "Failed!  - Failed: 4, Passed: ..." (no
    // identifier capture there, since "!" or ":" follow).
    private static readonly Regex DotnetTestFailedLine = new(
        @"^\s*Failed\s+([A-Za-z_][\w\.<>+`,]*?)(?:\s*\[[^\]]+\])?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Render a failed-test list as a compact prompt-friendly block. Caller
    /// is responsible for capping the final length; this method caps each
    /// message line at 240 chars so a single ugly assertion message can't
    /// blow the budget on its own.
    /// </summary>
    public static string FormatFailureSummary(IReadOnlyList<TrxFailure> failures, int maxFailures = 20)
    {
        if (failures is null || failures.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append("FAILED TESTS (").Append(Math.Min(failures.Count, maxFailures));
        if (failures.Count > maxFailures) sb.Append(" of ").Append(failures.Count);
        sb.Append("):\n");
        for (var i = 0; i < failures.Count && i < maxFailures; i++)
        {
            var f = failures[i];
            sb.Append("  • ").Append(f.TestName);
            if (!string.IsNullOrWhiteSpace(f.Message))
            {
                var msg = f.Message!.Replace("\r", " ").Replace("\n", " ").Trim();
                if (msg.Length > 240) msg = msg[..240] + "…";
                sb.Append("\n      ").Append(msg);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string SanitiseRunId(string runId)
    {
        var span = runId.AsSpan();
        var buf = new char[Math.Min(span.Length, 12)];
        var len = 0;
        foreach (var ch in span)
        {
            if (len >= buf.Length) break;
            if (char.IsLetterOrDigit(ch) || ch == '-') buf[len++] = char.ToLowerInvariant(ch);
        }
        return len == 0 ? "run" : new string(buf, 0, len);
    }
}
