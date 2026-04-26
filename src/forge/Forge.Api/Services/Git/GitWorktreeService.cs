// -----------------------------------------------------------------------
// <copyright file="GitWorktreeService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default implementation that shells out to the local <c>git</c> binary.
/// Constraints:
///   * <see cref="CreateAsync"/> + <see cref="RemoveAsync"/> reject paths
///     outside <see cref="GitWorktreeOptions.WorktreeRoot"/>.
///   * Read-only methods return empty results when the configured repo
///     path is missing — the API still boots in environments without a
///     git checkout (tests, fresh installs).
/// </summary>
public class GitWorktreeService : IGitWorktreeService
{
    private readonly GitWorktreeOptions options;
    private readonly ILogger<GitWorktreeService> logger;

    public GitWorktreeService(IOptions<GitWorktreeOptions> options, ILogger<GitWorktreeService> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<WorktreeEntry>> ListAsync(CancellationToken ct = default)
    {
        if (!this.RepoExists()) return Array.Empty<WorktreeEntry>();
        var (code, stdout, _) = await this.RunGit(new[] { "worktree", "list", "--porcelain" }, ct);
        if (code != 0) return Array.Empty<WorktreeEntry>();

        var entries = new List<WorktreeEntry>();
        string? path = null, branch = null, head = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("worktree "))
            {
                if (path is not null) entries.Add(new WorktreeEntry(path, branch ?? string.Empty, head ?? string.Empty));
                path = trimmed["worktree ".Length..];
                branch = head = null;
            }
            else if (trimmed.StartsWith("branch "))     branch = trimmed["branch ".Length..];
            else if (trimmed.StartsWith("HEAD "))       head = trimmed["HEAD ".Length..];
            else if (trimmed == "detached")             branch = "(detached)";
        }
        if (path is not null) entries.Add(new WorktreeEntry(path, branch ?? string.Empty, head ?? string.Empty));
        return entries;
    }

    public async Task CreateAsync(string path, string branch, string baseBranch, CancellationToken ct = default)
    {
        this.GuardPathInRoot(path);
        var (code, _, stderr) = await this.RunGit(new[] { "worktree", "add", "-b", branch, path, baseBranch }, ct);
        if (code != 0)
        {
            throw new InvalidOperationException($"git worktree add failed (exit {code}): {stderr}");
        }
    }

    public async Task RemoveAsync(string path, bool force, CancellationToken ct = default)
    {
        this.GuardPathInRoot(path);
        var args = force
            ? new[] { "worktree", "remove", "--force", path }
            : new[] { "worktree", "remove", path };
        var (code, _, stderr) = await this.RunGit(args, ct);
        if (code != 0)
        {
            throw new InvalidOperationException($"git worktree remove failed (exit {code}): {stderr}");
        }
    }

    public async Task<AheadBehind> AheadBehindAsync(string baseBranch, string branch, CancellationToken ct = default)
    {
        if (!this.RepoExists()) return new AheadBehind(0, 0);
        var (code, stdout, _) = await this.RunGit(
            new[] { "rev-list", "--left-right", "--count", $"{baseBranch}...{branch}" }, ct);
        if (code != 0) return new AheadBehind(0, 0);
        // Output is "<behind>\t<ahead>" (left = base, right = branch).
        var parts = stdout.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var behind) || !int.TryParse(parts[1], out var ahead))
        {
            return new AheadBehind(0, 0);
        }
        return new AheadBehind(ahead, behind);
    }

    public async Task<IReadOnlyList<string>> ConflictPathsAsync(string baseBranch, string branch, CancellationToken ct = default)
    {
        if (!this.RepoExists()) return Array.Empty<string>();
        // `git merge-tree --name-only` emits the resulting tree's hash on a
        // clean merge and the conflicting file paths on a conflict. We
        // therefore filter out lines that look like an SHA-1 hash (40 hex
        // chars), which keeps only the real path entries.
        var (code, stdout, _) = await this.RunGit(
            new[] { "merge-tree", "--name-only", "--no-messages", baseBranch, branch }, ct);
        if (code == 0)
        {
            return stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0 && !IsShaLine(l))
                .ToList();
        }

        var (code2, stdout2, _) = await this.RunGit(
            new[] { "diff", "--name-only", "--diff-filter=U", $"{baseBranch}...{branch}" }, ct);
        if (code2 != 0) return Array.Empty<string>();
        return stdout2
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !IsShaLine(l))
            .ToList();
    }

    private static bool IsShaLine(string line)
    {
        if (line.Length != 40) return false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private bool RepoExists() =>
        !string.IsNullOrWhiteSpace(this.options.RepoPath) &&
        Directory.Exists(this.options.RepoPath) &&
        Directory.Exists(Path.Combine(this.options.RepoPath, ".git"));

    private void GuardPathInRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(this.options.WorktreeRoot))
        {
            throw new InvalidOperationException("Forge:Git:WorktreeRoot is not configured");
        }
        var rootFull = Path.GetFullPath(this.options.WorktreeRoot);
        var pathFull = Path.GetFullPath(path);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!pathFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && pathFull != rootFull)
        {
            throw new UnauthorizedAccessException(
                $"Worktree path '{pathFull}' must live under '{rootFull}'");
        }
    }

    private async Task<(int code, string stdout, string stderr)> RunGit(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = this.options.RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuf.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) errBuf.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);
        var stdout = outBuf.ToString();
        var stderr = errBuf.ToString();
        if (proc.ExitCode != 0)
        {
            this.logger.LogDebug(
                "git {Args} exited {Code}: {Stderr}",
                string.Join(' ', args), proc.ExitCode, stderr.Trim());
        }
        return (proc.ExitCode, stdout, stderr);
    }
}
