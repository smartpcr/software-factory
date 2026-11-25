// -----------------------------------------------------------------------
// <copyright file="ProductDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase D — EF DbContext bound to a **per-product DB** (<c>{folder}\{slug}.db</c>).
/// Exposes entities owned by a single product: workstreams, cards,
/// PRs, work items, deliverables, task queue, builds, story spawns, etc.
///
/// <para>Construction is via <see cref="IProductDbContextFactory"/>, which
/// chooses the connection string based on the resolved product slug.
/// During Phase D (interim) the factory may bind every product context
/// to the same <c>forge.db</c> file because the per-product files don't
/// exist yet — that lands in Phase F.</para>
///
/// <para>Phase G — tightened to product entities only. Consumers that
/// need control-side data (users, crews, agents, products, repos,
/// agent usage, audit) take <see cref="ControlDbContext"/> in addition
/// to this context.</para>
/// </summary>
public class ProductDbContext : DbContext
{
    public ProductDbContext(DbContextOptions<ProductDbContext> options)
        : base(options)
    {
    }

    /// <summary>The product slug this context is bound to (for diagnostics).</summary>
    public string? ProductSlug { get; internal set; }

    // ── Product-side DbSets ─────────────────────────────────────────────
    // Prototype / board surface.
    public DbSet<EpicRow> Epics => this.Set<EpicRow>();

    public DbSet<ProductStoryRow> ProductStories => this.Set<ProductStoryRow>();

    public DbSet<CardRow> Cards => this.Set<CardRow>();

    public DbSet<ColumnRow> Columns => this.Set<ColumnRow>();

    public DbSet<WorkstreamRow> Workstreams => this.Set<WorkstreamRow>();
    public DbSet<ChatActionReceiptRow> ChatActionReceipts => this.Set<ChatActionReceiptRow>();

    public DbSet<WsEventRow> WsEvents => this.Set<WsEventRow>();

    public DbSet<WsStatRow> WsStats => this.Set<WsStatRow>();

    public DbSet<PrRow> Prs => this.Set<PrRow>();

    public DbSet<OverviewActivityRow> OverviewActivity => this.Set<OverviewActivityRow>();

    public DbSet<OverviewAgentRow> OverviewAgents => this.Set<OverviewAgentRow>();

    public DbSet<IdempotencyRow> Idempotency => this.Set<IdempotencyRow>();

    /// <summary>
    /// Phase 80.B — the full repo row now lives in the per-product DB
    /// ({slug}.db), keyed to its owning product. The control DB keeps only the
    /// thin <c>RepoRegistry</c> index (routing + global uniqueness + sync-due).
    /// Resolve a repo's product DB via <c>RepoLookup</c> when you only have a
    /// repoId.
    /// </summary>
    public DbSet<RepoRow> Repos => this.Set<RepoRow>();

    // Work-item surface.
    public DbSet<WorkItemRow> WorkItems => this.Set<WorkItemRow>();

    public DbSet<WorkItemLinkRow> WorkItemLinks => this.Set<WorkItemLinkRow>();

    public DbSet<WorkItemSyncRunRow> WorkItemSyncRuns => this.Set<WorkItemSyncRunRow>();

    public DbSet<SyncRunItemRow> SyncRunItems => this.Set<SyncRunItemRow>();

    public DbSet<DeliverableRow> Deliverables => this.Set<DeliverableRow>();

    public DbSet<AgentPairAssignmentRow> AgentPairAssignments => this.Set<AgentPairAssignmentRow>();

    public DbSet<AgentMemoryRow> AgentMemory => this.Set<AgentMemoryRow>();

    public DbSet<DeliverableQuestionRow> DeliverableQuestions => this.Set<DeliverableQuestionRow>();

    public DbSet<ProductIterationRow> ProductIterations => this.Set<ProductIterationRow>();

    public DbSet<ProductAreaPathRow> ProductAreaPaths => this.Set<ProductAreaPathRow>();

    public DbSet<RepoIterationBindingRow> RepoIterationBindings => this.Set<RepoIterationBindingRow>();

    public DbSet<ProductSavedPrefixRow> ProductSavedPrefixes => this.Set<ProductSavedPrefixRow>();

    public DbSet<TaskTranscriptEventRow> TaskTranscriptEvents => this.Set<TaskTranscriptEventRow>();

    public DbSet<IterationPromptRow> IterationPrompts => this.Set<IterationPromptRow>();

    /// <summary>Phase 92 — per-workstream human operator steering / follow-up prompts.</summary>
    public DbSet<OperatorPromptRow> OperatorPrompts => this.Set<OperatorPromptRow>();

    public DbSet<TaskQueueRow> TaskQueue => this.Set<TaskQueueRow>();

    public DbSet<TaskQueueHistoryRow> TaskQueueHistory => this.Set<TaskQueueHistoryRow>();

    public DbSet<AgentAssignmentRow> AgentAssignments => this.Set<AgentAssignmentRow>();

    public DbSet<PrCommentRow> PrComments => this.Set<PrCommentRow>();

    public DbSet<PrCommentHistoryRow> PrCommentHistory => this.Set<PrCommentHistoryRow>();

    // Build surface.
    public DbSet<BuildValidationRunRow> BuildValidationRuns => this.Set<BuildValidationRunRow>();

    public DbSet<BuildRow> Builds => this.Set<BuildRow>();

    public DbSet<BuildStageRow> BuildStages => this.Set<BuildStageRow>();

    public DbSet<BuildStageLogRow> BuildStageLogs => this.Set<BuildStageLogRow>();

    public DbSet<BuildArtifactRow> BuildArtifacts => this.Set<BuildArtifactRow>();

    public DbSet<BuildArtifactFileRow> BuildArtifactFiles => this.Set<BuildArtifactFileRow>();

    public DbSet<BuildFailurePointRow> BuildFailurePoints => this.Set<BuildFailurePointRow>();

    public DbSet<BuildFixPendingRow> BuildFixPending => this.Set<BuildFixPendingRow>();

    public DbSet<BuildAnnotationRow> BuildAnnotations => this.Set<BuildAnnotationRow>();

    // Story spawn surface.
    public DbSet<StorySpawnRow> StorySpawns => this.Set<StorySpawnRow>();

    public DbSet<WorkstreamDepRow> WorkstreamDeps => this.Set<WorkstreamDepRow>();

    // Phase 108 — product rule catalog.
    public DbSet<RuleTagRow> RuleTags => this.Set<RuleTagRow>();

    public DbSet<CatalogRuleRow> CatalogRules => this.Set<CatalogRuleRow>();

    public DbSet<WorkstreamTagOverrideRow> WorkstreamTagOverrides => this.Set<WorkstreamTagOverrideRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Phase G — product entities only. Control entities (users, crews,
        // agents, products, repos, agent usage, audit) live on
        // ControlDbContext. Cross-context relationships (e.g. ProductIteration
        // → Product) become loose string columns; FK semantics are enforced
        // at the application layer by ProductFanout/IProductDbContextFactory.
        DbModelProduct.Configure(mb);
    }
}
