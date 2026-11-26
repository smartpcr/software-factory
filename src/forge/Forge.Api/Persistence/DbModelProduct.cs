// -----------------------------------------------------------------------
// <copyright file="DbModelProduct.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase D — EF model configuration for the **Product DB** entities.
/// Called from <see cref="ProductDbContext.OnModelCreating"/>.
///
/// <para>Cross-context FKs are dropped because EF can't model an FK
/// across two physical DB files. The columns and indexes remain so
/// product-side queries still work and the foreign reference stays
/// queryable as a plain string. Callers that need the joined product /
/// repo data must read it from <c>ControlDbContext</c> separately.</para>
///
/// <para>Phase G — cross-context relationships (e.g. <c>ProductIterationRow</c>
/// → <c>ProductRow</c>) become loose string columns post-split. FK
/// semantics are enforced at the application layer by
/// <c>IProductDbContextFactory</c>.</para>
/// </summary>
public static class DbModelProduct
{
    public static void Configure(ModelBuilder mb)
    {
        ChatDeliveryModel.ConfigureProduct(mb);
        // Phase 80.B — repos moved here from the control DB. Within ONE
        // product the remote URL + clone root are still unique (per-product
        // indexes); the GLOBAL (cross-product) Q2/Q5 uniqueness moved to the
        // control `repo_registry`. The cross-context FK to products is dropped
        // (enforced at the app layer). Index names match the move service's
        // raw DDL so EnsureCreated (fresh DBs) + the migration don't double-create.
        mb.Entity<RepoRow>().ToTable("repos").HasKey(x => x.Id);
        mb.Entity<RepoRow>().HasIndex(x => x.RemoteUrl).IsUnique()
            .HasDatabaseName("IX_repos_RemoteUrl");
        mb.Entity<RepoRow>().HasIndex(x => x.CloneRoot).IsUnique()
            .HasDatabaseName("IX_repos_CloneRoot");
        mb.Entity<RepoRow>().HasIndex(x => x.ProductId)
            .HasDatabaseName("IX_repos_ProductId");

        // Phase 1 — prototype data tables.
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
        mb.Entity<IdempotencyRow>().ToTable("idempotency").HasKey(x => x.Key);

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

        // Phase 23 — work_item_sync_runs history table.
        mb.Entity<WorkItemSyncRunRow>().ToTable("work_item_sync_runs").HasKey(x => x.Id);
        mb.Entity<WorkItemSyncRunRow>().HasIndex(x => new { x.RepoId, x.StartedAt });

        // Phase 25 — per-item detail for sync runs.
        mb.Entity<SyncRunItemRow>().ToTable("sync_run_items").HasKey(x => x.Id);
        mb.Entity<SyncRunItemRow>().HasIndex(x => x.RunId);

        // Phase 14.1 — deliverables (one row per work-item × iteration).
        mb.Entity<DeliverableRow>().ToTable("deliverables").HasKey(x => x.Id);
        mb.Entity<DeliverableRow>().Property(x => x.GenCostUsd).HasConversion<double>();
        mb.Entity<DeliverableRow>().Property(x => x.EvalCostUsd).HasConversion<double>();
        mb.Entity<DeliverableRow>().HasIndex(x => x.WorkItemId);
        mb.Entity<DeliverableRow>().HasIndex(x => new { x.CrewId, x.CreatedAt });
        mb.Entity<DeliverableRow>().HasIndex(x => new { x.GeneratorAgentId, x.CreatedAt });
        mb.Entity<DeliverableRow>().HasIndex(x => new { x.EvaluatorAgentId, x.CreatedAt });

        // Phase 52 — agent_pair_assignment (one row per (work_item, pair) attempt).
        mb.Entity<AgentPairAssignmentRow>().ToTable("agent_pair_assignment").HasKey(x => x.Id);
        mb.Entity<AgentPairAssignmentRow>().Property(x => x.TotalCostUsd).HasConversion<double>();
        mb.Entity<AgentPairAssignmentRow>().HasIndex(x => new { x.WorkItemId, x.SequenceNo }).IsUnique();
        mb.Entity<AgentPairAssignmentRow>().HasIndex(x => new { x.GeneratorAgentId, x.CreatedAt });
        mb.Entity<AgentPairAssignmentRow>().HasIndex(x => new { x.EvaluatorAgentId, x.CreatedAt });

        // Phase 55 — agent_memory (per work-item, agent, role).
        mb.Entity<AgentMemoryRow>().ToTable("agent_memory").HasKey(x => x.Id);
        mb.Entity<AgentMemoryRow>().HasIndex(x => new { x.WorkItemId, x.AgentId, x.Role }).IsUnique();
        mb.Entity<AgentMemoryRow>().HasIndex(x => x.WorkItemId);

        // Phase 39 (Layer C) — open questions emitted by generators.
        mb.Entity<DeliverableQuestionRow>().ToTable("deliverable_questions").HasKey(x => x.Id);
        mb.Entity<DeliverableQuestionRow>().HasIndex(x => x.WorkItemId);
        mb.Entity<DeliverableQuestionRow>().HasIndex(x => new { x.WorkItemId, x.ExternalId, x.Iter });

        // Phase 15.1 — product-scoped iteration registry.
        // NOTE (Phase D): cross-context FK to ProductRow is omitted here
        // because EF can't model an FK across DB files. The application
        // layer enforces the relationship via IProductDbContextFactory.
        mb.Entity<ProductIterationRow>().ToTable("product_iterations").HasKey(x => x.Id);
        // Q15.4 — re-import resolves rename via id, so (product, providerNodeId) is unique.
        mb.Entity<ProductIterationRow>()
            .HasIndex(x => new { x.ProductId, x.ProviderNodeId })
            .IsUnique();
        // Plan & Design and the SprintManager filter by product first.
        mb.Entity<ProductIterationRow>().HasIndex(x => x.ProductId);
        mb.Entity<ProductIterationRow>().HasIndex(x => x.State);

        // Phase 15.2 — product-scoped area-path registry.
        mb.Entity<ProductAreaPathRow>().ToTable("product_area_paths").HasKey(x => x.Id);
        mb.Entity<ProductAreaPathRow>()
            .HasIndex(x => new { x.ProductId, x.ProviderNodeId })
            .IsUnique();
        mb.Entity<ProductAreaPathRow>().HasIndex(x => x.ProductId);
        // Q15.2 — at most one default per product is enforced by app code on write.
        mb.Entity<ProductAreaPathRow>().HasIndex(x => new { x.ProductId, x.IsDefaultFilter });

        // Phase 15.5 — repo ↔ iteration bindings.
        // The FK to RepoRow (cross-context) is dropped; the FK to
        // ProductIterationRow (in-context) is preserved.
        mb.Entity<RepoIterationBindingRow>().ToTable("repo_iteration_bindings").HasKey(x => x.Id);
        mb.Entity<RepoIterationBindingRow>()
            .HasIndex(x => new { x.RepoId, x.Role });
        mb.Entity<RepoIterationBindingRow>()
            .HasIndex(x => x.ProductIterationId);
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

        // Phase 16.1 — task transcript events.
        mb.Entity<TaskTranscriptEventRow>().ToTable("task_transcript_event").HasKey(x => x.Id);
        mb.Entity<TaskTranscriptEventRow>().HasIndex(x => new { x.WorkItemId, x.At });
        mb.Entity<TaskTranscriptEventRow>().HasIndex(x => x.WorkItemId);

        // Phase 82 — iteration_prompt: append-only audit of per-iteration
        // generator + evaluator prompts. Lives outside transcript retention
        // so the audit trail survives indefinitely; pruned only on
        // explicit workstream delete / restart / ClearTranscript.
        mb.Entity<IterationPromptRow>().ToTable("iteration_prompt").HasKey(x => x.Id);
        mb.Entity<IterationPromptRow>()
            .HasIndex(x => new { x.WorkItemId, x.Iter, x.Role, x.Attempt })
            .IsUnique()
            .HasDatabaseName("IX_iteration_prompt_wi_iter_role_attempt");
        mb.Entity<IterationPromptRow>()
            .HasIndex(x => new { x.WorkItemId, x.Iter })
            .HasDatabaseName("IX_iteration_prompt_wi_iter");

        // Phase 92 — per-workstream human operator steering prompts.
        mb.Entity<OperatorPromptRow>().ToTable("operator_prompts").HasKey(x => x.Id);
        mb.Entity<OperatorPromptRow>()
            .HasIndex(x => new { x.WorkstreamId, x.DismissedAt })
            .HasDatabaseName("IX_operator_prompts_ws_dismissed");

        // Phase 108 — product rule catalog (tags, rules, per-workstream tag overrides).
        mb.Entity<RuleTagRow>().ToTable("rule_tags").HasKey(x => x.Id);
        mb.Entity<RuleTagRow>()
            .HasIndex(x => new { x.ProductId, x.Name })
            .IsUnique()
            .HasDatabaseName("IX_rule_tags_product_name");
        mb.Entity<CatalogRuleRow>().ToTable("catalog_rules").HasKey(x => x.Id);
        mb.Entity<CatalogRuleRow>()
            .HasIndex(x => x.ProductId)
            .HasDatabaseName("IX_catalog_rules_product");
        mb.Entity<WorkstreamTagOverrideRow>().ToTable("workstream_tag_overrides").HasKey(x => x.Id);
        mb.Entity<WorkstreamTagOverrideRow>()
            .HasIndex(x => new { x.WorkstreamId, x.TagId })
            .IsUnique()
            .HasDatabaseName("IX_workstream_tag_overrides_ws_tag");
        mb.Entity<WorkItemRuleRow>().ToTable("work_item_rules").HasKey(x => x.Id);
        mb.Entity<WorkItemRuleRow>()
            .HasIndex(x => new { x.WorkItemId, x.RuleId })
            .IsUnique()
            .HasDatabaseName("IX_work_item_rules_item_rule");
        mb.Entity<RulePolicyRow>().ToTable("rule_policies").HasKey(x => x.Id);
        mb.Entity<RulePolicyRow>()
            .HasIndex(x => new { x.ProductId, x.Name })
            .IsUnique()
            .HasDatabaseName("IX_rule_policies_product_name");
        mb.Entity<WorkItemPolicyRow>().ToTable("work_item_policies").HasKey(x => x.Id);
        mb.Entity<WorkItemPolicyRow>()
            .HasIndex(x => new { x.WorkItemId, x.PolicyId })
            .IsUnique()
            .HasDatabaseName("IX_work_item_policies_item_policy");
        mb.Entity<RulePolicyScopeRow>().ToTable("rule_policy_scopes").HasKey(x => x.Id);
        mb.Entity<RulePolicyScopeRow>()
            .HasIndex(x => new { x.PolicyId, x.ScopeKind, x.ScopeId })
            .IsUnique()
            .HasDatabaseName("IX_rule_policy_scopes_policy_scope");

        // Phase 17.1 — persistent task queue + per-row history.
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

        // Phase 24 — PR comments + history.
        mb.Entity<PrCommentRow>().ToTable("pr_comments").HasKey(x => x.Id);
        mb.Entity<PrCommentRow>().HasIndex(x => new { x.PrId, x.ProviderCommentId }).IsUnique();
        mb.Entity<PrCommentRow>().HasIndex(x => new { x.PrId, x.RequiresAttention });
        mb.Entity<PrCommentRow>().HasIndex(x => x.ThreadId);
        mb.Entity<PrCommentHistoryRow>().ToTable("pr_comment_history").HasKey(x => x.Id);
        mb.Entity<PrCommentHistoryRow>().HasIndex(x => new { x.CommentId, x.At });

        // Phase 29 — build validation runs.
        mb.Entity<BuildValidationRunRow>().ToTable("build_validation_runs").HasKey(x => x.Id);
        mb.Entity<BuildValidationRunRow>().HasIndex(x => new { x.RepoId, x.StartedAt });

        // Phase 27 — build sync surface.
        mb.Entity<BuildRow>().ToTable("builds").HasKey(x => x.Id);
        mb.Entity<BuildRow>().HasIndex(x => x.PrId);
        mb.Entity<BuildRow>().HasIndex(x => new { x.PrId, x.IsActive });
        mb.Entity<BuildRow>().HasIndex(x => x.LastSyncAt);

        mb.Entity<BuildStageRow>().ToTable("build_stages").HasKey(x => x.Id);
        mb.Entity<BuildStageRow>().HasIndex(x => new { x.BuildId, x.StageOrder });

        mb.Entity<BuildStageLogRow>()
            .ToTable("build_stage_logs")
            .HasKey(x => new { x.StageId, x.LogIndex });

        mb.Entity<BuildArtifactRow>().ToTable("build_artifacts").HasKey(x => x.Id);
        mb.Entity<BuildArtifactRow>().HasIndex(x => x.BuildId);

        mb.Entity<BuildArtifactFileRow>().ToTable("build_artifact_files").HasKey(x => x.Id);
        mb.Entity<BuildArtifactFileRow>().HasIndex(x => x.ArtifactId);

        mb.Entity<BuildFailurePointRow>().ToTable("build_failure_points").HasKey(x => x.Id);
        mb.Entity<BuildFailurePointRow>().HasIndex(x => x.StageId);

        mb.Entity<BuildFixPendingRow>().ToTable("build_fix_pending").HasKey(x => x.Id);
        mb.Entity<BuildFixPendingRow>().HasIndex(x => x.WorkItemId).IsUnique();

        mb.Entity<BuildAnnotationRow>().ToTable("build_annotations").HasKey(x => x.Id);
        mb.Entity<BuildAnnotationRow>().HasIndex(x => x.BuildId);

        // Phase 28 — story spawns + workstream dependency edges.
        mb.Entity<StorySpawnRow>().ToTable("story_spawns").HasKey(x => x.Id);
        // Q28.1 — duplicate spawn for the same StoryId is rejected with 409.
        mb.Entity<StorySpawnRow>().HasIndex(x => x.StoryId).IsUnique();
        mb.Entity<StorySpawnRow>().HasIndex(x => x.Phase);

        mb.Entity<WorkstreamDepRow>()
            .ToTable("workstream_deps")
            .HasKey(x => new { x.WorkstreamId, x.DependsOnId });
        mb.Entity<WorkstreamDepRow>().HasIndex(x => x.WorkstreamId);
        mb.Entity<WorkstreamDepRow>().HasIndex(x => x.DependsOnId);

        // Phase 28 back-pointer index for story → workstreams roll-up.
        mb.Entity<WorkstreamRow>().HasIndex(x => x.StorySpawnId);

        // docs/forge/e2e.md Stage 1.1 — composite index for the swarm-control
        // DAG-render query (scope = single spawn, group by category to
        // render stacked impl/qa-e2e pairs). Index name matches the
        // CREATE INDEX emitted by Phase79Migration so upgraded DBs and
        // freshly-EnsureCreated DBs share the same schema fingerprint.
        mb.Entity<WorkstreamRow>()
            .HasIndex(x => new { x.StorySpawnId, x.Category })
            .HasDatabaseName("IX_workstreams_StorySpawnId_Category");
    }
}
