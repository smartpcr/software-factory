// -----------------------------------------------------------------------
// <copyright file="GitDiffDriver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Diagnostics;
using System.Text;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Wraps <c>git diff --name-status</c> to produce a list of file changes
/// between two commits. Per architecture.md §3.6.
/// Uses <c>git diff --numstat</c> for binary file detection instead of
/// extension-based heuristics, ensuring submodules and binary content
/// are correctly identified via git's own content detection.
/// </summary>
public class GitDiffDriver
{
    private readonly ILogger<GitDiffDriver> logger;

    public GitDiffDriver(ILogger<GitDiffDriver> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Returns the set of file changes between two commits.
    /// Uses NUL-delimited output (<c>-z</c>) for safe path parsing.
    /// Skips binary files (detected via <c>git diff --numstat</c>) and
    /// submodule changes (filtered via <c>--diff-filter=AMDRC</c>).
    /// </summary>
    public virtual async Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default)
    {
        // --no-ext-diff suppresses external diff drivers (submodules, binary diff tools)
        // --diff-filter=AMDRC excludes submodule-only changes (type T/U)
        var nameStatusArgs = $"diff --name-status -z -M --no-ext-diff --diff-filter=AMDRC {fromCommit} {toCommit} --";
        var output = await this.RunGitAsync(repoRoot, nameStatusArgs, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<FileChange>();
        }

        var allChanges = ParseNameStatusOutput(output);

        // Detect binary files using git's own content detection via --numstat.
        // Binary files show as "-\t-\tpath" in numstat output.
        var binaryPaths = await this.DetectBinaryFilesAsync(repoRoot, fromCommit, toCommit, ct).ConfigureAwait(false);

        var filtered = new List<FileChange>(allChanges.Count);
        foreach (var change in allChanges)
        {
            var pathToCheck = NormalizePath(change.Path);
            if (binaryPaths.Contains(pathToCheck))
            {
                this.logger.LogDebug("Skipping binary file (git-detected): {Path}", change.Path);
                continue;
            }

            // For renames, also check old path
            if (change.OldPath is not null && binaryPaths.Contains(NormalizePath(change.OldPath)))
            {
                this.logger.LogDebug("Skipping binary file (git-detected, old path): {Path}", change.OldPath);
                continue;
            }

            filtered.Add(change);
        }

        return filtered;
    }

    /// <summary>
    /// Uses <c>git diff --numstat -z</c> to identify binary files.
    /// Binary files are reported as <c>-\t-\tpath</c> in numstat output.
    /// </summary>
    internal virtual async Task<HashSet<string>> DetectBinaryFilesAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct)
    {
        var binaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var numstatArgs = $"diff --numstat -z -M {fromCommit} {toCommit} --";
            var numstatOutput = await this.RunGitAsync(repoRoot, numstatArgs, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(numstatOutput))
            {
                return binaryPaths;
            }

            // Parse NUL-delimited numstat output.
            // Format for text files: "added\tremoved\0path\0" (or "added\tremoved\0oldpath\0newpath\0" for renames)
            // Format for binary files: "-\t-\0path\0" (or "-\t-\0oldpath\0newpath\0" for renames)
            var parts = numstatOutput.Split('\0', StringSplitOptions.None);
            int i = 0;
            while (i < parts.Length)
            {
                var statsField = parts[i].Trim();
                if (string.IsNullOrEmpty(statsField))
                {
                    i++;
                    continue;
                }

                // Extract added/removed counts
                var tabParts = statsField.Split('\t');
                if (tabParts.Length < 2)
                {
                    i++;
                    continue;
                }

                bool isBinary = tabParts[0] == "-" && tabParts[1] == "-";

                // Check if there's an inline path component after the second tab
                string? inlinePath = tabParts.Length >= 3 ? tabParts[2] : null;

                if (inlinePath is not null)
                {
                    // Non-NUL format: stats and path in same field
                    if (isBinary)
                    {
                        binaryPaths.Add(NormalizePath(inlinePath));
                    }

                    i++;
                    continue;
                }

                // NUL-delimited: path is in the next field(s)
                if (i + 1 < parts.Length)
                {
                    var path = NormalizePath(parts[i + 1]);
                    if (isBinary && !string.IsNullOrWhiteSpace(path))
                    {
                        binaryPaths.Add(path);
                    }

                    i += 2;

                    // Renames have an extra path field
                    if (i < parts.Length && !string.IsNullOrWhiteSpace(parts[i])
                        && !parts[i].Contains('\t'))
                    {
                        if (isBinary)
                        {
                            binaryPaths.Add(NormalizePath(parts[i]));
                        }

                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogDebug(ex, "Failed to detect binary files via --numstat — proceeding without filter");
        }

        return binaryPaths;
    }

    /// <summary>
    /// Parses NUL-delimited <c>git diff --name-status -z</c> output.
    /// Format: STATUS\0path[\0oldpath] repeated.
    /// Rename entries: R{score}\0oldpath\0newpath
    /// </summary>
    internal static IReadOnlyList<FileChange> ParseNameStatusOutput(string output)
    {
        var results = new List<FileChange>();
        var parts = output.Split('\0', StringSplitOptions.None);

        int i = 0;
        while (i < parts.Length)
        {
            var statusField = parts[i].Trim();
            if (string.IsNullOrEmpty(statusField))
            {
                i++;
                continue;
            }

            var statusChar = statusField[0];

            switch (statusChar)
            {
                case 'A':
                    if (i + 1 >= parts.Length) { goto done; }
                    results.Add(new FileChange(FileChangeStatus.Added, NormalizePath(parts[i + 1])));
                    i += 2;
                    break;

                case 'M':
                    if (i + 1 >= parts.Length) { goto done; }
                    results.Add(new FileChange(FileChangeStatus.Modified, NormalizePath(parts[i + 1])));
                    i += 2;
                    break;

                case 'D':
                    if (i + 1 >= parts.Length) { goto done; }
                    results.Add(new FileChange(FileChangeStatus.Deleted, NormalizePath(parts[i + 1])));
                    i += 2;
                    break;

                case 'R':
                case 'C':
                    if (i + 2 >= parts.Length) { goto done; }
                    int.TryParse(statusField.AsSpan(1), out var score);
                    var oldPath = NormalizePath(parts[i + 1]);
                    var newPath = NormalizePath(parts[i + 2]);
                    results.Add(new FileChange(
                        statusChar == 'R' ? FileChangeStatus.Renamed : FileChangeStatus.Added,
                        newPath,
                        OldPath: oldPath,
                        SimilarityScore: score));
                    i += 3;
                    break;

                default:
                    // Unknown status — skip
                    i += 2;
                    break;
            }
        }

        done:
        return results;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }

    private async Task<string> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        this.logger.LogDebug("Running: git {Arguments} in {Dir}", arguments, workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) { stdout.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) { stderr.AppendLine(e.Data); }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            this.logger.LogWarning("git diff failed (exit {Code}): {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"git diff failed with exit code {process.ExitCode}: {error}");
        }

        return stdout.ToString();
    }
}