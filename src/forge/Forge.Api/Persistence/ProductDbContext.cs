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
/// <para><strong>Phase D.2 interim compromise (documented in
/// docs/forge/split-db.md):</strong> this context ALSO exposes the
/// control-side DbSets so bulk-renamed legacy code keeps compiling
/// against <c>db.Products</c> / <c>db.Repos</c> / <c>db.Users</c> etc.
/// During Phase D the underlying file is the same <c>forge.db</c> for
/// both control and product, so the cross-boundary reads work
/// transparently. Phase G will remove the control DbSets from this
/// class and force consumers to take both <see cref="ControlDbContext"/>
/// and <see cref="ProductDbContext"/> explicitly.</para>
/// </summary>
public class ProductDbContext : DbContext
{
    public ProductDbContext(DbContextOptions<ProductDbContext> options)
        : base(options)
    {
    }

    /// <summary>The product slug this context is bound to (for diagnostics).</summary>
    public string? ProductSlug { get; internal set; }

    // ── Control-side DbSets (Phase D.2 interim compromise — remove in Phase G) ──
    public DbSet<ProductRow> Products => this.Set<ProductRow>();

    public DbSet<RepoRow> Repos => this.Set<RepoRow>();

    public DbSet<UserRow> Users => this.Set<UserRow>();

    public DbSet<UserSettingsRow> UserSettings => this.Set<UserSettingsRow>();

    public DbSet<UserProductRoleRow> UserProductRoles => this.Set<UserProductRoleRow>();

    public DbSet<CrewRow> Crews => this.Set<CrewRow>();

    public DbSet<CrewMemberRow> CrewMembers => this.Set<CrewMemberRow>();

    public DbSet<CrewHistoryRow> CrewHistory => this.Set<CrewHistoryRow>();

    public DbSet<DomainExpertRow> DomainExperts => this.Set<DomainExpertRow>();

    public DbSet<AgentRow> Agents => this.Set<AgentRow>();

    public DbSet<AgentHistoryRow> AgentHistory => this.Set<AgentHistoryRow>();

    public DbSet<AgentLevelHistoryRow> AgentLevelHistory => this.Set<AgentLevelHistoryRow>();

    public DbSet<AgentUsageRow> AgentUsage => this.Set<AgentUsageRow>();

    public DbSet<AgentClaimRow> AgentClaims => this.Set<AgentClaimRow>();

    public DbSet<AuditRow> Audit => this.Set<AuditRow>();

    // ── Product-side DbSets ─────────────────────────────────────────────
    // Prototype / board surface.
    public DbSet<EpicRow> Epics => this.Set<EpicRow>();

    public DbSet<ProductStoryRow> ProductStories => this.Set<ProductStoryRow>();

    public DbSet<CardRow> Cards => this.Set<CardRow>();

    public DbSet<ColumnRow> Columns => this.Set<ColumnRow>();

    public DbSet<WorkstreamRow> Workstreams => this.Set<WorkstreamRow>();

    public DbSet<WsEventRow> WsEvents => this.Set<WsEventRow>();

    public DbSet<WsStatRow> WsStats => this.Set<WsStatRow>();

    public DbSet<PrRow> Prs => this.Set<PrRow>();

    public DbSet<OverviewActivityRow> OverviewActivity => this.Set<OverviewActivityRow>();

    public DbSet<OverviewAgentRow> OverviewAgents => this.Set<OverviewAgentRow>();

    public DbSet<IdempotencyRow> Idempotency => this.Set<IdempotencyRow>();

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

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Phase D.2 interim compromise: ProductDbContext maps BOTH control
        // and product entities so legacy code bulk-renamed from
        // ForgeDbContext continues to compile. The underlying file is the
        // same forge.db for both sides during Phase D, so the boundary
        // is purely conceptual until Phase F splits the physical files.
        // Phase G will drop DbModelControl.Configure from this class and
        // force callers to take both ControlDbContext + ProductDbContext
        // explicitly.
        DbModelControl.Configure(mb);
        DbModelProduct.Configure(mb);
    }
}
