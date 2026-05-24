// -----------------------------------------------------------------------
// <copyright file="DbBoundary.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence.Split;

/// <summary>
/// Phase B classification of every Forge table:
/// <list type="bullet">
///   <item><see cref="DbKind.Control"/> — stays in <c>forge.db</c>
///         (cross-product registries, identity, agents/crews, cost ledger,
///         audit, idempotency-by-key).</item>
///   <item><see cref="DbKind.Product"/> — moves to <c>{slug}.db</c>.
///         Partitioned by the strategy in <see cref="DbTableSpec.Strategy"/>.</item>
/// </list>
/// </summary>
public enum DbKind
{
    Control,
    Product,
}

/// <summary>
/// How a product-DB table is partitioned across product DBs during a split.
/// SQLite can't JOIN across DBs without <c>ATTACH DATABASE</c>, so the
/// partition resolver must produce a per-product row set in pure SQL
/// against the source <c>forge.db</c> alone.
/// </summary>
public enum PartitionStrategy
{
    /// <summary>Control-DB table; not partitioned.</summary>
    Global,

    /// <summary>Row has a <c>ProductId</c> column directly.</summary>
    DirectProductId,

    /// <summary>Row has <c>RepoId</c>; join <c>repos.ProductId</c>.</summary>
    ViaRepoId,

    /// <summary>Row has <c>WorkItemId</c>; chain <c>work_items.RepoId → repos.ProductId</c>.</summary>
    ViaWorkItemId,

    /// <summary>Row has <c>WorkstreamId</c>/<c>Ws</c>; chain <c>workstreams.RepoId → repos.ProductId</c>.</summary>
    ViaWorkstreamId,

    /// <summary>Row has <c>PrId</c>; chain <c>prs.RepoId → repos.ProductId</c>.</summary>
    ViaPrId,

    /// <summary>Row has <c>BuildId</c>; chain <c>builds.RepoId → repos.ProductId</c>.</summary>
    ViaBuildId,

    /// <summary>Row has <c>BuildStageId</c>; chain <c>build_stages.BuildId → builds.RepoId → repos.ProductId</c>.</summary>
    ViaBuildStageId,

    /// <summary>Row has <c>BuildArtifactId</c>; chain <c>build_artifacts.BuildId → builds.RepoId → repos.ProductId</c>.</summary>
    ViaBuildArtifactId,

    /// <summary>Row has <c>SyncRunId</c>/<c>RunId</c>; chain <c>work_item_sync_runs.RepoId → repos.ProductId</c>.</summary>
    ViaSyncRunId,

    /// <summary>Row has <c>QueueId</c>; chain <c>task_queue.WorkItemId → work_items.RepoId → repos.ProductId</c>.</summary>
    ViaQueueId,

    /// <summary>Row has <c>PrCommentId</c>; chain <c>pr_comments.PrId → prs.RepoId → repos.ProductId</c>.</summary>
    ViaPrCommentId,

    /// <summary>
    /// Table has no obvious partition key today. Rows are reported but the
    /// split-apply step will route them all to the default <c>atlas</c> product
    /// (or, in Phase F, to the operator-chosen primary product). Examples:
    /// <c>columns</c> (board metadata), <c>overview_activity</c>, <c>overview_agents</c>,
    /// <c>ws_stats</c> (no RepoId column today), <c>idempotency</c>
    /// (keyed by HTTP method+path+key, not by product).
    /// </summary>
    NoPartitionKey,
}

/// <summary>
/// Per-table classification used by the splitter dry-run (Phase B) and the
/// auto-split-on-first-boot migration (Phase F).
/// </summary>
/// <param name="TableName">Lowercase SQL table name as it exists in <c>forge.db</c>.</param>
/// <param name="Kind">Where the table belongs after the split.</param>
/// <param name="Strategy">How to partition rows when <see cref="Kind"/> is <see cref="DbKind.Product"/>.</param>
/// <param name="PartitionColumn">Source column used by <see cref="Strategy"/> (e.g., <c>ProductId</c>, <c>RepoId</c>, <c>WorkItemId</c>). <c>null</c> for Control / NoPartitionKey tables.</param>
public sealed record DbTableSpec(
    string TableName,
    DbKind Kind,
    PartitionStrategy Strategy,
    string? PartitionColumn);

