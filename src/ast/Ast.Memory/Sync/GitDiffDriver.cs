// -----------------------------------------------------------------------
// <copyright file="GitDiffDriver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The kind of change detected by <c>git diff --name-status</c>.
/// </summary>
public enum FileChangeKind
{
    /// <summary>File was added.</summary>
    Added,

    /// <summary>File was modified.</summary>
    Modified,

    /// <summary>File was deleted.</summary>
    Deleted,

    /// <summary>File was renamed (old path → new path).</summary>
    Renamed,

    /// <summary>File was copied.</summary>
    Copied
}

/// <summary>
/// Represents a single changed file between two commits.
/// For renames, <see cref="OldPath"/> contains the previous path.
/// </summary>
public sealed class FileChange
{
    /// <summary>Gets the kind of change.</summary>
    public required FileChangeKind Kind { get; init; }

    /// <summary>Gets the relative file path (destination for renames).</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the original file path before a rename or copy.
    /// Null for non-rename/non-copy changes.
    /// </summary>
    public string? OldPath { get; init; }

    /// <summary>
    /// Gets the similarity percentage for renames/copies (0–100).
    /// Null for non-rename/non-copy changes.
    /// </summary>
    public int? SimilarityPercent { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        this.Kind == FileChangeKind.Renamed
            ? $"{this.Kind}: {this.OldPath} → {this.Path} ({this.SimilarityPercent}%)"
            : $"{this.Kind}: {this.Path}";
}

/// <summary>
/// Wraps <c>git diff --name-status -M</c> to produce a list of changed files
/// between two commits. Skips binary files and submodule changes.
/// Per architecture.md §3.6.
/// </summary>
public class GitDiffDriver
{
    private readonly string repoRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitDiffDriver"/> class.
    /// </summary>
    /// <param name="repoRoot">Absolute path to the repository root.</param>
    public GitDiffDriver(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new ArgumentException("Repository root path must not be empty.", nameof(repoRoot));
        }

        this.repoRoot = repoRoot;
    }

