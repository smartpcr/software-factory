// -----------------------------------------------------------------------
// <copyright file="ForgeDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core DbContext for the Forge SQLite cache.
/// </summary>
public class ForgeDbContext : DbContext
{
    public ForgeDbContext(DbContextOptions<ForgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<EpicRow> Epics => this.Set<EpicRow>();

    public DbSet<ProductStoryRow> ProductStories => this.Set<ProductStoryRow>();

    public DbSet<CardRow> Cards => this.Set<CardRow>();

    public DbSet<ColumnRow> Columns => this.Set<ColumnRow>();

    public DbSet<WorkstreamRow> Workstreams => this.Set<WorkstreamRow>();

    public DbSet<WsEventRow> WsEvents => this.Set<WsEventRow>();

    public DbSet<PrRow> Prs => this.Set<PrRow>();

    public DbSet<WsStatRow> WsStats => this.Set<WsStatRow>();

    public DbSet<OverviewActivityRow> OverviewActivity => this.Set<OverviewActivityRow>();

    public DbSet<OverviewAgentRow> OverviewAgents => this.Set<OverviewAgentRow>();

    public DbSet<AuditRow> Audit => this.Set<AuditRow>();

    public DbSet<IdempotencyRow> Idempotency => this.Set<IdempotencyRow>();

    public DbSet<ProductRow> Products => this.Set<ProductRow>();

    public DbSet<RepoRow> Repos => this.Set<RepoRow>();

    public DbSet<UserRow> Users => this.Set<UserRow>();

    public DbSet<UserSettingsRow> UserSettings => this.Set<UserSettingsRow>();

    public DbSet<UserProductRoleRow> UserProductRoles => this.Set<UserProductRoleRow>();

    public DbSet<CrewRow> Crews => this.Set<CrewRow>();

    public DbSet<CrewMemberRow> CrewMembers => this.Set<CrewMemberRow>();

    public DbSet<CrewHistoryRow> CrewHistory => this.Set<CrewHistoryRow>();

    public DbSet<DomainExpertRow> DomainExperts => this.Set<DomainExpertRow>();

    public DbSet<AgentUsageRow> AgentUsage => this.Set<AgentUsageRow>();

    public DbSet<AgentRow> Agents => this.Set<AgentRow>();

    public DbSet<AgentHistoryRow> AgentHistory => this.Set<AgentHistoryRow>();

    public DbSet<WorkItemRow> WorkItems => this.Set<WorkItemRow>();

    public DbSet<WorkItemLinkRow> WorkItemLinks => this.Set<WorkItemLinkRow>();

    public DbSet<DeliverableRow> Deliverables => this.Set<DeliverableRow>();

    /// <summary>Phase 52 — one row per (work_item, pair) attempt; sloppy-rate KPI source.</summary>
    public DbSet<AgentPairAssignmentRow> AgentPairAssignments => this.Set<AgentPairAssignmentRow>();

    /// <summary>Phase 55 — per (work_item, agent, role) cross-iteration memory (session ids / conversation history).</summary>
    public DbSet<AgentMemoryRow> AgentMemory => this.Set<AgentMemoryRow>();

    /// <summary>Phase 39 (Layer C) — open questions emitted by generators, answered by operator via wizard.</summary>
    public DbSet<DeliverableQuestionRow> DeliverableQuestions => this.Set<DeliverableQuestionRow>();

    public DbSet<AgentLevelHistoryRow> AgentLevelHistory => this.Set<AgentLevelHistoryRow>();

    public DbSet<ProductIterationRow> ProductIterations => this.Set<ProductIterationRow>();

    public DbSet<ProductAreaPathRow> ProductAreaPaths => this.Set<ProductAreaPathRow>();

    public DbSet<RepoIterationBindingRow> RepoIterationBindings => this.Set<RepoIterationBindingRow>();

    public DbSet<ProductSavedPrefixRow> ProductSavedPrefixes => this.Set<ProductSavedPrefixRow>();

    public DbSet<TaskTranscriptEventRow> TaskTranscriptEvents => this.Set<TaskTranscriptEventRow>();

    public DbSet<TaskQueueRow> TaskQueue => this.Set<TaskQueueRow>();

    public DbSet<TaskQueueHistoryRow> TaskQueueHistory => this.Set<TaskQueueHistoryRow>();

    public DbSet<AgentAssignmentRow> AgentAssignments => this.Set<AgentAssignmentRow>();

    /// <summary>Phase 44 — agent busy/idle claim tracking.</summary>
    public DbSet<AgentClaimRow> AgentClaims => this.Set<AgentClaimRow>();

    public DbSet<WorkItemSyncRunRow> WorkItemSyncRuns => this.Set<WorkItemSyncRunRow>();

    public DbSet<SyncRunItemRow> SyncRunItems => this.Set<SyncRunItemRow>();

    public DbSet<PrCommentRow> PrComments => this.Set<PrCommentRow>();

    public DbSet<PrCommentHistoryRow> PrCommentHistory => this.Set<PrCommentHistoryRow>();

    public DbSet<BuildValidationRunRow> BuildValidationRuns => this.Set<BuildValidationRunRow>();

    public DbSet<BuildRow> Builds => this.Set<BuildRow>();

    public DbSet<BuildStageRow> BuildStages => this.Set<BuildStageRow>();

    public DbSet<BuildStageLogRow> BuildStageLogs => this.Set<BuildStageLogRow>();

    public DbSet<BuildArtifactRow> BuildArtifacts => this.Set<BuildArtifactRow>();

    public DbSet<BuildArtifactFileRow> BuildArtifactFiles => this.Set<BuildArtifactFileRow>();

    public DbSet<BuildFailurePointRow> BuildFailurePoints => this.Set<BuildFailurePointRow>();

    public DbSet<BuildFixPendingRow> BuildFixPending => this.Set<BuildFixPendingRow>();

    public DbSet<BuildAnnotationRow> BuildAnnotations => this.Set<BuildAnnotationRow>();

    /// <summary>forge_implementation.md Phase 28 — story spawn rows.</summary>
    public DbSet<StorySpawnRow> StorySpawns => this.Set<StorySpawnRow>();

    /// <summary>forge_implementation.md Phase 28 — workstream dependency edges.</summary>
    public DbSet<WorkstreamDepRow> WorkstreamDeps => this.Set<WorkstreamDepRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Phase D — single source of truth for entity configuration lives
        // in the two helpers below. ForgeDbContext binds the union (both
        // control and product entities live in the same physical forge.db
        // file during Phase D). After Phase F the split contexts will
        // each call only their relevant helper.
        DbModelControl.Configure(mb);
        DbModelProduct.Configure(mb);

        // Cross-context FK declarations — only meaningful while the
        // entities share a single physical DB file (Phase D interim).
        // Once Phase F splits the files these become loose string columns
        // (already declared by DbModelProduct.Configure); the FK
        // semantics are preserved at the application layer instead.
        mb.Entity<ProductIterationRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<ProductAreaPathRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<ProductSavedPrefixRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<RepoIterationBindingRow>()
            .HasOne<RepoRow>()
            .WithMany()
            .HasForeignKey(x => x.RepoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
