// -----------------------------------------------------------------------
// <copyright file="DefaultIncrementalSync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

/// <summary>
/// Default incremental sync that applies git diffs to the memory graph.
/// Runs <c>git diff --name-status</c> and updates nodes accordingly,
/// calling <see cref="IExtractionPipeline.ExtractFilesAsync"/> for added/modified files
/// to rebuild the full class/method graph (not just file nodes).
/// </summary>
public sealed class DefaultIncrementalSync : IIncrementalSync
{
    private readonly IMemoryGraphStore store;
    private readonly IExtractionPipeline pipeline;

    public DefaultIncrementalSync(IMemoryGraphStore store, IExtractionPipeline pipeline)
    {
        this.store = store;
        this.pipeline = pipeline;
    }

    /// <inheritdoc/>
    public async Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var filesChanged = 0;
        var nodesAdded = 0;
        var nodesRetired = 0;
        var edgesAdded = 0;
        var edgesRetired = 0;
        var expertiseDecayed = 0;

        // Resolve the commit index for temporal retirement.
        // Use git rev-list --count to get a monotonically increasing commit index.
        var commitIndex = await this.ResolveCommitIndexAsync(repoRoot, toCommit, ct);

        // Run git diff --name-status to get changed files
        var diffLines = await this.RunGitDiffAsync(repoRoot, fromCommit, toCommit, ct);

        var addedOrModifiedFiles = new List<string>();
        var deletedFiles = new List<string>();
        var renamedFiles = new List<(string OldPath, string NewPath)>();

        foreach (var line in diffLines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var status = parts[0];
            filesChanged++;

            // Handle rename (R###) and copy (C###) statuses which have two file paths
            if (status.StartsWith("R", StringComparison.Ordinal) && parts.Length >= 3)
            {
                var oldPath = parts[1].Replace('/', Path.DirectorySeparatorChar);
                var newPath = parts[2].Replace('/', Path.DirectorySeparatorChar);
                renamedFiles.Add((oldPath, newPath));
                addedOrModifiedFiles.Add(newPath);
            }
            else if (status.StartsWith("C", StringComparison.Ordinal) && parts.Length >= 3)
            {
                // Copy: old file stays, new file is added
                var newPath = parts[2].Replace('/', Path.DirectorySeparatorChar);
                addedOrModifiedFiles.Add(newPath);
            }
            else
            {
                var filePath = parts[1].Replace('/', Path.DirectorySeparatorChar);
                switch (status)
                {
                    case "D":
                        deletedFiles.Add(filePath);
                        break;
                    case "A":
                    case "M":
                    case "T": // type change
                        addedOrModifiedFiles.Add(filePath);
                        break;
                }
            }
        }

        // Retire nodes for renamed files (old path is retired, new path is added)
        foreach (var (oldPath, _) in renamedFiles)
        {
            var nodeId = $"/{oldPath.Replace('\\', '/')}";
            await this.store.RetireNodeAsync(nodeId, commitIndex, ct);
            nodesRetired++;

            var edges = await this.store.GetEdgesForNodeAsync(nodeId, cancellationToken: ct);
            foreach (var edge in edges)
            {
                await this.store.RetireEdgeAsync(edge.Id, commitIndex, ct);
                edgesRetired++;
            }
        }

        // Retire nodes for deleted files (temporal retirement, not deletion)
        foreach (var filePath in deletedFiles)
        {
            var nodeId = $"/{filePath.Replace('\\', '/')}";
            await this.store.RetireNodeAsync(nodeId, commitIndex, ct);
            nodesRetired++;

            // Retire edges referencing deleted file nodes
            var edges = await this.store.GetEdgesForNodeAsync(nodeId, cancellationToken: ct);
            foreach (var edge in edges)
            {
                await this.store.RetireEdgeAsync(edge.Id, commitIndex, ct);
                edgesRetired++;
            }
        }

        // For added/modified files, use the extraction pipeline to rebuild graph data
        if (addedOrModifiedFiles.Count > 0)
        {
            // Retire existing child nodes of modified files before re-extracting.
            // Only retire edges where the file is the parent (i.e., file→symbol containment),
            // not edges where the file is the child (directory→file containment).
            foreach (var filePath in addedOrModifiedFiles)
            {
                var nodeId = $"/{filePath.Replace('\\', '/')}";
                var existingEdges = await this.store.GetEdgesForNodeAsync(nodeId, cancellationToken: ct);
                foreach (var edge in existingEdges)
                {
                    // Only process edges where the file is in the "parent" role
                    if (edge.Roles.TryGetValue(nodeId, out var role) && role == "parent")
                    {
                        foreach (var childId in edge.NodeIds.Where(id => id != nodeId))
                        {
                            await this.store.RetireNodeAsync(childId, commitIndex, ct);
                            nodesRetired++;
                        }

                        await this.store.RetireEdgeAsync(edge.Id, commitIndex, ct);
                        edgesRetired++;
                    }
                }
            }

            var options = new ExtractionOptions(
                Languages: new[] { "C#", "PowerShell" },
                CommitSha: toCommit,
                CommitIndex: commitIndex);

            var extractionResult = await this.pipeline.ExtractFilesAsync(
                repoRoot, addedOrModifiedFiles, options, ct);

            nodesAdded += extractionResult.NodesCreated;
            edgesAdded += extractionResult.EdgesCreated;

            // Decay expertise attached to modified files
            foreach (var filePath in addedOrModifiedFiles)
            {
                var nodeId = $"/{filePath.Replace('\\', '/')}";
                var expertiseEntries = await this.store.GetExpertiseForNodeAsync(nodeId, ct: ct);
                foreach (var entry in expertiseEntries)
                {
                    entry.Confidence *= 0.9;
                    entry.UpdatedAt = DateTimeOffset.UtcNow;
                    await this.store.UpsertExpertiseAsync(entry, ct);
                    expertiseDecayed++;
                }
            }
        }

        stopwatch.Stop();

        await this.store.LogAuditAsync(
            "cli", "sync",
            new { fromCommit, toCommit, filesChanged, nodesAdded, nodesRetired, edgesAdded, edgesRetired, expertiseDecayed }, ct);

        return new SyncResult(
            FilesChanged: filesChanged,
            NodesAdded: nodesAdded,
            NodesRetired: nodesRetired,
            EdgesAdded: edgesAdded,
            EdgesRetired: edgesRetired,
            ExpertiseDecayed: expertiseDecayed,
            Duration: stopwatch.Elapsed);
    }

    private async Task<int> ResolveCommitIndexAsync(
        string repoRoot, string commitRef, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"rev-list --count {commitRef}",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process.");

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0 && int.TryParse(output.Trim(), out var count))
            {
                return count;
            }
        }
        catch (InvalidOperationException)
        {
            // Git process start or execution failure — propagate so callers see the error
            // instead of silently falling back to a timestamp-based index.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-git errors (e.g., output parsing) — fall back to timestamp-based index
            // but log that we did so. This avoids corrupting temporal history silently.
            System.Diagnostics.Debug.WriteLine(
                $"[DefaultIncrementalSync] Warning: rev-list failed for '{commitRef}': {ex.Message}. Using timestamp fallback.");
        }

        // Fallback: use Unix timestamp for ordering
        return (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % int.MaxValue);
    }

    private async Task<IReadOnlyList<string>> RunGitDiffAsync(
        string repoRoot, string fromCommit, string toCommit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"diff --name-status {fromCommit} {toCommit}",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process. Ensure git is installed and on PATH.");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? $"git diff exited with code {process.ExitCode}" : stderr.Trim();
            throw new InvalidOperationException($"git diff failed: {message}");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}