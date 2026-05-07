// -----------------------------------------------------------------------
// <copyright file="IncrementalSync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Diagnostics;
using System.Security.Cryptography;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implements <see cref="IIncrementalSync"/> with both git-diff-based and
/// FileSystemWatcher-based triggers. The same upsert/retire/decay core logic
/// (<see cref="SyncFilesAsync(FileChangeEntry[], string, int, CancellationToken)"/>)
/// is shared by <see cref="ApplyDiffAsync"/> and the file-system watcher.
/// See architecture.md §3.6 and §5.5.
///
/// ContentHash skip checks both <see cref="IFileSyncStateStore"/> (last-synced
/// per-file SHA-256 hashes) and existing graph node <c>FileContentHash</c> to avoid
/// redundant re-extraction on unchanged content.
/// </summary>
public sealed class IncrementalSync : IIncrementalSync
{
    private readonly IExtractionPipeline extractionPipeline;
    private readonly IMemoryGraphStore graphStore;
    private readonly IFileSyncStateStore fileSyncState;
    private readonly ISemanticReIndexer? semanticReIndexer;
    private readonly ILogger<IncrementalSync> logger;
    private readonly MemoryOptions options;

    private string? repoRoot;
    private FileSystemWatcher? watcher;
    private Timer? debounceTimer;
    private readonly object syncLock = new();
    private readonly Dictionary<string, FileChangeStatus> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim batchSemaphore = new(1, 1);
    private int commitIndex;
    private bool disposed;

    /// <summary>Session ID for audit logging.</summary>
    private readonly string sessionId = $"sync-{Guid.NewGuid():N}";

    /// <summary>Maximum retries for reading locked files during hash computation.</summary>
    private const int MaxHashRetries = 4;

    private static readonly int[] HashRetryDelaysMs = [100, 250, 500, 1000];

    /// <summary>
    /// Gets the current commit index used for versioning nodes/edges.
    /// Exposed for testing.
    /// </summary>
    public int CurrentCommitIndex => this.commitIndex;

    /// <summary>
    /// Raised after a debounced batch has been processed. Used by tests.
    /// </summary>
    public event EventHandler<BatchProcessedEventArgs>? BatchProcessed;

    public IncrementalSync(
        IExtractionPipeline extractionPipeline,
        IMemoryGraphStore graphStore,
        IFileSyncStateStore fileSyncState,
        ILogger<IncrementalSync> logger,
        IOptions<MemoryOptions> options,
        ISemanticReIndexer? semanticReIndexer = null)
    {
        this.extractionPipeline = extractionPipeline;
        this.graphStore = graphStore;
        this.fileSyncState = fileSyncState;
        this.semanticReIndexer = semanticReIndexer;
        this.logger = logger;
        this.options = options.Value;
        this.commitIndex = 0;
    }

    /// <summary>
    /// Constructor overload accepting explicit repoRoot and initialCommitIndex
    /// for testing scenarios.
    /// </summary>
    public IncrementalSync(
        string repoRoot,
        IExtractionPipeline extractionPipeline,
        IMemoryGraphStore graphStore,
        IFileSyncStateStore fileSyncState,
        ILogger<IncrementalSync> logger,
        IOptions<MemoryOptions> options,
        int initialCommitIndex = 0,
        ISemanticReIndexer? semanticReIndexer = null)
        : this(extractionPipeline, graphStore, fileSyncState, logger, options, semanticReIndexer)
    {
        this.repoRoot = repoRoot;
        this.commitIndex = initialCommitIndex;
    }

