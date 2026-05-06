// -----------------------------------------------------------------------
// <copyright file="IncrementalSync.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Sync;

using System.Diagnostics;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Keeps the memory graph current without full re-extraction.
/// Implements the incremental sync flow from architecture.md §3.6, §5.5.
/// Dependencies per §5.5: IExtractionPipeline, IMemoryGraphStore, IEvolutionEngine.
/// Also depends on ISemanticIndexer for re-embedding changed nodes (§3.6 step 9)
/// and ICommitRegistry for durable commit index allocation (§3.2.3).
///
/// Thread safety: ApplyDiffAsync is serialized via SemaphoreSlim to prevent
/// concurrent graph mutations. Edge versioning: changed edges are retired and
/// a new edge with the same deterministic ID is created (CreateEdgeAsync
/// overwrites the retired entry). Cross-file edge ownership uses role-based
/// heuristics (caller/source/parent/importer). Extraction exceptions are
/// tracked in SyncResult.ExtractionErrors so callers can detect missed updates;
/// zero-node extraction retires all existing nodes (file became empty).
/// </summary>
public class IncrementalSync : IIncrementalSync
{
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private readonly IMemoryGraphStore graphStore;
    private readonly ICommitRegistry commitRegistry;
    private readonly IExtractionPipeline extractionPipeline;
    private readonly IEvolutionEngine evolutionEngine;
    private readonly ISemanticIndexer semanticIndexer;
    private readonly GitDiffDriver diffDriver;
    private readonly MemoryOptions options;
    private readonly ILogger<IncrementalSync> logger;