    /// <summary>
    /// Gets the list of files changed between two commits.
    /// Uses <c>git diff --name-status -M -z</c> for rename detection.
    /// Binary files, submodule changes, and <c>.gitmodules</c> are excluded.
    /// The <c>-z</c> flag uses NUL-delimited output to avoid git's path quoting
    /// which can mangle paths with special characters (spaces, unicode, etc.).
    /// </summary>
    /// <param name="fromCommit">The base commit SHA or ref.</param>
    /// <param name="toCommit">The target commit SHA or ref.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="FileChange"/> describing each changed file.</returns>
    public async Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string fromCommit,
        string toCommit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromCommit))
        {
            throw new ArgumentException("fromCommit must not be empty.", nameof(fromCommit));
        }

        if (string.IsNullOrWhiteSpace(toCommit))
        {
            throw new ArgumentException("toCommit must not be empty.", nameof(toCommit));
        }

        var diffOutput = await this.RunGitAsync(
            $"diff --name-status -M -z --no-ext-diff --diff-filter=AMDRC {fromCommit}..{toCommit}",
            ct).ConfigureAwait(false);

        var binaryFiles = await this.GetBinaryFilesAsync(fromCommit, toCommit, ct).ConfigureAwait(false);
        var submodulePaths = await this.GetSubmodulePathsFromDiffAsync(fromCommit, toCommit, ct).ConfigureAwait(false);

        return ParseNulDelimitedNameStatus(diffOutput, binaryFiles, submodulePaths);
    }

    /// <summary>
    /// Synchronous convenience overload for <see cref="GetChangedFilesAsync"/>.
    /// </summary>
    public IReadOnlyList<FileChange> GetChangedFiles(string fromCommit, string toCommit) =>
        this.GetChangedFilesAsync(fromCommit, toCommit, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Identifies binary files in a diff range using <c>git diff --numstat</c>.
    /// Binary files show up as <c>-\t-\tpath</c>.
    /// </summary>
    internal async Task<HashSet<string>> GetBinaryFilesAsync(
        string fromCommit,
        string toCommit,
        CancellationToken ct)
    {
        var output = await this.RunGitAsync(
            $"diff --numstat -M --no-ext-diff {fromCommit}..{toCommit}",
            ct).ConfigureAwait(false);

        return ParseNumstatForBinaries(output);
    }

    /// <summary>
    /// Parses <c>git diff --numstat</c> output to find binary file paths.
    /// </summary>
    internal static HashSet<string> ParseNumstatForBinaries(string output)
    {
        var binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-\t-\t", StringComparison.Ordinal))
            {
                var path = trimmed.Substring(4);

                if (path.Contains(" => "))
                {
                    var parts = ParseRenameNumstatPath(path);
                    binaries.Add(parts.newPath);
                    binaries.Add(parts.oldPath);
                }
                else
                {
                    binaries.Add(path);
                }
            }
        }

        return binaries;
    }

    /// <summary>
    /// Identifies submodule paths by inspecting <c>git diff --raw</c> output
    /// for entries with mode 160000 (gitlink). This correctly detects submodules
    /// even when they have been deleted, renamed, or exist only in historical commits
    /// (unlike parsing .gitmodules which only reflects current state).
    /// </summary>
    internal async Task<HashSet<string>> GetSubmodulePathsFromDiffAsync(
        string fromCommit,
        string toCommit,
        CancellationToken ct)
    {
        var output = await this.RunGitAsync(
            $"diff --raw -M {fromCommit}..{toCommit}",
            ct).ConfigureAwait(false);

        return ParseRawDiffForSubmodules(output);
    }

    /// <summary>
    /// Parses <c>git diff --raw</c> output to extract paths with mode 160000 (gitlink/submodule).
    /// Raw diff format: <c>:old_mode new_mode old_sha new_sha status\tpath</c>
    /// or for renames: <c>:old_mode new_mode old_sha new_sha statusNNN\told_path\tnew_path</c>.
    /// Mode 160000 indicates a gitlink (submodule entry).
    /// </summary>
    internal static HashSet<string> ParseRawDiffForSubmodules(string rawOutput)
    {
        var submodules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(':'))
            {
                continue;
            }

            // Format: ":old_mode new_mode old_hash new_hash status\tpath[\tnew_path]"
            // Example: ":160000 160000 abc123 def456 M\tsubmodule_dir"
            // Example: ":000000 160000 0000000 abc123 A\tsubmodule_dir"
            // Example: ":160000 000000 abc123 0000000 D\tsubmodule_dir"
            var parts = trimmed.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6)
            {
                continue;
            }

            var oldMode = parts[0];
            var newMode = parts[1];

            // If either mode is 160000, this is a submodule entry
            if (oldMode == "160000" || newMode == "160000")
            {
                // The path(s) start at index 5
                submodules.Add(parts[5]);
                if (parts.Length > 6)
                {
                    submodules.Add(parts[6]);
                }
            }
        }

        return submodules;
    }

    /// <summary>
    /// Parses NUL-delimited output of <c>git diff --name-status -M -z</c>.
    /// With <c>-z</c>, fields are separated by NUL (\0) instead of tabs/newlines,
    /// which avoids quoting issues with special characters in file paths.
    /// Format: STATUS\0path\0 for simple, STATUS\0oldpath\0newpath\0 for renames/copies.
    /// </summary>
    internal static IReadOnlyList<FileChange> ParseNulDelimitedNameStatus(
        string output,
        HashSet<string>? binaryFiles = null,
        HashSet<string>? submodulePaths = null)
    {
        binaryFiles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        submodulePaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var changes = new List<FileChange>();
        var parts = output.Split('\0');

        var i = 0;
        while (i < parts.Length)
        {
            var status = parts[i].Trim();
            if (string.IsNullOrEmpty(status))
            {
                i++;
                continue;
            }

            var statusChar = status[0];
            FileChange? change = null;

            switch (statusChar)
            {
                case 'A' when i + 1 < parts.Length:
                    change = new FileChange { Kind = FileChangeKind.Added, Path = parts[i + 1] };
                    i += 2;
                    break;

                case 'M' when i + 1 < parts.Length:
                    change = new FileChange { Kind = FileChangeKind.Modified, Path = parts[i + 1] };
                    i += 2;
                    break;

                case 'D' when i + 1 < parts.Length:
                    change = new FileChange { Kind = FileChangeKind.Deleted, Path = parts[i + 1] };
                    i += 2;
                    break;

                case 'R' when i + 2 < parts.Length:
                    change = new FileChange
                    {
                        Kind = FileChangeKind.Renamed,
                        OldPath = parts[i + 1],
                        Path = parts[i + 2],
                        SimilarityPercent = ParseSimilarityPercent(status)
                    };
                    i += 3;
                    break;

                case 'C' when i + 2 < parts.Length:
                    change = new FileChange
                    {
                        Kind = FileChangeKind.Copied,
                        OldPath = parts[i + 1],
                        Path = parts[i + 2],
                        SimilarityPercent = ParseSimilarityPercent(status)
                    };
                    i += 3;
                    break;

                default:
                    i++;
                    continue;
            }

            if (change == null || ShouldSkip(change, binaryFiles, submodulePaths))
            {
                continue;
            }

            changes.Add(change);
        }

        return changes;
    }

    /// <summary>
    /// Parses the output of <c>git diff --name-status -M</c> (tab/newline delimited).
    /// Retained for unit tests that use synthetic tab-delimited output.
    /// </summary>
    internal static IReadOnlyList<FileChange> ParseNameStatusOutput(
        string output,
        HashSet<string>? binaryFiles = null,
        HashSet<string>? submodulePaths = null)
    {
        binaryFiles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        submodulePaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var changes = new List<FileChange>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var change = ParseStatusLine(trimmed);
            if (change == null)
            {
                continue;
            }

            if (ShouldSkip(change, binaryFiles, submodulePaths))
            {
                continue;
            }

            changes.Add(change);
        }

        return changes;
    }

    /// <summary>
    /// Determines whether a file change should be excluded from results.
    /// Excludes binary files, submodule entries, and <c>.gitmodules</c>
    /// (which appears as a side-effect of submodule add/remove operations).
    /// </summary>
    private static bool ShouldSkip(
        FileChange change,
        HashSet<string> binaryFiles,
        HashSet<string> submodulePaths)
    {
        // Skip binary files
        if (binaryFiles.Contains(change.Path) ||
            (change.OldPath != null && binaryFiles.Contains(change.OldPath)))
        {
            return true;
        }

        // Skip submodule changes
        if (IsSubmodulePath(change.Path, submodulePaths) ||
            (change.OldPath != null && IsSubmodulePath(change.OldPath, submodulePaths)))
        {
            return true;
        }

        // Skip .gitmodules (side-effect of submodule add/remove)
        if (change.Path.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
            (change.OldPath != null && change.OldPath.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a single line from <c>git diff --name-status</c> output.
    /// Formats:
    ///   A\tpath           (added)
    ///   M\tpath           (modified)
    ///   D\tpath           (deleted)
    ///   R095\told\tnew    (renamed with 95% similarity)
    ///   C075\told\tnew    (copied with 75% similarity)
    /// </summary>
    private static FileChange? ParseStatusLine(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 2)
        {
            return null;
        }

        var status = parts[0].Trim();
        if (string.IsNullOrEmpty(status))
        {
            return null;
        }

        var statusChar = status[0];
        switch (statusChar)
        {
            case 'A':
                return new FileChange { Kind = FileChangeKind.Added, Path = parts[1] };

            case 'M':
                return new FileChange { Kind = FileChangeKind.Modified, Path = parts[1] };

            case 'D':
                return new FileChange { Kind = FileChangeKind.Deleted, Path = parts[1] };

            case 'R' when parts.Length >= 3:
                return new FileChange
                {
                    Kind = FileChangeKind.Renamed,
                    OldPath = parts[1],
                    Path = parts[2],
                    SimilarityPercent = ParseSimilarityPercent(status)
                };

            case 'C' when parts.Length >= 3:
                return new FileChange
                {
                    Kind = FileChangeKind.Copied,
                    OldPath = parts[1],
                    Path = parts[2],
                    SimilarityPercent = ParseSimilarityPercent(status)
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Extracts the numeric similarity percentage from a rename/copy status
    /// like "R095" → 95, "C100" → 100.
    /// </summary>
    private static int? ParseSimilarityPercent(string status)
    {
        if (status.Length > 1 && int.TryParse(status.Substring(1), out var percent))
        {
            return percent;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a path is inside a submodule directory.
    /// </summary>
    private static bool IsSubmodulePath(string filePath, HashSet<string> submodulePaths)
    {
        foreach (var subPath in submodulePaths)
        {
            if (filePath.Equals(subPath, StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith(subPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses the rename syntax used in <c>git diff --numstat</c> output.
    /// Example: "dir/{old.cs => new.cs}" → ("dir/old.cs", "dir/new.cs")
    /// </summary>
    internal static (string oldPath, string newPath) ParseRenameNumstatPath(string path)
    {
        var arrowIdx = path.IndexOf(" => ", StringComparison.Ordinal);
        if (arrowIdx < 0)
        {
            return (path, path);
        }

        var braceOpen = path.LastIndexOf('{', arrowIdx);
        var braceClose = path.IndexOf('}', arrowIdx);

        if (braceOpen >= 0 && braceClose >= 0)
        {
            var prefix = path.Substring(0, braceOpen);
            var suffix = path.Substring(braceClose + 1);
            var oldPart = path.Substring(braceOpen + 1, arrowIdx - braceOpen - 1);
            var newPart = path.Substring(arrowIdx + 4, braceClose - arrowIdx - 4);
            return (prefix + oldPart + suffix, prefix + newPart + suffix);
        }

        var oldName = path.Substring(0, arrowIdx);
        var newName = path.Substring(arrowIdx + 4);
        return (oldName.Trim(), newName.Trim());
    }

    /// <summary>
    /// Runs a git command and returns stdout.
    /// Throws <see cref="InvalidOperationException"/> on non-zero exit.
    /// </summary>
    private async Task<string> RunGitAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = this.repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}