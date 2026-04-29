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

    public DbSet<AgentLevelHistoryRow> AgentLevelHistory => this.Set<AgentLevelHistoryRow>();

    public DbSet<ProductIterationRow> ProductIterations => this.Set<ProductIterationRow>();

    public DbSet<ProductAreaPathRow> ProductAreaPaths => this.Set<ProductAreaPathRow>();

    public DbSet<RepoIterationBindingRow> RepoIterationBindings => this.Set<RepoIterationBindingRow>();

    public DbSet<ProductSavedPrefixRow> ProductSavedPrefixes => this.Set<ProductSavedPrefixRow>();

    public DbSet<TaskTranscriptEventRow> TaskTranscriptEvents => this.Set<TaskTranscriptEventRow>();

    public DbSet<TaskQueueRow> TaskQueue => this.Set<TaskQueueRow>();

    public DbSet<TaskQueueHistoryRow> TaskQueueHistory => this.Set<TaskQueueHistoryRow>();

    public DbSet<AgentAssignmentRow> AgentAssignments => this.Set<AgentAssignmentRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<EpicRow>().ToTable("epics").HasKey(x => x.Id);
        mb.Entity<ProductStoryRow>().ToTable("product_stories").HasKey(x => x.Id);
        mb.Entity<ProductStoryRow>().HasIndex(x => x.ProductId);
        mb.Entity<CardRow>().ToTable("cards").HasKey(x => x.Id);
        mb.Entity<ColumnRow>().ToTable("columns").HasKey(x => x.Id);
        mb.Entity<WorkstreamRow>().ToTable("workstreams").HasKey(x => x.Id);
        mb.Entity<WsEventRow>().ToTable("ws_events").HasKey(x => x.Id);
        mb.Entity<PrRow>().ToTable("prs").HasKey(x => x.Id);
        mb.Entity<WsStatRow>().ToTable("ws_stats").HasKey(x => x.Id);
        mb.Entity<OverviewActivityRow>().ToTable("overview_activity").HasKey(x => x.Id);
        mb.Entity<OverviewAgentRow>().ToTable("overview_agents").HasKey(x => x.Id);
        mb.Entity<AuditRow>().ToTable("forge_actions").HasKey(x => x.Id);
        mb.Entity<IdempotencyRow>().ToTable("idempotency").HasKey(x => x.Key);

        // Phase 8 — products / repos with Q5 invariants enforced at the DB layer.
        mb.Entity<ProductRow>().ToTable("products").HasKey(x => x.Id);
        mb.Entity<ProductRow>().HasIndex(x => x.Slug).IsUnique();

        mb.Entity<RepoRow>().ToTable("repos").HasKey(x => x.Id);
        // Q5: same remote URL cannot live under two products.
        mb.Entity<RepoRow>().HasIndex(x => x.RemoteUrl).IsUnique();
        // Q2: same clone root cannot host two repos (worktrees would race).
        mb.Entity<RepoRow>().HasIndex(x => x.CloneRoot).IsUnique();
        // FK on product_id; cascade delete when a product goes away.
        mb.Entity<RepoRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 9.6 — identified users.
        mb.Entity<UserRow>().ToTable("users").HasKey(x => x.Id);
        mb.Entity<UserRow>().HasIndex(x => x.Email).IsUnique();

        // Phase 10.1 — per-user preferences.
        mb.Entity<UserSettingsRow>().ToTable("user_settings").HasKey(x => x.UserId);

        // Phase 9.12 — per-user / per-product role override.
        mb.Entity<UserProductRoleRow>()
            .ToTable("user_product_roles")
            .HasKey(x => new { x.UserId, x.ProductId });
        mb.Entity<UserProductRoleRow>().HasIndex(x => x.UserId);
        mb.Entity<UserProductRoleRow>().HasIndex(x => x.ProductId);

        // Phase 10.6 — crew rows.
        mb.Entity<CrewRow>().ToTable("crews").HasKey(x => x.Id);
        // SQLite stores decimal as TEXT; explicit conversion keeps EF happy.
        mb.Entity<CrewRow>().Property(x => x.ModelInputPricePerMTok).HasConversion<double>();
        mb.Entity<CrewRow>().Property(x => x.ModelOutputPricePerMTok).HasConversion<double>();
        // Phase 13.1 — hierarchy index for cheap parent walks.
        mb.Entity<CrewRow>().HasIndex(x => x.ParentCrewId);
        // Phase 16.2 — symmetric pair lookup. The partial-unique semantic
        // ("at most one pair per crew") is enforced in the migration via a
        // SQLite WHERE-clause index; the EF index here is just for fast lookup.
        mb.Entity<CrewRow>().HasIndex(x => x.PairedWithCrewId);

        // Phase 13.1 — crew membership.
        mb.Entity<CrewMemberRow>()
            .ToTable("crew_members")
            .HasKey(x => new { x.CrewId, x.AgentId });
        mb.Entity<CrewMemberRow>().HasIndex(x => x.CrewId);
        mb.Entity<CrewMemberRow>().HasIndex(x => x.AgentId);

        // Phase 13.1 — crew_history audit log.
        mb.Entity<CrewHistoryRow>().ToTable("crew_history").HasKey(x => x.Id);
        mb.Entity<CrewHistoryRow>().HasIndex(x => new { x.CrewId, x.At });

        // Phase 13.7 — domain-expert index.
        mb.Entity<DomainExpertRow>().ToTable("domain_experts").HasKey(x => x.Id);
        mb.Entity<DomainExpertRow>().HasIndex(x => x.ContentId).IsUnique();
        mb.Entity<DomainExpertRow>().HasIndex(x => x.RepoId);
        mb.Entity<DomainExpertRow>().HasIndex(x => x.Topic);

        // Phase 11.4 — agent invocation cost ledger.
        mb.Entity<AgentUsageRow>().ToTable("agent_usage").HasKey(x => x.Id);
        mb.Entity<AgentUsageRow>().Property(x => x.CostUsd).HasConversion<double>();
        // Salary roll-ups group by crew and walk by time.
        mb.Entity<AgentUsageRow>().HasIndex(x => new { x.CrewId, x.StartedAt });
        // Per-card cost lookup.
        mb.Entity<AgentUsageRow>().HasIndex(x => x.TaskId);

        // Phase 11.3 — first-class agent records.
        mb.Entity<AgentRow>().ToTable("agents").HasKey(x => x.Id);
        mb.Entity<AgentRow>().HasIndex(x => x.CrewId);

        // Phase 11.8 — agent_history audit table.
        mb.Entity<AgentHistoryRow>().ToTable("agent_history").HasKey(x => x.Id);
        mb.Entity<AgentHistoryRow>().HasIndex(x => new { x.AgentId, x.At });

        // Phase 11.10 — crew budgets denormalised onto the crew row.
        mb.Entity<CrewRow>().Property(x => x.DailyBudgetUsd).HasConversion<double?>();
        mb.Entity<CrewRow>().Property(x => x.MaxTaskBudgetUsd).HasConversion<double?>();

        // Phase 12.1 — work_items + work_item_links.
        mb.Entity<WorkItemRow>().ToTable("work_items").HasKey(x => x.Id);
        // Filter / sort indexes — every list view hits at least one of these.
        mb.Entity<WorkItemRow>().HasIndex(x => x.ParentId);
        mb.Entity<WorkItemRow>().HasIndex(x => x.IterationRef);
        mb.Entity<WorkItemRow>().HasIndex(x => new { x.RepoId, x.Type });
        mb.Entity<WorkItemRow>().HasIndex(x => new { x.Provider, x.ProviderRef });

        mb.Entity<WorkItemLinkRow>().ToTable("work_item_links").HasKey(x => x.Id);
        mb.Entity<WorkItemLinkRow>().HasIndex(x => x.FromId);
        mb.Entity<WorkItemLinkRow>().HasIndex(x => x.ToId);

        // Phase 14.1 — agent level + payroll columns on agents.
        mb.Entity<AgentRow>().Property(x => x.HourlyUsd).HasConversion<double>();

        // Phase 14.1 — crew schedule columns.
        // (Timezone + ScheduleEnabled are flat string/bool; no conversion needed.)

        // Phase 14.1 — deliverables (one row per work-item × iteration).
        mb.Entity<DeliverableRow>().ToTable("deliverables").HasKey(x => x.Id);
        mb.Entity<DeliverableRow>().Property(x => x.GenCostUsd).HasConversion<double>();
        mb.Entity<DeliverableRow>().Property(x => x.EvalCostUsd).HasConversion<double>();
        mb.Entity<DeliverableRow>().HasIndex(x => x.WorkItemId);
        mb.Entity<DeliverableRow>().HasIndex(x => new { x.CrewId, x.CreatedAt });
        mb.Entity<DeliverableRow>().HasIndex(x => new { x.GeneratorAgentId, x.CreatedAt });
        mb.Entity<DeliverableRow>().HasIndex(x => new { x.EvaluatorAgentId, x.CreatedAt });

        // Phase 14.1 — agent_level_history (one row per promotion/demotion).
        mb.Entity<AgentLevelHistoryRow>().ToTable("agent_level_history").HasKey(x => x.Id);
        mb.Entity<AgentLevelHistoryRow>().Property(x => x.WindowAvg).HasConversion<double>();
        mb.Entity<AgentLevelHistoryRow>().HasIndex(x => new { x.AgentId, x.CreatedAt });

        // Phase 15.1 — product-scoped iteration registry. Bound to product via FK.
        mb.Entity<ProductIterationRow>().ToTable("product_iterations").HasKey(x => x.Id);
        // Q15.4 — re-import resolves rename via id, so (product, providerNodeId) is unique.
        mb.Entity<ProductIterationRow>()
            .HasIndex(x => new { x.ProductId, x.ProviderNodeId })
            .IsUnique();
        // Plan & Design and the SprintManager filter by product first.
        mb.Entity<ProductIterationRow>().HasIndex(x => x.ProductId);
        mb.Entity<ProductIterationRow>().HasIndex(x => x.State);
        mb.Entity<ProductIterationRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 15.2 — product-scoped area-path registry.
        mb.Entity<ProductAreaPathRow>().ToTable("product_area_paths").HasKey(x => x.Id);
        mb.Entity<ProductAreaPathRow>()
            .HasIndex(x => new { x.ProductId, x.ProviderNodeId })
            .IsUnique();
        mb.Entity<ProductAreaPathRow>().HasIndex(x => x.ProductId);
        // Q15.2 — at most one default per product is enforced by app code on write
        // (a partial filtered unique index is SQLite-supported but EF doesn't
        // model it portably; the dispatcher zeroes the previous default first).
        mb.Entity<ProductAreaPathRow>().HasIndex(x => new { x.ProductId, x.IsDefaultFilter });
        mb.Entity<ProductAreaPathRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 15.5 — repo ↔ iteration bindings.
        mb.Entity<RepoIterationBindingRow>().ToTable("repo_iteration_bindings").HasKey(x => x.Id);
        // A binding row has a single iteration target and a single repo source.
        mb.Entity<RepoIterationBindingRow>()
            .HasIndex(x => new { x.RepoId, x.Role });
        mb.Entity<RepoIterationBindingRow>()
            .HasIndex(x => x.ProductIterationId);
        mb.Entity<RepoIterationBindingRow>()
            .HasOne<RepoRow>()
            .WithMany()
            .HasForeignKey(x => x.RepoId)
            .OnDelete(DeleteBehavior.Cascade);
        mb.Entity<RepoIterationBindingRow>()
            .HasOne<ProductIterationRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductIterationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 15.10 — saved-prefix shorthands per product.
        mb.Entity<ProductSavedPrefixRow>().ToTable("product_saved_prefixes").HasKey(x => x.Id);
        mb.Entity<ProductSavedPrefixRow>()
            .HasIndex(x => new { x.ProductId, x.Prefix, x.Kind })
            .IsUnique();
        mb.Entity<ProductSavedPrefixRow>().HasIndex(x => x.ProductId);
        mb.Entity<ProductSavedPrefixRow>()
            .HasOne<ProductRow>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 16.1 — task transcript events. Index on (WorkItemId, At) so
        // pagination on /api/work-items/{id}/transcript walks a single index.
        mb.Entity<TaskTranscriptEventRow>().ToTable("task_transcript_event").HasKey(x => x.Id);
        mb.Entity<TaskTranscriptEventRow>().HasIndex(x => new { x.WorkItemId, x.At });
        mb.Entity<TaskTranscriptEventRow>().HasIndex(x => x.WorkItemId);

        // Phase 17.1 — persistent task queue + per-row history.
        // Dequeue index: per (kind, target, state) walking by priority + FIFO.
        // Idempotency: partial unique on (WorkItemId) WHERE State IN OpenStates
        // is created in Phase17Migration since EF Core doesn't model SQLite
        // partial unique indexes portably.
        mb.Entity<TaskQueueRow>().ToTable("task_queue").HasKey(x => x.Id);
        mb.Entity<TaskQueueRow>().HasIndex(x => new { x.TargetKind, x.TargetId, x.State, x.PriorityRank, x.EnqueuedAt });
        mb.Entity<TaskQueueRow>().HasIndex(x => x.WorkItemId);
        mb.Entity<TaskQueueRow>().HasIndex(x => new { x.State, x.AckRequiredBy });
        mb.Entity<TaskQueueRow>().HasIndex(x => x.RoutedToCrewId);

        mb.Entity<TaskQueueHistoryRow>().ToTable("task_queue_history").HasKey(x => x.Id);
        mb.Entity<TaskQueueHistoryRow>().HasIndex(x => new { x.QueueId, x.At });

        // Phase 22 — agent assignment history (append-only timeline).
        mb.Entity<AgentAssignmentRow>().ToTable("agent_assignments").HasKey(x => x.Id);
        mb.Entity<AgentAssignmentRow>().HasIndex(x => new { x.WorkItemId, x.At })
            .HasDatabaseName("IX_agent_assignments_workitem_at");
    }
}
