// -----------------------------------------------------------------------
// <copyright file="RealStoryGitOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Diagnostics;
using System.Text;
using Forge.Api.Persistence;

/// <summary>
/// Real-git implementation of <see cref="IStoryGitOps"/> — drives the user's
/// actual repo at <see cref="RepoRow.CloneRoot"/> via the <c>git</c> CLI.
/// Branches go to <c>origin</c>; file existence / read goes through
/// <c>git show origin/{branch}:{path}</c> so we don't need to keep multiple
/// worktrees mounted.
///
/// <para>
/// Operations on the orchestrator's gate branches (<c>story/.../plan</c>,
/// <c>story/.../qa</c>, <c>ws/.../...</c>) all happen via a per-call
/// dedicated worktree under <c>{cloneRoot}/.worktree/spawn-{slug}/</c> —
/// matches the convention <see cref="Services.CrewQueueWorker"/> uses for
/// live work-item exec, and keeps story commits out of the operator's
/// primary checkout.
/// </para>
/// </summary>
public sealed class RealStoryGitOps : IStoryGitOps
{
    private readonly ILogger<RealStoryGitOps>? logger;

    public RealStoryGitOps(ILogger<RealStoryGitOps>? logger = null)
    {
        this.logger = logger;
    }

    public async Task EnsureBranchAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);

        // Make sure we have the latest base.
        await this.GitAsync(root, ct, "fetch", "origin", baseBranch);

        // Does the branch already exist on origin? If so, make sure local
        // tracking ref is up to date and exit.
        var (lsCode, lsOut, _) = await this.RunGitCaptureAsync(root, ct, "ls-remote", "--heads", "origin", branch);
        if (lsCode == 0 && !string.IsNullOrWhiteSpace(lsOut))
        {
            await this.GitAsync(root, ct, "fetch", "origin", branch);
            return;
        }

        // Doesn't exist remotely — create + push.
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);
        await this.EnsureWorktreeAsync(root, wtPath, branch, baseBranch, ct);
        await this.GitAsync(wtPath, ct, "push", "-u", "origin", branch);
    }

    public async Task<string> CommitFilesAsync(
        RepoRow? repo, string branch, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        var sanitized = SanitizeBranchSlug(branch);
        var wtPath = WorktreePath(root, sanitized);

        await this.EnsureWorktreeAsync(root, wtPath, branch, "main", ct);

        // Materialize the file map under the worktree.
        foreach (var (relPath, content) in files)
        {
            // Forbid escapes — every path must be repo-relative + forward-slash.
            if (Path.IsPathRooted(relPath) || relPath.Contains(".."))
                throw new InvalidOperationException($"Refusing absolute or parent-traversal path '{relPath}' in CommitFilesAsync.");
            var full = Path.Combine(wtPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(full, content, ct);
        }

        await this.GitAsync(wtPath, ct, "add", "-A");

        // Detect "nothing to commit" — git returns non-zero for an empty
        // commit unless --allow-empty is passed; we don't want empty
        // history rows.
        var (statusCode, statusOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "status", "--porcelain");
        if (statusCode == 0 && string.IsNullOrWhiteSpace(statusOut))
        {
            this.logger?.LogInformation("CommitFilesAsync: no changes to commit on branch {Branch}", branch);
        }
        else
        {
            await this.GitAsync(wtPath, ct, "-c", "user.email=forge-architect@local", "-c", "user.name=Forge Architect", "commit", "-m", message);
        }

        await this.GitAsync(wtPath, ct, "push", "origin", branch);

        // Return the head sha of the branch.
        var (shaCode, shaOut, _) = await this.RunGitCaptureAsync(wtPath, ct, "rev-parse", "HEAD");
        return shaCode == 0 ? shaOut.Trim() : string.Empty;
    }

    public async Task<bool> FileExistsAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        await this.RunGitCaptureAsync(root, ct, "fetch", "origin", branch);
        var (code, _, _) = await this.RunGitCaptureAsync(root, ct, "cat-file", "-e", $"origin/{branch}:{filePath}");
        return code == 0;
    }

    public async Task<string?> ReadFileAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);
        await this.RunGitCaptureAsync(root, ct, "fetch", "origin", branch);
        var (code, stdout, _) = await this.RunGitCaptureAsync(root, ct, "show", $"origin/{branch}:{filePath}");
        return code == 0 ? stdout : null;
    }

    public async Task MergeAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var root = ResolveCloneRoot(repo);

        // Merge happens in a dedicated worktree on the base branch so we
        // don't disturb whatever the operator has checked out at the root.
        var sanitized = SanitizeBranchSlug($"merge-{baseBranch}");
        var wtPath = WorktreePath(root, sanitized);
        await this.EnsureWorktreeAsync(root, wtPath, baseBranch, baseBranch, ct);

        await this.GitAsync(wtPath, ct, "fetch", "origin", baseBranch);
        await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
        await this.GitAsync(wtPath, ct, "reset", "--hard", $"origin/{baseBranch}");

        // No-ff merge so the merge commit shows up in history; matches what
        // GitHub's "Merge pull request" button does.
        await this.GitAsync(wtPath, ct,
            "-c", "user.email=forge-merger@local",
            "-c", "user.name=Forge",
            "merge", "--no-ff", "-m", $"Merge {branch} into {baseBranch}", $"origin/{branch}");
        await this.GitAsync(wtPath, ct, "push", "origin", baseBranch);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ResolveCloneRoot(RepoRow? repo)
    {
        if (repo is null || string.IsNullOrEmpty(repo.CloneRoot))
            throw new InvalidOperationException("RepoRow.CloneRoot is required for git ops. Configure the repo first.");
        if (!Directory.Exists(repo.CloneRoot))
            throw new InvalidOperationException($"CloneRoot '{repo.CloneRoot}' does not exist on disk. Clone the repo first.");
        return repo.CloneRoot;
    }

    private static string WorktreePath(string cloneRoot, string slug)
    {
        var wtRoot = Path.Combine(cloneRoot, ".worktree");
        Directory.CreateDirectory(wtRoot);
        return Path.Combine(wtRoot, $"spawn-{slug}");
    }

    private static string SanitizeBranchSlug(string branch)
    {
        var sb = new StringBuilder(branch.Length);
        foreach (var c in branch)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return sb.ToString().Trim('-');
    }

    private async Task EnsureWorktreeAsync(string cloneRoot, string wtPath, string branch, string baseBranch, CancellationToken ct)
    {
        // Step 1: prune stale worktree records first so a previous abort
        // doesn't leave a registered-but-missing entry that confuses us.
        await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");

        // Step 2: registered + on-disk → reuse. Normalize both sides because
        // `git worktree list` reports forward-slash paths even on Windows
        // while wtPath is backslash-separated.
        var (listCode, listOut, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "list", "--porcelain");
        var wtPathNormalized = wtPath.Replace('\\', '/');
        var listOutNormalized = listOut.Replace('\\', '/');
        if (listCode == 0 && listOutNormalized.Contains(wtPathNormalized, StringComparison.OrdinalIgnoreCase) && Directory.Exists(wtPath))
        {
            await this.GitAsync(wtPath, ct, "fetch", "origin", branch);
            var (code, _, _) = await this.RunGitCaptureAsync(wtPath, ct, "checkout", branch);
            if (code != 0)
            {
                await this.GitAsync(wtPath, ct, "checkout", "-B", branch, $"origin/{branch}");
            }
            return;
        }

        // Step 3: directory exists on disk but isn't a registered worktree
        // (orphaned from a prior aborted run). Force-remove via git first
        // so internal refs are cleaned, then nuke the directory.
        if (Directory.Exists(wtPath))
        {
            this.logger?.LogInformation("Removing orphaned worktree directory at {Path}", wtPath);
            await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "remove", "--force", wtPath);
            if (Directory.Exists(wtPath))
            {
                try { Directory.Delete(wtPath, recursive: true); }
                catch (IOException ex)
                {
                    this.logger?.LogWarning(ex, "Could not delete orphaned worktree dir {Path}", wtPath);
                }
            }
            await this.RunGitCaptureAsync(cloneRoot, ct, "worktree", "prune");
        }

        // Step 4: now add the worktree fresh.
        var (existsCode, existsOut, _) = await this.RunGitCaptureAsync(cloneRoot, ct, "ls-remote", "--heads", "origin", branch);
        if (existsCode == 0 && !string.IsNullOrWhiteSpace(existsOut))
        {
            // Branch already on origin → check it out into the worktree.
            await this.GitAsync(cloneRoot, ct, "fetch", "origin", branch);
            await this.GitAsync(cloneRoot, ct, "worktree", "add", wtPath, $"origin/{branch}");
            await this.GitAsync(wtPath, ct, "checkout", "-B", branch, $"origin/{branch}");
        }
        else
        {
            // New branch off baseBranch.
            await this.GitAsync(cloneRoot, ct, "fetch", "origin", baseBranch);
            await this.GitAsync(cloneRoot, ct, "worktree", "add", "-b", branch, wtPath, $"origin/{baseBranch}");
        }
    }

    /// <summary>Run git, throwing on non-zero exit. Use <see cref="RunGitCaptureAsync"/> when you want to inspect the result.</summary>
    private async Task GitAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var (code, stdout, stderr) = await this.RunGitCaptureAsync(cwd, ct, args);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {code}) in '{cwd}'.\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunGitCaptureAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started — is it on PATH?");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        this.logger?.LogDebug("git {Args} → exit={Exit} cwd={Cwd}", string.Join(' ', args), p.ExitCode, cwd);
        return (p.ExitCode, stdout, stderr);
    }
}