    public IncrementalSync(
        IMemoryGraphStore graphStore,
        ICommitRegistry commitRegistry,
        IExtractionPipeline extractionPipeline,
        IEvolutionEngine evolutionEngine,
        ISemanticIndexer semanticIndexer,
        GitDiffDriver diffDriver,
        IOptions<MemoryOptions> options,
        ILogger<IncrementalSync> logger)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.commitRegistry = commitRegistry ?? throw new ArgumentNullException(nameof(commitRegistry));
        this.extractionPipeline = extractionPipeline ?? throw new ArgumentNullException(nameof(extractionPipeline));
        this.evolutionEngine = evolutionEngine ?? throw new ArgumentNullException(nameof(evolutionEngine));
        this.semanticIndexer = semanticIndexer ?? throw new ArgumentNullException(nameof(semanticIndexer));
        this.diffDriver = diffDriver ?? throw new ArgumentNullException(nameof(diffDriver));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SyncResult> ApplyDiffAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct = default)
    {
        // Serialize all ApplyDiff mutations to prevent concurrent graph writes.
        await this.syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await this.ApplyDiffCoreAsync(repoRoot, fromCommit, toCommit, ct).ConfigureAwait(false);
        }
        finally
        {
            this.syncLock.Release();
        }
    }

    private async Task<SyncResult> ApplyDiffCoreAsync(
        string repoRoot,
        string fromCommit,
        string toCommit,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        this.logger.LogInformation("Starting incremental sync from {From} to {To}", fromCommit, toCommit);

        // (1) Compute changed file set — per architecture.md §3.9: git diff failure
        //     returns SyncResult with zero changes and no mutations.
        IReadOnlyList<FileChange> changes;
        try
        {
            changes = await this.diffDriver.GetChangedFilesAsync(repoRoot, fromCommit, toCommit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "git diff failed between {From} and {To} — returning zero-change SyncResult per §3.9", fromCommit, toCommit);
            return EmptyResult(stopwatch.Elapsed);
        }

        if (changes.Count == 0)
        {
            this.logger.LogInformation("No file changes detected between {From} and {To}", fromCommit, toCommit);
            return EmptyResult(stopwatch.Elapsed);
        }

        // (2) Register toCommit in the durable commits table via ICommitRegistry (§3.2.3, §4.3).
        //     Returns a monotonic commit_index that anchors all VersionRange values.
        var toCommitIndex = await this.commitRegistry.RegisterCommitAsync(toCommit, null, ct).ConfigureAwait(false);

        int filesProcessed = 0;
        int nodesAdded = 0;
        int nodesRetired = 0;
        int edgesAdded = 0;
        int edgesRetired = 0;
        int expertiseDecayed = 0;
        var changedNodes = new List<HypergraphNode>();
        var extractionErrors = new List<string>();

        // (3) Process each file by status
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();

            switch (change.Status)
            {
                case FileChangeStatus.Added:
                {
                    var r = await this.ProcessAddedFileAsync(
                        repoRoot, change.Path, toCommit, toCommitIndex, changedNodes, extractionErrors, ct).ConfigureAwait(false);
                    filesProcessed++;
                    nodesAdded += r.NodesAdded;
                    edgesAdded += r.EdgesAdded;
                    break;
                }

                case FileChangeStatus.Modified:
                {
                    var r = await this.ProcessModifiedFileAsync(
                        repoRoot, change.Path, toCommit, toCommitIndex, changedNodes, extractionErrors, ct).ConfigureAwait(false);
                    filesProcessed++;
                    nodesAdded += r.NodesAdded;
                    nodesRetired += r.NodesRetired;
                    edgesAdded += r.EdgesAdded;
                    edgesRetired += r.EdgesRetired;
                    expertiseDecayed += r.ExpertiseDecayed;
                    break;
                }

                case FileChangeStatus.Deleted:
                {
                    var (nr, er) = await this.ProcessDeletedFileAsync(
                        change.Path, toCommitIndex, ct).ConfigureAwait(false);
                    filesProcessed++;
                    nodesRetired += nr;
                    edgesRetired += er;
                    break;
                }

                case FileChangeStatus.Renamed:
                {
                    var r = await this.ProcessRenamedFileAsync(
                        repoRoot, change.OldPath!, change.Path, toCommit, toCommitIndex, changedNodes, extractionErrors, ct).ConfigureAwait(false);
                    filesProcessed++;
                    nodesAdded += r.NodesAdded;
                    nodesRetired += r.NodesRetired;
                    edgesAdded += r.EdgesAdded;
                    edgesRetired += r.EdgesRetired;
                    break;
                }
            }
        }

        // (4) Re-run SemanticIndexer on changed nodes (§3.6 step 9).
        //     SemanticIndexer.IndexNodesAsync generates embeddings and creates/updates
        //     SIMILAR_TO edges. The returned count is included in EdgesAdded so the
        //     SyncResult accurately reflects all edge mutations.
        int semanticEdgesCreated = 0;
        if (changedNodes.Count > 0)
        {
            semanticEdgesCreated = await this.semanticIndexer.IndexNodesAsync(changedNodes, ct).ConfigureAwait(false);
            edgesAdded += semanticEdgesCreated;
            this.logger.LogDebug(
                "SemanticIndexer: re-indexed {Count} changed nodes, {Edges} SIMILAR_TO edges created/updated",
                changedNodes.Count, semanticEdgesCreated);
        }

        // (5) NOTE: Time-based expertise decay (IEvolutionEngine.DecayStaleExpertiseAsync)
        //     is NOT invoked during incremental sync. IncrementalSync is responsible
        //     only for event-driven decay (code-change triggers per §3.5.1, §3.6 step 10).
        //     Time-based decay is a separate scheduled concern owned by IEvolutionEngine
        //     and must not run here — doing so could mutate/archive expertise entries
        //     beyond what the code-change event warrants.

        // (6) Audit log (best effort)
        try
        {
            await this.graphStore.LogAuditAsync(
                "incremental-sync",
                "ApplyDiff",
                new
                {
                    FromCommit = fromCommit,
                    ToCommit = toCommit,
                    FilesChanged = filesProcessed,
                    NodesAdded = nodesAdded,
                    NodesRetired = nodesRetired,
                    EdgesAdded = edgesAdded,
                    EdgesRetired = edgesRetired,
                    ExpertiseDecayed = expertiseDecayed,
                    SemanticEdgesCreated = semanticEdgesCreated,
                    ExtractionErrors = extractionErrors.Count,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Failed to write audit log — non-fatal");
        }

        stopwatch.Stop();
        this.logger.LogInformation(
            "Incremental sync complete: {Files} files, ↑{NA} −{NR} nodes, +{EA} −{ER} edges, {SE} semantic, {ED} expertise decayed, {EE} extraction errors in {Duration}ms",
            filesProcessed, nodesAdded, nodesRetired, edgesAdded, edgesRetired, semanticEdgesCreated, expertiseDecayed, extractionErrors.Count, stopwatch.ElapsedMilliseconds);

        return new SyncResult(
            FilesChanged: filesProcessed,
            NodesAdded: nodesAdded,
            NodesRetired: nodesRetired,
            EdgesAdded: edgesAdded,
            EdgesRetired: edgesRetired,
            ExpertiseDecayed: expertiseDecayed,
            Duration: stopwatch.Elapsed)
        {
            ExtractionErrors = extractionErrors,
            SemanticEdgesCreated = semanticEdgesCreated,
        };
    }

    private async Task<(bool Processed, int NodesAdded, int EdgesAdded)> ProcessAddedFileAsync(
        string repoRoot,
        string relativePath,
        string commitSha,
        int commitIndex,
        List<HypergraphNode> changedNodes,
        List<string> extractionErrors,
        CancellationToken ct)
    {
        this.logger.LogDebug("Processing added file: {Path}", relativePath);

        var extractionOpts = new ExtractionOptions
        {
            CommitSha = commitSha,
            CommitIndex = commitIndex,
            GenerateEmbeddings = false,
        };

        ExtractionResult result;
        try
        {
            result = await this.extractionPipeline.ExtractFilesAsync(
                repoRoot, new[] { relativePath }, extractionOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Extraction failed for added file {Path}", relativePath);
            extractionErrors.Add($"Added file extraction failed: {relativePath} — {ex.Message}");
            return (false, 0, 0);
        }

        // Surface partial extraction errors from ExtractionResult.Errors (§5.4).
        // These indicate files that were partially processed — the pipeline succeeded
        // but some symbols could not be extracted. Callers can inspect
        // SyncResult.ExtractionErrors to detect missed updates.
        if (result.Errors.Count > 0)
        {
            foreach (var err in result.Errors)
            {
                extractionErrors.Add($"Added file partial error: {relativePath} — {err}");
            }

            this.logger.LogWarning(
                "Extraction returned {ErrorCount} partial errors for added file {Path}",
                result.Errors.Count, relativePath);
        }

        var version = new VersionRange(commitSha, commitIndex, null, null);

        foreach (var node in result.Nodes)
        {
            await this.graphStore.UpsertNodeAsync(node, version, ct).ConfigureAwait(false);
        }

        foreach (var edge in result.Edges)
        {
            StampEdgeVersion(edge, commitSha, commitIndex);
            await this.graphStore.CreateEdgeAsync(edge, ct).ConfigureAwait(false);
        }

        changedNodes.AddRange(result.Nodes);
        return (true, result.Nodes.Count, result.Edges.Count);
    }

    /// <summary>
    /// Processes a modified file per architecture.md §3.6 steps 3-10:
    /// re-extract via IExtractionPipeline, diff old vs new nodes,
    /// upsert successor versions via UpsertNodeAsync (which atomically closes
    /// the previous active version's UntilIndex per §5.1 temporal upsert semantics).
    /// Removed nodes are retired via RetireNodeAsync (cascades to edges per §5.1).
    /// Edge lifecycle is handled by edge diffing: removed edges are retired,
    /// new edges are created, changed edges are replaced.
    /// </summary>
    private async Task<(bool Processed, int NodesAdded, int NodesRetired, int EdgesAdded, int EdgesRetired, int ExpertiseDecayed)>
        ProcessModifiedFileAsync(
            string repoRoot,
            string relativePath,
            string commitSha,
            int commitIndex,
            List<HypergraphNode> changedNodes,
            List<string> extractionErrors,
            CancellationToken ct)
    {
        this.logger.LogDebug("Processing modified file: {Path}", relativePath);

        var normalizedPath = NormalizePath(relativePath);

        var existingRawNodes = await this.QueryNodesByFilePathAsync(normalizedPath, ct).ConfigureAwait(false);
        var existingNodes = existingRawNodes
            .Select(n => new MemoryNode(n))
            .Where(mn => mn.Version == null || mn.Version.UntilIndex == null)
            .ToList();

        var extractionOpts = new ExtractionOptions
        {
            CommitSha = commitSha,
            CommitIndex = commitIndex,
            GenerateEmbeddings = false,
        };

        ExtractionResult result;
        try
        {
            result = await this.extractionPipeline.ExtractFilesAsync(
                repoRoot, new[] { relativePath }, extractionOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Extraction failed for modified file {Path}", relativePath);
            extractionErrors.Add($"Modified file extraction failed: {relativePath} — {ex.Message}");
            return (false, 0, 0, 0, 0, 0);
        }

        // Surface partial extraction errors from ExtractionResult.Errors (§5.4).
        if (result.Errors.Count > 0)
        {
            foreach (var err in result.Errors)
            {
                extractionErrors.Add($"Modified file partial error: {relativePath} — {err}");
            }

            this.logger.LogWarning(
                "Extraction returned {ErrorCount} partial errors for modified file {Path}",
                result.Errors.Count, relativePath);
        }

        if (result.Nodes.Count == 0 && existingNodes.Count > 0)
        {
            // File now produces zero nodes (e.g., became empty or all symbols removed).
            // Treat as "all old nodes removed" — retire them all. This is different from
            // an extraction failure (which is caught above and skips the file).
            this.logger.LogInformation(
                "Extraction returned 0 nodes for modified file {Path} which had {OldCount} nodes — retiring all",
                relativePath, existingNodes.Count);

            int retiredCount = 0;
            var emptyFileRetiredEdgeIds = new HashSet<string>();

            // Collect edges before retiring nodes (for accurate counting)
            foreach (var node in existingNodes)
            {
                var edges = await this.graphStore.GetEdgesForNodeAsync(
                    node.Inner.Id, null, ct).ConfigureAwait(false);
                foreach (var edge in edges)
                {
                    if (!IsRetiredEdge(edge))
                    {
                        emptyFileRetiredEdgeIds.Add(edge.Id);
                    }
                }
            }

            // Mark incoming CALLS edges stale
            foreach (var node in existingNodes)
            {
                await this.PropagateStaleEdgesAsync(node.Inner.Id, ct).ConfigureAwait(false);
            }

            // Retire all nodes (cascades to connected edges per §5.1)
            foreach (var node in existingNodes)
            {
                await this.graphStore.RetireNodeAsync(node.Inner.Id, commitIndex, ct).ConfigureAwait(false);
                retiredCount++;
            }

            return (true, 0, retiredCount, 0, emptyFileRetiredEdgeIds.Count, 0);
        }

        var newNodeMap = new Dictionary<string, HypergraphNode>();
        foreach (var node in result.Nodes)
        {
            newNodeMap[node.Id] = node;
        }

        var existingNodeMap = new Dictionary<string, MemoryNode>();
        foreach (var node in existingNodes)
        {
            existingNodeMap[node.Inner.Id] = node;
        }

        var fileNodeIds = new HashSet<string>(existingNodeMap.Keys);
        foreach (var id in newNodeMap.Keys)
        {
            fileNodeIds.Add(id);
        }

        int nodesAdded = 0;
        int nodesRetired = 0;
        int expertiseDecayed = 0;
        var modifiedNodeIds = new HashSet<string>();
        var removedNodeIds = new HashSet<string>();

        foreach (var (nodeId, newNode) in newNodeMap)
        {
            if (existingNodeMap.TryGetValue(nodeId, out var existingNode))
            {
                var oldHash = existingNode.ContentHash;
                var newHash = newNode.Metadata.TryGetValue(MemoryMetadataKeys.ContentHash, out var h)
                    ? h?.ToString() : null;

                if (oldHash != newHash)
                {
                    modifiedNodeIds.Add(nodeId);
                    changedNodes.Add(newNode);
                }
            }
            else
            {
                changedNodes.Add(newNode);
            }
        }

        foreach (var (nodeId, _) in existingNodeMap)
        {
            if (!newNodeMap.ContainsKey(nodeId))
            {
                removedNodeIds.Add(nodeId);
            }
        }

        // (A) Mark incoming CALLS edges stale for modified nodes (§3.6 step 8)
        foreach (var modifiedNodeId in modifiedNodeIds)
        {
            await this.PropagateStaleEdgesAsync(modifiedNodeId, ct).ConfigureAwait(false);
        }

        // (B) Collect old file-owned edges for diffing, filtering out already-retired edges
        var oldFileOwnedEdges = new Dictionary<string, Hyperedge>();
        foreach (var existingNode in existingNodes)
        {
            var edges = await this.graphStore.GetEdgesForNodeAsync(
                existingNode.Inner.Id, null, ct).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                if (IsRetiredEdge(edge))
                {
                    continue;
                }

                if (IsFileOwnedEdge(edge, fileNodeIds))
                {
                    oldFileOwnedEdges.TryAdd(edge.Id, edge);
                }
            }
        }

        var newEdgeMap = new Dictionary<string, Hyperedge>();
        foreach (var edge in result.Edges)
        {
            newEdgeMap[edge.Id] = edge;
        }

        // Track all edge IDs retired in this operation to avoid double-counting
        var retiredEdgeIds = new HashSet<string>();

        // (C) Before retiring removed nodes, collect ALL edges connected to them
        //     (including incoming cross-file edges) for cascade counting.
        foreach (var removedNodeId in removedNodeIds)
        {
            var edges = await this.graphStore.GetEdgesForNodeAsync(
                removedNodeId, null, ct).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                if (!IsRetiredEdge(edge))
                {
                    retiredEdgeIds.Add(edge.Id);
                }
            }
        }

        // (D) Retire old file-owned edges not in the new extraction (and not
        //     already covered by cascade from removed nodes)
        foreach (var (oldEdgeId, _) in oldFileOwnedEdges)
        {
            if (!newEdgeMap.ContainsKey(oldEdgeId))
            {
                retiredEdgeIds.Add(oldEdgeId);
                await this.graphStore.RetireEdgeAsync(oldEdgeId, commitIndex, ct).ConfigureAwait(false);
            }
        }

        // (E) Upsert modified nodes via UpsertNodeAsync. Per §5.1 temporal upsert
        //     semantics, UpsertNodeAsync atomically closes the previous active version
        //     (sets its UntilIndex = newVersion.SinceIndex) before creating the
        //     successor. This preserves temporal history without cascading edge
        //     retirement — edges reference the logical node ID and remain valid.
        var newVersion = new VersionRange(commitSha, commitIndex, null, null);
        foreach (var modifiedNodeId in modifiedNodeIds)
        {
            await this.graphStore.UpsertNodeAsync(
                newNodeMap[modifiedNodeId], newVersion, ct).ConfigureAwait(false);
            nodesAdded++;
        }

        // (F) Upsert truly new nodes (not in old extraction) with new SinceIndex
        foreach (var (nodeId, newNode) in newNodeMap)
        {
            if (!existingNodeMap.ContainsKey(nodeId))
            {
                await this.graphStore.UpsertNodeAsync(newNode, newVersion, ct).ConfigureAwait(false);
                nodesAdded++;
            }
        }

        // (G) Retire removed nodes via RetireNodeAsync (cascades to edges per §5.1).
        foreach (var nodeId in removedNodeIds)
        {
            await this.graphStore.RetireNodeAsync(nodeId, commitIndex, ct).ConfigureAwait(false);
            nodesRetired++;
        }

        // (H) Handle new/changed edges. For changed edges, retire the old version
        //     and create a successor using the SAME deterministic edge ID. The store's
        //     CreateEdgeAsync overwrites the retired dict entry, which is correct
        //     because RetireEdgeAsync only sets metadata on the existing object.
        //     Using the original ID ensures future extractions can match against
        //     active edges without version-suffix drift.
        int edgesAdded = 0;
        foreach (var (edgeId, newEdge) in newEdgeMap)
        {
            if (!oldFileOwnedEdges.TryGetValue(edgeId, out var oldEdge))
            {
                StampEdgeVersion(newEdge, commitSha, commitIndex);
                await this.graphStore.CreateEdgeAsync(newEdge, ct).ConfigureAwait(false);
                edgesAdded++;
            }
            else if (HasEdgeChanged(oldEdge, newEdge))
            {
                retiredEdgeIds.Add(edgeId);
                await this.graphStore.RetireEdgeAsync(edgeId, commitIndex, ct).ConfigureAwait(false);

                // Create successor edge using the original deterministic ID.
                // The old edge was retired in-place (metadata mutation); CreateEdgeAsync
                // overwrites the dict entry so the active edge uses the canonical ID
                // that future extractions will produce.
                StampEdgeVersion(newEdge, commitSha, commitIndex);
                await this.graphStore.CreateEdgeAsync(newEdge, ct).ConfigureAwait(false);
                edgesAdded++;
            }
        }

        // (I) Trigger event-driven expertise decay (§3.5.1, §3.6 step 10).
        //     After applying decay, entries whose confidence falls below
        //     ArchiveConfidenceFloor are archived to maintain quality.
        foreach (var modifiedNodeId in modifiedNodeIds)
        {
            double decayFactor = this.DetermineDecayFactor(modifiedNodeId, existingNodeMap, newNodeMap);
            var decayed = await this.ApplyEventDrivenDecayAsync(
                modifiedNodeId, decayFactor, ct).ConfigureAwait(false);
            expertiseDecayed += decayed;
        }

        return (true, nodesAdded, nodesRetired, edgesAdded, retiredEdgeIds.Count, expertiseDecayed);
    }

    /// <summary>
    /// Processes a renamed file: extracts the new path first, then retires the old
    /// path only if extraction succeeds. This prevents data loss when extraction fails.
    /// </summary>
    private async Task<(bool Processed, int NodesAdded, int NodesRetired, int EdgesAdded, int EdgesRetired)>
        ProcessRenamedFileAsync(
            string repoRoot,
            string oldPath,
            string newPath,
            string commitSha,
            int commitIndex,
            List<HypergraphNode> changedNodes,
            List<string> extractionErrors,
            CancellationToken ct)
    {
        this.logger.LogDebug("Processing renamed file: {OldPath} → {NewPath}", oldPath, newPath);

        // Extract the new path FIRST — if extraction fails, skip the whole rename
        // without retiring the old path (prevents data loss).
        var addResult = await this.ProcessAddedFileAsync(
            repoRoot, newPath, commitSha, commitIndex, changedNodes, extractionErrors, ct).ConfigureAwait(false);

        if (!addResult.Processed)
        {
            this.logger.LogWarning(
                "Extraction failed for renamed file new path {NewPath} — skipping entire rename", newPath);
            return (false, 0, 0, 0, 0);
        }

        // Old path succeeded, now retire the old path
        var (nodesRetired, edgesRetired) = await this.ProcessDeletedFileAsync(
            oldPath, commitIndex, ct).ConfigureAwait(false);

        return (true, addResult.NodesAdded, nodesRetired, addResult.EdgesAdded, edgesRetired);
    }

    /// <summary>
    /// Processes a deleted file per architecture.md §3.6 steps 6-7:
    /// (1) Collect all edges connected to ANY file nodes (active + historical).
    ///     Uses GetEdgesForNodeAsync which returns edges from the store regardless
    ///     of whether the node is active — ensuring orphaned edges connected to
    ///     previously retired node versions are also found.
    /// (2) Mark incoming CALLS edges stale so callers know the target is gone.
    /// (3) Identify which edges will be cascade-retired by RetireNodeAsync vs orphaned.
    /// (4) Retire all active nodes via RetireNodeAsync (cascades to connected edges §5.1).
    /// (5) Explicitly retire only orphaned edges not covered by the cascade.
    /// </summary>
    private async Task<(int NodesRetired, int EdgesRetired)> ProcessDeletedFileAsync(
        string relativePath,
        int commitIndex,
        CancellationToken ct)
    {
        this.logger.LogDebug("Processing deleted file: {Path}", relativePath);

        var normalizedPath = NormalizePath(relativePath);

        // Query ALL nodes for this file path, including previously retired versions.
        // This is critical for orphan edge cleanup: edges may reference historical
        // node versions that GetNodesAsync(default) would exclude. Without IncludeRetired,
        // orphaned edges connected to already-retired nodes would be missed.
        var allFileNodes = await this.graphStore.GetNodesAsync(
            new NodeFilter { FilePath = normalizedPath, IncludeRetired = true },
            limit: 10_000, ct).ConfigureAwait(false);

        if (allFileNodes.Count == 0)
        {
            // Fall back: parsers may store OS-native backslash paths on Windows
            var backslashPath = normalizedPath.Replace('/', '\\');
            if (backslashPath != normalizedPath)
            {
                allFileNodes = await this.graphStore.GetNodesAsync(
                    new NodeFilter { FilePath = backslashPath, IncludeRetired = true },
                    limit: 10_000, ct).ConfigureAwait(false);
            }
        }

        var activeNodes = allFileNodes
            .Where(n =>
            {
                var ver = VersionRangeHelper.ExtractVersion(n.Metadata);
                return ver == null || ver.UntilIndex == null;
            })
            .ToList();

        int nodesRetired = 0;
        var activeNodeIds = new HashSet<string>(activeNodes.Select(n => n.Id));
        var allNodeIds = new HashSet<string>(allFileNodes.Select(n => n.Id));

        // (1) Collect all unique active edges connected to ANY node in this file.
        //     Query every node ID (active and historical) to catch orphaned edges
        //     that reference nodes the store may have retired or excluded from
        //     GetNodesAsync results.
        var allEdges = new Dictionary<string, Hyperedge>();
        foreach (var node in allFileNodes)
        {
            var edges = await this.graphStore.GetEdgesForNodeAsync(
                node.Id, null, ct).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                if (!IsRetiredEdge(edge))
                {
                    allEdges.TryAdd(edge.Id, edge);
                }
            }
        }

        // (2) Mark incoming CALLS edges stale so callers know the target is gone
        foreach (var (edgeId, edge) in allEdges)
        {
            if (!IsCallsEdge(edge))
            {
                continue;
            }

            foreach (var (nodeId, role) in edge.Roles)
            {
                if (role == "callee" && allNodeIds.Contains(nodeId))
                {
                    await this.graphStore.UpdateEdgeMetadataAsync(
                        edgeId,
                        new Dictionary<string, object?> { [MemoryMetadataKeys.Stale] = true },
                        ct).ConfigureAwait(false);
                    break;
                }
            }
        }

        // (3) Identify cascade-covered edges (connected to active nodes that will be retired)
        var cascadeCoveredEdgeIds = new HashSet<string>();
        foreach (var node in activeNodes)
        {
            foreach (var (edgeId, edge) in allEdges)
            {
                if (edge.NodeIds.Contains(node.Id))
                {
                    cascadeCoveredEdgeIds.Add(edgeId);
                }
            }
        }

        // (4) Retire all active nodes. RetireNodeAsync cascades to connected edges (§5.1).
        foreach (var node in activeNodes)
        {
            await this.graphStore.RetireNodeAsync(node.Id, commitIndex, ct).ConfigureAwait(false);
            nodesRetired++;
        }

        // (5) Explicitly retire orphaned edges not covered by the cascade.
        //     These are edges connected to historical (already retired) node versions
        //     or edges whose node references are not in the active set.
        int orphanedEdgesRetired = 0;
        foreach (var (edgeId, _) in allEdges)
        {
            if (!cascadeCoveredEdgeIds.Contains(edgeId))
            {
                await this.graphStore.RetireEdgeAsync(edgeId, commitIndex, ct).ConfigureAwait(false);
                orphanedEdgesRetired++;
            }
        }

        int edgesRetired = cascadeCoveredEdgeIds.Count + orphanedEdgesRetired;
        return (nodesRetired, edgesRetired);
    }

    /// <summary>
    /// Applies event-driven expertise decay per architecture.md §3.5.1.
    /// Fetches expertise entries for the target node, multiplies stored
    /// Confidence by the decay factor, and persists the result via
    /// IMemoryGraphStore.UpsertExpertiseAsync. Entries whose confidence
    /// falls below ArchiveConfidenceFloor are archived to maintain quality.
    /// </summary>
    private async Task<int> ApplyEventDrivenDecayAsync(
        string targetNodeId,
        double decayFactor,
        CancellationToken ct)
    {
        var entries = await this.graphStore.GetExpertiseForNodeAsync(
            targetNodeId, 0.0, ct).ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return 0;
        }

        int decayed = 0;
        foreach (var entry in entries)
        {
            var newConfidence = entry.Confidence * decayFactor;

            // Per architecture.md §3.5.1 (lines 456-458): event-driven decay
            // ALWAYS persists the decayed confidence immediately. This stored value
            // is the "event-adjusted confidence" reflecting all accumulated decays.
            var updated = entry.WithConfidence(newConfidence);
            await this.graphStore.UpsertExpertiseAsync(updated, ct).ConfigureAwait(false);

            if (newConfidence < this.options.ArchiveConfidenceFloor)
            {
                // Confidence fell below floor — archive AFTER persisting the
                // decayed confidence, so the archived entry retains the
                // multiplied value per architecture.md §3.5.1.
                await this.graphStore.ArchiveExpertiseAsync(entry.Id, ct).ConfigureAwait(false);
                this.logger.LogDebug(
                    "Event-driven decay: expertise {Id} confidence {Old:F3} → {New:F3} < floor {Floor} — persisted then archived",
                    entry.Id, entry.Confidence, newConfidence, this.options.ArchiveConfidenceFloor);
            }
            else
            {
                this.logger.LogDebug(
                    "Event-driven decay: expertise {Id} confidence {Old:F3} → {New:F3} (factor {Factor})",
                    entry.Id, entry.Confidence, newConfidence, decayFactor);
            }

            decayed++;
        }

        return decayed;
    }

    private static bool IsFileOwnedEdge(Hyperedge edge, HashSet<string> fileNodeIds)
    {
        foreach (var (nodeId, role) in edge.Roles)
        {
            if (fileNodeIds.Contains(nodeId) &&
                (role == "caller" || role == "source" || role == "parent" || role == "importer"))
            {
                return true;
            }
        }

        bool hasDirectionalRole = edge.Roles.Values.Any(
            r => r == "caller" || r == "source" || r == "parent" || r == "importer");
        if (!hasDirectionalRole)
        {
            return edge.NodeIds.Any(fileNodeIds.Contains);
        }

        return false;
    }

    private static bool HasEdgeChanged(Hyperedge oldEdge, Hyperedge newEdge)
    {
        if (!oldEdge.NodeIds.SequenceEqual(newEdge.NodeIds))
        {
            return true;
        }

        if (oldEdge.Roles.Count != newEdge.Roles.Count)
        {
            return true;
        }

        foreach (var (nodeId, oldRole) in oldEdge.Roles)
        {
            if (!newEdge.Roles.TryGetValue(nodeId, out var newRole) || oldRole != newRole)
            {
                return true;
            }
        }

        var ignoredKeys = new HashSet<string>
        {
            MemoryMetadataKeys.SinceCommit,
            MemoryMetadataKeys.SinceIndex,
            MemoryMetadataKeys.UntilCommit,
            MemoryMetadataKeys.UntilIndex,
            MemoryMetadataKeys.Stale,
        };

        var oldFiltered = oldEdge.Metadata
            .Where(kv => !ignoredKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key)
            .ToList();
        var newFiltered = newEdge.Metadata
            .Where(kv => !ignoredKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key)
            .ToList();

        if (oldFiltered.Count != newFiltered.Count)
        {
            return true;
        }

        for (int i = 0; i < oldFiltered.Count; i++)
        {
            if (oldFiltered[i].Key != newFiltered[i].Key)
            {
                return true;
            }

            var oldVal = oldFiltered[i].Value?.ToString();
            var newVal = newFiltered[i].Value?.ToString();
            if (oldVal != newVal)
            {
                return true;
            }
        }

        return false;
    }

    private async Task PropagateStaleEdgesAsync(string modifiedNodeId, CancellationToken ct)
    {
        var edges = await this.graphStore.GetEdgesForNodeAsync(
            modifiedNodeId, null, ct).ConfigureAwait(false);

        foreach (var edge in edges)
        {
            if (!IsCallsEdge(edge))
            {
                continue;
            }

            bool isCallee = edge.Roles.TryGetValue(modifiedNodeId, out var role) && role == "callee";
            if (!isCallee)
            {
                continue;
            }

            await this.graphStore.UpdateEdgeMetadataAsync(
                edge.Id,
                new Dictionary<string, object?> { [MemoryMetadataKeys.Stale] = true },
                ct).ConfigureAwait(false);
        }
    }

    private static bool IsCallsEdge(Hyperedge edge)
    {
        return edge.Metadata.TryGetValue(MemoryMetadataKeys.SemanticRelation, out var relation)
            && relation is string rel
            && rel == "CALLS";
    }

    private static bool IsRetiredEdge(Hyperedge edge)
    {
        return edge.Metadata.TryGetValue(MemoryMetadataKeys.UntilIndex, out var val)
            && val is not null;
    }

    private double DetermineDecayFactor(
        string nodeId,
        Dictionary<string, MemoryNode> existingNodeMap,
        Dictionary<string, HypergraphNode> newNodeMap)
    {
        if (!existingNodeMap.TryGetValue(nodeId, out var oldNode) ||
            !newNodeMap.TryGetValue(nodeId, out var newNode))
        {
            return this.options.StaleDecayFactor;
        }

        var oldSource = GetSourceText(oldNode.Inner);
        var newSource = GetSourceText(newNode);

        if (oldSource is null || newSource is null)
        {
            // When SourceCode metadata is unavailable, we cannot distinguish
            // whitespace-only from substantive changes. The ContentHash already
            // changed (which triggered this decay path), so default to the
            // aggressive StaleDecayFactor (0.5) to correctly penalize what
            // is presumed to be a substantive change. This avoids under-decaying
            // expertise when source metadata is missing.
            this.logger.LogDebug(
                "SourceCode metadata unavailable for node {NodeId} — defaulting to StaleDecayFactor (substantive)",
                nodeId);
            return this.options.StaleDecayFactor;
        }

        var language = oldNode.Inner.Language;
        var oldNormalized = NormalizeForComparison(oldSource, language);
        var newNormalized = NormalizeForComparison(newSource, language);

        return oldNormalized == newNormalized
            ? this.options.MinorDecayFactor
            : this.options.StaleDecayFactor;
    }

    private static string? GetSourceText(HypergraphNode node)
    {
        return node.Metadata.TryGetValue(MemoryMetadataKeys.SourceCode, out var code) && code is string s
            ? s : null;
    }

    /// <summary>
    /// Strips whitespace and comment styles for minor-change detection.
    /// Language-aware: C# uses <c>//</c> and <c>/* */</c> comments;
    /// PowerShell uses <c>#</c> and <c>&lt;# #&gt;</c>.
    /// String literal content (including whitespace within strings) is preserved.
    /// </summary>
    internal static string NormalizeForComparison(string source, string? language = null)
    {
        bool isPs = string.Equals(language, "PowerShell", StringComparison.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder(source.Length);
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        bool inVerbatimString = false;
        char stringDelimiter = '\0';

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inString)
            {
                if (inVerbatimString)
                {
                    if (c == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        sb.Append(c);
                        sb.Append(source[i + 1]);
                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }
                }
                else
                {
                    if (c == '\\' && i + 1 < source.Length)
                    {
                        sb.Append(c);
                        sb.Append(source[i + 1]);
                        i++;
                        continue;
                    }

                    if (c == stringDelimiter)
                    {
                        inString = false;
                    }
                }

                sb.Append(c);
                continue;
            }

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                inString = true;
                inVerbatimString = true;
                stringDelimiter = '"';
                sb.Append(c);
                sb.Append(source[i + 1]);
                i++;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                inVerbatimString = false;
                stringDelimiter = c;
                sb.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (isPs)
            {
                if (c == '#')
                {
                    inLineComment = true;
                    continue;
                }

                if (c == '<' && i + 1 < source.Length && source[i + 1] == '#')
                {
                    int end = source.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        i = end + 1;
                    }
                    else
                    {
                        i = source.Length - 1;
                    }

                    continue;
                }
            }

            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Queries nodes by file path, handling both forward-slash (git-native)
    /// and backslash (Windows-native) path conventions. Existing parsers may
    /// store absolute or OS-native paths, so we query with the normalized
    /// forward-slash path first, then fall back to backslash if needed.
    /// </summary>
    private async Task<IReadOnlyList<HypergraphNode>> QueryNodesByFilePathAsync(
        string normalizedForwardSlashPath,
        CancellationToken ct)
    {
        var results = await this.graphStore.GetNodesAsync(
            new NodeFilter { FilePath = normalizedForwardSlashPath }, limit: 10_000, ct).ConfigureAwait(false);

        if (results.Count > 0)
        {
            return results;
        }

        // Fall back: parsers may store OS-native backslash paths on Windows
        var backslashPath = normalizedForwardSlashPath.Replace('/', '\\');
        if (backslashPath != normalizedForwardSlashPath)
        {
            results = await this.graphStore.GetNodesAsync(
                new NodeFilter { FilePath = backslashPath }, limit: 10_000, ct).ConfigureAwait(false);
        }

        return results;
    }

    private static void StampEdgeVersion(Hyperedge edge, string commitSha, int commitIndex)
    {
        edge.Metadata[MemoryMetadataKeys.SinceCommit] = commitSha;
        edge.Metadata[MemoryMetadataKeys.SinceIndex] = commitIndex;
    }

    private static SyncResult EmptyResult(TimeSpan duration)
    {
        return new SyncResult(0, 0, 0, 0, 0, 0, duration);
    }
}