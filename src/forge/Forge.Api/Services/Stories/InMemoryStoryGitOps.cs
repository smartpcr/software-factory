// -----------------------------------------------------------------------
// <copyright file="InMemoryStoryGitOps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Forge.Api.Persistence;

/// <summary>
/// forge_implementation.md Phase 28 — in-memory <see cref="IStoryGitOps"/>
/// implementation. Used by tests and when no real repo checkout is
/// available. Stores per-(repo, branch) tree as a dictionary; merge copies
/// from source onto target. Sha is the SHA-256 of the serialised tree —
/// enough for the artifact-existence checks to be meaningful.
/// </summary>
public sealed class InMemoryStoryGitOps : IStoryGitOps
{
    // (repoId | "_default") + "@" + branch → tree (path → content)
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> trees = new();
    private readonly ConcurrentDictionary<string, string> tips = new();

    public Task EnsureBranchAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var key = Key(repo, branch);
        var baseKey = Key(repo, baseBranch);
        if (this.trees.ContainsKey(key)) return Task.CompletedTask;
        // Branch off base → copy the base tree.
        if (!this.trees.TryGetValue(baseKey, out var baseTree))
        {
            // Initialise main with an empty tree if it doesn't exist.
            baseTree = new Dictionary<string, string>(StringComparer.Ordinal);
            this.trees[baseKey] = baseTree;
            this.tips[baseKey] = ShaOf(baseTree);
        }
        var tree = new Dictionary<string, string>(baseTree, StringComparer.Ordinal);
        this.trees[key] = tree;
        this.tips[key] = ShaOf(tree);
        return Task.CompletedTask;
    }

    public Task<string> CommitFilesAsync(
        RepoRow? repo, string branch, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        var key = Key(repo, branch);
        if (!this.trees.TryGetValue(key, out var tree))
        {
            tree = new Dictionary<string, string>(StringComparer.Ordinal);
            this.trees[key] = tree;
        }
        foreach (var (path, body) in files) tree[path] = body;
        var sha = ShaOf(tree, message);
        this.tips[key] = sha;
        return Task.FromResult(sha);
    }

    public Task<bool> FileExistsAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default) =>
        Task.FromResult(this.trees.TryGetValue(Key(repo, branch), out var t) && t.ContainsKey(filePath));

    public Task<string?> ReadFileAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default)
    {
        if (!this.trees.TryGetValue(Key(repo, branch), out var t)) return Task.FromResult<string?>(null);
        return Task.FromResult(t.TryGetValue(filePath, out var content) ? content : null);
    }

    public Task MergeAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var srcKey = Key(repo, branch);
        var dstKey = Key(repo, baseBranch);
        if (!this.trees.TryGetValue(srcKey, out var src)) return Task.CompletedTask;
        if (!this.trees.TryGetValue(dstKey, out var dst))
        {
            dst = new Dictionary<string, string>(StringComparer.Ordinal);
            this.trees[dstKey] = dst;
        }
        foreach (var (path, body) in src) dst[path] = body;
        this.tips[dstKey] = ShaOf(dst, $"merge {branch} → {baseBranch}");
        return Task.CompletedTask;
    }

    private static string Key(RepoRow? repo, string branch) =>
        $"{repo?.Id ?? "_default"}@{branch}";

    private static string ShaOf(Dictionary<string, string> tree, string? salt = null)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in tree.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(k).Append('\0').Append(v).Append('\0');
        }
        if (salt is not null) sb.Append(salt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
