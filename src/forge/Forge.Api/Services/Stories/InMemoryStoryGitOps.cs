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

    public Task<bool> BranchHasChangesVsBaseAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var branchTree = this.TryGetTreeForRead(repo, branch, out var b)
            ? b : new Dictionary<string, string>(StringComparer.Ordinal);
        var baseTree = this.TryGetTreeForRead(repo, baseBranch, out var bb)
            ? bb : new Dictionary<string, string>(StringComparer.Ordinal);
        // Changed when any path added/modified on the branch, or a base path
        // dropped from it. The fake doesn't model .gitignore, so a normal
        // commit always registers as a change (the production gitignore case
        // is exercised by RealStoryGitOps + the live system).
        var changed = branchTree.Count != baseTree.Count
            || branchTree.Any(kv => !baseTree.TryGetValue(kv.Key, out var v) || v != kv.Value);
        return Task.FromResult(changed);
    }

    public Task<bool> FileExistsAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default) =>
        Task.FromResult(this.ReadCandidates(repo, branch).Any(t => t.ContainsKey(filePath)));

    public Task<string?> ReadFileAsync(RepoRow? repo, string branch, string filePath, CancellationToken ct = default)
    {
        foreach (var t in this.ReadCandidates(repo, branch))
        {
            if (t.TryGetValue(filePath, out var content)) return Task.FromResult<string?>(content);
        }
        return Task.FromResult<string?>(null);
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
        if (this.trees.ContainsKey(key)) return Task.FromResult(true);

        // Phase 80.F — tests register worktrees with repo:null (the "_default"
        // key) but a spawn now binds a real repo, so the orchestrator probes
        // under that repo's id. Fall back to the default key so a default-repo
        // registration is visible to a repo-specific probe — and a default-key
        // removal still reads as wiped. (A repo-specific registration above
        // already takes precedence, so true per-repo modelling is unaffected.)
        if (repo is not null)
        {
            var defaultKey = Key(null, branch);
            if (this.removedBranches.ContainsKey(defaultKey)) return Task.FromResult(false);
            return Task.FromResult(this.trees.ContainsKey(defaultKey));
        }
        return Task.FromResult(false);
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

    public Task AssertPlanDocPathsNotIgnoredAsync(
        RepoRow? repo, string branch, IEnumerable<string> repoRelativePaths, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MergeAsync(RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
    {
        var dstKey = Key(repo, baseBranch);
        if (!this.TryGetTreeForRead(repo, branch, out var src)) return Task.CompletedTask;
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
    /// Phase 65 — in-memory stub. Returns a synthetic snapshot derived
    /// from the test tree's tip SHA when the branch exists, else null.
    /// Numstat counts are 0 since the in-memory tree tracks content but
    /// not per-iter commit boundaries (tests that care about file counts
    /// bind a custom IStoryGitOps).
    /// </summary>
    public Task<IterCommitSnapshot?> SnapshotBranchHeadAsync(
        RepoRow? repo, string branch, string? baselineSha, CancellationToken ct = default)
    {
        var key = Key(repo, branch);
        if (!this.tips.TryGetValue(key, out var tip)) return Task.FromResult<IterCommitSnapshot?>(null);
        return Task.FromResult<IterCommitSnapshot?>(
            new IterCommitSnapshot(tip, $"in-memory tip for {branch}", 0, 0, 0));
    }

    /// <summary>
    /// 2026-05-16 — in-memory stub for the worktree-changes snapshot. The
    /// in-memory git surface doesn't model a working tree separately from
    /// branch trees, so we return the same shape as
    /// <see cref="SnapshotBranchHeadAsync"/> with zero counts. Tests that
    /// need real per-iter file deltas should use the integration-grade
    /// <c>RealStoryGitOps</c> against a fixture repo.
    /// </summary>
    public Task<IterCommitSnapshot?> SnapshotWorktreeChangesAsync(
        RepoRow? repo, string worktreePath, string baseBranch, CancellationToken ct = default)
    {
        // worktreePath isn't modeled — fall back to the base-branch tip
        // so consumers still get a non-null result for happy-path tests.
        var key = Key(repo, baseBranch);
        var sha = this.tips.TryGetValue(key, out var tip) ? tip : string.Empty;
        return Task.FromResult<IterCommitSnapshot?>(
            new IterCommitSnapshot(sha, $"in-memory worktree at {worktreePath}", 0, 0, 0));
    }

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
        var dstKey = Key(repo, branch);
        if (!this.TryGetTreeForRead(repo, upstreamBranch, out var src)) return Task.FromResult(true);
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

    /// <summary>
    /// Phase 80.F — resolve a tree for a READ when the caller's repo binding
    /// doesn't match the writer's. Tests merge/probe with <c>repo:null</c>
    /// (the <c>_default</c> key) while the orchestrator now binds a real repo
    /// and writes/probes under that repo's id (and vice-versa). Resolution:
    /// <list type="number">
    ///   <item>exact <see cref="Key"/>(repo, branch);</item>
    ///   <item>a repo-specific probe falls back to the default-key tree (a
    ///         default registration is visible to a repo-specific probe —
    ///         mirrors the <see cref="WorktreeExistsAsync"/> bridge);</item>
    ///   <item>a default (null) probe falls back to the SOLE repo-specific
    ///         tree for that branch, so a null-keyed read sees a repo-written
    ///         branch. Skipped when more than one repo owns the branch, so
    ///         true multi-repo isolation (Phase80MultiRepoTests) is
    ///         unaffected.</item>
    /// </list>
    /// </summary>
    private IEnumerable<Dictionary<string, string>> ReadCandidates(RepoRow? repo, string branch)
    {
        if (this.trees.TryGetValue(Key(repo, branch), out var exact)) yield return exact;
        if (repo is not null && this.trees.TryGetValue(Key(null, branch), out var def)) yield return def;
        if (repo is null)
        {
            var defaultKey = Key(null, branch);
            var suffix = "@" + branch;
            var repoTrees = this.trees
                .Where(kv => kv.Key != defaultKey && kv.Key.EndsWith(suffix, StringComparison.Ordinal))
                .Select(kv => kv.Value)
                .ToList();
            if (repoTrees.Count == 1) yield return repoTrees[0];
        }
    }

    /// <summary>
    /// Resolve a single tree for a READ that needs one tree (merge source,
    /// branch-vs-base diff). Prefers the first NON-EMPTY candidate so an
    /// empty exact-key tree (e.g. an <c>EnsureBranch</c>-created
    /// <c>atlas-platform@main</c>) doesn't shadow a populated default-key
    /// tree (the merged <c>_default@main</c>); falls back to the first
    /// existing candidate when every candidate is empty.
    /// </summary>
    private bool TryGetTreeForRead(RepoRow? repo, string branch, out Dictionary<string, string> tree)
    {
        Dictionary<string, string>? first = null;
        foreach (var t in this.ReadCandidates(repo, branch))
        {
            first ??= t;
            if (t.Count > 0) { tree = t; return true; }
        }
        tree = first!;
        return first is not null;
    }

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

    // ── Phase 79c — merge-conflict resolver shims ────────────────────────
    // The in-memory ops never produce real conflicts (merge just copies
    // tree entries). Tests that exercise the resolver path stub these
    // methods on a per-test basis; the defaults model the "no conflicts"
    // happy path so existing test suites compile.
    public Task<MergeResult> MergeBaseIntoBranchAndDetectConflictsAsync(
        RepoRow? repo, string branch, string baseBranch, CancellationToken ct = default)
        => Task.FromResult(new MergeResult(MergeState.Clean, Array.Empty<ConflictedFile>(), null));

    public Task<MergeResult> ContinueMergeAndPushAsync(
        RepoRow? repo,
        string branch,
        IReadOnlyDictionary<string, string> resolvedFiles,
        CancellationToken ct = default)
        => Task.FromResult(new MergeResult(MergeState.Clean, Array.Empty<ConflictedFile>(), null));

    public Task AbortMergeAsync(RepoRow? repo, string branch, CancellationToken ct = default)
        => Task.CompletedTask;
}
