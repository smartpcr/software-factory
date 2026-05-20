// -----------------------------------------------------------------------
// <copyright file="GateLogPaths.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

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
}