/// <summary>
/// The canonical Phase B/F boundary catalog. Keep alphabetised within each
/// section so diffs stay readable. Adding a table here is the contract that
/// the Phase F auto-split honours; the dry-run will flag any table that
/// exists in <c>forge.db</c> but isn't catalogued.
/// </summary>
public static class DbBoundary
{
    public static readonly IReadOnlyList<DbTableSpec> AllTables = new[]
    {
        // ── Control DB ────────────────────────────────────────────────────
        new DbTableSpec("agent_claims", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("agent_history", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("agent_level_history", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("agent_usage", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("agents", DbKind.Control, PartitionStrategy.Global, null),
        // EF maps AuditRow to "forge_actions" (Phase 6.10 audit log).
        new DbTableSpec("forge_actions", DbKind.Control, PartitionStrategy.Global, null),
        // Single-row meta table tracking "which seed step has already run"
        // so boot re-runs don't double-seed. Strictly global.
        new DbTableSpec("forge_seed_state", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("crew_history", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("crew_members", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("crews", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("domain_experts", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("products", DbKind.Control, PartitionStrategy.Global, null),
        // Phase G "due/dirty" optimization — control-side watermark per
        // product so background workers can skip products with no pending
        // work without opening the per-product DB.
        new DbTableSpec("product_runtime", DbKind.Control, PartitionStrategy.Global, null),
        // Phase 80 — control-side repo index/mirror (global uniqueness +
        // repoId→product routing + sync-due enumeration). Stays control even
        // after the full `repos` row moves to the per-product DB (Stage 80.B).
        new DbTableSpec("repo_registry", DbKind.Control, PartitionStrategy.Global, null),
        // Phase 80.B — `repos` now physically lives in each per-product DB
        // (the RepoRow EF entity moved to ProductDbContext, and
        // RepoMoveToProductService relocates the rows at boot). It stays
        // classified Control here because the historical Phase8/23/26/29/33/34/59
        // migrations create/ALTER it via the control context and the catalog
        // maps them as control; reclassifying would force a migration-body
        // connection rewrite. The splitter therefore leaves repos in the source
        // file and the move service carries it to {slug}.db — same end state.
        new DbTableSpec("repos", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("user_product_roles", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("user_settings", DbKind.Control, PartitionStrategy.Global, null),
        new DbTableSpec("users", DbKind.Control, PartitionStrategy.Global, null),

        // ── Product DB — direct ProductId ────────────────────────────────
        new DbTableSpec("product_area_paths", DbKind.Product, PartitionStrategy.DirectProductId, "ProductId"),
        new DbTableSpec("product_iterations", DbKind.Product, PartitionStrategy.DirectProductId, "ProductId"),
        new DbTableSpec("product_saved_prefixes", DbKind.Product, PartitionStrategy.DirectProductId, "ProductId"),
        new DbTableSpec("product_stories", DbKind.Product, PartitionStrategy.DirectProductId, "ProductId"),
        // epics has nullable ProductId; orphan rows (NULL) get routed to the
        // default product by the apply step.
        new DbTableSpec("epics", DbKind.Product, PartitionStrategy.DirectProductId, "ProductId"),

        // ── Product DB — via RepoId ──────────────────────────────────────
        new DbTableSpec("build_validation_runs", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("builds", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("cards", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("prs", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("repo_iteration_bindings", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("story_spawns", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("work_item_sync_runs", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("work_items", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),
        new DbTableSpec("workstreams", DbKind.Product, PartitionStrategy.ViaRepoId, "RepoId"),

        // ── Product DB — via WorkItemId chain ────────────────────────────
        new DbTableSpec("agent_assignments", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        new DbTableSpec("agent_memory", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        // EF maps AgentPairAssignmentRow to "agent_pair_assignment" (singular).
        new DbTableSpec("agent_pair_assignment", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        new DbTableSpec("build_fix_pending", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        new DbTableSpec("deliverable_questions", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        new DbTableSpec("deliverables", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        // Phase 82 — append-only per-iteration prompt audit, keyed by
        // WorkItemId (one row per generator/evaluator invocation).
        new DbTableSpec("iteration_prompt", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        new DbTableSpec("task_queue", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        // task_queue_history has QueueId (FK on task_queue); chain through it.
        new DbTableSpec("task_queue_history", DbKind.Product, PartitionStrategy.ViaQueueId, "QueueId"),
        // EF maps TaskTranscriptEventRow to "task_transcript_event" (singular).
        new DbTableSpec("task_transcript_event", DbKind.Product, PartitionStrategy.ViaWorkItemId, "WorkItemId"),
        // work_item_links uses FromId/ToId (not WorkItemId). Partition by
        // FromId; rows whose FromId points at a non-work-item (cross-kind
        // links) surface as broken-foreign-key orphans.
        new DbTableSpec("work_item_links", DbKind.Product, PartitionStrategy.ViaWorkItemId, "FromId"),

        // ── Product DB — via WorkstreamId chain ──────────────────────────
        new DbTableSpec("workstream_deps", DbKind.Product, PartitionStrategy.ViaWorkstreamId, "WorkstreamId"),
        new DbTableSpec("ws_events", DbKind.Product, PartitionStrategy.ViaWorkstreamId, "Ws"),

        // ── Product DB — via PrId chain ──────────────────────────────────
        new DbTableSpec("pr_comments", DbKind.Product, PartitionStrategy.ViaPrId, "PrId"),

        // ── Product DB — via PrCommentId chain ───────────────────────────
        new DbTableSpec("pr_comment_history", DbKind.Product, PartitionStrategy.ViaPrCommentId, "CommentId"),

        // ── Product DB — via Build chain ─────────────────────────────────
        new DbTableSpec("build_annotations", DbKind.Product, PartitionStrategy.ViaBuildId, "BuildId"),
        new DbTableSpec("build_artifacts", DbKind.Product, PartitionStrategy.ViaBuildId, "BuildId"),
        // build_failure_points chains via build_stages, not directly via builds.
        new DbTableSpec("build_failure_points", DbKind.Product, PartitionStrategy.ViaBuildStageId, "StageId"),
        new DbTableSpec("build_stages", DbKind.Product, PartitionStrategy.ViaBuildId, "BuildId"),

        new DbTableSpec("build_stage_logs", DbKind.Product, PartitionStrategy.ViaBuildStageId, "StageId"),
        new DbTableSpec("build_artifact_files", DbKind.Product, PartitionStrategy.ViaBuildArtifactId, "ArtifactId"),

        // ── Product DB — via SyncRunId ───────────────────────────────────
        // EF column on sync_run_items is "RunId" (not "SyncRunId").
        new DbTableSpec("sync_run_items", DbKind.Product, PartitionStrategy.ViaSyncRunId, "RunId"),

        // ── Product DB — no obvious partition key today ──────────────────
        // Phase F will need to add a column (e.g., ProductId) OR route
        // every row to the default product. Flagged here so the dry-run
        // surfaces the row counts.
        new DbTableSpec("columns", DbKind.Product, PartitionStrategy.NoPartitionKey, null),
        new DbTableSpec("idempotency", DbKind.Product, PartitionStrategy.NoPartitionKey, null),
        new DbTableSpec("overview_activity", DbKind.Product, PartitionStrategy.NoPartitionKey, null),
        new DbTableSpec("overview_agents", DbKind.Product, PartitionStrategy.NoPartitionKey, null),
        new DbTableSpec("ws_stats", DbKind.Product, PartitionStrategy.NoPartitionKey, null),
    };

    public static readonly IReadOnlySet<string> KnownTableNames =
        AllTables.Select(t => t.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static DbTableSpec? TryGet(string tableName) =>
        AllTables.FirstOrDefault(t => string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase));
}