    /// <inheritdoc/>
    public async Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default)
    {
        this.repoRoot = repoRoot;

        // Get changed files from git diff --name-status with explicit status
        var changeEntries = await GetGitDiffEntriesAsync(repoRoot, fromCommit, toCommit, ct)
            .ConfigureAwait(false);

        // Filter to watched extensions (apply per-path, not per-entry,
        // so extension-changing renames still handle both old and new path)
        var watched = new HashSet<string>(this.options.WatchedExtensions, StringComparer.OrdinalIgnoreCase);
        var filteredEntries = changeEntries
            .Where(e => watched.Contains(Path.GetExtension(e.Path)))
            .ToArray();

        if (filteredEntries.Length == 0)
        {
            return new SyncResult(0, 0, 0, 0, 0, 0, TimeSpan.Zero);
        }

        var newIndex = Interlocked.Increment(ref this.commitIndex);
        return await this.SyncFilesAsync(filteredEntries, toCommit, newIndex, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parses git diff --name-status output (NUL-delimited for safety) to
    /// get the list of changed file paths with explicit status codes.
    /// Handles Added, Modified, Deleted, Renamed, and Copied status codes.
    /// </summary>
    internal static async Task<FileChangeEntry[]> GetGitDiffEntriesAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--name-status");
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add(fromCommit);
        psi.ArgumentList.Add(toCommit);
        psi.ArgumentList.Add("--");

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            return [];
        }

        // Parse NUL-delimited output: status\0path\0 (or status\0oldpath\0newpath\0 for renames)
        var parts = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<FileChangeEntry>();
        var i = 0;
        while (i < parts.Length)
        {
            var status = parts[i];
            if (status.Length == 0)
            {
                i++;
                continue;
            }

            var code = status[0];
            switch (code)
            {
                case 'R':
                    // Rename: old path → Deleted, new path → Created
                    if (i + 2 < parts.Length)
                    {
                        entries.Add(new FileChangeEntry(
                            parts[i + 1].Replace('\\', '/'), FileChangeStatus.Deleted));
                        entries.Add(new FileChangeEntry(
                            parts[i + 2].Replace('\\', '/'), FileChangeStatus.Created));
                        i += 3;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                case 'C':
                    // Copy: only new path is Created; old path is NOT deleted
                    if (i + 2 < parts.Length)
                    {
                        entries.Add(new FileChangeEntry(
                            parts[i + 2].Replace('\\', '/'), FileChangeStatus.Created));
                        i += 3;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                case 'A':
                    if (i + 1 < parts.Length)
                    {
                        entries.Add(new FileChangeEntry(
                            parts[i + 1].Replace('\\', '/'), FileChangeStatus.Created));
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                case 'D':
                    if (i + 1 < parts.Length)
                    {
                        entries.Add(new FileChangeEntry(
                            parts[i + 1].Replace('\\', '/'), FileChangeStatus.Deleted));
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                case 'M' or 'T':
                    if (i + 1 < parts.Length)
                    {
                        entries.Add(new FileChangeEntry(
                            parts[i + 1].Replace('\\', '/'), FileChangeStatus.Modified));
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                default:
                    // Unknown status — treat as Modified
                    if (i + 1 < parts.Length)
                    {
                        entries.Add(new FileChangeEntry(
                            parts[i + 1].Replace('\\', '/'), FileChangeStatus.Modified));
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;
            }
        }

        return entries.ToArray();
    }

    /// <inheritdoc/>
    public void StartFileWatcher(string repoRoot)
    {
        this.repoRoot = repoRoot;
        this.StartInternal();
    }

    /// <inheritdoc/>
    public void StopFileWatcher()
    {
        this.StopInternal();
    }

    /// <summary>
    /// Starts the file system watcher on the current repoRoot.
    /// </summary>
    public void Start()
    {
        if (this.repoRoot == null)
        {
            throw new InvalidOperationException("repoRoot not set. Use StartFileWatcher(repoRoot) or constructor overload.");
        }

        this.StartInternal();
    }

    /// <summary>
    /// Stops the watcher and disposes resources.
    /// </summary>
    public void Stop()
    {
        this.StopInternal();
    }

    /// <summary>
    /// Inject file-change events programmatically (for testing without a real watcher).
    /// Coalescing rules handle editor atomic-save patterns (delete → create on the
    /// same path) by treating that sequence as Modified rather than Deleted.
    /// </summary>
    public void InjectChange(string relativePath, FileChangeStatus status = FileChangeStatus.Modified)
    {
        lock (this.syncLock)
        {
            if (this.pendingChanges.TryGetValue(relativePath, out var existing))
            {
                if (existing == FileChangeStatus.Deleted && status == FileChangeStatus.Created)
                {
                    // Atomic save pattern: editor deletes then recreates the file.
                    // Treat as Modified so extraction runs on the recreated content
                    // instead of retiring graph state.
                    this.pendingChanges[relativePath] = FileChangeStatus.Modified;
                }
                else if (status == FileChangeStatus.Deleted)
                {
                    // Deleted overrides non-Deleted
                    this.pendingChanges[relativePath] = FileChangeStatus.Deleted;
                }
                else if (existing != FileChangeStatus.Deleted)
                {
                    // Non-Deleted updates Non-Deleted (later status wins)
                    this.pendingChanges[relativePath] = status;
                }
                // else: non-Created arriving after Deleted → keep Deleted
            }
            else
            {
                this.pendingChanges[relativePath] = status;
            }

            this.ResetDebounceTimer();
        }
    }

    /// <summary>
    /// Process the debounced batch of changed files. Visible for testing.
    /// Drains pendingChanges and delegates to <see cref="SyncFilesAsync(FileChangeEntry[], string, int, CancellationToken)"/>.
    /// Serialized via <see cref="batchSemaphore"/> to prevent overlapping
    /// timer callbacks from running concurrently.
    /// </summary>
    internal async Task ProcessBatchAsync()
    {
        await this.batchSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.disposed)
            {
                return;
            }

            FileChangeEntry[] batch;
            lock (this.syncLock)
            {
                if (this.pendingChanges.Count == 0)
                {
                    return;
                }

                batch = this.pendingChanges
                    .Select(kv => new FileChangeEntry(kv.Key, kv.Value))
                    .ToArray();
                this.pendingChanges.Clear();
            }

            var newIndex = Interlocked.Increment(ref this.commitIndex);
            var workingTreeSha = $"working-tree-{newIndex}";

            this.logger.LogInformation(
                "Processing batch of {Count} changed files at commit index {Index}",
                batch.Length, newIndex);

            var result = await this.SyncFilesAsync(batch, workingTreeSha, newIndex).ConfigureAwait(false);

            this.BatchProcessed?.Invoke(this, new BatchProcessedEventArgs(
                batch.Select(e => e.Path).ToArray(),
                result.NodesAdded, result.NodesRetired,
                result.EdgesAdded, result.EdgesRetired, result.ExpertiseDecayed));

            this.logger.LogInformation(
                "Batch complete: {NodesAdded} upserted, {NodesRetired} retired, {EdgesAdded} edges added, {EdgesRetired} edges retired, {ExpertiseDecayed} expertise decayed",
                result.NodesAdded, result.NodesRetired, result.EdgesAdded, result.EdgesRetired, result.ExpertiseDecayed);
        }
        finally
        {
            this.batchSemaphore.Release();
        }
    }

    /// <summary>
    /// Core sync logic shared by both <see cref="ApplyDiffAsync"/> and the
    /// FileSystemWatcher trigger. Given a set of changed files with explicit status:
    /// 1. Separate deleted (by explicit <see cref="FileChangeStatus"/>) vs existing files
    /// 2. Retire nodes/edges for deleted files
    /// 3. ContentHash check via <see cref="IFileSyncStateStore"/> AND existing graph node
    ///    <see cref="MemoryMetadataKeys.FileContentHash"/>
    /// 4. Extract ONCE with combined changed file set
    /// 5. Diff extraction against graph baseline: upsert only changed nodes/edges,
    ///    retire removed (using edge ownership by SourceFile metadata), apply expertise
    ///    decay only for changed ContentHash nodes
    /// 6. Upsert only NEW edges (not already in graph); retire graph edges owned by
    ///    this file but missing from extraction
    /// </summary>
    internal async Task<SyncResult> SyncFilesAsync(
        FileChangeEntry[] entries,
        string commitSha,
        int newIndex,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var versionRange = new VersionRange(commitSha, newIndex, null, null);

        // ── Phase 1: Separate deleted vs non-deleted via explicit status ──
        // Reconcile against filesystem state: even if marked Deleted, if the
        // file now exists on disk (e.g., out-of-order watcher events or atomic
        // saves that weren't fully coalesced), treat as Modified.
        var deletedFiles = new List<string>();
        var existingFiles = new List<string>();

        foreach (var entry in entries)
        {
            var fullPath = Path.Combine(this.repoRoot!, entry.Path.Replace('/', Path.DirectorySeparatorChar));

            if (entry.Status == FileChangeStatus.Deleted)
            {
                if (File.Exists(fullPath))
                {
                    // File was marked deleted but exists on disk — atomic save
                    // or out-of-order events. Treat as existing (Modified).
                    existingFiles.Add(entry.Path);
                    this.logger.LogDebug(
                        "File {FilePath} marked Deleted but exists on disk — treating as Modified",
                        entry.Path);
                }
                else
                {
                    deletedFiles.Add(entry.Path);
                }
            }
            else
            {
                // For non-Deleted status, verify file actually exists on disk
                if (!File.Exists(fullPath))
                {
                    deletedFiles.Add(entry.Path);
                }
                else
                {
                    existingFiles.Add(entry.Path);
                }
            }
        }

        var totalNodesAdded = 0;
        var totalNodesRetired = 0;
        var totalEdgesAdded = 0;
        var totalEdgesRetired = 0;
        var totalExpertiseDecayed = 0;

        // ── Phase 2: Retire nodes for deleted files ──
        foreach (var deletedPath in deletedFiles)
        {
            var (nr, er) = await this.RetireNodesForFileAsync(deletedPath, newIndex, ct).ConfigureAwait(false);
            totalNodesRetired += nr;
            totalEdgesRetired += er;

            totalExpertiseDecayed += await this.ApplyExpertiseDecayForFileAsync(
                deletedPath, isSubstantiveChange: true, ct).ConfigureAwait(false);

            await this.fileSyncState.RemoveAsync(deletedPath, ct).ConfigureAwait(false);

            await this.LogAuditSafeAsync("file_deleted", new { path = deletedPath, nodesRetired = nr, edgesRetired = er }, ct).ConfigureAwait(false);
        }

        // ── Phase 3: ContentHash check via IFileSyncStateStore AND graph node FileContentHash ──
        var filesToExtract = new List<string>();
        var currentFileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requeuePaths = new List<string>();

        foreach (var relativePath in existingFiles)
        {
            string currentHash;
            try
            {
                var fullPath = Path.Combine(this.repoRoot!, relativePath.Replace('/', Path.DirectorySeparatorChar));
                currentHash = await ComputeFileHashWithRetryAsync(fullPath, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // File still locked after retries — requeue for next batch
                this.logger.LogWarning(
                    ex, "File {FilePath} locked after retries — requeueing for next batch",
                    relativePath);
                requeuePaths.Add(relativePath);
                continue;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to compute hash for {FilePath} — skipping", relativePath);
                continue;
            }

            // Check 1: compare against last-synced hash in the sync state store
            var lastSyncedHash = await this.fileSyncState.GetContentHashAsync(relativePath, ct).ConfigureAwait(false);

            if (string.Equals(lastSyncedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                this.logger.LogDebug(
                    "ContentHash unchanged for {FilePath} (matches last-synced hash) — skipping",
                    relativePath);
                continue;
            }

            // Check 2: fall back to existing graph node FileContentHash when state store has no entry
            if (lastSyncedHash == null)
            {
                var graphBaseline = await this.graphStore.GetNodesAsync(
                    new NodeFilter { FilePath = relativePath },
                    limit: 10_000,
                    ct).ConfigureAwait(false);

                if (graphBaseline.Count > 0)
                {
                    // Check ALL active nodes for the file — if every active node
                    // has a matching FileContentHash (or ContentHash fallback), we
                    // can skip. If any active node lacks the hash, proceed with
                    // extraction. If NO active nodes exist (all are retired), we
                    // must proceed with extraction to repopulate the graph.
                    var allMatch = true;
                    var activeNodeCount = 0;
                    foreach (var gNode in graphBaseline)
                    {
                        // Skip already-retired nodes
                        if (gNode.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) && ui != null)
                        {
                            continue;
                        }

                        activeNodeCount++;

                        var graphFileHash = gNode.Metadata.TryGetValue(
                            MemoryMetadataKeys.FileContentHash, out var gfh) ? gfh as string : null;

                        if (graphFileHash == null)
                        {
                            graphFileHash = gNode.Metadata.TryGetValue(
                                MemoryMetadataKeys.ContentHash, out var gh) ? gh as string : null;
                        }

                        if (graphFileHash == null ||
                            !string.Equals(graphFileHash, currentHash, StringComparison.OrdinalIgnoreCase))
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch && activeNodeCount > 0)
                    {
                        this.logger.LogDebug(
                            "ContentHash unchanged for {FilePath} (all {Count} active graph nodes match) — skipping",
                            relativePath, activeNodeCount);
                        // Update sync state store so future checks are faster
                        await this.fileSyncState.SetContentHashAsync(relativePath, currentHash, ct).ConfigureAwait(false);
                        continue;
                    }
                }
            }

            filesToExtract.Add(relativePath);
            currentFileHashes[relativePath] = currentHash;
        }

        // Requeue locked files for a future batch
        if (requeuePaths.Count > 0)
        {
            lock (this.syncLock)
            {
                foreach (var path in requeuePaths)
                {
                    this.pendingChanges.TryAdd(path, FileChangeStatus.Modified);
                }

                if (this.pendingChanges.Count > 0)
                {
                    this.ResetDebounceTimer();
                }
            }
        }

        if (filesToExtract.Count == 0)
        {
            stopwatch.Stop();
            return new SyncResult(
                entries.Length, totalNodesAdded, totalNodesRetired,
                totalEdgesAdded, totalEdgesRetired, totalExpertiseDecayed, stopwatch.Elapsed);
        }

        // ── Phase 4: Extract ONCE with combined changed file set ──
        ExtractionResult extractionResult;
        try
        {
            extractionResult = await this.extractionPipeline.ExtractFilesAsync(
                this.repoRoot!,
                filesToExtract,
                new ExtractionOptions(
                    Languages: new[] { "C#", "PowerShell" },
                    CommitSha: commitSha,
                    CommitIndex: newIndex),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Extraction failed for batch — watcher continues running");
            stopwatch.Stop();
            return new SyncResult(
                entries.Length, totalNodesAdded, totalNodesRetired,
                totalEdgesAdded, totalEdgesRetired, totalExpertiseDecayed, stopwatch.Elapsed);
        }

        // Log extraction errors per-file but don't abort the entire batch
        if (extractionResult.Errors.Count > 0)
        {
            foreach (var error in extractionResult.Errors)
            {
                this.logger.LogWarning("Extraction error: {Error}", error);
            }
        }

        // Build a set of paths that the pipeline skipped (unsupported extension).
        // Do NOT retire existing graph nodes for skipped files.
        var skippedPathSet = new HashSet<string>(
            extractionResult.SkippedPaths,
            StringComparer.OrdinalIgnoreCase);

        // ── Phase 5: Diff extraction results against graph baseline ──
        var newNodesByFile = extractionResult.ExtractedNodes
            .GroupBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Build set of all newly extracted edge IDs for diffing
        var newEdgeIds = new HashSet<string>(
            extractionResult.ExtractedEdges.Select(e => e.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in filesToExtract)
        {
            // Skip files that the pipeline couldn't handle (e.g., unsupported extension)
            if (skippedPathSet.Contains(relativePath))
            {
                this.logger.LogDebug(
                    "File {FilePath} was skipped by pipeline — preserving existing graph state",
                    relativePath);
                continue;
            }

            // Skip files that had extraction errors — preserve existing graph state
            if (extractionResult.Errors.Any(e => e.Contains(relativePath, StringComparison.OrdinalIgnoreCase)))
            {
                if (!newNodesByFile.ContainsKey(relativePath))
                {
                    this.logger.LogWarning(
                        "Extraction error for {FilePath} — preserving existing graph state",
                        relativePath);
                    continue;
                }
            }

            // Get existing graph nodes for this file (the "from" baseline)
            var existingNodes = await this.graphStore.GetNodesAsync(
                new NodeFilter { FilePath = relativePath },
                limit: 10_000,
                ct).ConfigureAwait(false);

            // Filter to only active nodes (no UntilIndex set)
            var activeExistingNodes = existingNodes
                .Where(n => !n.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) || ui == null)
                .ToList();

            var existingNodeMap = activeExistingNodes
                .ToDictionary(n => n.Id, n => n, StringComparer.OrdinalIgnoreCase);

            // Build a secondary index by stable key (NodeType:Scope:Name) for
            // matching nodes when Roslyn line-number-based IDs shift due to
            // whitespace/comment-only edits. Multiple nodes may share a key
            // (e.g., overloads), so we use a list.
            var existingNodesByStableKey = new Dictionary<string, List<HypergraphNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in activeExistingNodes)
            {
                var stableKey = GetNodeStableKey(node);
                if (!existingNodesByStableKey.TryGetValue(stableKey, out var list))
                {
                    list = new List<HypergraphNode>();
                    existingNodesByStableKey[stableKey] = list;
                }

                list.Add(node);
            }

            // Track which existing nodes were matched (by stable key) to avoid
            // double-retiring them in Phase 5b.
            var matchedExistingNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var newNodes = newNodesByFile.GetValueOrDefault(relativePath) ?? new List<HypergraphNode>();
            var newNodeIdsForFile = new HashSet<string>(
                newNodes.Select(n => n.Id),
                StringComparer.OrdinalIgnoreCase);

            // Per-node decay tracking: maps nodeId → true (substantive) or false (minor).
            // Only nodes that actually changed get an entry — unchanged nodes are excluded
            // so their expertise is NOT decayed (architecture guidance: target changed code nodes).
            var nodeDecayMap = new Dictionary<string, NodeDecayKind>(StringComparer.OrdinalIgnoreCase);

            // Track new node IDs that need semantic re-indexing (substantive changes only)
            var reindexNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Compute file prefix for edge ownership checks (used in 5b and 5c)
            var filePrefix = ComputeContentHash(relativePath)[..8];

            // ── Phase 5a: UPSERT only nodes whose ContentHash actually changed ──
            // Match new nodes against existing by ID first, then by stable key
            // (NodeType:Scope:Name) to handle Roslyn line-number-based ID shifts.
            foreach (var newNode in newNodes)
            {
                var newContentHash = newNode.Metadata.TryGetValue(
                    MemoryMetadataKeys.ContentHash, out var nch) ? nch as string : null;

                // Try direct ID match first
                HypergraphNode? matchedExisting = null;
                if (existingNodeMap.TryGetValue(newNode.Id, out var directMatch))
                {
                    matchedExisting = directMatch;
                    matchedExistingNodeIds.Add(directMatch.Id);
                }
                else
                {
                    // Fallback: match by stable key (NodeType:Scope:Name).
                    // This handles Roslyn extractors that embed line numbers
                    // in node IDs — when whitespace shifts lines, the ID changes
                    // but the stable key stays the same.
                    var stableKey = GetNodeStableKey(newNode);
                    if (existingNodesByStableKey.TryGetValue(stableKey, out var candidates))
                    {
                        // Pick the first unmatched candidate with same stable key
                        foreach (var candidate in candidates)
                        {
                            if (!matchedExistingNodeIds.Contains(candidate.Id))
                            {
                                matchedExisting = candidate;
                                matchedExistingNodeIds.Add(candidate.Id);
                                break;
                            }
                        }
                    }
                }

                if (matchedExisting != null)
                {
                    var existingContentHash = matchedExisting.Metadata.TryGetValue(
                        MemoryMetadataKeys.ContentHash, out var ech) ? ech as string : null;

                    if (string.Equals(existingContentHash, newContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Node structural hash unchanged despite file content change —
                        // this is a minor change (whitespace/comment-only edit).
                        // Per-node: only mark for decay if the node ID shifted (line change).
                        // If ID is the same, no decay at all — the node content is identical.

                        // If the node ID changed (line shift), retire old and upsert new
                        // so the graph stays current — but still treat as minor change.
                        if (!string.Equals(matchedExisting.Id, newNode.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            await this.RetireNodeWithoutCascadeAsync(matchedExisting.Id, commitSha, newIndex, ct).ConfigureAwait(false);
                            totalNodesRetired++;
                            await this.graphStore.UpsertNodeAsync(newNode, versionRange, ct).ConfigureAwait(false);
                            totalNodesAdded++;
                            // Minor decay only for the shifted node (structure didn't change)
                            MarkNodeForDecay(nodeDecayMap, matchedExisting.Id, NodeDecayKind.Minor);
                            await this.LogAuditSafeAsync("node_id_shifted", new { oldId = matchedExisting.Id, newId = newNode.Id, file = relativePath }, ct).ConfigureAwait(false);
                        }

                        // Node with same ID and same ContentHash → no entry in decay map,
                        // no expertise decay occurs for this specific node.
                        continue;
                    }

                    // ContentHash changed — determine if minor or substantive
                    if (IsMinorChange(matchedExisting, newNode))
                    {
                        MarkNodeForDecay(nodeDecayMap, matchedExisting.Id, NodeDecayKind.Minor);
                    }
                    else
                    {
                        MarkNodeForDecay(nodeDecayMap, matchedExisting.Id, NodeDecayKind.Substantive);
                        reindexNodeIds.Add(newNode.Id);
                    }

                    // If the node ID changed, retire old first (without cascade — edge/expertise
                    // handling is done separately in Phases 5b–5e)
                    if (!string.Equals(matchedExisting.Id, newNode.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        await this.RetireNodeWithoutCascadeAsync(matchedExisting.Id, commitSha, newIndex, ct).ConfigureAwait(false);
                        totalNodesRetired++;
                    }
                }
                else
                {
                    // New node — no existing expertise to decay (brand new code).
                    // Mark for re-indexing since it's genuinely new content.
                    reindexNodeIds.Add(newNode.Id);
                }

                await this.graphStore.UpsertNodeAsync(newNode, versionRange, ct).ConfigureAwait(false);
                totalNodesAdded++;

                await this.LogAuditSafeAsync("node_upserted", new { nodeId = newNode.Id, file = relativePath }, ct).ConfigureAwait(false);
            }

            // ── Phase 5b: RETIRE nodes in graph but NOT in new extraction ──
            // A node is "unmatched" if its ID is not in the new extraction AND it
            // was not matched by stable key in Phase 5a (line-shift scenario).
            // Uses non-cascading retire so IncrementalSync controls edge/expertise fate.
            foreach (var existingNode in activeExistingNodes)
            {
                if (newNodeIdsForFile.Contains(existingNode.Id))
                {
                    continue;
                }

                if (matchedExistingNodeIds.Contains(existingNode.Id))
                {
                    // Already handled in Phase 5a (stable-key match with ID shift)
                    continue;
                }

                // Selectively handle connected edges BEFORE retiring the node.
                // File-owned edges are retired; cross-file edges are marked stale
                // so their source file's next sync can decide their fate.
                var edges = await this.graphStore.GetEdgesForNodeAsync(existingNode.Id, null, ct).ConfigureAwait(false);
                foreach (var edge in edges)
                {
                    if (edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var eui) && eui != null)
                    {
                        continue;
                    }

                    if (IsEdgeOwnedByFile(edge, relativePath, filePrefix))
                    {
                        await this.graphStore.RetireEdgeAsync(edge.Id, newIndex, ct).ConfigureAwait(false);
                        totalEdgesRetired++;
                        await this.LogAuditSafeAsync("edge_retired_with_node", new { edgeId = edge.Id, nodeId = existingNode.Id }, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Cross-file edge: mark as stale but preserve it. The edge's
                        // source file will handle retirement on its next sync.
                        await this.graphStore.UpdateEdgeMetadataAsync(
                            edge.Id,
                            new Dictionary<string, object?> { [MemoryMetadataKeys.Stale] = true },
                            ct).ConfigureAwait(false);
                    }
                }

                // Retire the node itself without cascade (edges handled above,
                // expertise decay handled in Phase 5e)
                await this.RetireNodeWithoutCascadeAsync(existingNode.Id, commitSha, newIndex, ct).ConfigureAwait(false);
                totalNodesRetired++;
                MarkNodeForDecay(nodeDecayMap, existingNode.Id, NodeDecayKind.Substantive);

                await this.LogAuditSafeAsync("node_retired", new { nodeId = existingNode.Id, file = relativePath }, ct).ConfigureAwait(false);
            }

            // ── Phase 5c: RETIRE edges OWNED BY THIS FILE that are absent from extraction ──
            // Only retire edges where SourceFile metadata matches the current file,
            // OR where the edge ID has the file's prefix (legacy fallback).
            // Cross-file, incoming, semantic, and expertise edges are preserved.
            // Check edges for both direct-ID-matched AND stable-key-matched existing nodes.
            var checkedEdgeNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in newNodes)
            {
                // The node may match an existing node by direct ID or by stable key.
                // We need to check edges on the existing node that corresponds to this
                // new node, whichever way it matched.
                string? existingNodeIdToCheck = null;
                if (existingNodeMap.ContainsKey(node.Id))
                {
                    existingNodeIdToCheck = node.Id;
                }
                else
                {
                    // Check if this new node was matched to an existing node via stable key
                    var stableKey = GetNodeStableKey(node);
                    if (existingNodesByStableKey.TryGetValue(stableKey, out var candidates))
                    {
                        existingNodeIdToCheck = candidates
                            .FirstOrDefault(c => matchedExistingNodeIds.Contains(c.Id))?.Id;
                    }
                }

                if (existingNodeIdToCheck == null || !checkedEdgeNodeIds.Add(existingNodeIdToCheck))
                {
                    continue;
                }

                var existingEdges = await this.graphStore.GetEdgesForNodeAsync(existingNodeIdToCheck, null, ct).ConfigureAwait(false);
                var activeExistingEdges = existingEdges
                    .Where(e => !e.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var eui) || eui == null)
                    .ToList();

                    foreach (var existingEdge in activeExistingEdges)
                    {
                        // Only retire edges owned by this file
                        if (!IsEdgeOwnedByFile(existingEdge, relativePath, filePrefix))
                        {
                            continue;
                        }

                        if (!newEdgeIds.Contains(existingEdge.Id))
                        {
                            await this.graphStore.RetireEdgeAsync(existingEdge.Id, newIndex, ct).ConfigureAwait(false);
                            totalEdgesRetired++;

                            await this.LogAuditSafeAsync("edge_retired", new { edgeId = existingEdge.Id, file = relativePath }, ct).ConfigureAwait(false);
                        }
                    }

                    // Mark downstream CALLS/CmdletCall edges as stale (§3.6 step 8)
                    foreach (var existingEdge in activeExistingEdges)
                    {
                        if (existingEdge.EdgeType == HyperedgeType.MethodInvocation ||
                            existingEdge.EdgeType == HyperedgeType.CmdletCall)
                        {
                            await this.graphStore.UpdateEdgeMetadataAsync(
                                existingEdge.Id,
                                new Dictionary<string, object?> { [MemoryMetadataKeys.Stale] = true },
                                ct).ConfigureAwait(false);

                            await this.LogAuditSafeAsync("edge_marked_stale", new { edgeId = existingEdge.Id }, ct).ConfigureAwait(false);
                        }
                    }
            }

            // ── Phase 5d: Upsert extracted edges — create new, update changed ──
            // Select edges owned by this file (SourceFile metadata or ID prefix).
            // This prevents cross-file edges from being processed under both endpoint
            // files when both are in the same debounced batch, avoiding duplicates.
            // Edges are compared by content (NodeIds, EdgeType, Roles, Weight,
            // and non-internal metadata) — if an edge with the same ID exists but
            // has different endpoints/roles/metadata, the old edge is retired and
            // the new edge is created. This ensures changed call chains are updated.
            var edgesForThisFile = extractionResult.ExtractedEdges
                .Where(e => IsEdgeOwnedByFile(e, relativePath, filePrefix))
                .ToList();

            foreach (var edge in edgesForThisFile)
            {
                // Look up existing active edge with the same ID in the graph.
                // Search through both existing nodes (direct ID match) and
                // stable-key-matched old nodes (whose IDs shifted due to line changes).
                Hyperedge? matchedExistingEdge = null;

                // First pass: search via nodes that are in the existing graph by direct ID
                foreach (var nodeId in edge.NodeIds)
                {
                    if (existingNodeMap.ContainsKey(nodeId))
                    {
                        var existingEdges = await this.graphStore.GetEdgesForNodeAsync(nodeId, null, ct).ConfigureAwait(false);
                        matchedExistingEdge = existingEdges.FirstOrDefault(e =>
                            string.Equals(e.Id, edge.Id, StringComparison.OrdinalIgnoreCase) &&
                            (!e.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) || ui == null));
                        if (matchedExistingEdge != null)
                        {
                            break;
                        }
                    }
                }

                // Second pass: search via stable-key-matched old node IDs.
                // When a node's ID shifts (e.g., line number changed), the old node ID
                // is in matchedExistingNodeIds but NOT in the new edge's NodeIds.
                // We need to check edges on those old nodes to find the same-ID edge.
                if (matchedExistingEdge == null)
                {
                    foreach (var nodeId in edge.NodeIds)
                    {
                        // Find matching new node, then look up its stable-key-matched old node
                        var newNodeForEdge = newNodes.FirstOrDefault(n =>
                            string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase));
                        if (newNodeForEdge == null)
                        {
                            continue;
                        }

                        var stableKey = GetNodeStableKey(newNodeForEdge);
                        if (existingNodesByStableKey.TryGetValue(stableKey, out var candidates))
                        {
                            foreach (var candidate in candidates)
                            {
                                if (!matchedExistingNodeIds.Contains(candidate.Id))
                                {
                                    continue;
                                }

                                var existingEdges = await this.graphStore.GetEdgesForNodeAsync(candidate.Id, null, ct).ConfigureAwait(false);
                                matchedExistingEdge = existingEdges.FirstOrDefault(e =>
                                    string.Equals(e.Id, edge.Id, StringComparison.OrdinalIgnoreCase) &&
                                    (!e.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) || ui == null));
                                if (matchedExistingEdge != null)
                                {
                                    break;
                                }
                            }
                        }

                        if (matchedExistingEdge != null)
                        {
                            break;
                        }
                    }
                }

                if (matchedExistingEdge == null)
                {
                    // Brand new edge — stamp version metadata before creating
                    edge.Metadata[MemoryMetadataKeys.SinceCommit] = commitSha;
                    edge.Metadata[MemoryMetadataKeys.SinceIndex] = newIndex;
                    await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
                    totalEdgesAdded++;

                    await this.LogAuditSafeAsync("edge_created", new { edgeId = edge.Id, file = relativePath }, ct).ConfigureAwait(false);
                }
                else if (!EdgesAreStructurallyEqual(matchedExistingEdge, edge))
                {
                    // Same ID but different content (endpoints, roles, type, or metadata changed).
                    // Retire the stale edge and create the updated version.
                    await this.graphStore.RetireEdgeAsync(matchedExistingEdge.Id, newIndex, ct).ConfigureAwait(false);
                    totalEdgesRetired++;

                    // Stamp version metadata on the replacement edge
                    edge.Metadata[MemoryMetadataKeys.SinceCommit] = commitSha;
                    edge.Metadata[MemoryMetadataKeys.SinceIndex] = newIndex;
                    await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
                    totalEdgesAdded++;

                    await this.LogAuditSafeAsync("edge_updated", new { edgeId = edge.Id, file = relativePath, reason = "content_changed" }, ct).ConfigureAwait(false);
                }
                // else: same ID and structurally equal — no action needed
            }

            // ── Phase 5e: Apply per-node expertise decay ONLY for changed nodes ──
            // Unlike file-level decay, this targets only nodes whose ContentHash
            // actually changed. Unchanged nodes in the same file are NOT decayed.
            if (nodeDecayMap.Count > 0)
            {
                totalExpertiseDecayed += await this.ApplyExpertiseDecayForNodesAsync(
                    nodeDecayMap, ct).ConfigureAwait(false);
            }

            // ── Phase 5e.1: Semantic re-embedding (§3.6 step 9) ──
            // Re-embed only substantively changed nodes (not all nodes in file).
            if (reindexNodeIds.Count > 0)
            {
                var changedNodes = newNodes
                    .Where(n => reindexNodeIds.Contains(n.Id))
                    .ToList();

                if (this.semanticReIndexer != null && changedNodes.Count > 0)
                {
                    try
                    {
                        await this.semanticReIndexer.ReIndexAsync(changedNodes, ct).ConfigureAwait(false);
                        this.logger.LogDebug(
                            "Semantic re-embedding completed for {Count} changed nodes in {FilePath}",
                            changedNodes.Count, relativePath);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(
                            ex,
                            "Semantic re-embedding failed for {FilePath} — graph sync completed but embeddings may be stale",
                            relativePath);
                    }
                }
                else if (changedNodes.Count > 0)
                {
                    this.logger.LogDebug(
                        "Semantic re-embedding pending for {Count} changed nodes in {FilePath} (no ISemanticReIndexer registered)",
                        changedNodes.Count, relativePath);
                }

                await this.LogAuditSafeAsync("semantic_reembed_completed", new { file = relativePath, nodeCount = changedNodes.Count, invoked = this.semanticReIndexer != null }, ct).ConfigureAwait(false);
            }

            // ── Phase 5f: Update sync state with new file hash ──
            if (currentFileHashes.TryGetValue(relativePath, out var fileHash))
            {
                try
                {
                    await this.fileSyncState.SetContentHashAsync(relativePath, fileHash, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Failed to update sync state hash for {FilePath}", relativePath);
                }
            }
        }

        stopwatch.Stop();
        return new SyncResult(
            entries.Length, totalNodesAdded, totalNodesRetired,
            totalEdgesAdded, totalEdgesRetired, totalExpertiseDecayed, stopwatch.Elapsed);
    }

    /// <summary>
    /// Determines whether an edge is owned by (was extracted from) the specified file.
    /// Checks explicit <see cref="MemoryMetadataKeys.SourceFile"/> metadata first,
    /// then falls back to edge ID prefix matching for legacy edges.
    /// </summary>
    private static bool IsEdgeOwnedByFile(Hyperedge edge, string relativePath, string filePrefix)
    {
        // Primary: explicit SourceFile metadata
        if (edge.Metadata.TryGetValue(MemoryMetadataKeys.SourceFile, out var sf) && sf is string sourceFile)
        {
            return string.Equals(sourceFile, relativePath, StringComparison.OrdinalIgnoreCase);
        }

        // Fallback: edge ID prefix matching (legacy edges without SourceFile)
        return edge.Id.StartsWith(filePrefix + "_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Apply event-driven expertise decay for all expertise entries targeting
    /// nodes in the specified file. Per architecture.md §3.5.1:
    /// - Substantive change: Confidence ← Confidence × StaleDecayFactor (0.5)
    /// - Minor change (whitespace/comment-only): Confidence ← Confidence × MinorDecayFactor (0.9)
    /// Used only for deleted files (all nodes in a deleted file are substantively changed).
    /// For non-deleted files, use <see cref="ApplyExpertiseDecayForNodesAsync"/> instead.
    /// </summary>
    private async Task<int> ApplyExpertiseDecayForFileAsync(
        string relativePath, bool isSubstantiveChange, CancellationToken ct = default)
    {
        var decayFactor = isSubstantiveChange
            ? this.options.StaleDecayFactor
            : this.options.MinorDecayFactor;

        var decayCount = 0;
        var nodesInFile = await this.graphStore.GetNodesAsync(
            new NodeFilter { FilePath = relativePath },
            limit: 10_000,
            ct).ConfigureAwait(false);

        foreach (var node in nodesInFile)
        {
            var expertiseEntries = await this.graphStore.GetExpertiseForNodeAsync(node.Id, 0.0, ct).ConfigureAwait(false);
            foreach (var entry in expertiseEntries)
            {
                if (entry.Archived)
                {
                    continue;
                }

                var newConfidence = entry.Confidence * decayFactor;
                if (newConfidence < this.options.ArchiveConfidenceFloor)
                {
                    await this.graphStore.ArchiveExpertiseAsync(entry.Id, ct).ConfigureAwait(false);
                    await this.LogAuditSafeAsync("expertise_archived", new { expertiseId = entry.Id, confidence = newConfidence }, ct).ConfigureAwait(false);
                }
                else
                {
                    entry.Confidence = newConfidence;
                    entry.UpdatedAt = DateTime.UtcNow;
                    await this.graphStore.UpsertExpertiseAsync(entry, ct).ConfigureAwait(false);
                    await this.LogAuditSafeAsync("expertise_decayed", new { expertiseId = entry.Id, confidence = newConfidence, factor = decayFactor }, ct).ConfigureAwait(false);
                }

                decayCount++;
            }
        }

        return decayCount;
    }

    /// <summary>
    /// Apply per-node expertise decay. Only nodes with entries in
    /// <paramref name="nodeDecayMap"/> have their expertise decayed.
    /// Unchanged nodes (no entry in the map) are completely untouched.
    /// Per architecture.md §3.5.1:
    /// - Substantive: Confidence ← Confidence × StaleDecayFactor (0.5)
    /// - Minor: Confidence ← Confidence × MinorDecayFactor (0.9)
    /// </summary>
    private async Task<int> ApplyExpertiseDecayForNodesAsync(
        Dictionary<string, NodeDecayKind> nodeDecayMap,
        CancellationToken ct = default)
    {
        var decayCount = 0;

        foreach (var (nodeId, decayKind) in nodeDecayMap)
        {
            var decayFactor = decayKind == NodeDecayKind.Substantive
                ? this.options.StaleDecayFactor
                : this.options.MinorDecayFactor;

            var expertiseEntries = await this.graphStore.GetExpertiseForNodeAsync(nodeId, 0.0, ct).ConfigureAwait(false);
            foreach (var entry in expertiseEntries)
            {
                if (entry.Archived)
                {
                    continue;
                }

                var newConfidence = entry.Confidence * decayFactor;
                if (newConfidence < this.options.ArchiveConfidenceFloor)
                {
                    await this.graphStore.ArchiveExpertiseAsync(entry.Id, ct).ConfigureAwait(false);
                    await this.LogAuditSafeAsync("expertise_archived", new { expertiseId = entry.Id, nodeId, confidence = newConfidence }, ct).ConfigureAwait(false);
                }
                else
                {
                    entry.Confidence = newConfidence;
                    entry.UpdatedAt = DateTime.UtcNow;
                    await this.graphStore.UpsertExpertiseAsync(entry, ct).ConfigureAwait(false);
                    await this.LogAuditSafeAsync("expertise_decayed", new { expertiseId = entry.Id, nodeId, confidence = newConfidence, factor = decayFactor }, ct).ConfigureAwait(false);
                }

                decayCount++;
            }
        }

        return decayCount;
    }

    /// <summary>
    /// Adds a node to the decay map with "substantive wins" semantics:
    /// once a node is marked <see cref="NodeDecayKind.Substantive"/>, it
    /// cannot be downgraded to <see cref="NodeDecayKind.Minor"/>.
    /// </summary>
    private static void MarkNodeForDecay(
        Dictionary<string, NodeDecayKind> map,
        string nodeId,
        NodeDecayKind kind)
    {
        if (map.TryGetValue(nodeId, out var existing))
        {
            // Substantive wins — never downgrade
            if (existing == NodeDecayKind.Substantive)
            {
                return;
            }
        }

        map[nodeId] = kind;
    }

    private async Task<(int nodesRetired, int edgesRetired)> RetireNodesForFileAsync(
        string relativePath, int untilCommitIndex, CancellationToken ct = default)
    {
        var existingNodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { FilePath = relativePath },
            limit: 10_000,
            ct).ConfigureAwait(false);

        var nodesRetired = 0;
        var edgesRetired = 0;
        var filePrefix = ComputeContentHash(relativePath)[..8];

        foreach (var node in existingNodes)
        {
            if (node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var ui) && ui != null)
            {
                continue;
            }

            // Handle edges BEFORE retiring the node to avoid cascade conflicts.
            // File-owned edges are retired; cross-file edges are marked stale.
            var edges = await this.graphStore.GetEdgesForNodeAsync(node.Id, null, ct).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                if (edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var eui) && eui != null)
                {
                    continue;
                }

                if (!IsEdgeOwnedByFile(edge, relativePath, filePrefix))
                {
                    // Mark cross-file edges pointing to deleted nodes as stale
                    await this.graphStore.UpdateEdgeMetadataAsync(
                        edge.Id,
                        new Dictionary<string, object?> { [MemoryMetadataKeys.Stale] = true },
                        ct).ConfigureAwait(false);
                    continue;
                }

                await this.graphStore.RetireEdgeAsync(edge.Id, untilCommitIndex, ct).ConfigureAwait(false);
                edgesRetired++;
            }

            // Retire the node without cascade — edges are handled above,
            // expertise decay is handled by the caller (ApplyExpertiseDecayForFileAsync)
            await this.RetireNodeWithoutCascadeAsync(node.Id, null, untilCommitIndex, ct).ConfigureAwait(false);
            nodesRetired++;
        }

        return (nodesRetired, edgesRetired);
    }

    /// <summary>
    /// Computes file hash with retry logic for handling transient file locks.
    /// Uses <see cref="FileShare.ReadWrite"/> and exponential backoff.
    /// </summary>
    private static async Task<string> ComputeFileHashWithRetryAsync(string fullPath, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt <= MaxHashRetries; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var hashBytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch (IOException) when (attempt < MaxHashRetries)
            {
                await Task.Delay(HashRetryDelaysMs[attempt], ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"Failed to hash {fullPath} after {MaxHashRetries + 1} attempts");
    }

    private static string ComputeContentHash(string content)
    {
        var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Logs an audit entry. Failures are caught and logged as warnings
    /// to avoid breaking the sync loop. See architecture.md §3.6 step 11.
    /// </summary>
    private async Task LogAuditSafeAsync(string operation, object detail, CancellationToken ct)
    {
        try
        {
            await this.graphStore.LogAuditAsync(this.sessionId, operation, detail, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Audit log failed for operation {Operation}", operation);
        }
    }

    /// <summary>
    /// Retires a node by setting its <c>UntilIndex</c> WITHOUT cascading to connected
    /// edges or archiving expertise. This preserves cross-file edges (which
    /// IncrementalSync marks as stale separately) and allows expertise decay to
    /// run before any archival.
    ///
    /// Uses <see cref="IMemoryGraphStore.UpsertNodeAsync"/> with the original
    /// <c>SinceCommit/SinceIndex</c> preserved, avoiding the cascade that
    /// <see cref="IMemoryGraphStore.RetireNodeAsync"/> performs on edges and
    /// expertise. Idempotent: does nothing if the node is already retired or
    /// does not exist.
    /// </summary>
    private async Task RetireNodeWithoutCascadeAsync(
        string nodeId, string? untilCommit, int untilIndex, CancellationToken ct)
    {
        var node = await this.graphStore.GetNodeAsync(nodeId, ct).ConfigureAwait(false);
        if (node == null)
        {
            return;
        }

        // Already retired — do not overwrite an earlier retirement
        if (node.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var existingUi) && existingUi != null)
        {
            return;
        }

        // Preserve the node's original birth version
        var sinceCommit = node.Metadata.TryGetValue(MemoryMetadataKeys.SinceCommit, out var sc) ? sc as string ?? "" : "";
        var sinceIndex = 0;
        if (node.Metadata.TryGetValue(MemoryMetadataKeys.SinceIndex, out var si))
        {
            sinceIndex = si is int i ? i : (si is long l ? (int)l : 0);
        }

        var version = new VersionRange(sinceCommit, sinceIndex, untilCommit, untilIndex);
        await this.graphStore.UpsertNodeAsync(node, version, ct).ConfigureAwait(false);
    }

    private void StartInternal()
    {
        if (this.watcher != null)
        {
            return;
        }

        this.watcher = new FileSystemWatcher(this.repoRoot!)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        foreach (var ext in this.options.WatchedExtensions)
        {
            this.watcher.Filters.Add($"*{ext}");
        }

        this.watcher.Changed += this.OnFileChanged;
        this.watcher.Created += this.OnFileCreated;
        this.watcher.Renamed += this.OnFileRenamed;
        this.watcher.Deleted += this.OnFileDeleted;
        this.watcher.Error += this.OnWatcherError;

        this.logger.LogInformation("IncrementalSync file watcher started on {RepoRoot}", this.repoRoot);
    }

    private void StopInternal()
    {
        this.watcher?.Dispose();
        this.watcher = null;
        this.debounceTimer?.Dispose();
        this.debounceTimer = null;
        this.logger.LogInformation("IncrementalSync file watcher stopped");
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var relativePath = Path.GetRelativePath(this.repoRoot!, e.FullPath).Replace('\\', '/');
        this.InjectChange(relativePath, FileChangeStatus.Modified);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        var relativePath = Path.GetRelativePath(this.repoRoot!, e.FullPath).Replace('\\', '/');
        this.InjectChange(relativePath, FileChangeStatus.Created);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var oldPath = Path.GetRelativePath(this.repoRoot!, e.OldFullPath).Replace('\\', '/');
        var newPath = Path.GetRelativePath(this.repoRoot!, e.FullPath).Replace('\\', '/');
        this.InjectChange(oldPath, FileChangeStatus.Deleted);
        this.InjectChange(newPath, FileChangeStatus.Created);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        var relativePath = Path.GetRelativePath(this.repoRoot!, e.FullPath).Replace('\\', '/');
        this.InjectChange(relativePath, FileChangeStatus.Deleted);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        this.logger.LogError(e.GetException(), "FileSystemWatcher error — watcher continues running");
    }

    private void ResetDebounceTimer()
    {
        this.debounceTimer?.Dispose();
        this.debounceTimer = new Timer(
            _ => _ = this.ProcessBatchSafe(),
            null,
            this.options.IncrementalSyncDebounceMs,
            Timeout.Infinite);
    }

    private async Task ProcessBatchSafe()
    {
        try
        {
            await this.ProcessBatchAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Unexpected error in batch processing — watcher continues running");
        }
    }

    /// <summary>
    /// Determines whether the change between two node versions is "minor"
    /// (whitespace/comment-only). Per architecture.md §3.5.1, minor changes
    /// use MinorDecayFactor (0.9) instead of StaleDecayFactor (0.5).
    ///
    /// When ContentHash has changed (which is the precondition for calling
    /// this method), the source text of the node actually changed. The
    /// StructuralHash (name/type/scope/modifiers) cannot distinguish between
    /// body-only logic changes and pure comment edits. To avoid misclassifying
    /// substantive body edits as minor, ALL ContentHash changes are treated
    /// as substantive. True minor changes (line-number shifts without text
    /// changes) produce identical ContentHash and are caught earlier in
    /// Phase 5a without ever reaching this method.
    /// </summary>
    private static bool IsMinorChange(
        HypergraphNode existingNode, HypergraphNode newNode)
    {
        // ContentHash has already been verified as different by the caller.
        // Any actual source-text change is treated as substantive because the
        // current hash scheme cannot distinguish comment-only from logic edits.
        return false;
    }

    /// <summary>
    /// Returns a stable key for a node that excludes line numbers. The real
    /// Roslyn extractor (<see cref="Ast.Csharp.CSharpHyperedgeExtractor"/>)
    /// embeds the line number in the node ID, so whitespace/comment-only
    /// edits that shift lines produce new IDs for the same logical node.
    /// This key allows Phase 5a to recognise line-shifted nodes and apply
    /// the minor-change/no-structural-churn path rather than treating them
    /// as removed + added (which would cause StaleDecayFactor instead of
    /// MinorDecayFactor).
    /// </summary>
    internal static string GetNodeStableKey(HypergraphNode node)
    {
        return $"{node.NodeType ?? ""}:{node.Scope ?? ""}:{node.Name ?? ""}";
    }

    /// <summary>
    /// Determines whether two edges with the same ID are structurally equal.
    /// Compares EdgeType, NodeIds (order-sensitive), Roles, Weight, and
    /// non-internal metadata keys. Internal metadata (UntilIndex, SinceIndex,
    /// Stale, etc.) are excluded from comparison since they are managed by
    /// the sync engine, not the extraction pipeline.
    /// </summary>
    internal static bool EdgesAreStructurallyEqual(Hyperedge existing, Hyperedge extracted)
    {
        // Compare EdgeType
        if (existing.EdgeType != extracted.EdgeType)
        {
            return false;
        }

        // Compare NodeIds (order matters for directed edges)
        if (existing.NodeIds.Count != extracted.NodeIds.Count)
        {
            return false;
        }

        for (var i = 0; i < existing.NodeIds.Count; i++)
        {
            if (!string.Equals(existing.NodeIds[i], extracted.NodeIds[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Compare Roles
        if (existing.Roles.Count != extracted.Roles.Count)
        {
            return false;
        }

        foreach (var kvp in extracted.Roles)
        {
            if (!existing.Roles.TryGetValue(kvp.Key, out var existingRole) ||
                !string.Equals(existingRole, kvp.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Compare Weight
        if (Math.Abs(existing.Weight - extracted.Weight) > 0.0001)
        {
            return false;
        }

        // Compare non-internal metadata (exclude sync-managed keys)
        var internalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MemoryMetadataKeys.UntilIndex,
            MemoryMetadataKeys.UntilCommit,
            MemoryMetadataKeys.SinceIndex,
            MemoryMetadataKeys.SinceCommit,
            MemoryMetadataKeys.Stale
        };

        var extractedUserMeta = extracted.Metadata
            .Where(kv => !internalKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var existingUserMeta = existing.Metadata
            .Where(kv => !internalKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        if (extractedUserMeta.Count != existingUserMeta.Count)
        {
            return false;
        }

        foreach (var kvp in extractedUserMeta)
        {
            if (!existingUserMeta.TryGetValue(kvp.Key, out var existingVal))
            {
                return false;
            }

            var extractedStr = kvp.Value?.ToString() ?? string.Empty;
            var existingStr = existingVal?.ToString() ?? string.Empty;
            if (!string.Equals(extractedStr, existingStr, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.StopInternal();
        this.batchSemaphore.Dispose();
    }
}

/// <summary>
/// Event args for the BatchProcessed event.
/// </summary>
public sealed class BatchProcessedEventArgs : EventArgs
{
    public BatchProcessedEventArgs(
        string[] processedFiles, int nodesAdded, int nodesRetired,
        int edgesAdded, int edgesRetired, int expertiseDecayed)
    {
        this.ProcessedFiles = processedFiles;
        this.NodesAdded = nodesAdded;
        this.NodesRetired = nodesRetired;
        this.EdgesAdded = edgesAdded;
        this.EdgesRetired = edgesRetired;
        this.ExpertiseDecayed = expertiseDecayed;
    }

    public string[] ProcessedFiles { get; }
    public int NodesAdded { get; }
    public int NodesRetired { get; }
    public int EdgesAdded { get; }
    public int EdgesRetired { get; }
    public int ExpertiseDecayed { get; }
}

/// <summary>
/// Classifies how a node changed, determining which decay factor to apply.
/// <see cref="Substantive"/>: code structure changed → StaleDecayFactor.
/// <see cref="Minor"/>: whitespace/comment-only → MinorDecayFactor.
/// </summary>
internal enum NodeDecayKind
{
    Minor,
    Substantive
}