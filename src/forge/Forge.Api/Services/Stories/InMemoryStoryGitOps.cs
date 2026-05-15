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
    // Phase 59 — branches whose worktree was explicitly removed via
    // RemoveWorktreeAsync. Used by WorktreeExistsAsync so tests can model
    // the "PR merged → worktree wiped" sequence without a real disk.
    private readonly ConcurrentDictionary<string, bool> removedBranches = new();

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

    /// <inheritdoc />
    /// <remarks>
    /// Phase 50 — in-memory equivalent of a force-reset to base: drops any
    /// existing tree under <paramref name="branch"/> and recreates it as a
    /// copy of <paramref name="baseBranch"/>'s tree. Mirrors what the real
    /// git path does via <c>git push --force</c>. Used by tests verifying
    /// fresh-spawn behavior after a prior spawn delete.
    /// </remarks>
    public Task EnsureBranchFreshAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var key = Key(repo, branch);
        var baseKey = Key(repo, baseBranch);
        if (!this.trees.TryGetValue(baseKey, out var baseTree))
        {
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
        RepoRow? repo, string branch, string worktreeSlug,
        IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worktreeSlug))
        {
            throw new ArgumentException(
                $"worktreeSlug is required (branch={branch}).", nameof(worktreeSlug));
        }
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

    /// <summary>
    /// In tests there's no real repo on disk — return a deterministic synthetic
    /// path so callers can use it as <c>WorktreePath</c> when stubbing the
    /// architect strategy. Tests that exercise file I/O against this path
    /// would still need a real <see cref="RealStoryGitOps"/>; the default
    /// architect strategy in tests is <c>DeterministicStoryArtifactStrategy</c>
    /// which doesn't touch git.
    /// </summary>
    public Task<string> EnsureWorktreeAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default, string? worktreeSlug = null)
    {
        return Task.FromResult($"/tmp/forge-test-worktree/{repo?.Id ?? "_default"}/{branch.Replace('/', '-')}");
    }

    /// <summary>
    /// Phase 49 follow-up — in-memory stub. No real worktree exists for
    /// tests so this is a no-op; the contract is "best-effort; never
    /// throws" which a no-op trivially satisfies.
    /// </summary>
    public Task RemoveWorktreeAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        this.removedBranches[Key(repo, branch)] = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 59 — in-memory probe. Returns <c>false</c> after the test has
    /// called <see cref="RemoveWorktreeAsync"/> for this (repo, branch); the
    /// in-memory tree dictionary by itself can't model "branch exists but
    /// worktree was wiped", so the explicit remove tracking is the test
    /// hook. Defaults to <c>true</c> when the branch has any tree row, and
    /// <c>false</c> when no tree row exists OR a prior remove was recorded.
    /// </summary>
    public Task<bool> WorktreeExistsAsync(RepoRow? repo, string branch, CancellationToken ct = default)
    {
        var key = Key(repo, branch);
        if (this.removedBranches.ContainsKey(key)) return Task.FromResult(false);
        return Task.FromResult(this.trees.ContainsKey(key));
    }

    /// <summary>
    /// In-memory commit — appends the file content to the branch tree. Mirrors
    /// the contract of <see cref="CommitFilesAsync"/> but for a single path.
    /// </summary>
    public async Task<string> CommitPathInWorktreeAsync(
        RepoRow? repo, string branch, string repoRelativePath, string message, CancellationToken ct = default)
    {
        // Read the file from the deterministic path the EnsureWorktree call
        // returned — but in tests the file probably doesn't exist on disk.
        // Test strategies write directly via CommitFilesAsync. This stub just
        // touches the in-memory tree with a placeholder so the branch tip
        // advances and tests asserting "branch had a commit" pass.
        await this.CommitFilesAsync(
            repo, branch, "test-worktree",
            new Dictionary<string, string>(StringComparer.Ordinal) { [repoRelativePath] = $"<commit:{message}>" },
            message, ct);
        return this.tips.TryGetValue(Key(repo, branch), out var sha) ? sha : string.Empty;
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

    public Task PushBranchAsync(RepoRow? repo, string branch, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ScrubForgeScratchFromIndexAsync(string wtPath, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Phase 45 follow-up — in-memory test stub returns an empty list since
    /// the test deterministic strategy doesn't write to a real worktree on
    /// disk. Tests that exercise the workstream impl flow either bind their
    /// own IStoryGitOps or accept the empty-changes path (which the
    /// orchestrator treats as "no commit needed" — fine for tests).
    /// </summary>
    public Task<IReadOnlyList<string>> GetChangedFilesAsync(
        RepoRow? repo, string worktreePath, string? baseBranch = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// Phase 45 — fast-forward the in-memory base branch to the upstream tip.
    /// Tests rely on the base branch carrying every artifact already merged
    /// into <paramref name="upstreamBranch"/>, so we copy the upstream tree
    /// over the base. This is the test-grade equivalent of
    /// <c>git rebase origin/{upstream}</c>.
    /// </summary>
    public Task RebaseBaseBranchAsync(
        RepoRow? repo, string baseBranch, string upstreamBranch, CancellationToken ct = default)
    {
        var srcKey = Key(repo, upstreamBranch);
        var dstKey = Key(repo, baseBranch);
        if (!this.trees.TryGetValue(srcKey, out var src)) return Task.CompletedTask;
        if (!this.trees.TryGetValue(dstKey, out var dst))
        {
            dst = new Dictionary<string, string>(StringComparer.Ordinal);
            this.trees[dstKey] = dst;
        }
        foreach (var (path, body) in src) dst[path] = body;
        this.tips[dstKey] = ShaOf(dst, $"rebase {baseBranch} onto {upstreamBranch}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 46 — merge upstream into a feature branch. In-memory: copy all
    /// upstream entries into the feature tree (like a successful merge).
    /// Always returns <c>true</c> (tests never simulate conflicts here).
    /// </summary>
    public Task<bool> MergeUpstreamIntoBranchAsync(
        RepoRow? repo, string branch, string upstreamBranch, bool force = true, CancellationToken ct = default)
    {
        var srcKey = Key(repo, upstreamBranch);
        var dstKey = Key(repo, branch);
        if (!this.trees.TryGetValue(srcKey, out var src)) return Task.FromResult(true);
        if (!this.trees.TryGetValue(dstKey, out var dst))
        {
            dst = new Dictionary<string, string>(StringComparer.Ordinal);
            this.trees[dstKey] = dst;
        }
        foreach (var (path, body) in src) dst.TryAdd(path, body);
        this.tips[dstKey] = ShaOf(dst, $"merge {upstreamBranch} into {branch}");
        return Task.FromResult(true);
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
